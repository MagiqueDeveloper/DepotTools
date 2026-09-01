using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DepotToolsGui.Services;

public sealed record HydraCloudSyncResult(long AppId, string Action, string? Error = null);

public sealed class HydraCloudSyncService : IDisposable
{
    private const string VariantId = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private const string RawPath = "<home>/DepotTools/Ludusavi";
    private readonly HydraCloudService _cloud;
    private readonly SteamLibraryService _steamLibrary;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Threading.Timer _timer;
    private readonly string _ludusaviPath;
    private readonly LudusaviService _ludusavi;
    private readonly string _statePath;
    private bool _started;
    // Pre-signed R2/S3 URLs are absolute and must not go through _cloud.SendAsync (it prefixes the
    // base address), so they get their own client. Shared + static: one per request leaked sockets.
    private static readonly HttpClient SignedUrlClient = new() { Timeout = TimeSpan.FromMinutes(5) };

    public event Action<string>? StatusChanged;

    public HydraCloudSyncService(HydraCloudService cloud, SteamLibraryService steamLibrary,
        SettingsService settings, LudusaviService ludusavi)
    {
        _cloud = cloud;
        _steamLibrary = steamLibrary;
        _settings = settings;
        _ludusavi = ludusavi;
        _ludusaviPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DepotToolsGui", "ludusavi", "ludusavi.exe");
        _statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DepotToolsGui", "hydra-cloud-sync.json");
        _timer = new System.Threading.Timer(_ => _ = SyncAllAsync("periodic"), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15));
        _ = SyncAllAsync("startup");
    }

    public async Task<IReadOnlyList<HydraCloudSyncResult>> SyncAllAsync(string trigger, CancellationToken ct = default)
    {
        if (!_settings.CloudSavesEnabled || !_cloud.HasActiveSubscription) return [];
        if (await _ludusavi.EnsureAsync(null, ct) is null) return [];
        if (!await _gate.WaitAsync(0, ct)) return [];
        try { return await SyncAllCoreAsync(trigger, ct); }
        finally { _gate.Release(); }
    }

    public async Task StopAndSyncAsync(CancellationToken ct = default)
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        if (!_started) return;
        // Shutdown must not be dropped the way periodic/startup syncs are: wait for the gate instead
        // of skipping when a sync is already running, bounded by the caller's ct.
        await _gate.WaitAsync(ct);
        try { await SyncAllCoreAsync("shutdown", ct); }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<HydraCloudSyncResult>> SyncAllCoreAsync(string trigger, CancellationToken ct)
    {
        var results = new List<HydraCloudSyncResult>();
        foreach (long appId in _steamLibrary.GetInstalledAppIds())
        {
            ct.ThrowIfCancellationRequested();
            try { results.Add(await SyncGameAsync(appId, ct)); }
            catch (Exception ex) { results.Add(new(appId, "error", ex.Message)); }
        }
        return results;
    }

    private async Task<HydraCloudSyncResult> SyncGameAsync(long appId, CancellationToken ct)
    {
        string temp = Path.Combine(Path.GetTempPath(), "DepotToolsGui", "hydra-cloud", appId.ToString());
        Directory.CreateDirectory(temp);
        try
        {
            StatusChanged?.Invoke($"Scanning save data for {appId}…");
            if (!await RunLudusaviAsync("backup", appId, temp, ct))
                return new(appId, "unsupported-game");
            var local = BuildSnapshot(temp);
            if (local.Files.Count == 0) return new(appId, "no-save-data");

            var remote = await GetRemoteSnapshotAsync(appId, ct);
            var state = LoadState().GetValueOrDefault(appId.ToString());
            if (remote is null)
            {
                await UploadSnapshotAsync(appId, local, 0, null, ct);
                SaveState(appId, local.AggregateHash, local.AggregateHash);
                return new(appId, "uploaded");
            }

            if (remote.AggregateHash == local.AggregateHash)
            {
                SaveState(appId, local.AggregateHash, remote.AggregateHash);
                return new(appId, "already-synced");
            }

            bool localUnchanged = state is not null && state.LocalHash == local.AggregateHash;
            bool remoteUnchanged = state is not null && state.RemoteHash == remote.AggregateHash;
            if (localUnchanged && !remoteUnchanged)
            {
                try { await RestoreSnapshotAsync(appId, remote, temp, ct); }
                catch
                {
                    // A mid-restore throw may have left local saves half-overwritten. The pre-sync
                    // backup in `temp` is still intact (SyncGameAsync only deletes it in `finally`),
                    // so roll the local copies back from it before propagating.
                    try { await RunLudusaviAsync("restore", appId, temp, ct); } catch { /* best-effort rollback */ }
                    throw;
                }
                SaveState(appId, remote.AggregateHash, remote.AggregateHash);
                return new(appId, "restored");
            }
            if (remoteUnchanged && !localUnchanged)
            {
                await UploadSnapshotAsync(appId, local, remote.Version, remote.Id, ct);
                SaveState(appId, local.AggregateHash, local.AggregateHash);
                return new(appId, "uploaded");
            }

            return new(appId, "conflict", "Local and Hydra Cloud saves both changed; no files were overwritten.");
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private async Task<RemoteSnapshot?> GetRemoteSnapshotAsync(long appId, CancellationToken ct)
    {
        using var response = await _cloud.SendAsync(HttpMethod.Get, $"/profile/cloud-saves/snapshots?shop=steam&objectId={appId}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var values = await response.Content.ReadFromJsonAsync<List<RemoteSnapshot>>(cancellationToken: ct) ?? [];
        if (values.Count > 1) throw new InvalidDataException("Hydra returned multiple active snapshots for one game.");
        return values.Count == 0 ? null : ValidateRemote(values[0]);
    }

    private async Task UploadSnapshotAsync(long appId, LocalSnapshot local, int baseVersion, string? expectedId, CancellationToken ct)
    {
        var payload = new PrepareRequest("steam", appId.ToString(), "windows", Environment.MachineName,
            local.AggregateHash, baseVersion, [], [new SnapshotVariant(VariantId, "default")], local.Files);
        using var preparedResponse = await _cloud.SendAsync(HttpMethod.Post, "/profile/cloud-saves/prepare-snapshot", JsonContent.Create(payload), ct);
        preparedResponse.EnsureSuccessStatusCode();
        var prepared = await preparedResponse.Content.ReadFromJsonAsync<PrepareResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("Hydra returned an empty snapshot preparation response.");
        if (prepared.SnapshotHash != local.AggregateHash || prepared.Files.Count != local.Files.Count)
            throw new InvalidDataException("Hydra snapshot preparation did not match the local snapshot.");

        foreach (var item in prepared.Files)
        {
            var source = local.Files.FirstOrDefault(f => f.VariantId == item.VariantId && f.RawPath == item.RawPath && f.RelativePath == item.RelativePath)
                ?? throw new InvalidDataException("Hydra requested an unknown save file.");
            if (item.Status == "skip") continue;
            if (item.UploadUrl is null || item.RequiredHeaders is null) throw new InvalidDataException("Hydra returned an incomplete upload request.");
            using var upload = new HttpRequestMessage(HttpMethod.Put, item.UploadUrl);
            upload.Headers.TryAddWithoutValidation("Content-Length", source.SizeBytes.ToString());
            upload.Headers.TryAddWithoutValidation("x-amz-checksum-sha256", Convert.ToBase64String(Convert.FromHexString(source.Hash)));
            upload.Content = new StreamContent(File.OpenRead(source.AbsolutePath));
            using var result = await SignedUrlClient.SendAsync(upload, ct);
            result.EnsureSuccessStatusCode();
        }

        using var commitResponse = await _cloud.SendAsync(HttpMethod.Post, "/profile/cloud-saves/commit-snapshot",
            JsonContent.Create(new { pendingSnapshotId = prepared.PendingSnapshotId }), ct);
        if (commitResponse.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException("Hydra snapshot changed during upload; retrying is required.");
        commitResponse.EnsureSuccessStatusCode();
        var committed = await commitResponse.Content.ReadFromJsonAsync<CommitResponse>(cancellationToken: ct)
            ?? throw new InvalidDataException("Hydra returned an empty commit response.");
        if (committed.Version != baseVersion + 1 || committed.AggregateHash != local.AggregateHash || (expectedId is not null && committed.SnapshotId != expectedId))
            throw new InvalidDataException("Hydra committed snapshot did not match the local snapshot.");
    }

    private async Task RestoreSnapshotAsync(long appId, RemoteSnapshot remote, string temp, CancellationToken ct)
    {
        using var manifestResponse = await _cloud.SendAsync(HttpMethod.Get, $"/profile/cloud-saves/snapshot-restore-manifest?snapshotId={Uri.EscapeDataString(remote.Id)}", null, ct);
        manifestResponse.EnsureSuccessStatusCode();
        var manifest = await manifestResponse.Content.ReadFromJsonAsync<RestoreManifest>(cancellationToken: ct)
            ?? throw new InvalidDataException("Hydra returned an empty restore manifest.");
        if (manifest.Snapshot.Id != remote.Id || manifest.Snapshot.Version != remote.Version || manifest.Files.Count != remote.FileCount)
            throw new InvalidDataException("Hydra restore manifest did not match the snapshot summary.");

        using var urlsResponse = await _cloud.SendAsync(HttpMethod.Get, $"/profile/cloud-saves/snapshot-download-urls?snapshotId={Uri.EscapeDataString(remote.Id)}", null, ct);
        urlsResponse.EnsureSuccessStatusCode();
        var urls = await urlsResponse.Content.ReadFromJsonAsync<List<DownloadFile>>(cancellationToken: ct) ?? [];
        if (urls.Count != manifest.Files.Count) throw new InvalidDataException("Hydra returned incomplete restore URLs.");
        foreach (var file in urls)
        {
            var target = SafeCombine(temp, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var response = await SignedUrlClient.GetAsync(file.DownloadUrl, ct);
            response.EnsureSuccessStatusCode();
            await using var output = File.Create(target);
            await response.Content.CopyToAsync(output, ct);
            if (ComputeHash(target) != file.Hash || new FileInfo(target).Length != file.SizeBytes) throw new InvalidDataException("Downloaded save failed integrity verification.");
        }
        await RunLudusaviAsync("restore", appId, temp, ct);
    }

    private async Task<bool> RunLudusaviAsync(string command, long appId, string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ludusaviPath)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        psi.ArgumentList.Add(command); psi.ArgumentList.Add(appId.ToString()); psi.ArgumentList.Add("--path"); psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("--force"); psi.ArgumentList.Add("--api"); psi.ArgumentList.Add("--no-cloud-sync"); psi.ArgumentList.Add("--format"); psi.ArgumentList.Add("simple");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start Ludusavi.");
        // Read stdout/stderr concurrently with waiting: awaiting exit first deadlocks once ludusavi's
        // redirected pipe fills (real with --api JSON output).
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode == 0) return true;
        if (command == "backup" && stdout.Contains("\"unknownGames\"", StringComparison.Ordinal)) return false;
        throw new InvalidOperationException(stderr.Trim());
    }

    private static LocalSnapshot BuildSnapshot(string root)
    {
        var files = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(path =>
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            var info = new FileInfo(path);
            return new LocalFile(VariantId, RawPath, relative, ComputeHash(path), info.Length, info.LastWriteTimeUtc.ToString("O"), path);
        }).OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList() : [];
        return new LocalSnapshot(files, AggregateHash(files));
    }

    private Dictionary<string, SyncState> LoadState()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, SyncState>>(File.ReadAllText(_statePath)) ?? []; }
        catch { return []; }
    }
    private void SaveState(long appId, string localHash, string remoteHash)
    {
        var state = LoadState(); state[appId.ToString()] = new SyncState(localHash, remoteHash);
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!); File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
    }

    private static RemoteSnapshot ValidateRemote(RemoteSnapshot value)
    {
        if (string.IsNullOrWhiteSpace(value.Id) || value.Version < 1 || value.FileCount < 0 || value.TotalSizeBytes < 0 || value.AggregateHash.Length != 64) throw new InvalidDataException("Invalid Hydra snapshot summary.");
        return value;
    }
    internal static string SafeCombine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Split('/').Any(x => x is "" or "." or "..")) throw new InvalidDataException("Unsafe Hydra save path.");
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe Hydra save path.");
        return full;
    }
    private static string ComputeHash(string path) { using var sha = SHA256.Create(); using var stream = File.OpenRead(path); return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant(); }
    internal static string AggregateHashForTests(IEnumerable<(string RelativePath, string Hash, long SizeBytes)> files)
    {
        var local = files.Select(f => new LocalFile(VariantId, RawPath, f.RelativePath, f.Hash, f.SizeBytes, "2026-01-01T00:00:00Z", "")).ToList();
        return AggregateHash(local);
    }
    internal static string SafeCombineForTests(string root, string relative) => SafeCombine(root, relative);
    private static string AggregateHash(IReadOnlyList<LocalFile> files)
    {
        var canonical = new { snapshotHashVersion = 1, variants = new[] { new { variantId = VariantId, kind = "default" } }, files = files.OrderBy(f => f.RelativePath).Select(f => new { variantId = f.VariantId, rawPath = f.RawPath, relativePath = f.RelativePath, hash = f.Hash, sizeBytes = f.SizeBytes }).ToArray() };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(canonical, new JsonSerializerOptions(JsonSerializerDefaults.Web))))).ToLowerInvariant();
    }
    public void Dispose() { _timer.Dispose(); _gate.Dispose(); }

    private sealed record SyncState(string LocalHash, string RemoteHash);
    private sealed record LocalSnapshot(List<LocalFile> Files, string AggregateHash);
    private sealed record LocalFile(string VariantId, string RawPath, string RelativePath, string Hash, long SizeBytes, string LastModifiedAt,
        [property: JsonIgnore] string AbsolutePath);
    private sealed record SnapshotVariant(string VariantId, string Kind);
    private sealed record PrepareRequest(string Shop, string ObjectId, string Platform, string Hostname, string SnapshotHash, int BaseVersion, string[] CustomPathRawPaths, SnapshotVariant[] Variants, List<LocalFile> Files);
    private sealed record PrepareResponse(string PendingSnapshotId, string SnapshotHash, List<PreparedFile> Files);
    private sealed record PreparedFile(string VariantId, string RawPath, string RelativePath, string Status, string? UploadUrl, Dictionary<string, string>? RequiredHeaders);
    private sealed record CommitResponse(string SnapshotId, int Version, int FileCount, long TotalSizeBytes, string AggregateHash);
    private sealed record RemoteSnapshot(string Id, int Version, string CreatedAt, string UpdatedAt, int FileCount, long TotalSizeBytes, string AggregateHash);
    private sealed record RestoreManifest(RestoreSnapshot Snapshot, List<RestoreFile> Files);
    private sealed record RestoreSnapshot(string Id, int Version, string Shop, string ObjectId);
    private sealed record RestoreFile(string VariantId, string RawPath, string RelativePath, string Hash, long SizeBytes, string LastModifiedAt);
    private sealed record DownloadFile(string VariantId, string RawPath, string RelativePath, string Hash, long SizeBytes, string LastModifiedAt, string DownloadUrl);
}
