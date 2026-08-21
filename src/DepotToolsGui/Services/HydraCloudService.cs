using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DepotToolsGui.Services;

public sealed record HydraCloudAccount(string? DisplayName, DateTimeOffset? SubscriptionExpiresAt)
{
    public bool HasActiveSubscription => SubscriptionExpiresAt is { } expiry && expiry > DateTimeOffset.UtcNow;
}

public sealed class HydraCloudAuthException(string message) : Exception(message);

/// <summary>Hydra Cloud authentication and subscription-aware API transport.</summary>
public sealed class HydraCloudService
{
    public const string ApiBaseUrl = "https://hydra-api-us-east-1.losbroxas.org";
    public const string AuthBaseUrl = "https://auth.hydralauncher.gg";

    private static readonly string AuthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DepotToolsGui", "hydra-cloud.auth");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DepotTools-HydraCloud-v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private HydraCloudToken? _token;
    private HydraCloudAccount? _account;

    public HydraCloudService() : this(new HttpClientHandler()) { }

    internal HydraCloudService(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { BaseAddress = new Uri(ApiBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        LoadToken();
    }

    public bool IsSignedIn => _token is not null;
    public HydraCloudAccount? Account => _account;
    public bool HasActiveSubscription => _account?.HasActiveSubscription == true;
    public event Action? StateChanged;

    public Uri GetSignInUri() => new($"{AuthBaseUrl}/?lng=en");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_token is null) return;
        try { await RefreshIfNeededAsync(ct); await RefreshAccountAsync(ct); }
        catch { /* Offline startup must remain usable. */ }
    }

    public async Task HandleAuthUriAsync(string uri, CancellationToken ct = default)
    {
        if (!uri.StartsWith("hydralauncher://auth", StringComparison.OrdinalIgnoreCase))
            throw new HydraCloudAuthException("The Hydra sign-in callback was not recognized.");

        var parsed = new Uri(uri);
        string? encoded = parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0].Equals("payload", StringComparison.OrdinalIgnoreCase))
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(encoded))
            throw new HydraCloudAuthException("Hydra did not return an authentication payload.");

        string base64 = encoded.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        HydraCloudToken? token;
        try
        {
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            token = new HydraCloudToken(
                root.GetProperty("accessToken").GetString() ?? "",
                root.GetProperty("refreshToken").GetString() ?? "",
                DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expiresIn").GetInt64() - 300));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException)
        {
            throw new HydraCloudAuthException("Hydra returned an invalid authentication payload.");
        }

        if (string.IsNullOrWhiteSpace(token.AccessToken) || string.IsNullOrWhiteSpace(token.RefreshToken))
            throw new HydraCloudAuthException("Hydra returned incomplete authentication credentials.");

        _token = token;
        SaveToken();
        await RefreshAccountAsync(ct);
        StateChanged?.Invoke();
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        try { if (_token is not null) await SendAsync(HttpMethod.Post, "/auth/logout", null, ct, allowRefresh: false); }
        catch { /* Local credentials must still be removed if the service is offline. */ }
        _token = null;
        _account = null;
        try { if (File.Exists(AuthPath)) File.Delete(AuthPath); } catch { }
        StateChanged?.Invoke();
    }

    public async Task<HydraCloudAccount?> RefreshAccountAsync(CancellationToken ct = default)
    {
        if (_token is null) return _account = null;
        using var response = await SendAsync(HttpMethod.Get, "/profile/me", null, ct);
        if (!response.IsSuccessStatusCode) return _account;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        string? displayName = root.TryGetProperty("displayName", out var name) ? name.GetString() : null;
        DateTimeOffset? expiry = null;
        if (root.TryGetProperty("subscription", out var sub) && sub.ValueKind == JsonValueKind.Object
            && sub.TryGetProperty("expiresAt", out var expires) && expires.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(expires.GetString(), out var parsed)) expiry = parsed;
        _account = new HydraCloudAccount(displayName, expiry);
        StateChanged?.Invoke();
        return _account;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken ct = default, bool allowRefresh = true)
    {
        if (_token is null) throw new HydraCloudAuthException("Sign in to Hydra Cloud first.");
        await RefreshIfNeededAsync(ct);
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token.AccessToken);
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && allowRefresh)
        {
            response.Dispose();
            await RefreshTokenAsync(ct);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token.AccessToken);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        return response;
    }

    private async Task RefreshIfNeededAsync(CancellationToken ct)
    {
        if (_token is not null && _token.ExpiresAt <= DateTimeOffset.UtcNow) await RefreshTokenAsync(ct);
    }

    private async Task RefreshTokenAsync(CancellationToken ct)
    {
        if (_token is null) throw new HydraCloudAuthException("Sign in to Hydra Cloud first.");
        using var body = JsonContent.Create(new { refreshToken = _token.RefreshToken });
        using var response = await _http.PostAsync("/auth/refresh", body, ct);
        if (!response.IsSuccessStatusCode)
        {
            _token = null; _account = null; DeleteToken(); StateChanged?.Invoke();
            throw new HydraCloudAuthException("Hydra sign-in expired. Please sign in again.");
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        _token = _token with
        {
            AccessToken = doc.RootElement.GetProperty("accessToken").GetString() ?? "",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(doc.RootElement.GetProperty("expiresIn").GetInt64() - 300)
        };
        SaveToken();
    }

    private void LoadToken()
    {
        try
        {
            if (!File.Exists(AuthPath)) return;
            byte[] protectedBytes = File.ReadAllBytes(AuthPath);
            string json = Encoding.UTF8.GetString(ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser));
            _token = JsonSerializer.Deserialize<HydraCloudToken>(json, JsonOptions);
        }
        catch { _token = null; }
    }

    private void SaveToken()
    {
        if (_token is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(AuthPath)!);
        string json = JsonSerializer.Serialize(_token, JsonOptions);
        File.WriteAllBytes(AuthPath, ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser));
    }

    private static void DeleteToken() { try { if (File.Exists(AuthPath)) File.Delete(AuthPath); } catch { } }
    private sealed record HydraCloudToken(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
}
