using System.Windows.Controls;
using DepotToolsGui.ViewModels;

namespace DepotToolsGui.Views;

public partial class DownloadsView : UserControl
{
    public DownloadsView(DownloadsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
