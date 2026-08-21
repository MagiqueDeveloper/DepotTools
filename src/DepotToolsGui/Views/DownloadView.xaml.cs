using System.Windows.Controls;
using System.Windows.Input;
using DepotToolsGui.ViewModels;

namespace DepotToolsGui.Views;

public partial class DownloadView : UserControl
{
    public DownloadView(DownloadViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _ = viewModel.LoadFeaturedAsync();
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
