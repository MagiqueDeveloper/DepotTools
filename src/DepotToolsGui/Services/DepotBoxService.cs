using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DepotToolsGui.Models;

namespace DepotToolsGui.Services;

public class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public record DownloadedFile(string FilePath, string FileName);

/// <summary>Typed client for the DepotBox API. API keys are supplied by the user and never compiled in.</summary>
public class DepotBoxService(SettingsService settings, CoverCache covers)
{
    // Interim staging destination: downloads land here, get installed into Steam, then are deleted.
    // Under %TEMP% (not the user's Downloads) so nothing accumulates in a user-visible folder.
    private static readonly string InterimDownloadsFolder =
        Path.Combine(Path.GetTempPath(), "DepotToolsGui", "downloads");

    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(AppConfig.DepotBoxBaseUrl),
        Timeout = TimeSpan.FromMinutes(15),
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly object _rateGate = new();
    private DateTime _rateWindowStarted = DateTime.UtcNow;
    private int _rateWindowRequests;

    private int CurrentMinuteRequests
    {
        get
        {
            lock (_rateGate)
            {
                ResetRateWindowIfNeeded();
                return _rateWindowRequests;
            }
        }
    }

    private void CountApiRequest()
    {
        lock (_rateGate)
        {
            ResetRateWindowIfNeeded();
            _rateWindowRequests++;
        }
    }

    private void ResetRateWindowIfNeeded()
    {
        if (DateTime.UtcNow - _rateWindowStarted >= TimeSpan.FromMinutes(1))
        {
            _rateWindowStarted = DateTime.UtcNow;
            _rateWindowRequests = 0;
        }
    }

    // ── Endpoints ───────────────────────────────────────────────────

    public async Task<List<SteamSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (settings.UseApiKey && string.IsNullOrWhiteSpace(settings.DepotBoxApiKey)) return [];
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint("/api/search-games"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { searchTerm = query, limit = 8, filter_dlc = "exclude", filter_availability = true }),
                System.Text.Encoding.UTF8, "application/json"),
        };
        if (settings.UseApiKey && !string.IsNullOrWhiteSpace(settings.DepotBoxApiKey))
            req.Headers.TryAddWithoutValidation("X-API-Key", settings.DepotBoxApiKey);
        CountApiRequest();
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return [];
        var data = await ReadJsonAsync<DepotBoxSearchResponse>(res, ct);
        return (data?.Games ?? []).Select(i => new SteamSearchResult
        {
            AppId = i.AppId,
            Name = i.Name,
            Icon = i.HeaderImageUrl ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{i.AppId}/capsule_sm_120.jpg",
        }).ToList();
    }

    /// <summary>Steam's featured "top sellers" + "new releases" lists for the Add page strips. Public,
    /// no auth. Returns empty lists on any failure (the strips just don't show). Each list keeps only real
    /// games (type 0) that have a capsule image, capped to keep the strips light.</summary>
    public async Task<(List<SteamFeaturedItem> TopSellers, List<SteamFeaturedItem> NewReleases)> GetFeaturedAsync(
        CancellationToken ct = default)
    {
        try
        {
            var res = await _http.GetAsync($"{AppConfig.SteamFeaturedUrl}?cc=us&l=english", ct);
            if (!res.IsSuccessStatusCode) return ([], []);
            var data = await ReadJsonAsync<SteamFeaturedResponse>(res, ct);

            // Steam's featuredcategories genuinely repeats appids within a list (e.g. top_sellers returns
            // the same game 2–3×), so DistinctBy the appid. Keeps the first, preserving Steam's order.
            static List<SteamFeaturedItem> Clean(SteamFeaturedCategory? c) =>
                (c?.Items ?? [])
                    .Where(i => i.Type == 0 && i.Id > 0 && !string.IsNullOrEmpty(i.LargeCapsuleImage))
                    .DistinctBy(i => i.Id)
                    .Take(20)
                    .ToList();

            return (Clean(data?.TopSellers), Clean(data?.NewReleases));
        }
        catch { return ([], []); }
    }

    /// <summary>Public endpoint, no auth required.</summary>
    /// <summary>Game metadata straight from Steam's appdetails (cached to details\&lt;appid&gt;.json via the
    /// throttle, interactive priority), no DepotBox proxy. ANY fetch path funnels through here (normal /
    /// DLC / fast / plugin add), so this is also where the header image gets warmed into covers\.</summary>
    public async Task<GameDetails?> GetDetailsAsync(string appid, CancellationToken ct = default)
    {
        if (!long.TryParse(appid, out long id) || settings.UseApiKey && string.IsNullOrWhiteSpace(settings.DepotBoxApiKey)) return null;
        using var res = await SendApiAsync(HttpMethod.Get, $"/api/games/{id}", ct);
        if (!res.IsSuccessStatusCode) return null;
        var envelope = await ReadJsonAsync<DepotBoxGameDetailsResponse>(res, ct);
        var d = envelope?.Data;
        if (d is null) return null;
        var details = new GameDetails
        {
            AppId = d.AppId,
            Name = d.Name,
            Type = d.IsDlc ? "dlc" : "game",
            HeaderImage = d.HeaderImageUrl,
        };
        if (details.HeaderImage is { Length: > 0 } img)
            _ = covers.EnsureAsync(id, img, CancellationToken.None);
        return details;
    }

    /// <summary>Source name → "available" | "unavailable" | other status.</summary>
    public async Task<Dictionary<string, string>> CheckSourcesAsync(string appid, CancellationToken ct = default)
    {
        if (settings.UseApiKey && string.IsNullOrWhiteSpace(settings.DepotBoxApiKey)) return [];
        using var res = await SendApiAsync(HttpMethod.Get, $"/api/games/{Uri.EscapeDataString(appid)}/availability", ct);
        if (!res.IsSuccessStatusCode) return [];
        var data = await ReadJsonAsync<DepotBoxAvailabilityResponse>(res, ct);
        return data?.Sources?.ToDictionary(kv => kv.Key, kv => kv.Value ? "available" : "unavailable") ?? [];
    }

    public static bool IsValidKeyFormat(string? key) => !string.IsNullOrWhiteSpace(key) && key.Length >= 16 && key.Length <= 256;

    public async Task<DepotBoxUsageRecord?> GetStatsAsync(string key, CancellationToken ct = default)
    {
        if (settings.UseApiKey && string.IsNullOrWhiteSpace(key)) return null;
        using var res = await SendApiAsync(HttpMethod.Get, "/api/usage/stats", ct, settings.UseApiKey ? key : null);
        if (!res.IsSuccessStatusCode) return null;
        try
        {
            var record = await ReadJsonAsync<DepotBoxUsageRecord>(res, ct);
            // The broker serves camelCase (dailyUsage/dailyLimit/canMakeRequests) while the model maps
            // snake_case, so a straight deserialize silently yields all defaults — never throw. Treat
            // "limit didn't land" as a parse failure and keep showing the old local rate-limiter view;
            // otherwise the row would lock (CanMakeRequests=false) and FastFetch would find no source.
            if (record is null || record.DailyLimit <= 0)
                throw new InvalidDataException("Usage stats payload did not match the expected shape.");
            return record;
        }
        catch
        {
            // Parse failure → fall back to the old local rate-limiter view rather than showing nothing.
            return new DepotBoxUsageRecord
            {
                DailyUsage = CurrentMinuteRequests,
                DailyLimit = 60,
                CanMakeRequests = CurrentMinuteRequests < 60,
            };
        }
    }

    public async Task<DepotBoxManifestStatus?> CheckStatusAsync(string key, string appid, CancellationToken ct = default)
    {
        if (settings.UseApiKey && string.IsNullOrWhiteSpace(key)) return null;
        using var res = await SendApiAsync(HttpMethod.Get, $"/api/games/{Uri.EscapeDataString(appid)}/availability", ct, settings.UseApiKey ? key : null);
        if (!res.IsSuccessStatusCode) return null;
        var data = await ReadJsonAsync<DepotBoxAvailabilityResponse>(res, ct);
        return new DepotBoxManifestStatus { ManifestFileExists = data?.Sources?.Values.Any(v => v) == true };
    }

    /// <summary>DepotBox availability is the source list used by the download picker.</summary>
    private async Task<HttpResponseMessage> SendApiAsync(HttpMethod method, string url, CancellationToken ct, string? key = null)
    {
        using var req = new HttpRequestMessage(method, Endpoint(url));
        var apiKey = settings.UseApiKey ? key ?? settings.DepotBoxApiKey : null;
        if (!string.IsNullOrWhiteSpace(apiKey)) req.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        CountApiRequest();
        return await _http.SendAsync(req, ct);
    }

    public async Task<StandardUsage?> GetStandardUsageAsync(CancellationToken ct = default)
    {
        var stats = await GetStatsAsync(settings.DepotBoxApiKey ?? "", ct);
        return stats is null ? null : new StandardUsage(stats.DailyUsage, stats.DailyLimit);
    }

    public async Task<SupporterStatus?> GetSupporterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var res = await SendAsync(HttpMethod.Get, "/api/me/supporter-status", ct);
            return await ReadJsonAsync<SupporterStatus>(res, ct);
        }
        catch { return null; }
    }

    public async Task<DlcInfo?> GetDlcInfoAsync(string appid, string baseAppId, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, $"/api/dlc/info?appid={appid}&base={baseAppId}", ct);
        return await ReadJsonAsync<DlcInfo>(res, ct);
    }

    public Task<DownloadedFile> DownloadManifestAsync(
        string appid, string key, IProgress<double?>? progress, CancellationToken ct = default) =>
        DownloadManifestAsync(appid, "DepotBox", null, progress, ct);

    public Task<DownloadedFile> DownloadManifestAsync(
        string appid, string source, string? gameName, IProgress<double?>? progress, CancellationToken ct = default)
    {
        return DownloadFileAsync($"/api/direct-download?appid={Uri.EscapeDataString(appid)}",
            $"{appid}.zip", progress, ct);
    }

    public Task<DownloadedFile> GenerateDlcAsync(
        string appid, string baseAppId, string? gameName, IProgress<double?>? progress, CancellationToken ct = default)
    {
        string url = $"/api/dlc/generate?appid={appid}&base={baseAppId}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.lua", progress, ct);
    }

    // ── DepotBox game fixes ──────────────────────────────────────────
    public async Task<DenuvoListingsResponse?> GetDenuvoListingsAsync(CancellationToken ct = default)
    {
        if (settings.UseApiKey && string.IsNullOrWhiteSpace(settings.DepotBoxApiKey)) return null;
        using var res = await SendAsync(HttpMethod.Get, "/api/game-fixes?tag=online,bypass,hypervisor", ct);
        if (!res.IsSuccessStatusCode) return null;
        return await ReadJsonAsync<DenuvoListingsResponse>(res, ct);
    }

    public async Task<DenuvoFixesResponse?> GetDenuvoFixesAsync(string appid, CancellationToken ct = default)
    {
        if (settings.UseApiKey && string.IsNullOrWhiteSpace(settings.DepotBoxApiKey)) return null;
        using var res = await SendAsync(HttpMethod.Get, $"/api/game-fixes?q={Uri.EscapeDataString(appid)}", ct);
        if (!res.IsSuccessStatusCode) return null;
        var list = await ReadJsonAsync<DenuvoListingsResponse>(res, ct);
        var game = list?.Games.FirstOrDefault(g => g.AppId == appid);
        return game is null ? null : new DenuvoFixesResponse
        {
            AppId = game.AppId,
            Name = game.Name,
            HeaderImage = game.HeaderImage,
            Fixes = game.Fixes,
        };
    }

    public Task<DownloadedFile> DownloadDenuvoAsync(
        string fixId, string slot, string fallbackName, IProgress<double?>? progress, CancellationToken ct = default)
    {
        return DownloadFileAsync($"/api/game-fixes/download?id={Uri.EscapeDataString(fixId)}",
            fallbackName, progress, ct);
    }

    // ── Plumbing ────────────────────────────────────────────────────

    private Uri Endpoint(string path)
    {
        var baseUrl = settings.UseApiKey ? AppConfig.DepotBoxBaseUrl : AppConfig.DepotToolsApiBaseUrl;
        return new Uri(new Uri(baseUrl), path);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var req = new HttpRequestMessage(method, Endpoint(url));
        if (settings.UseApiKey && !string.IsNullOrWhiteSpace(settings.DepotBoxApiKey))
            req.Headers.TryAddWithoutValidation("X-API-Key", settings.DepotBoxApiKey);
        CountApiRequest();

        var res = await _http.SendAsync(req, completion, ct);
        if (res.IsSuccessStatusCode) return res;

        string message = string.Format(Resources.Strings.Api_Err_RequestFailed, (int)res.StatusCode);
        try
        {
            var err = JsonSerializer.Deserialize<ApiError>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            // Deliberately NOT localized: this text comes from the DepotBox API, which serves English
            // only. There is no key to look up, and a server message is more specific than our generic
            // fallback, so it wins. The fallback above and the 401 case below are the localizable parts.
            if (!string.IsNullOrWhiteSpace(err?.Error)) message = err.Error;
        }
        catch { /* non-JSON error body */ }

        if (res.StatusCode == HttpStatusCode.Unauthorized) message = Resources.Strings.Api_Err_SessionExpired;
        throw new ApiException(message, res.StatusCode);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);

    private async Task<DownloadedFile> DownloadFileAsync(
        string url, string fallbackName, IProgress<double?>? progress, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Get, url, ct, HttpCompletionOption.ResponseHeadersRead);
        return await SaveResponseAsync(res, fallbackName, progress, ct);
    }

    /// <summary>Download a file from an absolute URL with NO auth header (e.g. a signed R2 link).</summary>
    private async Task<DownloadedFile> DownloadFromUrlAsync(
        string url, string fallbackName, IProgress<double?>? progress, CancellationToken ct)
    {
        // New request (not via SendAsync) so no Bearer header and the absolute URL isn't prefixed.
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new ApiException(string.Format(Resources.Strings.Api_Err_DownloadFailed, (int)res.StatusCode), res.StatusCode);
        return await SaveResponseAsync(res, fallbackName, progress, ct);
    }

    private async Task<DownloadedFile> SaveResponseAsync(
        HttpResponseMessage res, string fallbackName, IProgress<double?>? progress, CancellationToken ct)
    {
        try
        {
            string fileName = res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? fallbackName;
            foreach (char c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');

            string folder = InterimDownloadsFolder;
            Directory.CreateDirectory(folder);
            string filePath = Path.Combine(folder, fileName);

            long? total = res.Content.Headers.ContentLength;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(filePath);

            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                written += read;
                progress?.Report(total is > 0 ? (double)written / total.Value : null);
            }

            return new DownloadedFile(filePath, fileName);
        }
        finally
        {
            res.Dispose();
        }
    }
}
