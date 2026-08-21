namespace DepotToolsGui.Services;

/// <summary>
/// DepotTools does not use the retired LuaTools/Supabase account system.
/// DepotBox access is authorized exclusively with the user's API key in Settings.
/// </summary>
public sealed class AuthService
{
    public string? DisplayName => null;
    public string? Email => null;
    public string? AvatarUrl => null;
    public bool IsSignedIn => false;
    public bool IsGuest => true;
    public bool IsBotProvisioned => false;

    public event Action? AuthStateChanged;

    public Task<bool> InitializeAsync() => Task.FromResult(false);

    public Task SignInAsync(CancellationToken ct = default) =>
        Task.FromException(new AuthException("DepotBox API keys are managed in Settings; account sign-in is not used."));

    public Task SignInWithCodeAsync(string code, CancellationToken ct = default) =>
        Task.FromException(new AuthException("DepotBox API keys are managed in Settings; account sign-in is not used."));

    public Task<string> GetValidAccessTokenAsync() =>
        Task.FromException<string>(new AuthException("DepotBox API key required."));

    public void SignOut() => AuthStateChanged?.Invoke();
}

public sealed class AuthException(string message) : Exception(message);
