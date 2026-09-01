using Velopack;
using Velopack.Sources;

namespace DepotToolsGui.Services;

/// <summary>
/// User-approved application updates through Velopack + GitHub Releases. Checking, downloading, and
/// applying are deliberately separate so callers can surface an available update before any package is
/// transferred or the process restarts.
/// <para>
/// Resilience is two-layered: (1) <see cref="ProxiedFileDownloader"/> routes each repo's feed + package
/// downloads through GitHub mirrors for blocked/throttled regions (e.g. China); (2) it tries each repo in
/// <see cref="AppConfig.GithubReleasesRepos"/> in order, so if the PRIMARY repo is gone entirely
/// (banned / DMCA'd / account removed. Something the mirrors can't fix) it falls through to a backup repo.
/// </para>
/// </summary>
public class UpdateService
{
    // One UpdateManager per configured repo, in priority order (primary first). All share the proxied
    // downloader so every repo is also mirror-resilient.
    private readonly UpdateManager[] _managers =
        AppConfig.GithubReleasesRepos
            .Select(repo => new UpdateManager(
                new GithubSource(repo, accessToken: null, prerelease: false,
                    downloader: new ProxiedFileDownloader())))
            .ToArray();

    // The manager + info whose repo actually produced the available update, as one value so a worker
    // write can never be observed torn by the UI thread's read before downloading it.
    private (UpdateManager? Mgr, UpdateInfo? Info) _available;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>True when the latest successful check found an update ready for the user to approve.</summary>
    public bool HasAvailableUpdate => _available.Info is not null;

    /// <summary>The release version found by the latest successful check, or null when none is available.</summary>
    public string? AvailableVersion => _available.Info?.TargetFullRelease.Version.ToString();

    /// <summary>Check for an update without downloading it. Tries each repo in order until one yields a
    /// usable result; the first reachable repo wins. No-op for un-installed (development) builds.</summary>
    public async Task<bool> CheckForUpdateAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // IsInstalled is a property of the Velopack install, not the repo, so any manager answers it.
            if (_managers.Length == 0 || !_managers[0].IsInstalled) return false; // `dotnet run` / unpacked builds

            foreach (var mgr in _managers)
            {
                try
                {
                    var info = await mgr.CheckForUpdatesAsync();
                    // A reachable repo returning null means we're already up to date. STOP. Don't fall
                    // through to a backup (it may lag behind the primary and would offer no/older update).
                    // Backups exist for an UNreachable primary, which surfaces as an exception below.
                    if (info is null)
                    {
                        _available = default;
                        return false;
                    }

                    _available = (mgr, info);
                    return true;
                }
                catch
                {
                    // This repo is unreachable/gone. Fall through to the next backup.
                }
            }

            // Every repo failed (offline, or all repos down). Keep a previous available update actionable.
            return HasAvailableUpdate;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Download the update returned by <see cref="CheckForUpdateAsync"/> and immediately restart
    /// into it. Returns false when no update is currently available.</summary>
    public async Task<bool> DownloadAndApplyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var available = _available;
            if (available.Mgr is null || available.Info is null) return false;

            await available.Mgr.DownloadUpdatesAsync(available.Info);
            available.Mgr.ApplyUpdatesAndRestart(available.Info);
            return true;
        }
        finally { _gate.Release(); }
    }
}
