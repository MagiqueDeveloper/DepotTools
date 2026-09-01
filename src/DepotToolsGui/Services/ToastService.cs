using System.Windows;
using System.Windows.Controls;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace DepotToolsGui.Services;

/// <summary>
/// App-wide bottom-right toast feedback. Thin wrapper over Wpf.Ui's SnackbarService. The presenter
/// is attached once from MainWindow (see App.OnStartup). Safe to call from any thread.
/// </summary>
public class ToastService
{
    private readonly SnackbarService _snackbar = new();
    private SnackbarPresenter? _presenter;

    /// <summary>Wire the presenter that hosts the toasts (called once after the window is built).</summary>
    public void Attach(SnackbarPresenter presenter)
    {
        _presenter = presenter;
        _snackbar.SetSnackbarPresenter(presenter);
    }

    /// <summary>Show a transient toast (auto-dismiss). Marshals to the UI thread; no-ops if unattached.</summary>
    public void Show(string title, string message, bool error = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        void Post() => _snackbar.Show(
            title, message,
            error ? ControlAppearance.Caution : ControlAppearance.Secondary,
            null,
            TimeSpan.FromSeconds(3));

        if (dispatcher.CheckAccess()) Post();
        else dispatcher.Invoke(Post);
    }

    /// <summary>
    /// Show a PERSISTENT toast with a primary action and, when supplied, a secondary action. Actions are
    /// real buttons because this Wpf.Ui version exposes no writable template-button command.
    /// </summary>
    public void ShowAction(
        string title,
        string message,
        string actionLabel,
        Action onAction,
        bool error = false,
        string? secondaryActionLabel = null,
        Action? onSecondaryAction = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        void Post()
        {
            if (_presenter is null) return;
            var bar = new Snackbar(_presenter)
            {
                Title = title,
                Appearance = error ? ControlAppearance.Caution : ControlAppearance.Secondary,
                Icon = new SymbolIcon(SymbolRegular.ArrowSync24),
                // No "infinite" sentinel exists: Timeout is how long it's VISIBLE (Zero = dismiss
                // instantly), so use a very large value to effectively persist until acted on / closed.
                Timeout = TimeSpan.FromDays(1),
                IsCloseButtonEnabled = true,
            };

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var actionBtn = new Wpf.Ui.Controls.Button
            {
                Content = actionLabel,
                Appearance = ControlAppearance.Primary,
                Margin = new Thickness(0, 8, 0, 0),
            };
            actionBtn.Click += (_, _) => onAction();
            actions.Children.Add(actionBtn);

            if (secondaryActionLabel is not null && onSecondaryAction is not null)
            {
                var secondaryBtn = new Wpf.Ui.Controls.Button
                {
                    Content = secondaryActionLabel,
                    Appearance = ControlAppearance.Secondary,
                    Margin = new Thickness(8, 8, 0, 0),
                };
                secondaryBtn.Click += (_, _) =>
                {
                    onSecondaryAction();
                    bar.Hide();
                };
                actions.Children.Add(secondaryBtn);
            }

            bar.Content = new StackPanel
            {
                Children =
                {
                    new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    actions,
                },
            };
            bar.Show();
        }

        if (dispatcher.CheckAccess()) Post();
        else dispatcher.Invoke(Post);
    }
}
