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
            _pauseButton = new System.Windows.Controls.Button { Content = LocalizationManager.Get("Code.Pause.Pause"), MinWidth = 150, Height = 46 };
            _pauseButton.SetResourceReference(FrameworkElement.StyleProperty, "QuietActionButton");
            var menu = new System.Windows.Controls.ContextMenu();
            menu.Style = (Style)FindResource("PauseContextMenu");
            menu.Template = CreatePauseContextMenuTemplate();
            foreach (int minutes in new[] { 5, 15, 30 })
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = LocalizationManager.T("Code.Pause.Minutes", minutes),
                    Tag = minutes,
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
            _trayPause = new System.Windows.Forms.ToolStripMenuItem(LocalizationManager.Get("Code.Pause.PauseTemporarily"));
            foreach (int minutes in new[] { 5, 15, 30 })
                _trayPause.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem(
                    LocalizationManager.T("Code.Pause.Minutes", minutes), null, async (_, _) => await SetPauseAsync(minutes)) { Tag = minutes });
            _trayResume = new System.Windows.Forms.ToolStripMenuItem(LocalizationManager.Get("Code.Pause.ResumeProtection"), null, async (_, _) => await SetPauseAsync(null));
            _trayContextMenu.Items.Insert(4, _trayPause);
            _trayContextMenu.Items.Insert(5, _trayResume);
            _trayProfiles = new System.Windows.Forms.ToolStripMenuItem(LocalizationManager.Get("Code.Pause.ChangeProfile"));
            _trayContextMenu.Items.Insert(6, _trayProfiles);
        }
        RefreshPauseMenuText();
    }

    private static ControlTemplate CreatePauseContextMenuTemplate()
    {
        var template = new ControlTemplate(typeof(System.Windows.Controls.ContextMenu));
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
        surface.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
        surface.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(System.Windows.Controls.Control.BorderThicknessProperty));
        surface.SetValue(Border.PaddingProperty, new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        surface.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var items = new FrameworkElementFactory(typeof(ItemsPresenter));
        items.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
        surface.AppendChild(items);
        template.VisualTree = surface;
        return template;
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
            var entry = new System.Windows.Forms.ToolStripMenuItem(profile.Name ?? LocalizationManager.Get("Code.Pause.ProfileFallback")) { Checked = ReferenceEquals(cmbProfiles.SelectedItem, profile) };
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
            if (!response.Success) throw UiErrorLocalizer.CreateBrokerResponseException(response.Error);
            ApplyBrokerStatus(response);
        }
        catch (Exception error)
        {
            if (UiErrorLocalizer.TryGetBrokerFailureCode(error, out BrokerFailureCode failureCode))
                DiagnosticLog.WriteManagerBrokerFailure(DiagnosticEvent.BrokerActionFailure, failureCode);
            else
                DiagnosticLog.WriteManager(DiagnosticEvent.BrokerActionFailure, error);
            brokerStatusUncertain = true;
            brokerActionInProgress = false;
            System.Windows.MessageBox.Show(UiErrorLocalizer.Localize(error), "NextDNS Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            await CheckCurrentStatusAsync();
        }
        finally { brokerActionInProgress = false; UpdateUI(); }
    }

    private void UpdatePauseUi()
    {
        bool canChange = hasBrokerStatus && !brokerStatusUncertain && !brokerActionInProgress && !profileSwitchInProgress && !statusCheckInProgress;
        if (_pauseButton is not null)
        {
            _pauseButton.Content = _pauseUntil.HasValue
                ? LocalizationManager.Get("Code.Pause.Resume")
                : LocalizationManager.Get("Code.Pause.Pause");
            _pauseButton.IsEnabled = canChange && (isProtected || _pauseUntil.HasValue);
        }
        if (_trayPause is not null) _trayPause.Enabled = canChange && isProtected;
        if (_trayResume is not null) _trayResume.Enabled = canChange && _pauseUntil.HasValue;
    }

    private void RefreshPauseMenuText()
    {
        if (_pauseButton is not null)
        {
            _pauseButton.Content = _pauseUntil.HasValue
                ? LocalizationManager.Get("Code.Pause.Resume")
                : LocalizationManager.Get("Code.Pause.Pause");
            if (_pauseButton.ContextMenu is { } menu)
            {
                foreach (object item in menu.Items)
                {
                    if (item is System.Windows.Controls.MenuItem pauseItem && pauseItem.Tag is int minutes)
                        pauseItem.Header = LocalizationManager.T("Code.Pause.Minutes", minutes);
                }
            }
        }

        if (_trayPause is not null)
        {
            _trayPause.Text = LocalizationManager.Get("Code.Pause.PauseTemporarily");
            foreach (System.Windows.Forms.ToolStripItem item in _trayPause.DropDownItems)
            {
                if (item.Tag is int minutes)
                    item.Text = LocalizationManager.T("Code.Pause.Minutes", minutes);
            }
        }
        if (_trayResume is not null) _trayResume.Text = LocalizationManager.Get("Code.Pause.ResumeProtection");
        if (_trayProfiles is not null) _trayProfiles.Text = LocalizationManager.Get("Code.Pause.ChangeProfile");
    }
}
