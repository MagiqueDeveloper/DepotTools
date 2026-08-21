using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotToolsGui.Models;
using DepotToolsGui.Services;

namespace DepotToolsGui.ViewModels;

/// <summary>
/// First-run welcome overlay. Shown once and offers sign-in plus recommended settings.
/// DepotTools has no Steam plugin or loader integration.
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly CacheService _cache;
    private readonly SettingsService _settings;
    private readonly UnlockerService _unlocker;
    private readonly SteamService _steam;
    private readonly ToastService _toast;

    public OnboardingViewModel(AuthService auth, CacheService cache, SettingsService settings,
        UnlockerService unlocker, SteamService steam, ToastService toast)
    {
        _auth = auth;
        _cache = cache;
        _settings = settings;
        _unlocker = unlocker;
        _steam = steam;
        _toast = toast;
        _auth.AuthStateChanged += () => IsSignedIn = _auth.IsSignedIn;
        IsSignedIn = _auth.IsSignedIn;
    }

    /// <summary>Whether the overlay is visible. Set true by App on a fresh first launch.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>Set by App: refresh the Home dashboard after onboarding applies its actions (so the mode
    /// and plugin status tiles reflect the fresh install).</summary>
    public Func<Task>? RefreshHome { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuest))]
    private bool _isSignedIn;
    public bool IsGuest => !IsSignedIn;

    [ObservableProperty] private bool _isSigningIn;

    // The only optional first-run choice is whether to apply recommended settings.
    [ObservableProperty] private bool _applyRecommended = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;

    /// <summary>Sign-in error text (shown under the sign-in button); null when none.</summary>
    [ObservableProperty] private string? _statusLine;

    [RelayCommand]
    private void SetApplyRecommended(bool value) => ApplyRecommended = value;

    [RelayCommand]
    private async Task SignIn()
    {
        if (IsSigningIn) return;
        IsSigningIn = true;
        StatusLine = null;
        try { await _auth.SignInAsync(); }
        catch (Exception ex) { StatusLine = ex.Message; }
        finally { IsSigningIn = false; }
    }

    [RelayCommand]
    private void Finish()
    {
        if (IsBusy) return;
        bool applyRecommended = ApplyRecommended;

        _cache.OnboardingComplete = true;
        IsOpen = false;

        if (applyRecommended)
            _ = ApplyChoicesAsync();
    }

    /// <summary>Applies the recommended settings in the background.</summary>
    private async Task ApplyChoicesAsync()
    {
        IsBusy = true;
        _toast.Show(Resources.Strings.Onboarding_Title, Resources.Strings.Onboarding_Applying);
        try
        {
            // Close Steam ONCE up front so both installs run against a stopped Steam, then relaunch it once
            // at the end. Avoids the double restart of letting each installer manage Steam separately.
            // (The plugin installer only relaunches Steam if it was up when it ran; since we pre-stopped it,
            // it won't, and our StartSteam below is the single relaunch.)
            await Task.Run(_steam.StopSteam);

            {
                _settings.FastFetch = true;
                var result = await _unlocker.InstallAsync(UnlockerMode.Bst); // the Recommended mode
                if (!result.Success)
                    _toast.Show(Resources.Strings.Onboarding_Title, result.Error ?? "", error: true);
            }

            await Task.Run(_steam.StartSteam);
        }
        catch (Exception ex)
        {
            _toast.Show(Resources.Strings.Onboarding_Title, ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
            // Refresh the Home dashboard so the mode + plugin status tiles reflect what we just installed.
            if (RefreshHome is not null)
                try { await RefreshHome(); } catch { /* best effort */ }
        }
    }
}
