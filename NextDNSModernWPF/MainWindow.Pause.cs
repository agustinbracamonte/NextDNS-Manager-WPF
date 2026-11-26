using System.Windows;
using System.Windows.Controls;
using NextDNSManager.Shared;

namespace NextDNSModernWPF;

public partial class MainWindow
{
    private DateTimeOffset? _pauseUntil;
    private System.Windows.Controls.Button? _pauseButton;
    private System.Windows.Forms.ToolStripMenuItem? _trayPause;
    private System.Windows.Forms.ToolStripMenuItem? _trayResume;
    private System.Windows.Forms.ToolStripMenuItem? _trayProfiles;
    private System.Windows.Threading.DispatcherTimer? _pauseStatusTimer;

    private void InitializePauseUi()
    {
        _pauseStatusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _pauseStatusTimer.Tick += PauseStatusTimer_Tick;
        _pauseStatusTimer.Start();
        if (HomePauseHost is StackPanel panel)
        {
            _pauseButton = new System.Windows.Controls.Button { Content = "Pausar", MinWidth = 150, Height = 46 };
            _pauseButton.SetResourceReference(FrameworkElement.StyleProperty, "QuietActionButton");
            var menu = new System.Windows.Controls.ContextMenu();
            menu.Style = (Style)FindResource("PauseContextMenu");
            foreach (int minutes in new[] { 5, 15, 30 })
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = $"{minutes} minutos",
                    Style = (Style)FindResource("PauseContextMenuItem")
                };
                item.Click += async (_, _) => await SetPauseAsync(minutes);
                menu.Items.Add(item);
            }
            _pauseButton.ContextMenu = menu;
            _pauseButton.Click += async (_, _) =>
            {
                if (_pauseUntil.HasValue) await SetPauseAsync(null);
                else { menu.PlacementTarget = _pauseButton; menu.IsOpen = true; }
            };
            panel.Children.Add(_pauseButton);
        }
        if (_trayContextMenu is not null)
        {
            _trayPause = new System.Windows.Forms.ToolStripMenuItem("Pausar temporalmente");
            foreach (int minutes in new[] { 5, 15, 30 })
                _trayPause.DropDownItems.Add($"{minutes} minutos", null, async (_, _) => await SetPauseAsync(minutes));
            _trayResume = new System.Windows.Forms.ToolStripMenuItem("Reanudar protección", null, async (_, _) => await SetPauseAsync(null));
            _trayContextMenu.Items.Insert(4, _trayPause);
            _trayContextMenu.Items.Insert(5, _trayResume);
            _trayProfiles = new System.Windows.Forms.ToolStripMenuItem("Cambiar perfil");
            _trayContextMenu.Items.Insert(6, _trayProfiles);
        }
    }

    private async void PauseStatusTimer_Tick(object? sender, EventArgs e)
    {
        if (_pauseUntil.HasValue && !brokerActionInProgress && !statusCheckInProgress)
            await CheckCurrentStatusAsync();
    }

    private void RefreshTrayProfiles()
    {
        if (_trayProfiles is null) return;
        _trayProfiles.DropDownItems.Clear();
        _trayProfiles.Enabled = !_pauseUntil.HasValue && hasBrokerStatus && !brokerStatusUncertain && !brokerActionInProgress && !profileSwitchInProgress && profiles.Count > 0;
        foreach (Profile profile in profiles)
        {
            var entry = new System.Windows.Forms.ToolStripMenuItem(profile.Name ?? "Perfil") { Checked = ReferenceEquals(cmbProfiles.SelectedItem, profile) };
            entry.Click += (_, _) => cmbProfiles.SelectedItem = profile;
            _trayProfiles.DropDownItems.Add(entry);
        }
    }

    private async Task SetPauseAsync(int? minutes)
    {
        if (brokerActionInProgress || profileSwitchInProgress || statusCheckInProgress) return;
        brokerActionInProgress = true;
        UpdateUI();
        try
        {
            var response = await brokerClient.SendAsync(new BrokerRequest { Operation = minutes.HasValue ? "pause" : "resume", PauseMinutes = minutes });
            if (!response.Success) throw new InvalidOperationException(response.Error ?? "No se pudo cambiar la pausa.");
            ApplyBrokerStatus(response);
        }
        catch (Exception error)
        {
            brokerStatusUncertain = true;
            brokerActionInProgress = false;
            System.Windows.MessageBox.Show(error.Message, "NextDNS Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            await CheckCurrentStatusAsync();
        }
        finally { brokerActionInProgress = false; UpdateUI(); }
    }

    private void UpdatePauseUi()
    {
        bool canChange = hasBrokerStatus && !brokerStatusUncertain && !brokerActionInProgress && !profileSwitchInProgress && !statusCheckInProgress;
        if (_pauseButton is not null)
        {
            _pauseButton.Content = _pauseUntil.HasValue ? "Reanudar" : "Pausar";
            _pauseButton.IsEnabled = canChange && (isProtected || _pauseUntil.HasValue);
        }
        if (_trayPause is not null) _trayPause.Enabled = canChange && isProtected;
        if (_trayResume is not null) _trayResume.Enabled = canChange && _pauseUntil.HasValue;
    }
}
