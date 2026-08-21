using System.IO;
using System.Text.Json;

namespace DepotToolsGui.Services;

/// <summary>App-tracked bookkeeping (not user settings). Install fingerprints, cached digests, etc.</summary>
public class CacheData
{
    // ── OpenSteamTools install fingerprint (Mode page up-to-date check) ──
    public string? OpenSteamToolsInstalledVersion { get; set; }
    public string? OpenSteamToolsInstalledZipDigest { get; set; }

    // ── Steam appdetails rate-limit window (rolling ~200 req / ~200s per IP) ──
    // Unix-ms timestamps of recent requests, so the sliding window survives restarts and we don't
    // burst fresh into a still-counting window.
    public List<long> SteamApiRequestTimes { get; set; } = [];

    // ── Donate Keys dedup (permanent) ────────────────────────────────
    // Appids whose decryption keys have already been donated. The backend accepts each depot only
    // once per IP, so this is permanent. A donated appid is never re-sent.
    public List<string> DonatedAppIds { get; set; } = [];

    // ── Hardware appid blacklist (refreshed from GitHub, ~14-day TTL) ──
    // Steam "hardware" appids (Deck, Index, controllers, VR) to hide from featured/search.
    public List<long> HardwareAppIds { get; set; } = [];
    public long HardwareAppIdsFetchedAtMs { get; set; } // Unix-ms of last successful fetch; 0 = never

    // ── Loaded-apps notification list (dismissable) ──────────────────
    // Appids the plugin surfaces as "recently loaded" on the store page; cleared on dismiss.
    // Replaces the old Lua backend's loadedappids.txt.
    public List<long> LoadedAppIds { get; set; } = [];

    // ── First-run onboarding ─────────────────────────────────────────
    // True once the user has seen (and dismissed) the welcome overlay, or the app decided they're
    // already set up. Kept in cache (not settings). It's app bookkeeping, not a user preference.
    public bool OnboardingComplete { get; set; }
}

/// <summary>
/// Persists internal app state to %AppData%\DepotToolsGui\cache.json. Distinct from user-facing
/// settings (SettingsService). Use this for values the app records about itself (versions/hashes
/// of installed components, cached lookups), not for choices the user makes.
/// </summary>
public class CacheService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DepotToolsGui");
    private static readonly string FilePath = Path.Combine(Dir, "cache.json");

    private CacheData _cache = new();

    public CacheService() => Load();

    /// <summary>OpenSteamTools: the release tag last installed (for the up-to-date check).</summary>
    public string? OpenSteamToolsInstalledVersion
    {
        get => _cache.OpenSteamToolsInstalledVersion;
        set { _cache.OpenSteamToolsInstalledVersion = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>OpenSteamTools: sha256 of the release zip last installed (inner DLL hashes aren't published).</summary>
    public string? OpenSteamToolsInstalledZipDigest
    {
        get => _cache.OpenSteamToolsInstalledZipDigest;
        set { _cache.OpenSteamToolsInstalledZipDigest = string.IsNullOrWhiteSpace(value) ? null : value; Save(); }
    }

    /// <summary>Unix-ms timestamps of recent Steam appdetails requests (rolling rate-limit window).</summary>
    public IReadOnlyList<long> GetSteamApiRequestTimes() => _cache.SteamApiRequestTimes;

    /// <summary>Persist the current rate-limit window (called periodically by the limiter, not per request).</summary>
    public void SaveSteamApiRequestTimes(IEnumerable<long> times)
    {
        _cache.SteamApiRequestTimes = times.ToList();
        Save();
    }

    // ── Donate Keys dedup (permanent) ────────────────────────────────

    /// <summary>Appids whose keys have already been donated (never re-sent).</summary>
    public IReadOnlyCollection<string> GetDonatedAppIds() => _cache.DonatedAppIds;

    public void SaveDonatedAppIds(IEnumerable<string> appIds)
    {
        _cache.DonatedAppIds = appIds.Distinct().ToList();
        Save();
    }

    // ── Hardware appid blacklist ─────────────────────────────────────

    /// <summary>Cached hardware appids to filter out of featured/search.</summary>
    public IReadOnlyList<long> GetHardwareAppIds() => _cache.HardwareAppIds;

    /// <summary>Unix-ms of the last successful blacklist fetch (0 = never), for the TTL check.</summary>
    public long GetHardwareAppIdsFetchedAt() => _cache.HardwareAppIdsFetchedAtMs;

    public void SaveHardwareAppIds(IEnumerable<long> ids, long fetchedAtMs)
    {
        _cache.HardwareAppIds = ids.Distinct().ToList();
        _cache.HardwareAppIdsFetchedAtMs = fetchedAtMs;
        Save();
    }

    // ── Loaded-apps notification list ────────────────────────────────

    /// <summary>Appids surfaced as "recently loaded" on the store page (dismissable).</summary>
    public IReadOnlyList<long> GetLoadedAppIds() => _cache.LoadedAppIds;

    public void SaveLoadedAppIds(IEnumerable<long> ids)
    {
        _cache.LoadedAppIds = ids.Distinct().ToList();
        Save();
    }

    /// <summary>Clear the loaded-apps notification list (ReadLoadedApps → DismissLoadedApps).</summary>
    public void ClearLoadedAppIds()
    {
        _cache.LoadedAppIds = [];
        Save();
    }

    /// <summary>Remove a single appid from the loaded-apps notification list (e.g. after its Lua is
    /// removed) so a removed game doesn't linger in the "recently added" popup.</summary>
    public void RemoveLoadedAppId(long id)
    {
        if (_cache.LoadedAppIds.Remove(id))
            Save();
    }

    /// <summary>True once the first-run onboarding overlay has been completed (or auto-skipped because the
    /// user is already set up). Persisted so it shows at most once.</summary>
    public bool OnboardingComplete
    {
        get => _cache.OnboardingComplete;
        set { _cache.OnboardingComplete = value; Save(); }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _cache = JsonSerializer.Deserialize<CacheData>(File.ReadAllText(FilePath)) ?? new CacheData();
        }
        catch
        {
            _cache = new CacheData();
        }
    }

    private void Save()
    {
        bool empty = _cache.OpenSteamToolsInstalledVersion is null
            && _cache.OpenSteamToolsInstalledZipDigest is null
            && _cache.SteamApiRequestTimes.Count == 0
            && _cache.DonatedAppIds.Count == 0
            && _cache.HardwareAppIds.Count == 0
            && _cache.HardwareAppIdsFetchedAtMs == 0
            && _cache.LoadedAppIds.Count == 0
            && !_cache.OnboardingComplete;
        if (empty)
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { /* best effort */ }
            return;
        }

        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
    }
}
