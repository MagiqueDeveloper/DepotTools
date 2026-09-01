using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DepotToolsGui.ViewModels;

namespace DepotToolsGui.Views;

public partial class DownloadView : UserControl
{
    private readonly TranslateTransform _fetchStatusFrontTransform = new();
    private readonly TranslateTransform _fetchStatusIncomingTransform = new();
    private string _fetchStatusText = "";
    private int _fetchStatusAnimationGeneration;

    public DownloadView(DownloadViewModel viewModel)
    {
        InitializeComponent();
        FetchStatusFront.RenderTransform = _fetchStatusFrontTransform;
        FetchStatusIncoming.RenderTransform = _fetchStatusIncomingTransform;
        FetchStatusFront.Text = viewModel.FetchStatusText;
        _fetchStatusText = viewModel.FetchStatusText;
        DataContext = viewModel;
        PropertyChangedEventManager.AddHandler(
            viewModel,
            ViewModel_FetchStatusTextChanged,
            nameof(DownloadViewModel.FetchStatusText));
        _ = viewModel.LoadFeaturedAsync();
    }

    private void ViewModel_FetchStatusTextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DownloadViewModel viewModel) return;

        if (Dispatcher.CheckAccess())
        {
            RollFetchStatus(viewModel.FetchStatusText);
            return;
        }

        _ = Dispatcher.BeginInvoke(() => RollFetchStatus(viewModel.FetchStatusText));
    }

    private void RollFetchStatus(string nextText)
    {
        if (string.Equals(_fetchStatusText, nextText, StringComparison.Ordinal)) return;

        var outgoingText = _fetchStatusText;
        var generation = ++_fetchStatusAnimationGeneration;
        StopFetchStatusAnimations();
        FetchStatusFront.Text = outgoingText;
        FetchStatusIncoming.Text = nextText;
        _fetchStatusText = nextText;

        if (!SystemParameters.ClientAreaAnimation)
        {
            FetchStatusFront.Text = nextText;
            FetchStatusIncoming.Text = "";
            return;
        }

        var height = FetchStatusHost.ActualHeight > 0 ? FetchStatusHost.ActualHeight : 18;
        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var outgoingPosition = new DoubleAnimation(0, -height, duration) { EasingFunction = easing };
        var incomingPosition = new DoubleAnimation(height, 0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fadeOut = new DoubleAnimation(1, 0, duration);
        var fadeIn = new DoubleAnimation(0, 1, duration);

        incomingPosition.Completed += (_, _) => CompleteFetchStatusRoll(generation, nextText);
        _fetchStatusFrontTransform.BeginAnimation(TranslateTransform.YProperty, outgoingPosition);
        FetchStatusFront.BeginAnimation(OpacityProperty, fadeOut);
        _fetchStatusIncomingTransform.BeginAnimation(TranslateTransform.YProperty, incomingPosition);
        FetchStatusIncoming.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void CompleteFetchStatusRoll(int generation, string finalText)
    {
        if (generation != _fetchStatusAnimationGeneration) return;

        StopFetchStatusAnimations();
        FetchStatusFront.Text = finalText;
        FetchStatusIncoming.Text = "";
    }

    private void StopFetchStatusAnimations()
    {
        _fetchStatusFrontTransform.BeginAnimation(TranslateTransform.YProperty, null);
        _fetchStatusIncomingTransform.BeginAnimation(TranslateTransform.YProperty, null);
        FetchStatusFront.BeginAnimation(OpacityProperty, null);
        FetchStatusIncoming.BeginAnimation(OpacityProperty, null);
        _fetchStatusFrontTransform.Y = 0;
        _fetchStatusIncomingTransform.Y = 0;
        FetchStatusFront.Opacity = 1;
        FetchStatusIncoming.Opacity = 0;
    }

    /// <summary>The featured strips scroll horizontally, but they're nested inside the page's vertical
    /// ScrollViewer, so a normal mouse wheel would bubble up and scroll the page instead. Translate the
    /// vertical wheel delta into horizontal scroll while the pointer is over a strip, and mark it handled
    /// so the outer ScrollViewer doesn't also move.</summary>
    private void FeaturedStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }
}
