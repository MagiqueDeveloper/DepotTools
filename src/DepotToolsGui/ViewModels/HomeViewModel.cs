using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotToolsGui.Services;

namespace DepotToolsGui.ViewModels;

/// <summary>
/// Home dashboard: library stats, a "recently added" cover strip, and Steam/account status.
/// Reuses the same stplug-in scan + name/cover caches as the Manage page.
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    /// <summary>Set by App: navigate to Manage and open this appid's detail (Home "recently added" click).</summary>
    public Action<long>? NavigateToGame { get; set; }

    // Section-navigation hooks wired by App (each → MainWindow.NavigateToXxx).
    public Action? NavigateToManage { get; set; }
    public Action? NavigateToSettings { get; set; }
    public Action? NavigateToMode { get; set; }

    private readonly SteamService _steam;
    private readonly AuthService _auth;
    private readonly SteamAppListCache _appList;
    private readonly SteamAppInfoCache _appInfo;
    private readonly CoverCache _covers;
    private readonly UnlockerService _unlocker;

    /// <summary>Drag-and-drop installer shown on the page; refreshes the library after a drop.</summary>
    public DropInstallViewModel Drop { get; }

    // ── Library stats ───────────────────────────────────────────────
    [ObservableProperty] private int _gameCount;

    // ── Recently added strip ────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecent))]
    private ObservableCollection<LuaTileViewModel> _recent = [];

    public bool HasRecent => Recent.Count > 0;

    // ── Steam + account status ──────────────────────────────────────
    [ObservableProperty] private bool _steamFound;
    [ObservableProperty] private string _steamStatus = Resources.Strings.Home_CheckingSteam;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGuest))]
    private bool _isSignedIn;

    public bool IsGuest => !IsSignedIn;
    [ObservableProperty] private string _accountStatus = Resources.Strings.Home_BrowsingAsGuest;

    // ── Active unlocker mode ────────────────────────────────────────
    [ObservableProperty] private string _modeStatus = Resources.Strings.Home_NoModeSelected;

    public HomeViewModel(SteamService steam, AuthService auth,
        SteamAppListCache appList, SteamAppInfoCache appInfo, CoverCache covers, DropInstallViewModel drop,
        UnlockerService unlocker)
    {
        _steam = steam;
        _auth = auth;
        _appList = appList;
        _appInfo = appInfo;
        _covers = covers;
        _unlocker = unlocker;
        Drop = drop;
        _auth.AuthStateChanged += RefreshAccount;
        // Library refresh on any install (drag-drop, plugin, Add page, Fixes) is driven by
        // LuaInstaller.Installed, wired in App → RefreshLibraryAsync.
    }

    /// <summary>Open a recently-added game in the Manage detail view.</summary>
    [RelayCommand]
    private void OpenGame(LuaTileViewModel tile) => NavigateToGame?.Invoke(tile.AppId);

    // Clickable dashboard cells → section navigation.
    [RelayCommand] private void OpenManage() => NavigateToManage?.Invoke();
    [RelayCommand] private void OpenSettings() => NavigateToSettings?.Invoke();
    [RelayCommand] private void OpenMode() => NavigateToMode?.Invoke();

    /// <summary>Called when the page is shown. Refresh everything.</summary>
    public async Task LoadAsync()
    {
        RefreshSteam();
        RefreshAccount();
        RefreshMode();
        await RefreshLibraryAsync();
    }

    private void RefreshMode() =>
        ModeStatus = _unlocker.SelectedModeDisplayName is { } name
            ? string.Format(Resources.Strings.Home_ModeIs, name)
            : Resources.Strings.Home_NoModeSelected;

    private void RefreshSteam()
    {
        SteamFound = _steam.IsValid;
        SteamStatus = SteamFound
            ? string.Format(Resources.Strings.Home_SteamDetected, _steam.EffectivePath)
            : Resources.Strings.Home_SteamNotFound;
    }

    /// <summary>Rebuild the library count + "Recently added" strip (and warm the recent covers). Public
    /// so App can call it from LuaInstaller.Installed to refresh live after any add.</summary>
    public async Task RefreshLibraryAsync()
    {
        string? dir = _steam.StPlugInDir;
        if (dir is null || !Directory.Exists(dir))
        {
            GameCount = 0;
            Recent = [];
            return;
        }

        await _appList.EnsureLoadedAsync();

        var tiles = await Task.Run(() =>
            Directory.EnumerateFiles(dir, "*.lua")
                .Select(path => (path, name: Path.GetFileNameWithoutExtension(path)))
                .Where(f => long.TryParse(f.name, out _))
                .Select(f =>
                {
                    long appid = long.Parse(f.name);
                    var info = new FileInfo(f.path);
                    string? name = _appList.GetName(appid) ?? _appInfo.GetCached(appid)?.Name;
                    // Base = when added to the folder; if edited since (LastWrite later), use that. Newer is more relevant.
                    var added = info.LastWriteTime > info.CreationTime ? info.LastWriteTime : info.CreationTime;
                    return new LuaTileViewModel(appid, f.path, added, name ?? string.Format(Resources.Strings.Common_AppFallback, appid), name is null);
                })
                .OrderByDescending(t => t.AddedAt)
                .ToList());

        GameCount = tiles.Count;

        var recent = tiles.Take(4).ToList();
        Recent = new ObservableCollection<LuaTileViewModel>(recent);
        foreach (var t in recent) _ = t.EnsureResolvedAsync(_appInfo, _covers); // warm covers
    }

    private void RefreshAccount()
    {
        IsSignedIn = _auth.IsSignedIn;
        AccountStatus = IsSignedIn
            ? (_auth.DisplayName is { } n ? string.Format(Resources.Strings.Home_SignedInAs, n) : Resources.Strings.Home_SignedIn)
            : Resources.Strings.Home_BrowsingAsGuest;
    }
}
