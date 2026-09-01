using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotToolsGui.Services;
using DepotToolsGui.Models;
using Microsoft.Win32;

namespace DepotToolsGui.ViewModels;

/// <summary>A selectable UI language. <see cref="Tag"/> is the BCP-47 tag ("en", "zh-Hans") or null for
/// "follow the system display language".</summary>
/// <summary>One bundled runtime tool on the Settings → RUNTIMES card: install/reinstall/remove with a
/// live status line. The tool-specific behavior is injected so this class stays dumb plumbing.</summary>
public partial class RuntimeToolViewModel : ObservableObject
{
    public string Name { get; }
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallLabel))]
    private bool _isInstalled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallLabel))]
    private bool _isBusy;

    // Keep both commands' CanExecute in sync with busy/installed state (CanExecute reads these).
    partial void OnIsInstalledChanged(bool value) => NotifyCommands();
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    private void NotifyCommands()
    {
        InstallCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    public IRelayCommand InstallCommand { get; }
    public IRelayCommand RemoveCommand { get; }

    public string InstallLabel => IsInstalled
        ? Resources.Strings.Settings_Runtime_Reinstall
        : Resources.Strings.Settings_Runtime_Install;

    private readonly Func<bool> _detectInstalled;
    private readonly Func<string?> _readVersion;
    private readonly Func<Task<bool>> _installAsync;
    private readonly Func<bool> _remove;

    private readonly bool _preRemoveOnReinstall;

    public RuntimeToolViewModel(string name, Func<bool> detectInstalled, Func<string?> readVersion,
        Func<Task<bool>> installAsync, Func<bool> remove, bool preRemoveOnReinstall = true)
    {
        Name = name;
        _detectInstalled = detectInstalled;
        _readVersion = readVersion;
        _installAsync = installAsync;
        _remove = remove;
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => !IsBusy);
        _preRemoveOnReinstall = preRemoveOnReinstall;
        RemoveCommand = new RelayCommand(Remove, () => !IsBusy && IsInstalled);
        Refresh();
    }

    private async Task InstallAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            bool wasInstalled = IsInstalled;
            // Reinstall = remove + install for tools whose "install" is a no-op while the exe exists.
            // SteamAutoCrack opts out (_preRemoveOnReinstall = false): its EnsureToolAsync(force:true)
            // re-downloads in place, so a locked exe surfaces as a failed install, not a deleted tool.
            if (wasInstalled && _preRemoveOnReinstall && !_remove()) return; // remove failed (locked?)
            bool ok = await _installAsync();
            Refresh(ok);
            if (!ok)
                MessageBox.Show(string.Format(Resources.Strings.Settings_Runtime_InstallFailed, Name),
                    Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsBusy = false; }
    }

    private void Remove()
    {
        if (IsBusy || !IsInstalled) return;
        if (MessageBox.Show(string.Format(Resources.Strings.Settings_Runtime_RemoveConfirm, Name),
                Name, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        if (!_remove())
        {
            MessageBox.Show(string.Format(Resources.Strings.Settings_Runtime_RemoveFailed, Name),
                Name, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Refresh(false);
    }

    /// <summary>Re-read on-disk state into the status line. Pass the new installed state when known
    /// (the cache write and the file system can race); null re-detects.</summary>
    public void Refresh(bool? installed = null)
    {
        bool present = installed ?? _detectInstalled();
        IsInstalled = present;
        StatusText = present
            ? string.Format(Resources.Strings.Settings_Runtime_Version, _readVersion() ?? "installed")
            : Resources.Strings.Settings_Runtime_NotInstalled;
    }
}

public record LanguageOption(string Display, string? Tag);

public partial class SettingsViewModel : ObservableObject
{
    /// <summary>The three bundled GitHub-fetched tools, managed on the RUNTIMES card.</summary>
    public ObservableCollection<RuntimeToolViewModel> RuntimeTools { get; } = [];

    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly SteamService _steam;
    private readonly DepotBoxService _depotBox;
    private readonly HydraCloudService _hydraCloud;
    private readonly HydraCloudSyncService _hydraCloudSync;
    private readonly System.Windows.Threading.DispatcherTimer _depotBoxStatsTimer;

    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private string? _avatarUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRealUser))]
    private bool _isGuest = true;

    public bool IsRealUser => !IsGuest;

    // ── Login-required redirect banner ─────────────────────────────
    /// <summary>Set by App when navigating here from a protected action. Null = banner hidden.</summary>
    [ObservableProperty] private string? _loginRequiredMessage;

    [RelayCommand]
    private void DismissLoginRequired() => LoginRequiredMessage = null;

    // ── Bot-provisioned account re-link banner ──────────────────────
    /// <summary>True when the signed-in session is a Discord bot placeholder (@bot.DepotBox).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBotLinkBanner))]
    private bool _isBotProvisioned;

    /// <summary>Session-only. Resets next launch so the banner re-checks on every startup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBotLinkBanner))]
    private bool _botBannerDismissed;

    public bool ShowBotLinkBanner => false;

    // ── Steam location ──────────────────────────────────────────────
    [ObservableProperty] private string _steamPath = "";
    [ObservableProperty] private bool _isSteamOverridden;
    [ObservableProperty] private string _steamSource = "";
    [ObservableProperty] private string? _steamWarning;

    // ── Install behavior ────────────────────────────────────────────
    /// <summary>Auto Update Apps (Don't Lock Manifests). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _autoUpdateApps;

    partial void OnAutoUpdateAppsChanged(bool value) => _settings.AutoUpdateApps = value;

    /// <summary>FastFetch: auto-download from the first available source. Same persisted setting the Add
    /// screen's toggle drives (both read/write SettingsService.FastFetch).</summary>
    [ObservableProperty] private bool _fastFetch;

    partial void OnFastFetchChanged(bool value) => _settings.FastFetch = value;

    // ── Startup behavior ────────────────────────────────────────────
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "DepotTools";

    /// <summary>Launch the app on Windows sign-in (writes HKCU …\Run). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _startWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        _settings.StartWithWindows = value;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (value)
                // --minimized: start silently in the tray on sign-in so it just serves the
                // local backend (127.0.0.1:6767) for the Steam plugin without stealing focus.
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --minimized");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { /* registry write blocked. Setting is still saved, just not applied this run */ }
    }

    /// <summary>Minimize to the system tray instead of the taskbar. Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _minimizeToTray;

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        if (!value) RequestShowWindow?.Invoke(); // turning it off restores the window if it's hidden in the tray
    }

    /// <summary>Periodically check for DepotTools updates and offer the user an explicit update action.</summary>
    [ObservableProperty] private bool _updateNotificationsEnabled;

    partial void OnUpdateNotificationsEnabledChanged(bool value) => _settings.UpdateNotificationsEnabled = value;

    /// <summary>Set by App: restore the main window from the tray (used when Minimize-to-tray is turned off).</summary>
    public Action? RequestShowWindow { get; set; }

    // ── Language ────────────────────────────────────────────────────
    /// <summary>Available UI languages (native endonyms, matching Steam's list). "System default"
    /// (null tag) follows Windows. Languages whose .resx isn't present fall back to English.</summary>
    public ObservableCollection<LanguageOption> LanguageOptions { get; } =
    [
        new(Resources.Strings.Settings_Language_SystemDefault, null),
        new("English", "en"),
        new("简体中文", "zh-Hans"),
        new("繁體中文", "zh-Hant"),
        new("日本語", "ja"),
        new("한국어", "ko"),
        new("Español (España)", "es"),
        new("Español (Latinoamérica)", "es-419"),
        new("Português (Brasil)", "pt-BR"),
        new("Português (Portugal)", "pt-PT"),
        new("Français", "fr"),
        new("Deutsch", "de"),
        new("Italiano", "it"),
        new("Nederlands", "nl"),
        new("Polski", "pl"),
        new("Русский", "ru"),
        new("Українська", "uk"),
        new("العربية", "ar"),
        new("Čeština", "cs"),
        new("Magyar", "hu"),
        new("Română", "ro"),
        new("Türkçe", "tr"),
        new("Ελληνικά", "el"),
        new("Български", "bg"),
        new("ไทย", "th"),
        new("Tiếng Việt", "vi"),
        new("Bahasa Indonesia", "id"),
        new("Dansk", "da"),
        new("Suomi", "fi"),
        new("Norsk", "nb"),
        new("Svenska", "sv"),
    ];

    [ObservableProperty] private LanguageOption _selectedLanguage = null!;

    private bool _suppressLanguagePrompt; // true during ctor init so we don't prompt on first bind

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is null || _suppressLanguagePrompt) return;
        _settings.Language = value.Tag; // null = follow system
        // The whole UI is built with parse-time x:Static resources, so a relaunch is needed to re-read it.
        RequestRestartPrompt?.Invoke();
    }

    // ── DepotBox API key ──────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDepotBoxKeyInput))]
    [NotifyPropertyChangedFor(nameof(ShowDepotBoxStats))]
    [NotifyPropertyChangedFor(nameof(DepotBoxStatsPending))]
    private bool _useApiKey;

    partial void OnUseApiKeyChanged(bool value)
    {
        _settings.UseApiKey = value;
        DepotBoxIsKeyConfigured = ShouldShowDepotBoxStats(value, _settings.DepotBoxApiKey);
        if (DepotBoxIsKeyConfigured)
        {
            _depotBoxStatsTimer.Start();
            RefreshDepotBoxStatsCommand.Execute(null);
        }
        else
        {
            _depotBoxStatsTimer.Stop();
            DepotBoxStats = null;
        }
    }

    /// <summary>The key the user is typing/pasting. Starts blank. The saved key is never shown back.</summary>
    [ObservableProperty] private string _depotBoxKeyInput = "";

    /// <summary>Status line under the key box (usage/expiry on success, or an error). Null = hidden.</summary>
    [ObservableProperty] private string? _depotBoxKeyStatus;

    /// <summary>Color for the status line: red on error, green on success.</summary>
    [ObservableProperty] private string _depotBoxKeyStatusColor = "#22c55e";
    /// <summary>True when a key is saved in settings (drives the "Clear" button + "configured" label).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDepotBoxKeyInput))]
    [NotifyPropertyChangedFor(nameof(ShowDepotBoxStats))]
    [NotifyPropertyChangedFor(nameof(DepotBoxStatsPending))]
    [NotifyPropertyChangedFor(nameof(DepotBoxStatsText))]
    private bool _depotBoxIsKeyConfigured;

    /// <summary>True while validating (disables the buttons via <see cref="CanEditDepotBoxKey"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDepotBoxKey))]
    private bool _isValidatingDepotBoxKey;

    public bool CanEditDepotBoxKey => !IsValidatingDepotBoxKey;

    [ObservableProperty] private bool _isRefreshingDepotBoxStats;

    /// <summary>Latest stats for the saved key. Null = not loaded / no key / error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DepotBoxStatsDisplay))]
    [NotifyPropertyChangedFor(nameof(DepotBoxStatsText))]
    [NotifyPropertyChangedFor(nameof(DepotBoxStatsPending))]
    [NotifyPropertyChangedFor(nameof(DepotBoxUsagePercent))]
    [NotifyPropertyChangedFor(nameof(ShowDepotBoxStats))]
    private DepotBoxUsageRecord? _depotBoxStats;

    /// <summary>"12 / 25 requests today" or "12 / 25 requests today · expires 2026-08-15".</summary>
    public string? DepotBoxStatsDisplay =>
        DepotBoxStats is not null ? FormatDepotBoxStats(DepotBoxStats) : null;

    /// <summary>Progress bar fill 0.0–1.0.</summary>
    public double DepotBoxUsagePercent =>
        DepotBoxStats is { DailyLimit: > 0 } ? (double)DepotBoxStats.DailyUsage / DepotBoxStats.DailyLimit : 0;

    /// <summary>Show the usage card whenever a key is configured. It renders a loading state until stats
    /// arrive (never blank), so the section can't look broken while the fetch is in flight or after a
    /// transient failure (the key input is collapsed once configured, so this card is the only content).</summary>
    public bool ShowDepotBoxStats => UseApiKey && DepotBoxIsKeyConfigured;

    /// <summary>Show the custom-key editor only when the custom provider is selected and no key is saved.</summary>
    public bool ShowDepotBoxKeyInput => UseApiKey && !DepotBoxIsKeyConfigured;

    internal static bool ShouldShowDepotBoxStats(bool useApiKey, string? savedKey) =>
        useApiKey && !string.IsNullOrWhiteSpace(savedKey);

    /// <summary>Stats not yet loaded for a configured key → show the "Loading…" placeholder + indeterminate bar.</summary>
    public bool DepotBoxStatsPending => ShowDepotBoxStats && DepotBoxStats is null;

    /// <summary>Placeholder text shown until real stats load.</summary>
    public string DepotBoxStatsText => DepotBoxStatsDisplay ?? Resources.Strings.Common_Loading;

    // ── Hydra Cloud ──────────────────────────────────────────────────
    [ObservableProperty] private bool _hydraCloudSignedIn;
    [ObservableProperty] private bool _hydraCloudHasSubscription;
    [ObservableProperty] private string? _hydraCloudAccountName;
    [ObservableProperty] private string? _hydraCloudStatus;
    [ObservableProperty] private bool _cloudSavesEnabled;

    partial void OnCloudSavesEnabledChanged(bool value)
    {
        _settings.CloudSavesEnabled = value;
        if (value) _ = _hydraCloudSync.SyncAllAsync("enabled");
    }

    public Func<Task>? RequestHydraSignIn { get; set; }

    [RelayCommand]
    private async Task SignInHydraAsync()
    {
        if (RequestHydraSignIn is not null) await RequestHydraSignIn();
        RefreshHydraCloudState();
        if (CloudSavesEnabled) _ = _hydraCloudSync.SyncAllAsync("signed-in");
    }

    [RelayCommand]
    private async Task SignOutHydraAsync()
    {
        await _hydraCloud.SignOutAsync();
        RefreshHydraCloudState();
    }

    [RelayCommand]
    private async Task RefreshHydraCloudAsync()
    {
        try
        {
            await _hydraCloud.InitializeAsync();
            await _hydraCloud.RefreshAccountAsync();
            RefreshHydraCloudState();
            HydraCloudStatus = HydraCloudHasSubscription
                ? Resources.Strings.Settings_HydraCloud_Ready
                : Resources.Strings.Settings_HydraCloud_SubscriptionRequired;
        }
        catch (Exception ex)
        {
            HydraCloudStatus = ex.Message;
        }
    }

    private void RefreshHydraCloudState()
    {
        HydraCloudSignedIn = _hydraCloud.IsSignedIn;
        HydraCloudHasSubscription = _hydraCloud.HasActiveSubscription;
        HydraCloudAccountName = _hydraCloud.Account?.DisplayName;
    }

    /// <summary>Set by App so the guest "Sign in" button can run the Discord flow.</summary>
    public Func<Task>? RequestSignIn { get; set; }

    /// <summary>Set by App: actually relaunch the app (used after a language change).</summary>
    public Action? RequestRestart { get; set; }

    /// <summary>Show the "language changed. Restart now?" toast. Wired here so the VM stays UI-agnostic;
    /// App provides the toast + restart action.</summary>
    public Action? RequestRestartPrompt { get; set; }

    public SettingsViewModel(SettingsService settings, AuthService auth, SteamService steam,
        DepotBoxService depotBox, HydraCloudService hydraCloud, HydraCloudSyncService hydraCloudSync,
        LudusaviService ludusavi, SteamAutoCrackService sac, DepotDownloaderService depotDownloader)
    {
        _settings = settings;
        _auth = auth;
        _steam = steam;
        _depotBox = depotBox;
        _hydraCloud = hydraCloud;
        _hydraCloudSync = hydraCloudSync;
        _cloudSavesEnabled = settings.CloudSavesEnabled;
        _hydraCloud.StateChanged += RefreshHydraCloudState;
        RefreshHydraCloudState();
        _depotBoxStatsTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _depotBoxStatsTimer.Tick += (_, _) => RefreshDepotBoxStatsCommand.Execute(null);
        RefreshAccount();
        RefreshSteam();
        _autoUpdateApps = settings.AutoUpdateApps; // init from saved value (default ON) without triggering Save
        _fastFetch = settings.FastFetch;
        _startWithWindows = settings.StartWithWindows; // default OFF. Init without triggering the registry write
        _minimizeToTray = settings.MinimizeToTray;
        _updateNotificationsEnabled = settings.UpdateNotificationsEnabled;
        _useApiKey = settings.UseApiKey;
        _depotBoxIsKeyConfigured = ShouldShowDepotBoxStats(settings.UseApiKey, settings.DepotBoxApiKey);

        // Select the saved language (or "System default") without firing the restart prompt.
        _suppressLanguagePrompt = true;
        _selectedLanguage = LanguageOptions.FirstOrDefault(o => o.Tag == settings.Language) ?? LanguageOptions[0];
        _suppressLanguagePrompt = false;

        // ── Runtime tools (Settings → RUNTIMES) ──────────────────────
        RuntimeTools.Add(new RuntimeToolViewModel("Ludusavi",
            detectInstalled: () => File.Exists(ludusavi.ExePath),
            readVersion: () => ludusavi.CachedVersion,
            installAsync: () => Task.Run(async () => await ludusavi.EnsureAsync(null, CancellationToken.None) is not null),
            remove: ludusavi.Remove));
        RuntimeTools.Add(new RuntimeToolViewModel("SteamAutoCrack",
            detectInstalled: () => File.Exists(SteamAutoCrackService.ExePath),
            readVersion: () => sac.CachedVersion,
            installAsync: () => Task.Run(async () => await sac.EnsureToolAsync(null, force: true, CancellationToken.None) is not null),
            remove: sac.Remove,
            preRemoveOnReinstall: false));
        RuntimeTools.Add(new RuntimeToolViewModel("DepotDownloaderMod",
            detectInstalled: () => File.Exists(DepotDownloaderService.ExePath),
            readVersion: () => depotDownloader.CachedVersion,
            installAsync: () => Task.Run(async () => await depotDownloader.EnsureToolAsync(null, CancellationToken.None) is not null),
            remove: depotDownloader.Remove));
    }


    private void RefreshSteam()
    {
        string? path = _steam.EffectivePath;
        IsSteamOverridden = _steam.IsOverridden;
        SteamPath = path ?? Resources.Strings.Settings_SteamNotFound;
        SteamSource = IsSteamOverridden ? Resources.Strings.Settings_SteamSource_Custom
            : path is null ? Resources.Strings.Settings_SteamSource_NotFound
            : Resources.Strings.Settings_SteamSource_Auto;
        SteamWarning = path is not null && !_steam.IsValid ? Resources.Strings.Settings_SteamWarning_NoExe : null;
    }

    public void RefreshAccount()
    {
        IsGuest = _auth.IsGuest;
        DisplayName = _auth.DisplayName;
        Email = _auth.Email;
        AvatarUrl = _auth.AvatarUrl;
        IsBotProvisioned = _auth.IsBotProvisioned;
        if (!IsGuest) LoginRequiredMessage = null;
    }

    /// <summary>Hide the re-link banner for this session (returns next launch if still a bot account).</summary>
    [RelayCommand]
    private void DismissBotBanner() => BotBannerDismissed = true;

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (RequestSignIn is not null) await RequestSignIn();
    }

    // ── Discord bot code sign-in ────────────────────────────────────

    /// <summary>The 6-char code the user typed from the Discord <c>/login</c> DM.</summary>
    [ObservableProperty] private string _codeInput = "";

    /// <summary>True while redeeming. Disables the Redeem button via <see cref="CanRedeemCode"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRedeemCode))]
    private bool _isRedeemingCode;

    /// <summary>Error shown under the code box (expired/invalid/server). Null = hidden.</summary>
    [ObservableProperty] private string? _codeError;

    public bool CanRedeemCode => !IsRedeemingCode;

    /// <summary>Redeem the Discord bot code for a session (no browser needed).</summary>
    [RelayCommand]
    private async Task SignInWithCodeAsync()
    {
        string code = CodeInput.Trim();
        if (code.Length != 6) return;

        IsRedeemingCode = true;
        CodeError = null;
        try
        {
            await _auth.SignInWithCodeAsync(code);
            CodeInput = "";
        }
        catch (Exception ex)
        {
            CodeError = ex.Message;
        }
        finally
        {
            IsRedeemingCode = false;
        }
    }

    [RelayCommand]
    private void OverrideSteamFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Resources.Strings.Settings_ChooseSteamFolder,
            InitialDirectory = _steam.EffectivePath ?? "",
        };
        if (dialog.ShowDialog() == true)
        {
            _settings.SteamPathOverride = dialog.FolderName;
            RefreshSteam();
        }
    }

    [RelayCommand]
    private void ClearSteamOverride()
    {
        _settings.SteamPathOverride = null;
        RefreshSteam();
    }

    [RelayCommand]
    private void OpenSteamFolder()
    {
        string? path = _steam.EffectivePath;
        if (path is not null && System.IO.Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenWebsite() =>
        Process.Start(new ProcessStartInfo(AppConfig.DepotBoxBaseUrl) { UseShellExecute = true });

    [RelayCommand]
    private void OpenDepotBox() =>
        Process.Start(new ProcessStartInfo(AppConfig.DepotBoxBaseUrl) { UseShellExecute = true });

    [RelayCommand]
    private void SignOut() => _auth.SignOut();

    // ── DepotBox key management ───────────────────────────────────────

    /// <summary>Called by the View when loaded. Auto-refreshes stats if a key is saved.</summary>
    public void OnViewLoaded()
    {
        if (DepotBoxIsKeyConfigured)
        {
            _depotBoxStatsTimer.Start();
            RefreshDepotBoxStatsCommand.Execute(null);
        }
        // Re-sync FastFetch in case the Add screen's toggle changed it this session (both are singletons
        // that only read the setting at construction). No-op if unchanged; a real change writes back the
        // same value, so no feedback loop.
        FastFetch = _settings.FastFetch;
        _ = RefreshHydraCloudAsync();
    }

    /// <summary>Re-fetch usage stats for the saved key. Silent no-op if no key is saved.</summary>
    [RelayCommand]
    private async Task RefreshDepotBoxStatsAsync()
    {
        string? key = _settings.DepotBoxApiKey;
        if (string.IsNullOrEmpty(key)) return;

        IsRefreshingDepotBoxStats = true;
        try
        {
            DepotBoxStats = await _depotBox.GetStatsAsync(key);
        }
        catch
        {
            DepotBoxStats = null;
        }
        finally
        {
            IsRefreshingDepotBoxStats = false;
        }
    }

    /// <summary>Validate the typed key (format first, then a live stats call) and, if good, save it.</summary>
    [RelayCommand]
    private async Task ValidateAndSaveDepotBoxKeyAsync()
    {
        string key = DepotBoxKeyInput.Trim();

        if (!DepotBoxService.IsValidKeyFormat(key))
        {
            ShowDepotBoxStatus(Resources.Strings.Settings_DepotBoxKeyBad, isError: true);
            return;
        }

        IsValidatingDepotBoxKey = true;
        try
        {
            var stats = await _depotBox.GetStatsAsync(key);
            if (stats is null)
            {
                // Could be a bad/expired key (401), or a network problem. Both surface as null.
                ShowDepotBoxStatus(Resources.Strings.Settings_DepotBoxKeyError, isError: true);
                return;
            }

            _settings.DepotBoxApiKey = key;
            DepotBoxIsKeyConfigured = true;
            _depotBoxStatsTimer.Start();
            DepotBoxKeyInput = "";
            DepotBoxKeyStatus = null;
            DepotBoxStats = stats;
        }
        finally
        {
            IsValidatingDepotBoxKey = false;
        }
    }

    /// <summary>Forget the saved key and reset the status line.</summary>
    [RelayCommand]
    private void ClearDepotBoxKey()
    {
        _settings.DepotBoxApiKey = null;
        _depotBoxStatsTimer.Stop();
        DepotBoxIsKeyConfigured = false;
        DepotBoxKeyInput = "";
        DepotBoxKeyStatus = null;
        DepotBoxStats = null;
    }

    private void ShowDepotBoxStatus(string text, bool isError)
    {
        DepotBoxKeyStatus = text;
        DepotBoxKeyStatusColor = isError ? "#f87171" : "#22c55e";
    }

    /// <summary>Requests used in the current DepotBox one-minute rate window.</summary>
    private static string FormatDepotBoxStats(DepotBoxUsageRecord stats)
    {
        return $"{stats.DailyUsage}/{stats.DailyLimit} requests this minute";
    }
}
