using Velopack;
using Velopack.Sources;

namespace DepotToolsGui.Services;

/// <summary>
/// Silent background auto-update via Velopack + GitHub Releases. Checks on launch,
/// downloads the (delta) update in the background, and stages it to apply on next exit.
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

    // The manager + info whose repo actually produced the staged update, as one value so a worker
    // write can never be observed torn by the UI thread's read in ApplyAndRestart.
    private (UpdateManager? Mgr, UpdateInfo? Info) _staged;

    /// <summary>True once an update is downloaded and waiting to be applied.</summary>
    public bool HasStagedUpdate => _staged.Info is not null;

    /// <summary>Check for, download, and stage an update. Tries each repo in order until one yields a
    /// usable update; the first success wins. No-op for un-installed (dev) builds.</summary>
    public async Task CheckAndStageAsync()
    {
        // IsInstalled is a property of the Velopack install, not the repo, so any manager answers it.
        if (_managers.Length == 0 || !_managers[0].IsInstalled) return; // `dotnet run` / unpacked builds

        foreach (var mgr in _managers)
        {
            try
            {
                var info = await mgr.CheckForUpdatesAsync();
                // A reachable repo returning null means we're already up to date. STOP. Don't fall
                // through to a backup (it may lag behind the primary and would offer no/older update).
                // Backups exist for an UNreachable primary, which surfaces as an exception below.
                if (info is null) return;

                await mgr.DownloadUpdatesAsync(info);
                _staged = (mgr, info);
                return; // staged from this repo. Done
            }
            catch
            {
                // This repo is unreachable/gone (or a download failed). Fall through to the next backup.
            }
        }
        // Every repo failed (offline, or all repos down). Fail silently, retry next launch.
    }

    /// <summary>Apply the staged update now and relaunch into the new version. <paramref name="restartArgs"/>
    /// are passed to the relaunched process (e.g. the loader's --minimized --tray-locked) so the new
    /// instance keeps its session semantics.</summary>
    public void ApplyAndRestart(string[]? restartArgs = null)
    {
        if (_staged.Mgr is not null && _staged.Info is not null)
            _staged.Mgr.ApplyUpdatesAndRestart(_staged.Info, restartArgs);
    }

    /// <summary>Apply the staged update after the app exits (no forced restart).</summary>
    public void ApplyOnExit()
    {
        if (_staged.Mgr is not null && _staged.Info is not null)
            _staged.Mgr.WaitExitThenApplyUpdates(_staged.Info, silent: true, restart: false);
    }
}
