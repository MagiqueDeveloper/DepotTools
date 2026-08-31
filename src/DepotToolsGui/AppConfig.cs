namespace DepotToolsGui;

/// <summary>
/// Compiled-in client configuration. The Supabase URL and anon key are public
/// client values (they also ship in the DepotBox web bundle).
/// </summary>
public static class AppConfig
{
    public const string DepotBoxBaseUrl = "https://depotbox.org";
    public const string DepotToolsApiBaseUrl = "https://depot.magique.dev";

    // The standard DepotBox daily download cap (DepotBox-keyed downloads are exempt). Hardcoded here
    // because the web app enforces it inline with no API field exposing it; change in one place if it moves.
    public const int DailyDownloadLimit = 25;

    // Public upstream APIs the app calls directly (no DepotBox proxy needed for guest browsing).
    public const string SteamStoreSearchUrl = "https://store.steampowered.com/api/storesearch/";
    // Steam's storefront "featured categories" (top sellers, new releases, etc.). Drives the Add page's
    // featured strips. Public, no auth.
    public const string SteamFeaturedUrl = "https://store.steampowered.com/api/featuredcategories";

    // Community list of Steam "hardware" appids (Steam Deck, Index, controllers, VR headsets). Fetched
    // via GithubProxy (raw.githubusercontent.com → mirror fallback) and cached ~14 days, to filter
    // hardware out of featured/search. Array of { "appid": <long>, "name": ... } objects.
    public const string HardwareAppIdListUrl =
        "https://raw.githubusercontent.com/jsnli/steamappidlist/master/data/hardware_appid.json";

    // Public GitHub repos used only for signed application updates. No plugin or loader assets are fetched.
    public static readonly string[] GithubReleasesRepos =
    [
        "https://github.com/MagiqueDeveloper/DepotTools",
    ];

    /// <summary>The primary releases repo.</summary>
    public static string GithubReleasesRepo => GithubReleasesRepos[0];

    // ── GitHub proxy mirrors (for blocked/throttled regions, e.g. China) ──────────────
    // github.com / api.github.com are often unreachable in some countries. Any GitHub request is tried
    // DIRECT first, then prefixed onto the MATCHING mirrors ("<mirror>https://<github-url>") until one works.
    // Two capability classes: GithubProxy.Candidates picks by URL so we never make a guaranteed-wasted hop
    // (an API mirror 400s a download; a download mirror 403s the API):
    //   • API metadata (api.github.com): ONLY our self-hosted DepotBox/gh proxy can serve it. Server-side
    //     PAT (60→5000/hr) + cache. No PUBLIC proxy serves the REST API (they all 403 it), so there's no
    //     public backup here. Fixes the plugin release-metadata lookup in China / under rate-limit. 404s
    //     harmlessly until the /api/gh route is deployed, then lights up automatically.
    //   • Downloads (github.com releases / raw / objects): the public download proxies. DepotBox/gh is
    //     API-only (its route 400s downloads) so it is deliberately NOT in this list.
    public static readonly string[] GithubApiMirrors =
    [
        "https://DepotBox/api/gh/",   // self-hosted route (src/app/api/gh/[...rest]): proxies api.github.com with our PAT
    ];
    public static readonly string[] GithubDownloadMirrors =
    [
        "https://ghproxy.net/",    // download-only (verified live 2026-07)
        "https://ghfast.top/",     // download-only
        "https://gh.ddlc.top/",    // download-only
    ];

    // ── Fetched third-party tools (downloaded+cached at runtime, never bundled) ─────────────
    // DepotDownloaderMod pulls raw depot content from Steam's CDN. This is the re-pack repo (a
    // self-contained win-x64 flat zip with DepotDownloaderMod.exe at the archive root), NOT the upstream
    // SteamAutoCracks/DepotDownloaderMod repo, whose assets are a `Release.rar` System.IO.Compression
    // can't read and whose build is framework-dependent net9.0.
    public const string DepotDownloaderRepo = "mendy-tools/DepotDownloaderMod";

    // SteamAutoCrack: the Downloads page fetches this release and launches its GUI. Framework-dependent
    // net10.0-windows, so SteamAutoCrackService installs the .NET 10 Desktop runtime on demand.
    public const string SteamAutoCrackRepo = "SteamAutoCracks/Steam-auto-crack";

    // Ludusavi: the save-backup engine behind Hydra Cloud sync. Fetch a pinned release — its CLI and
    // `simple` output format are not API-stable across releases, so bump deliberately, never via /latest.
    public const string LudusaviRepo = "mtkennerly/ludusavi";
}
