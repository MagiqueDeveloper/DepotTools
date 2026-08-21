using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DepotToolsGui.Models;
using DepotToolsGui.Services;

namespace DepotToolsGui.ViewModels;

/// <summary>One card on the Mode page. A single unlocker backend's name, status, and action button.</summary>
public partial class ModeCardViewModel(UnlockerMode mode, string title, string description) : ObservableObject
{
    public UnlockerMode Mode { get; } = mode;
    public string Title { get; } = title;
    public string Description { get; } = description;

    [ObservableProperty] private string _statusText = Resources.Strings.Mode_Checking;
    [ObservableProperty] private string _buttonText = Resources.Strings.Mode_Btn_Install;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActionButton))]
    private bool _isActive;

    /// <summary>
    /// Custom has nothing to install, update or reinstall, so once it's the active mode there is no
    /// action left to offer and the button is hidden. The ACTIVE badge already says it's selected.
    /// Every other card always has something to do (install, update, or switch to it).
    /// </summary>
    public bool ShowActionButton => !(Mode == UnlockerMode.Custom && IsActive);

    /// <summary>Our own fork is the steer-here option.</summary>
    public bool IsRecommended => Mode == UnlockerMode.Bst;

    /// <summary>OST is the nightly channel, so it carries the amber warning.</summary>
    public bool IsExperimental => Mode == UnlockerMode.Ost;

    /// <summary>Both OpenSteamTool-derived builds carry native CloudRedirect support.</summary>
    public bool SupportsCloudRedirect => Mode is UnlockerMode.Ost or UnlockerMode.Bst;
}

/// <summary>
/// "Mode" page: OpenSteamTools / BetterSteamTools / Custom. Mutually exclusive, one active at a time.
/// Checks status on page open; each card installs/switches after a Steam-shutdown confirmation, then
/// relaunches Steam so the new mode takes effect.
/// </summary>
public partial class ModeViewModel : ObservableObject
{
    private readonly UnlockerService _unlocker;
    private readonly ToastService _toast;
    private readonly SteamService _steam;

    public ObservableCollection<ModeCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;

    // ── Steam-shutdown confirmation overlay ──────────────────────────
    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _confirmTitle = "";
    private ModeCardViewModel? _pendingCard;

    public ModeViewModel(UnlockerService unlocker, ToastService toast, SteamService steam)
    {
        _unlocker = unlocker;
        _toast = toast;
        _steam = steam;
    }

    /// <summary>True if a hidden mode should be shown: its reveal-file exists, or it's the active mode.</summary>
    private bool IsModeVisible(ModeDefinition def)
    {
        if (def.HiddenUnlessFile is null) return true;          // always-visible mode
        if (_unlocker.SelectedMode == def.Mode) return true;    // active → keep visible even if file gone
        string? root = _steam.EffectivePath;
        return root is not null && File.Exists(Path.Combine(root, def.HiddenUnlessFile));
    }

    /// <summary>Rebuild the visible card list (hidden modes appear only when revealed). Preserves
    /// existing cards so their bound state isn't reset; adds/removes as visibility changes.</summary>
    private void SyncCards()
    {
        var visible = _unlocker.Modes
            .Where(IsModeVisible)
            .ToList();

        // Remove cards no longer visible.
        for (int i = Cards.Count - 1; i >= 0; i--)
            if (!visible.Any(d => d.Mode == Cards[i].Mode))
                Cards.RemoveAt(i);

        // Add newly-visible cards in definition order.
        foreach (var def in visible)
            if (!Cards.Any(c => c.Mode == def.Mode))
            {
                int idx = visible.IndexOf(def);
                idx = Math.Min(idx, Cards.Count);
                Cards.Insert(idx, new ModeCardViewModel(def.Mode, def.DisplayName, def.Description));
            }
    }

    /// <summary>
    /// Page open / refresh. Only the ACTIVE mode is checked against GitHub (inactive cards just show
    /// "Switch to this": switching re-fetches anyway, so pinging for them is wasted, and their hash
    /// check would be misleading since modes share filenames). Active check is cached briefly.
    /// </summary>
    private bool _detectionAttempted;

    public async Task LoadAsync(bool forceRefresh = false)
    {
        // First time with no mode selected: try to auto-detect an existing install by hashing the
        // on-disk DLLs against published releases, and adopt the match as active.
        if (!_detectionAttempted && _unlocker.SelectedMode is null)
        {
            _detectionAttempted = true;
            await _unlocker.DetectActiveModeAsync();
        }

        // Re-evaluate which cards are visible (hidden modes appear only when revealed).
        SyncCards();

        var active = _unlocker.SelectedMode;
        foreach (var card in Cards)
        {
            if (card.Mode == active)
            {
                card.StatusText = Resources.Strings.Mode_Checking;
                Apply(card, await _unlocker.GetStateAsync(card.Mode, forceRefresh));
            }
            else
            {
                // No network for inactive modes.
                Apply(card, new ModeState(card.Mode, ModeStatus.NotInstalled, IsActive: false, null));
            }
        }
    }

    private DateTime _lastCheck;

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        // 30s cooldown: a forced refresh hits GitHub, and the unauthenticated API allows only 60/hr.
        if (IsBusy || DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(30)) return;
        _lastCheck = DateTime.UtcNow;
        await LoadAsync(forceRefresh: true);
    }

    private void Apply(ModeCardViewModel card, ModeState s)
    {
        // Only the ACTIVE mode shows real install/update status. Inactive modes share filenames with
        // the active one (different contents), so their hash check is meaningless. Show them simply
        // as the switch target instead of a misleading "Update available".
        if (s.Status == ModeStatus.UserManaged)
            // Custom: there's nothing to be up to date WITH, so the status never varies and the only
            // action is switching to it. Checked before IsActive so it never reads "Not active".
            card.StatusText = Resources.Strings.Mode_UserManaged;
        else if (s.Status == ModeStatus.Unknown)
            card.StatusText = Resources.Strings.Mode_StatusUnavailable;
        else if (!s.IsActive)
            card.StatusText = Resources.Strings.Mode_NotActive;
        else
            card.StatusText = s.Status switch
            {
                ModeStatus.NotInstalled => Resources.Strings.Mode_NotInstalled,
                ModeStatus.UpToDate => Resources.Strings.Mode_UpToDate,
                ModeStatus.UpdateAvailable => Resources.Strings.Mode_UpdateAvailable,
                _ => Resources.Strings.Mode_StatusUnavailable,
            };

        card.ButtonText = (s.IsActive, s.Status) switch
        {
            // Never rendered: ShowActionButton hides the button in this exact case. Mapped anyway so
            // a regression in that binding surfaces a harmless "Switch to this" rather than falling
            // through to "Install" on a mode that installs nothing.
            (true, ModeStatus.UserManaged) => Resources.Strings.Mode_Btn_Switch,
            (true, ModeStatus.UpToDate) => Resources.Strings.Mode_Btn_Reinstall,
            (true, ModeStatus.UpdateAvailable) => Resources.Strings.Mode_Btn_Update,
            (true, _) => Resources.Strings.Mode_Btn_Install,
            (false, _) => Resources.Strings.Mode_Btn_Switch,
        };
        card.IsActive = s.IsActive;
    }

    // ── Install with confirmation ────────────────────────────────────

    /// <summary>Card button → ask the user to confirm (Steam will be closed) before doing anything.</summary>
    [RelayCommand]
    private void Install(ModeCardViewModel card)
    {
        if (IsBusy) return;
        _pendingCard = card;
        ConfirmTitle = card.IsActive
            ? string.Format(Resources.Strings.Mode_Confirm_Reinstall, card.Title)
            : string.Format(Resources.Strings.Mode_Confirm_Switch, card.Title);
        IsConfirming = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirming = false;
        _pendingCard = null;
    }

    [RelayCommand]
    private async Task ConfirmInstall()
    {
        IsConfirming = false;
        var card = _pendingCard;
        _pendingCard = null;
        if (card is null) return;

        await RunInstall(card.Mode);
    }

    private async Task RunInstall(UnlockerMode mode)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        try
        {
            var prog = new Progress<double?>(p =>
            {
                IsProgressIndeterminate = p is null;
                if (p is not null) Progress = p.Value * 100;
            });

            // Correct order: kill Steam → write the files → relaunch Steam.
            // (Files can't be overwritten while Steam holds them open. CloudRedirect's CLI also closes
            //  Steam itself, but stopping first is harmless and keeps all modes consistent.)
            await Task.Run(_steam.StopSteam);

            var result = await _unlocker.InstallAsync(mode, prog);

            if (result.Success)
            {
                bool started = await Task.Run(_steam.StartSteam);
                _toast.Show(Resources.Strings.Mode_Toast_Updated, started
                    ? string.Format(Resources.Strings.Mode_Toast_Updated_Restarting, mode)
                    : string.Format(Resources.Strings.Mode_Toast_Updated_Start, mode));
            }
            else
            {
                // Install failed: bring Steam back up anyway so the user isn't left without it.
                await Task.Run(_steam.StartSteam);
                _toast.Show(Resources.Strings.Mode_Toast_InstallFailed, result.Error ?? Resources.Strings.Mode_Toast_InstallFailed_Body, error: true);
            }

            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
        }
    }
}
