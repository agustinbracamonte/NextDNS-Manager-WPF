using System.Windows;
using System.Windows.Controls;
using NextDNSManager.Shared;

namespace NextDNSModernWPF;

public partial class MainWindow
{
    private TextBlock? _dnsSummary;

    private void InitializeNetworkInformation()
    {
        StackPanel panel = HomeNetworkHost;
        var container = new StackPanel { Margin = new Thickness(0) };
        _dnsSummary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0) };
        _dnsSummary.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        container.Children.Add(_dnsSummary);
        panel.Children.Add(container);
        RefreshNetworkInformation();
    }

    private void RefreshNetworkInformation()
    {
        if (_dnsSummary is null) return;
        try
        {
            var snapshot = NetworkDiagnostics.Capture();
            var rows = snapshot.Adapters.Where(a => a.IsOperational && a.Type != "Loopback" && a.DnsServers.Length > 0)
                .Select(a => $"{a.Name}: {string.Join(", ", a.DnsServers)}");
            string result = string.Join("\n", rows);
            _dnsSummary.Text = string.IsNullOrWhiteSpace(result) ? "No hay DNS configurado disponible." : result;
        }
        catch { _dnsSummary.Text = "No se pudo consultar la configuración de red."; }
    }
}
