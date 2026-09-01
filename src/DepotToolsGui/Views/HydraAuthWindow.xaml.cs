using System.Windows;
using DepotToolsGui.Services;
using Microsoft.Web.WebView2.Core;

namespace DepotToolsGui.Views;

public partial class HydraAuthWindow : Window
{
    private readonly HydraCloudService _cloud;

    public HydraAuthWindow(HydraCloudService cloud)
    {
        InitializeComponent();
        _cloud = cloud;
        Loaded += async (_, _) => await InitializeBrowserAsync();
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.NavigationStarting += Browser_NavigationStarting;
            Browser.CoreWebView2.Navigate(_cloud.GetSignInUri().ToString());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                string.Format(DepotToolsGui.Resources.Strings.HydraAuth_WebView2Required, ex.Message),
                DepotToolsGui.Resources.Strings.HydraAuth_WebView2Unavailable, MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("hydralauncher", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("auth", StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true;
        try
        {
            await _cloud.HandleAuthUriAsync(uri.ToString());
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, DepotToolsGui.Resources.Strings.HydraAuth_SignInFailed, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
