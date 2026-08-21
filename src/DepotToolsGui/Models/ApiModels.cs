using System.Text.Json.Serialization;

namespace DepotToolsGui.Models;

// DepotBox API DTOs
public class DepotBoxSearchResponse
{
    [JsonPropertyName("games")] public List<DepotBoxGame> Games { get; set; } = [];
}

public class DepotBoxGame
{
    [JsonPropertyName("appid")] public long AppId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("header_image_url")] public string? HeaderImageUrl { get; set; }
    [JsonPropertyName("is_dlc")] public bool IsDlc { get; set; }
}

public class DepotBoxGameDetailsResponse
{
    [JsonPropertyName("data")] public DepotBoxGame? Data { get; set; }
}

public class DepotBoxAvailabilityResponse
{
    [JsonPropertyName("sources")] public Dictionary<string, bool>? Sources { get; set; }
}

public class DepotBoxUsageResponse
{
    [JsonPropertyName("stats")] public DepotBoxUsageStats? Stats { get; set; }
}

public class DepotBoxUsageStats
{
    [JsonPropertyName("last24h")] public int Last24h { get; set; }
}

// Steam search result used by the UI.
public class SteamSearchResult
{
    public long AppId { get; set; }
    public string Name { get; set; } = "";
    public string? Icon { get; set; }
}

// Steam's public store-search response (called directly by the app)
public class SteamStoreSearchResponse
{
    [JsonPropertyName("items")] public List<SteamStoreItem> Items { get; set; } = [];
}

public class SteamStoreItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tiny_image")] public string? TinyImage { get; set; }
}

// ── Steam featuredcategories (Add page "Featured" strips) ───────────
// Only the two app-list categories we surface are modeled. Each item carries everything we render
// (appid + name + wide capsule art), so no per-app lookup is needed.
public class SteamFeaturedResponse
{
    [JsonPropertyName("top_sellers")] public SteamFeaturedCategory? TopSellers { get; set; }
    [JsonPropertyName("new_releases")] public SteamFeaturedCategory? NewReleases { get; set; }
}

public class SteamFeaturedCategory
{
    [JsonPropertyName("items")] public List<SteamFeaturedItem> Items { get; set; } = [];
}

public class SteamFeaturedItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    // 616×353 capsule. The nice wide art for a featured card.
    [JsonPropertyName("large_capsule_image")] public string? LargeCapsuleImage { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; } // 0 = game; non-zero are bundles/etc., so skip
}

public class GameDetails
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("appid")] public long AppId { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("baseAppId")] public string? BaseAppId { get; set; }
    [JsonPropertyName("genres")] public List<string> Genres { get; set; } = [];
    [JsonPropertyName("headerImage")] public string? HeaderImage { get; set; }
    [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }

    [JsonIgnore] public bool IsDlc => string.Equals(Type, "dlc", StringComparison.OrdinalIgnoreCase);
}

public class DlcDepot
{
    [JsonPropertyName("depotId")] public string DepotId { get; set; } = "";
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("oslist")] public string? OsList { get; set; }
    [JsonPropertyName("included")] public bool Included { get; set; }

    [JsonIgnore] public string Meta
    {
        get
        {
            var parts = new List<string> { Language ?? "default" };
            if (!string.IsNullOrEmpty(OsList)) parts.Add(OsList);
            return string.Join(" · ", parts);
        }
    }
}

public class DlcInfo
{
    [JsonPropertyName("appid")] public string AppId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("depotCount")] public int DepotCount { get; set; }
    [JsonPropertyName("haveCount")] public int HaveCount { get; set; }
    [JsonPropertyName("missingCount")] public int MissingCount { get; set; }
    [JsonPropertyName("depots")] public List<DlcDepot> Depots { get; set; } = [];
}

/// <summary>DepotBox (depotboxmanifest.com) <c>/api/v1/user/stats</c> response. Usage for the user's own key.</summary>
public class DepotBoxUsageRecord
{
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("daily_usage")] public int DailyUsage { get; set; }
    [JsonPropertyName("daily_limit")] public int DailyLimit { get; set; }
    [JsonPropertyName("can_make_requests")] public bool CanMakeRequests { get; set; }
    [JsonPropertyName("api_key_expires_at")] public string? ApiKeyExpiresAt { get; set; }
}

/// <summary>DepotBox <c>/api/v1/status/{appid}</c> response. Whether a manifest exists (free, no usage count).</summary>
public class DepotBoxManifestStatus
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("manifest_file_exists")] public bool ManifestFileExists { get; set; }
}

public class ApiError
{
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>The DepotBox standard daily download usage (counted from user_downloads, limit 25/day).</summary>
public record StandardUsage(int Used, int Limit);

public class SupporterStatus
{
    [JsonPropertyName("isSupporter")] public bool IsSupporter { get; set; }
}

/// <summary>Response from /api/auth/code/redeem. A Discord bot login code exchanged for a magic-link token.</summary>
public class CodeRedeemResponse
{
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

// ── Supabase auth DTOs ──────────────────────────────────────────────

public class SupabaseSession
{
    [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("user")] public SupabaseUser? User { get; set; }
}

public class SupabaseUser
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("user_metadata")] public UserMetadata? Metadata { get; set; }
}

public class UserMetadata
{
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("custom_claims")] public CustomClaims? CustomClaims { get; set; }
}

public class CustomClaims
{
    [JsonPropertyName("global_name")] public string? GlobalName { get; set; }
}

/// <summary>Persisted (DPAPI-encrypted) auth state.</summary>
public class StoredAuth
{
    public string RefreshToken { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
}

/// <summary>Per-source UI metadata, mirroring src/lib/source-meta.ts on the website.</summary>
public static class SourceMeta
{
    public record Meta(string? DisplayName = null, string? DiscordUrl = null, bool RequiresUserKey = false);

    public static readonly Dictionary<string, Meta> All = new()
    {
        ["Ryuu"] = new(DiscordUrl: "https://discord.gg/manifests"),
        ["TwentyTwo Cloud"] = new(DiscordUrl: "https://discord.gg/RrukXPyv5b"),
        ["Sushi"] = new(DiscordUrl: "https://discord.gg/hMdv5dQhcN"),
        ["Skyflare"] = new(DiscordUrl: "https://discord.gg/depottools"),
        ["DepotBox"] = new(DisplayName: "DepotBox", RequiresUserKey: true),
    };

    public static Meta Get(string name) => All.TryGetValue(name, out var m) ? m : new Meta();
}
