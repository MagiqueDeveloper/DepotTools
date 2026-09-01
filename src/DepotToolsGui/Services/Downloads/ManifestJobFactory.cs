using System.IO;
using System.IO.Compression;
using DepotToolsGui.Models;

namespace DepotToolsGui.Services.Downloads;

/// <summary>
/// Builds the <see cref="DownloadJob"/>s for raw depot downloads and SteamAutoCrack launches. Every job
/// runs through <see cref="DownloadQueue"/>.
/// </summary>
public class ManifestJobFactory(
    CoverCache covers,
    DepotDownloaderService depotTool,
    SteamDepotInfo depotInfo,
    SteamAutoCrackService sac)
{
    // ── Job builders ─────────────────────────────────────────────────

    /// <summary>
    /// Raw depot content for a game. ONE queue item covers the whole selection; internally it runs the
    /// downloader once per depot, in list order.
    /// </summary>
    /// <remarks>
    /// Sequential by necessity, not preference: the tool's <c>-manifestfile</c> is a single value applied
    /// to every depot in its own loop, so a batched call would feed them all the same manifest.
    /// </remarks>
    public DownloadJob CreateDepotJob(
        long appId, string gameName, IReadOnlyList<DepotSelection> selections, string outDir,
        Action<DownloadItem, JobResult?>? onFinished = null)
    {
        return new DownloadJob(
            DownloadKind.Depot,
            $"depot:{appId}",
            appId,
            gameName,
            Resources.Strings.Downloads_Kind_Depot,
            covers.GetLocalPath(appId),
            (item, progress, ct) => RunDepotsAsync(item, appId, gameName, selections, outDir, progress, ct),
            // Nothing to install: the depots were written straight to outDir.
            (_, _, _) => Task.FromResult(new JobResult(true,
                string.Format(Resources.Strings.Depot_Status_Done, selections.Count, outDir), outDir)),
            ConfirmAsync: null,
            OnFinished: onFinished,
            OutputPath: outDir);
    }

    /// <summary>
    /// Fetch SteamAutoCrack (installing the .NET runtime it needs first) and open it.
    /// </summary>
    /// <remarks>
    /// Modelled as a queue job so the ~100 MB first run shows real progress and can be cancelled, rather
    /// than freezing a button. It only OPENS their GUI: the shipped release has no CLI and the GUI takes
    /// no arguments, so nothing about the actual crack can be driven from here.
    /// </remarks>
    /// <param name="launchWhenDone">
    /// False for the background-update path. Finishing an update must NOT open a second SteamAutoCrack
    /// window while the user already has one open.
    /// </param>
    public DownloadJob CreateSteamAutoCrackJob(
        bool launchWhenDone = true, Action<DownloadItem, JobResult?>? onFinished = null)
    {
        return new DownloadJob(
            DownloadKind.Tool,
            "tool:steamautocrack",
            0,
            "SteamAutoCrack", // a product name; deliberately not localized
            Resources.Strings.Downloads_Kind_Tool,
            null,
            async (item, progress, ct) =>
            {
                // Runtime BEFORE the 41 MB tool: no point paying for the download if the user declines
                // the elevation prompt.
                OnUi(() => item.Detail = Resources.Strings.Downloads_SAC_GettingRuntime);
                var runtimeProgress = new ProgressRelay<double?>(f =>
                {
                    if (f is { } v) progress.Report(new DownloadProgress((long)(v * 1000), 1000));
                });
                var prepared = await sac.EnsureRuntimeAsync(runtimeProgress, ct);
                if (prepared != SacPrepareResult.Ready)
                {
                    // Declining the prompt, and "installed but needs a reboot", are both outcomes where
                    // nothing went wrong — they settle as Cancelled so the row isn't dressed as an error.
                    bool notAFailure = prepared is SacPrepareResult.RuntimeDeclined
                                              or SacPrepareResult.RuntimeNeedsRestart;
                    throw new DownloadAbortedException(prepared switch
                    {
                        SacPrepareResult.RuntimeDeclined => Resources.Strings.Err_CancelledByUser,
                        SacPrepareResult.RuntimeNeedsRestart => Resources.Strings.Downloads_SAC_Err_Restart,
                        _ => Resources.Strings.Downloads_SAC_Err_Runtime,
                    }, isCancellation: notAFailure);
                }

                OnUi(() => item.Detail = Resources.Strings.Downloads_SAC_GettingTool);
                progress.Report(new DownloadProgress(0, null)); // hand the bar back before the real download
                // force when this job was queued by the background update probe: that probe already
                // recorded the check timestamp, so the throttle would otherwise skip this download.
                string? exe = await sac.EnsureToolAsync(progress, force: !launchWhenDone, ct)
                    ?? throw new DownloadAbortedException(Resources.Strings.Downloads_SAC_Err_Tool);

                OnUi(() => item.Detail = null);
                // Directory sentinel, same as CreateDepotJob: the queue's staged-file cleanup no-ops on it.
                return new DownloadedFile(Path.GetDirectoryName(exe)!, "SteamAutoCrack");
            },
            (_, _, _) => Task.FromResult(
                !launchWhenDone ? new JobResult(true, Resources.Strings.Downloads_SAC_Updated)
                : sac.Launch() ? new JobResult(true, Resources.Strings.Downloads_SAC_Launched)
                : new JobResult(false, Resources.Strings.Downloads_SAC_Err_Launch)),
            ConfirmAsync: null,
            OnFinished: onFinished);
    }

    private async Task<DownloadedFile> RunDepotsAsync(
        DownloadItem item, long appId, string gameName, IReadOnlyList<DepotSelection> selections,
        string outDir, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var keys = depotTool.ResolveKeys(appId);
        if (keys.Count == 0) throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoKeys);

        // Sampled ONCE, before anything runs. Checking it inside the loop would be self-fulfilling:
        // the first depot creates outDir, so every later depot would see it and think a previous session
        // had written there. (Harmless in cost — a depot whose files don't exist yet validates nothing —
        // but the intent is "did an earlier run leave partial files here", which is only true up front.)
        bool outDirExisted = Directory.Exists(outDir);

        // ── Phase 0: the downloader itself ───────────────────────────────────────────────────────────
        // Hoisted out of the per-depot loop so the ~37 MB first fetch (and any update) happens once, with
        // visible progress. RunAsync still calls EnsureToolAsync per depot, but those hit its fast path.
        OnUi(() => item.Detail = Resources.Strings.Downloads_Depots_GettingTool);
        if (await depotTool.EnsureToolAsync(progress, ct) is null)
            throw new DownloadAbortedException(Resources.Strings.Depot_Err_Tool);

        // Hand the bar back. On a fresh install the step above just drove it to 100% against the tool's
        // own size; leaving it there would show a full bar through Phase 1 and then snap to 0% when the
        // depots start. A null total reads as indeterminate until Phase 2 knows the real one.
        progress.Report(new DownloadProgress(0, null));

        // ── Phase 1: resolve EVERYTHING before a single byte is written ──────────────────────────────
        // Sizes for every selection (including finished ones, so a resumed job's baseline is right), and
        // manifests only for what's left to do. Doing this inside the download loop meant a manifest that
        // couldn't be fetched aborted the job after earlier depots had already pulled tens of GB, and it
        // left the free-space check below summing 0 for every unresolved shared depot.
        var resolved = new List<DepotSelection>(selections.Count);
        for (int i = 0; i < selections.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Formatted into a local BEFORE the closure: `i` is a for-loop variable, so it is shared
            // across iterations and would have moved on by the time the dispatcher ran the lambda.
            string prep = string.Format(Resources.Strings.Downloads_Depots_Preparing, i + 1, selections.Count);
            OnUi(() => item.Detail = prep);

            // A shared redistributable carries no gid or size in the game's own app-info (it's a
            // three-field stub pointing at the owning app), so both are resolved here rather than at
            // pick time. Cached per app by SteamDepotInfo, and the owner is app 228980 for nearly
            // every game, so this costs one lookup per session across all downloads.
            var sized = await ResolveSharedAsync(selections[i], ct);

            // Resolve the manifest, fetching it into depotcache if Steam doesn't already have it. This is
            // what lets a depot be downloaded at all when the game was added with "Auto Update Apps" on,
            // which comments out the pins and skips the manifest files. Skipped for a depot already
            // finished — its bytes are on disk and nothing will re-read the manifest.
            // `prep` (not the download-phase caption) is the step text here: EnsureManifestAsync appends
            // "· fetching manifest" to whatever it's given, so passing the other string would relabel the
            // row mid-pre-flight as though depots were already downloading.
            if (!item.CompletedDepots.Contains(sized.DepotId))
            {
                sized = sized with { ManifestPath = await EnsureManifestAsync(item, sized, prep, ct) };

                // Without a key the tool cannot decrypt a single chunk, and a depot that fails aborts the
                // whole job below — so refuse here, before anything is written, naming the depot instead
                // of surfacing the downloader's own "No valid depot key" much later.
                if (!keys.TryGetValue(sized.DepotId, out string? hex) || !TryParseKey(hex, out byte[] key))
                    throw new DownloadAbortedException(
                        string.Format(Resources.Strings.Depot_Err_NoKeyFor, sized.DepotId));

                // A key that exists but is WRONG can only be caught when the manifest still has its
                // filenames encrypted, which is the small minority — see ManifestFile.KeyLooksValid.
                if (!ManifestFile.KeyLooksValid(sized.ManifestPath, key))
                    throw new DownloadAbortedException(
                        string.Format(Resources.Strings.Depot_Err_BadKey, sized.DepotId));
            }

            // The manifest's own cb_disk_original beats app info's size: it is exact, and app info may
            // not have carried a size at all (a token-gated app returns no depot list, so those depots
            // arrive here as 0 and would otherwise be budgeted as free).
            if (ManifestFile.TryRead(sized.ManifestPath) is { SizeOnDisk: > 0 } info)
                sized = sized with { Size = info.SizeOnDisk };

            resolved.Add(sized);
        }

        // ── Phase 2: budget, now that the sizes are real ─────────────────────────────────────────────
        // Refuse up front rather than part-way through. The downloader pre-allocates every file at its
        // full size BEFORE fetching a byte, so a short disk fails almost immediately — but only after it
        // has already created multi-GB of zero-filled files. Checking here also gives a message that says
        // what's actually wrong instead of a raw allocation error.
        long totalSize = resolved.Sum(s => s.Size);
        long needed = resolved.Where(s => !item.CompletedDepots.Contains(s.DepotId)).Sum(s => s.Size);
        if (needed > 0 && DepotDownloaderService.FreeSpaceFor(outDir) is { } free && free < needed)
            throw new DownloadAbortedException(string.Format(
                Resources.Strings.Depot_Err_NoSpace, ByteFormat.Size(needed), ByteFormat.Size(free)));

        // ── Phase 3: download ────────────────────────────────────────────────────────────────────────
        string keysFile = DepotDownloaderService.WriteKeysFile(keys);
        try
        {
            long done = 0;
            for (int i = 0; i < resolved.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ready = resolved[i];

                // Resume skips what's already finished rather than re-hashing tens of GB. Its size is the
                // resolved one, so a finished shared depot no longer contributes 0 to the baseline.
                if (item.CompletedDepots.Contains(ready.DepotId)) { done += ready.Size; continue; }

                // Re-checked per depot, not just once up front: the volume is shared with everything else
                // on the machine, so a budget that cleared at the start can be gone by depot 12. Running
                // out mid-download is not reported as a disk error — the tool simply stops printing, and
                // the silence watchdog kills it ten minutes later as a "timeout", which explains nothing.
                if (ready.Size > 0 && DepotDownloaderService.FreeSpaceFor(outDir) is { } left
                    && left < ready.Size)
                    throw new DownloadAbortedException(string.Format(
                        Resources.Strings.Depot_Err_NoSpace,
                        ByteFormat.Size(ready.Size), ByteFormat.Size(left)));

                string step = string.Format(Resources.Strings.Downloads_Depots_Progress, i + 1, resolved.Count);
                OnUi(() => item.Detail = step);

                // Only the FIRST depot after a resume is the partially-written one, so only it needs the
                // (expensive) re-hash. Consume the flag so later depots download at full speed.
                //
                // An existing output folder forces the same treatment even on a fresh item: it means a
                // previous session already wrote here, and CompletedDepots does not survive an app
                // restart. Skipping validation there would hand back a half-written file reported as
                // complete, which is this tool's worst failure mode.
                bool validate = item.NeedsValidate || outDirExisted;
                item.NeedsValidate = false;

                long baseBytes = done;
                var relay = new ProgressRelay<double>(f =>
                    progress.Report(new DownloadProgress(baseBytes + (long)(f * ready.Size), totalSize)));

                // The phase is PARSED from the downloader's own output rather than guessed. A big depot
                // pre-allocates every new file at full size before fetching a byte, so the row used to sit
                // at "Downloading - 0 B of 4.49 GB" looking hung for minutes. Reported only on change.
                var phases = new ProgressRelay<DepotPhase>(ph => OnUi(() =>
                {
                    item.Detail = ph switch
                    {
                        DepotPhase.PreAllocating => $"{step} · {Resources.Strings.Downloads_Depot_PreAllocating}",
                        DepotPhase.Validating => $"{step} · {Resources.Strings.Downloads_Depot_Validating}",
                        DepotPhase.Manifest => $"{step} · {Resources.Strings.Downloads_Depot_FetchingManifest}",
                        _ => step,
                    };

                    // Verifying is a real status (it gates Pause and the label), so keep driving it -
                    // but from what the tool actually reports, not from "validate was requested and no
                    // bytes have arrived yet", which also covered pre-allocation and plain slow starts.
                    item.Status = ph == DepotPhase.Validating
                        ? DownloadStatus.Verifying
                        : DownloadStatus.Downloading;
                }));

                // Recorded so a cancel can delete exactly what this download created. Collected off the
                // UI thread on purpose: a big depot reports thousands of files and none of it is visible.
                var created = new ProgressRelay<string>(path => item.CreatedFiles.Add(path));

                var res = await depotTool.RunAsync(
                    appId, ready, keysFile, outDir, validate, relay, ct, phases, created);
                if (!res.Ok)
                    throw new DownloadAbortedException(res.Error == "tool"
                        ? Resources.Strings.Depot_Err_Tool
                        : string.Format(Resources.Strings.Depot_Err_Failed, ready.DepotId, res.Error ?? ""));

                item.CompletedDepots.Add(ready.DepotId);
                done += ready.Size;
                progress.Report(new DownloadProgress(done, totalSize));
            }

            OnUi(() => item.Detail = null);
            // Sentinel for the queue's file plumbing: a directory, so the staged-file cleanup no-ops on it.
            return new DownloadedFile(outDir, gameName);
        }
        finally
        {
            DeleteStaged(keysFile); // holds decryption keys; never leave it lying around
        }
    }

    /// <summary>
    /// A depot key as bytes. Keys come from a lua file and from Steam's config.vdf, so a malformed one is
    /// a real possibility and reads the same as having no key at all: the download cannot proceed.
    /// </summary>
    private static bool TryParseKey(string? hex, out byte[] key)
    {
        key = [];
        if (hex is not { Length: 64 }) return false; // AES-256, hex-encoded
        try { key = Convert.FromHexString(hex); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// Fill in a shared depot's manifest id and size from the app that actually owns its content.
    /// Returns the selection unchanged for an ordinary depot (one that already declares its own gid).
    /// </summary>
    private async Task<DepotSelection> ResolveSharedAsync(DepotSelection sel, CancellationToken ct)
    {
        if (sel.ManifestId is not null || sel.FromAppId is not { } owner) return sel;

        var info = await depotInfo.GetAsync(owner, ct);
        if (info?.Depots.FirstOrDefault(d => d.Id == sel.DepotId) is not { PublicManifestId: not null } owned)
            throw new DownloadAbortedException(Resources.Strings.Depot_Err_NoManifest);

        return sel with { ManifestId = owned.PublicManifestId, Size = owned.Size };
    }

    /// <summary>
    /// The depotcache path for a depot's manifest, or null when Steam doesn't have one. Null is fine:
    /// <see cref="DepotDownloaderService.RunAsync"/> then omits -manifestfile and the tool fetches the
    /// manifest from the CDN itself given -manifest.
    /// </summary>
    private Task<string?> EnsureManifestAsync(
        DownloadItem item, DepotSelection sel, string step, CancellationToken ct)
    {
        return Task.FromResult<string?>(
            sel.ManifestId is not null ? depotTool.ResolveManifestPath(sel.DepotId, sel.ManifestId) : null);
    }

    /// <summary>Marshal an observable-property write onto the dispatcher (this runs on a worker).</summary>
    private static void OnUi(Action a) =>
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(a);

    /// <summary>Best-effort delete of a staged download once it has been consumed.</summary>
    public static void DeleteStaged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
