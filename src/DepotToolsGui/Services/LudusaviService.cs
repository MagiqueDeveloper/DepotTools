using System.IO;
using System.IO.Compression;
using System.Text.Json;
using DepotToolsGui.Models;
using DepotToolsGui.Services.Downloads;
using Microsoft.Extensions.Logging;

namespace DepotToolsGui.Services;

/// <summary>
/// Fetches the Ludusavi save-backup engine at runtime (never bundled). Unlike
/// <see cref="DepotDownloaderService"/> and <see cref="SteamAutoCrackService"/>, it deliberately never
/// auto-updates: Ludusavi's CLI and <c>simple</c> output feed <see cref="HydraCloudSyncService"/>, so an
/// unvetted upstream change could silently break save sync. Once fetched, the local copy stays until it
/// is removed.
/// </summary>
public class LudusaviService(GithubProxy gh, CacheService cache, ILogger<LudusaviService> log)
{
    private static readonly string ToolDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DepotToolsGui", "ludusavi");

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Where ludusavi.exe lives once fetched (the portable win64 zip extracts it at the root).</summary>
    public string ExePath => Path.Combine(ToolDir, "ludusavi.exe");

    /// <summary>Release tag recorded when the tool was fetched, for the Settings status line. Null for
    /// a copy present on disk from before version tracking (or a failed fetch).</summary>
    public string? CachedVersion => cache.LudusaviVersion;

    /// <summary>
    /// Ensure ludusavi.exe is on disk, fetching it once if not. Null only if it couldn't be obtained.
    /// </summary>
    public async Task<string?> EnsureAsync(IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (File.Exists(ExePath)) return ExePath;

        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(ExePath)) return ExePath; // won the race

            string url = $"https://api.github.com/repos/{AppConfig.LudusaviRepo}/releases/latest";
            using var res = await gh.SendAsync(url, ct);
            if (res is null || !res.IsSuccessStatusCode)
            {
                log.LogDebug("Ludusavi release lookup failed: {Status}", res?.StatusCode);
                return null;
            }

            var release = JsonSerializer.Deserialize<GithubRelease>(
                await res.Content.ReadAsStringAsync(ct), JsonOpts);
            var asset = release?.Assets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && a.Name.Contains("win64", StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                log.LogDebug("Ludusavi release has no win64 .zip asset");
                return null;
            }

            Directory.CreateDirectory(ToolDir);
            string zipPath = Path.Combine(ToolDir, "ludusavi.zip");
            var sink = progress is null ? null : new ProgressRelay<double?>(f =>
                progress.Report(new DownloadProgress(
                    (long)((f ?? 0) * asset.Size), asset.Size > 0 ? asset.Size : null)));
            await gh.DownloadAsync(asset.DownloadUrl, zipPath, sink, ct);

            // Verify before extracting over a working copy: this is an executable we then run.
            if (!AssetHash.Matches(zipPath, asset.Digest))
            {
                log.LogDebug("Ludusavi asset digest mismatch");
                try { File.Delete(zipPath); } catch { }
                return null;
            }

            ZipFile.ExtractToDirectory(zipPath, ToolDir, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { /* leftover zip is harmless */ }

            if (!File.Exists(ExePath)) return null;

            cache.LudusaviVersion = release!.TagName;
            cache.LudusaviCheckedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return ExePath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Obtaining Ludusavi failed");
            return null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Delete the tool folder and clear its cached version so the next Ensure re-downloads. False when
    /// the folder can't go (exe locked because the tool is running); the cached version is then left
    /// intact so status stays truthful.
    /// </summary>
    public bool Remove()
    {
        try
        {
            if (Directory.Exists(ToolDir)) Directory.Delete(ToolDir, recursive: true);
            cache.LudusaviVersion = null;
            return true;
        }
        catch { return false; } // exe locked (tool running) → caller surfaces the failure
    }
}