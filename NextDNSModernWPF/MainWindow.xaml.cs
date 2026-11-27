#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NextDNSManager.Shared;

namespace NextDNSModernWPF
{
    public class Profile
    {
        public string Name { get; set; }
        public string ID { get; set; }
        public override string ToString() => Name ?? string.Empty;
    }

    public partial class MainWindow : Window
    {
        private List<Profile> profiles = new List<Profile>();
        private readonly bool _demoCaptureMode;

        // CORRECCIÓN 1: Guardar configuración en %AppData% para que no ensucie la carpeta del programa
        private string appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextDNSManager");
        private string profilesFile;

        private bool isProtected = false;
        private bool hasBrokerStatus;
        private bool brokerStatusUncertain;
        private bool needsDnsRestore;
        private string activeProfileId;
        private string brokerStatusError;
        private bool suppressProfileSelectionChanged;
        private bool brokerActionInProgress;
        private bool profileSwitchInProgress;
        private bool profileSwitchWorkerRunning;
        private bool statusCheckInProgress;
        private Profile pendingProfileSelection;
        private Profile switchingToProfile;
        private readonly BrokerClient brokerClient = new BrokerClient();
        private readonly BrokerStatusRetryPolicy brokerStatusRetryPolicy = new BrokerStatusRetryPolicy();
        private readonly UpdateChecker updateChecker = new UpdateChecker();
        private readonly UpdateInstaller updateInstaller = new UpdateInstaller();
        private CancellationTokenSource updateDownloadCancellation;
        private DispatcherTimer updateTimer;
        private DispatcherTimer brokerStatusRetryTimer;
        private bool updateCheckInProgress;
        private bool updateInstallInProgress;
        private UpdateInfo availableUpdate;
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private System.Windows.Forms.ContextMenuStrip _trayContextMenu;
        private System.Windows.Forms.ToolStripMenuItem _trayStatusMenuItem;
        private System.Windows.Forms.ToolStripMenuItem _trayEnableMenuItem;
        private System.Windows.Forms.ToolStripMenuItem _trayDisableMenuItem;
        private System.Windows.Forms.ToolStripMenuItem _trayRefreshStatusMenuItem;
        private System.Windows.Forms.ToolStripMenuItem _trayCheckUpdatesMenuItem;
        private System.Windows.Forms.ToolStripMenuItem _trayOpenMenuItem;
        private System.Windows.Forms.ToolStripMenuItem _trayExitMenuItem;
        private System.Drawing.Icon _trayIcon;
        private bool _allowClose;
        private bool _trayDisposed;

        public MainWindow() : this(demoCaptureMode: false, ThemeVariant.Dark)
        {
        }

        internal MainWindow(bool demoCaptureMode, ThemeVariant demoTheme)
        {
            _demoCaptureMode = demoCaptureMode;
            LocalizationManager.Initialize();
            if (!demoCaptureMode)
                ThemeManager.Initialize(System.Windows.Application.Current);

            InitializeComponent();

            if (demoCaptureMode)
            {
                InitializeDemoCapture(demoTheme);
                return;
            }

            InitializeNetworkInformation();
            cmbTheme.SelectionChanged += CmbTheme_SelectionChanged;
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
            LocalizationManager.LanguageChanged += LocalizationManager_LanguageChanged;
            SyncThemeSelection();
            lblInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            cmbProfiles.SelectionChanged += CmbProfiles_SelectionChanged;
            brokerStatusRetryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(BrokerStatusRetryPolicy.InitialDelaySeconds)
            };
            brokerStatusRetryTimer.Tick += BrokerStatusRetryTimer_Tick;

            try
            {
                // Configurar ruta del JSON
                if (!Directory.Exists(appDataFolder)) Directory.CreateDirectory(appDataFolder);
                profilesFile = Path.Combine(appDataFolder, "profiles.json");

                try { this.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/escudo_dns.ico")); } catch { }
                InitializeTrayIcon();
                InitializePauseUi();

                LoadProfiles();
                InitializeApiUi();
                btnAction.IsEnabled = false;
                _ = CheckCurrentStatusAsync(enableActionWhenComplete: true);
                updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
                updateTimer.Tick += UpdateTimer_Tick;
                updateTimer.Start();
                _ = CheckForUpdatesAsync(false);
            }
            catch
            {
                DetachThemeEvents();
                DisposeTrayIcon();
                throw;
            }
        }

        private void InitializeDemoCapture(ThemeVariant theme)
        {
            // These values are intentionally synthetic. This branch avoids the
            // normal profile, API-key, network, broker, registry, and tray setup.
            profiles = new List<Profile>
            {
                new Profile { Name = "Personal (demo)", ID = "A1B2C3" },
                new Profile { Name = "Trabajo (demo)", ID = "D4E5F6" }
            };
            activeProfileId = "A1B2C3";
            hasBrokerStatus = true;
            isProtected = true;

            cmbProfiles.ItemsSource = profiles;
            cmbProfiles.SelectedIndex = 0;
            cmbTheme.SelectedItem = cmbTheme.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), theme.ToString(), StringComparison.OrdinalIgnoreCase));

            var demoDnsSummary = new TextBlock
            {
                Text = "45.90.28.0, 45.90.30.0 (datos de demostración)",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            };
            demoDnsSummary.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            HomeNetworkHost.Children.Add(demoDnsSummary);

            var demoPauseButton = new System.Windows.Controls.Button { Content = "Pausar", MinWidth = 150, Height = 46 };
            demoPauseButton.SetResourceReference(FrameworkElement.StyleProperty, "QuietActionButton");
            HomePauseHost.Children.Add(demoPauseButton);

            // The capture window is non-interactive, so no XAML action can run.
            IsHitTestVisible = false;
            Focusable = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UpdateUI();
        }

        internal void CloseAfterDemoCapture()
        {
            if (!_demoCaptureMode)
                throw new InvalidOperationException("Solo la ventana de captura puede cerrarse por este método.");

            _allowClose = true;
            Close();
        }

        private void InitializeTrayIcon()
        {
            System.Windows.Forms.NotifyIcon notifyIcon = null;
            System.Windows.Forms.ContextMenuStrip contextMenu = null;
            System.Drawing.Icon trayIcon = null;
            try
            {
                notifyIcon = new System.Windows.Forms.NotifyIcon();
                contextMenu = new System.Windows.Forms.ContextMenuStrip();

                try
                {
                    using (Stream iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/escudo_dns.ico")).Stream)
                    using (var sourceIcon = new System.Drawing.Icon(iconStream, 32, 32))
                        trayIcon = (System.Drawing.Icon)sourceIcon.Clone();
                }
                catch { trayIcon = (System.Drawing.Icon)System.Drawing.SystemIcons.Shield.Clone(); }

                notifyIcon.Icon = trayIcon;
                notifyIcon.Text = "NextDNS Manager";
                notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
                notifyIcon.BalloonTipClicked += NotifyIcon_BalloonTipClicked;

                _trayStatusMenuItem = new System.Windows.Forms.ToolStripMenuItem { Enabled = false };
                _trayEnableMenuItem = new System.Windows.Forms.ToolStripMenuItem(LocalizationManager.Get("Code.Tray.EnableNextDns"), null,
                    async (s, e) => await PerformActionAsync("activate"));
                _trayDisableMenuItem = new System.Windows.Forms.ToolStripMenuItem(LocalizationManager.Get("Code.Tray.DeactivateNextDns"), null,
                    async (s, e) => await PerformActionAsync("deactivate"));
                _trayRefreshStatusMenuItem = new System.Windows.Forms.ToolStripMenuItem(LocalizationManager.Get("Code.Tray.RefreshStatus"), null,
                    async (s, e) => await CheckCurrentStatusAsync());
                _trayCheckUpdatesMenuItem = new System.Windows.Forms.ToolStripMenuItem(LocalizationManager.Get("Code.Tray.CheckUpdates"), null,
                    async (s, e) => await CheckForUpdatesAsync(true));
                contextMenu.Opening += TrayContextMenu_Opening;
                contextMenu.Items.Add(_trayStatusMenuItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(_trayEnableMenuItem);
                contextMenu.Items.Add(_trayDisableMenuItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(_trayRefreshStatusMenuItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                _trayOpenMenuItem = new System.Windows.Forms.ToolStripMenuItem(LocalizationManager.Get("Code.Tray.Open"), null, (s, e) => RestoreFromTray());
                contextMenu.Items.Add(_trayOpenMenuItem);
                contextMenu.Items.Add(_trayCheckUpdatesMenuItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                _trayExitMenuItem = new System.Windows.Forms.ToolStripMenuItem(LocalizationManager.Get("Code.Tray.Exit"), null, (s, e) => ExitApplication());
                contextMenu.Items.Add(_trayExitMenuItem);
                notifyIcon.ContextMenuStrip = contextMenu;
                notifyIcon.Visible = true;

                _notifyIcon = notifyIcon;
                _trayContextMenu = contextMenu;
                _trayIcon = trayIcon;
            }
            catch
            {
                try { if (notifyIcon != null) notifyIcon.Visible = false; } catch { }
                try { if (notifyIcon != null) notifyIcon.ContextMenuStrip = null; } catch { }
                try { notifyIcon?.Dispose(); } catch { }
                try { contextMenu?.Dispose(); } catch { }
                try { trayIcon?.Dispose(); } catch { }
                throw;
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            bool applicationShuttingDown = System.Windows.Application.Current?.Dispatcher.HasShutdownStarted == true;
            if (!_allowClose && !applicationShuttingDown)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (updateTimer != null) { updateTimer.Stop(); updateTimer.Tick -= UpdateTimer_Tick; }
            if (brokerStatusRetryTimer != null) { brokerStatusRetryTimer.Stop(); brokerStatusRetryTimer.Tick -= BrokerStatusRetryTimer_Tick; }
            if (_pauseStatusTimer != null) { _pauseStatusTimer.Stop(); _pauseStatusTimer.Tick -= PauseStatusTimer_Tick; _pauseStatusTimer = null; }
            try { updateDownloadCancellation?.Cancel(); } catch { }
            DetachThemeEvents();
            cmbProfiles.SelectionChanged -= CmbProfiles_SelectionChanged;
            DisposeTrayIcon();
            base.OnClosed(e);
        }

        private void CmbTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbTheme.SelectedItem is not ComboBoxItem selectedItem ||
                !Enum.TryParse(selectedItem.Tag?.ToString(), ignoreCase: true, out ThemePreference preference))
                return;

            if (preference != ThemeManager.Preference)
                ThemeManager.SetPreference(preference);
        }

        private void ThemeManager_ThemeChanged(object sender, EventArgs e)
        {
            SyncThemeSelection();
            UpdateUI();
        }

        private void SyncThemeSelection()
        {
            if (cmbTheme == null) return;

            string preference = ThemeManager.Preference.ToString();
            ComboBoxItem selectedItem = cmbTheme.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), preference, StringComparison.OrdinalIgnoreCase));
            if (selectedItem != null && cmbTheme.SelectedItem != selectedItem)
                cmbTheme.SelectedItem = selectedItem;
        }

        private void DetachThemeEvents()
        {
            cmbTheme.SelectionChanged -= CmbTheme_SelectionChanged;
            ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
            LocalizationManager.LanguageChanged -= LocalizationManager_LanguageChanged;
        }

        private void LocalizationManager_LanguageChanged(object sender, EventArgs e)
        {
            if (_demoCaptureMode || _trayDisposed) return;
            brokerStatusError = LocalizationManager.TranslateExistingText(brokerStatusError);
            RefreshLocalizedCodeMessages();
            RefreshLocalizedTrayText();
            RefreshApiLocalizedChoices();
            UpdateUI();
            RefreshTrayProfiles();
        }

        private void RefreshLocalizedTrayText()
        {
            if (_trayEnableMenuItem is not null) _trayEnableMenuItem.Text = LocalizationManager.Get("Code.Tray.EnableNextDns");
            if (_trayDisableMenuItem is not null) _trayDisableMenuItem.Text = needsDnsRestore
                ? LocalizationManager.Get("Code.Home.RestoreDns")
                : LocalizationManager.Get("Code.Tray.DeactivateNextDns");
            if (_trayRefreshStatusMenuItem is not null) _trayRefreshStatusMenuItem.Text = LocalizationManager.Get("Code.Tray.RefreshStatus");
            if (_trayCheckUpdatesMenuItem is not null) _trayCheckUpdatesMenuItem.Text = LocalizationManager.Get("Code.Tray.CheckUpdates");
            if (_trayOpenMenuItem is not null) _trayOpenMenuItem.Text = LocalizationManager.Get("Code.Tray.Open");
            if (_trayExitMenuItem is not null) _trayExitMenuItem.Text = LocalizationManager.Get("Code.Tray.Exit");
            if (_notifyIcon is not null) _notifyIcon.Text = "NextDNS Manager";
            RefreshPauseMenuText();
        }

        internal void RestoreFromTray()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RestoreFromTray));
                return;
            }

            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        }

        internal void AllowCloseForSessionEnding()
        {
            _allowClose = true;
        }

        private void NotifyIcon_DoubleClick(object sender, EventArgs e)
        {
            RestoreFromTray();
        }

        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Q && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                ExitApplication();
            }
        }

        private void ExitApplication()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ExitApplication));
                return;
            }

            _allowClose = true;
            Close();
        }

        private void DisposeTrayIcon()
        {
            if (_trayDisposed)
                return;

            _trayDisposed = true;
            System.Windows.Forms.NotifyIcon notifyIcon = _notifyIcon;
            System.Windows.Forms.ContextMenuStrip contextMenu = _trayContextMenu;
            System.Drawing.Icon trayIcon = _trayIcon;
            _notifyIcon = null;
            _trayContextMenu = null;
            _trayIcon = null;

            TryLogTrayCleanup(() => { if (notifyIcon != null) notifyIcon.Visible = false; });
            TryLogTrayCleanup(() => { if (notifyIcon != null) notifyIcon.DoubleClick -= NotifyIcon_DoubleClick; });
            TryLogTrayCleanup(() => { if (notifyIcon != null) notifyIcon.BalloonTipClicked -= NotifyIcon_BalloonTipClicked; });
            TryLogTrayCleanup(() => { if (notifyIcon != null) notifyIcon.ContextMenuStrip = null; });
            TryLogTrayCleanup(() => { if (contextMenu != null) contextMenu.Opening -= TrayContextMenu_Opening; });
            TryLogTrayCleanup(() => notifyIcon?.Dispose());
            TryLogTrayCleanup(() => contextMenu?.Dispose());
            TryLogTrayCleanup(() => trayIcon?.Dispose());
        }

        private static void TryLogTrayCleanup(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception exception)
            {
                DiagnosticLog.WriteManager(DiagnosticEvent.TrayCleanupFailure, exception);
            }
        }

        private void TrayContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            UpdateTrayMenuState();
            RefreshTrayProfiles();
        }

        private async void UpdateTimer_Tick(object sender, EventArgs e) => await CheckForUpdatesAsync(false);

        private async Task CheckForUpdatesAsync(bool manual)
        {
            if (updateCheckInProgress) return;
            updateCheckInProgress = true;
            try
            {
                UpdateInfo update = await updateChecker.CheckAsync();
                if (_trayDisposed) return;
                if (updateTimer != null) updateTimer.Interval = TimeSpan.FromHours(24);
                if (update == null)
                {
                    if (manual) System.Windows.MessageBox.Show(LocalizationManager.Get("Code.Update.NoNewVersion"), LocalizationManager.Get("Code.Update.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                availableUpdate = update;
                btnUpdate.Content = LocalizationManager.T("Code.Update.InstallVersion", update.Version);
                btnUpdate.Visibility = Visibility.Visible;
                string marker = Path.Combine(appDataFolder, "last-update-notified.txt");
                bool notified = false;
                try { notified = File.Exists(marker) && File.ReadAllText(marker).Trim() == update.Version; } catch (IOException) { }
                if (manual || !notified)
                {
                    _notifyIcon.BalloonTipTitle = LocalizationManager.Get("Code.Update.AvailableTitle");
                    _notifyIcon.BalloonTipText = LocalizationManager.T("Code.Update.AvailableNotification", update.Version);
                    _notifyIcon.ShowBalloonTip(8000);
                    try { File.WriteAllText(marker, update.Version); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                if (manual)
                {
                    RestoreFromTray();
                    System.Windows.MessageBox.Show(LocalizationManager.T("Code.Update.AvailableDetails", update.Version), LocalizationManager.Get("Code.Update.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                if (updateTimer != null) updateTimer.Interval = TimeSpan.FromMinutes(15);
                DiagnosticLog.WriteManager(DiagnosticEvent.UpdateCheckFailure, ex);
                if (manual && !_trayDisposed)
                    System.Windows.MessageBox.Show(LocalizationManager.T("Code.Update.CheckFailed", UiErrorLocalizer.Localize(ex)), LocalizationManager.Get("Code.Update.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                updateCheckInProgress = false;
                UpdateTrayMenuState();
            }
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e) => await DownloadAndInstallUpdateAsync();
        private void NotifyIcon_BalloonTipClicked(object sender, EventArgs e) { if (availableUpdate != null) RestoreFromTray(); }

        private async Task DownloadAndInstallUpdateAsync()
        {
            if (availableUpdate == null || updateInstallInProgress || _trayDisposed)
                return;

            UpdateInfo update = availableUpdate;
            updateInstallInProgress = true;
            updateDownloadCancellation = new CancellationTokenSource();
            btnUpdate.IsEnabled = false;
            btnUpdate.Content = LocalizationManager.T("Code.Update.DownloadingVersion", update.Version);
            btnCancelUpdate.IsEnabled = true;
            updateProgressPanel.Visibility = Visibility.Visible;
            updateProgressBar.Value = 0;
            UpdateTrayMenuState();

            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (_trayDisposed)
                    return;

                string stage = TranslateUpdateStage(value.Stage);
                updateProgressText.Text = value.TotalBytes > 0
                    ? LocalizationManager.T("Code.Update.ProgressWithSize", stage, FormatBytes(value.BytesReceived), FormatBytes(value.TotalBytes))
                    : stage;
                updateProgressBar.IsIndeterminate = value.TotalBytes <= 0;
                updateProgressBar.Value = value.TotalBytes > 0
                    ? Math.Clamp(value.BytesReceived * 100d / value.TotalBytes, 0d, 100d)
                    : 0;
            });

            try
            {
                using UpdateInstaller.VerifiedInstaller installer = await updateInstaller.DownloadAndVerifyAsync(
                    update,
                    progress,
                    updateDownloadCancellation.Token);

                if (_trayDisposed)
                    return;

                btnCancelUpdate.IsEnabled = false;
                updateProgressText.Text = LocalizationManager.Get("Code.Update.RecheckingRelease");
                installer.EnsureStillVerified();
                UpdateLaunchCoordinator.HandoffResult handoff = await UpdateLaunchCoordinator.HandOffToUnelevatedHelperAsync(installer);
                if (handoff == UpdateLaunchCoordinator.HandoffResult.Cancelled)
                {
                    updateProgressText.Text = LocalizationManager.Get("Code.Update.InstallationCancelled");
                    return;
                }
                if (handoff != UpdateLaunchCoordinator.HandoffResult.Ready)
                {
                    System.Windows.MessageBox.Show(
                        this,
                        LocalizationManager.Get("Code.Update.PreparationFailed"),
                        LocalizationManager.Get("Code.Update.NotStartedTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                ExitApplication();
            }
            catch (OperationCanceledException)
            {
                if (!_trayDisposed)
                    updateProgressText.Text = LocalizationManager.Get("Code.Update.DownloadCancelled");
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteManager(DiagnosticEvent.UpdateDownloadFailure, ex);
                if (!_trayDisposed)
                {
                    updateProgressText.Text = LocalizationManager.Get("Code.Update.VerificationFailed");
                    System.Windows.MessageBox.Show(
                        this,
                        LocalizationManager.Get("Code.Update.DownloadFailed"),
                        LocalizationManager.Get("Code.Update.Title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                updateDownloadCancellation?.Dispose();
                updateDownloadCancellation = null;
                updateInstallInProgress = false;
                if (!_trayDisposed)
                {
                    btnUpdate.IsEnabled = availableUpdate != null;
                    if (availableUpdate != null)
                        btnUpdate.Content = LocalizationManager.T("Code.Update.InstallVersion", availableUpdate.Version);
                    btnCancelUpdate.IsEnabled = false;
                    updateProgressPanel.Visibility = Visibility.Collapsed;
                    UpdateTrayMenuState();
                }
            }
        }

        private void BtnCancelUpdate_Click(object sender, RoutedEventArgs e) => updateDownloadCancellation?.Cancel();

        private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.0} MB"
            : $"{bytes / 1024d:0.0} KB";

        private static string TranslateUpdateStage(string stage) => stage switch
        {
            "Descargando SHA256SUMS…" => LocalizationManager.Get("Code.Update.StageChecksums"),
            "Descargando instalador…" => LocalizationManager.Get("Code.Update.StageInstaller"),
            "Descarga verificada." => LocalizationManager.Get("Code.Update.StageVerified"),
            _ => stage
        };

        private async void BtnAction_Click(object sender, RoutedEventArgs e) => await PerformActionAsync();

        private async Task PerformActionAsync(string requestedOperation = null)
        {
            if (brokerActionInProgress || profileSwitchInProgress || statusCheckInProgress) return;
            if (brokerStatusUncertain)
            {
                await CheckCurrentStatusAsync();
                return;
            }
            if (requestedOperation == "activate" && isProtected && !needsDnsRestore) return;
            if (requestedOperation == "deactivate" && !isProtected && !needsDnsRestore && !_pauseUntil.HasValue) return;
            btnAction.IsEnabled = false;
            brokerActionInProgress = true;
            UpdateUI();
            string operation = requestedOperation ?? (isProtected || needsDnsRestore ? "deactivate" : "activate");
            Task<ActivationPreflightReport> preflightTask = null;
            try
            {
                Profile selectedProfile = cmbProfiles.SelectedItem as Profile;
                string profileId = null;

                if (operation == "activate")
                {
                    if (selectedProfile == null)
                    {
                        System.Windows.MessageBox.Show(LocalizationManager.Get("Code.Profile.SelectRequired"), LocalizationManager.Get("Code.Profile.RequiredTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    profileId = selectedProfile.ID;
                    if (!IsValidProfileId(profileId))
                    {
                        System.Windows.MessageBox.Show(LocalizationManager.Get("Code.Profile.InvalidId"), LocalizationManager.Get("Code.Profile.InvalidTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                brokerStatusError = null;
                BrokerResponse response = await brokerClient.SendAsync(new BrokerRequest
                {
                    Version = BrokerProtocol.Version,
                    Operation = operation,
                    ProfileId = profileId,
                    ReportClientInfo = operation == "activate" && chkReportName.IsChecked == true,
                    SetupRouter = operation == "activate" && chkSetupRouter.IsChecked == true
                });

                if (response.Success)
                {
                    ApplyBrokerStatus(response);
                    if (operation == "activate" && (!response.Active || !string.Equals(response.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)))
                        throw UiErrorLocalizer.CreateActivationNotConfirmedException();
                }
                else
                {
                    // Preserve the broker's original response separately from the
                    // localized display message and allowlisted diagnostic code.
                    throw UiErrorLocalizer.CreateBrokerResponseException(response.Error);
                }
            }
            catch (Exception ex)
            {
                bool hasFailureCode = UiErrorLocalizer.TryGetBrokerFailureCode(ex, out BrokerFailureCode failureCode);
                if (ex is BrokerResponseException brokerResponse &&
                    brokerResponse.ActivationFailure.HasEnvelope && brokerResponse.ActivationFailure.MetadataIsKnown)
                {
                    DiagnosticLog.WriteManagerActivationFailure(
                        DiagnosticEvent.BrokerActionFailure,
                        brokerResponse.FailureCode,
                        brokerResponse.ActivationFailure.Stage,
                        brokerResponse.ActivationFailure.Category);
                }
                else if (hasFailureCode)
                    DiagnosticLog.WriteManagerBrokerFailure(DiagnosticEvent.BrokerActionFailure, failureCode);
                else
                    DiagnosticLog.WriteManager(DiagnosticEvent.BrokerActionFailure, ex);

                if (operation == "activate" && ex is BrokerResponseException responseFailure &&
                    responseFailure.FailureCode == BrokerFailureCode.BrokerReportedError &&
                    (!responseFailure.ActivationFailure.HasEnvelope ||
                     (responseFailure.ActivationFailure.MetadataIsKnown &&
                      responseFailure.ActivationFailure.Stage is not (
                          ActivationFailureStage.RequestValidation or ActivationFailureStage.DnsRestore))))
                    preflightTask = ActivationPreflightCollector.CaptureAsync();

                brokerStatusError = UiErrorLocalizer.Localize(ex);
                brokerStatusUncertain = true;
                brokerActionInProgress = false;
                await CheckCurrentStatusAsync();
                if (!_trayDisposed)
                {
                    string displayMessage = UiErrorLocalizer.Localize(ex);
                    ActivationPreflightCaptureResult captureResult = preflightTask is null
                        ? null
                        : await ActivationPreflightCapturePolicy.AwaitReportAsync(
                            preflightTask,
                            TimeSpan.FromSeconds(5));
                    if (captureResult?.Report is { } report)
                    {
                        string summary = ActivationPreflightFormatter.Format(report);
                        if (!string.IsNullOrWhiteSpace(summary))
                            displayMessage += Environment.NewLine + Environment.NewLine + summary;
                    }
                    else if (captureResult?.TimedOut == true)
                    {
                        displayMessage += Environment.NewLine + Environment.NewLine +
                            ActivationPreflightFormatter.FormatCaptureTimeout();
                    }

                    System.Windows.MessageBox.Show(displayMessage, LocalizationManager.Get("Code.Profile.OperationUnconfirmedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                brokerActionInProgress = false;
                UpdateUI();
                if (pendingProfileSelection != null && isProtected && !brokerStatusUncertain && !needsDnsRestore)
                    _ = ProcessPendingProfileSwitchesAsync();
            }
        }

        private void CmbProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressProfileSelectionChanged) return;

            Profile selected = cmbProfiles.SelectedItem as Profile;
            if (statusCheckInProgress)
            {
                SelectProfileById(activeProfileId);
                return;
            }
            if (brokerActionInProgress && !profileSwitchInProgress)
            {
                SelectProfileById(activeProfileId);
                return;
            }
            if (brokerStatusUncertain)
            {
                SelectProfileById(activeProfileId);
                return;
            }
            if (!isProtected || !hasBrokerStatus || needsDnsRestore || selected == null)
                return;

            string latestSelection = ProfileSwitchSelection.QueueLatest(
                selected.ID,
                activeProfileId,
                profileSwitchInProgress ? switchingToProfile?.ID : null);
            if (latestSelection == null)
            {
                pendingProfileSelection = null;
                return;
            }

            pendingProfileSelection = selected;
            if (!profileSwitchWorkerRunning && !brokerActionInProgress)
                _ = ProcessPendingProfileSwitchesAsync();
        }

        private async Task ProcessPendingProfileSwitchesAsync()
        {
            if (profileSwitchWorkerRunning) return;
            profileSwitchWorkerRunning = true;
            try
            {
                while (pendingProfileSelection != null && !_trayDisposed)
                {
                    Profile target = pendingProfileSelection;
                    pendingProfileSelection = null;
                    if (!isProtected || !hasBrokerStatus || brokerStatusUncertain || needsDnsRestore)
                        break;
                    if (!IsValidProfileId(target.ID))
                    {
                        System.Windows.MessageBox.Show(LocalizationManager.Get("Code.Profile.InvalidId"), LocalizationManager.Get("Code.Profile.InvalidTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        SelectProfileById(activeProfileId);
                        continue;
                    }
                    if (string.Equals(target.ID, activeProfileId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    switchingToProfile = target;
                    profileSwitchInProgress = true;
                    brokerActionInProgress = true;
                    btnAction.IsEnabled = false;
                    brokerStatusError = null;
                    UpdateUI();
                    Exception switchError = null;
                    try
                    {
                        BrokerResponse response = await brokerClient.SendAsync(new BrokerRequest
                        {
                            Version = BrokerProtocol.Version,
                            Operation = "activate",
                            ProfileId = target.ID,
                            ReportClientInfo = chkReportName.IsChecked == true,
                            SetupRouter = chkSetupRouter.IsChecked == true
                        });
                        if (!response.Success)
                            throw UiErrorLocalizer.CreateBrokerResponseException(response.Error);
                        ApplyBrokerStatus(response);
                        if (!response.Active || !string.Equals(response.ProfileId, target.ID, StringComparison.OrdinalIgnoreCase))
                            throw UiErrorLocalizer.CreateProfileSelectionNotConfirmedException();
                    }
                    catch (Exception ex)
                    {
                        if (UiErrorLocalizer.TryGetBrokerFailureCode(ex, out BrokerFailureCode failureCode))
                            DiagnosticLog.WriteManagerBrokerFailure(DiagnosticEvent.ProfileSwitchFailure, failureCode);
                        else
                            DiagnosticLog.WriteManager(DiagnosticEvent.ProfileSwitchFailure, ex);
                        switchError = ex;
                        brokerStatusError = UiErrorLocalizer.Localize(ex);
                        brokerStatusUncertain = true;
                    }
                    finally
                    {
                        brokerActionInProgress = false;
                        profileSwitchInProgress = false;
                        switchingToProfile = null;
                    }

                    if (switchError != null)
                    {
                        await CheckCurrentStatusAsync();
                        if (!brokerStatusUncertain && isProtected && string.Equals(activeProfileId, target.ID, StringComparison.OrdinalIgnoreCase))
                        {
                            // The request may have completed even if its reply was lost.
                            continue;
                        }
                        SelectProfileById(activeProfileId);
                        if (!_trayDisposed)
                            System.Windows.MessageBox.Show(UiErrorLocalizer.Localize(switchError), LocalizationManager.Get("Code.Profile.SwitchUnconfirmedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                        if (brokerStatusUncertain || !isProtected || needsDnsRestore)
                            pendingProfileSelection = null;
                    }

                    UpdateUI();
                }
            }
            finally
            {
                profileSwitchWorkerRunning = false;
                profileSwitchInProgress = false;
                brokerActionInProgress = false;
                switchingToProfile = null;
                btnAction.IsEnabled = hasBrokerStatus;
                UpdateUI();
                if (pendingProfileSelection != null && isProtected && !brokerStatusUncertain && !needsDnsRestore)
                    _ = ProcessPendingProfileSwitchesAsync();
            }
        }

        private async void BrokerStatusRetryTimer_Tick(object sender, EventArgs e)
        {
            if (!BrokerStatusRetryPolicy.ShouldRetry(brokerStatusUncertain, needsDnsRestore) ||
                brokerActionInProgress || profileSwitchInProgress) return;
            await CheckCurrentStatusAsync();
        }

        internal async Task CheckCurrentStatusAsync(bool enableActionWhenComplete = false)
        {
            if (statusCheckInProgress || brokerActionInProgress || profileSwitchInProgress) return;
            statusCheckInProgress = true;
            UpdateUI();
            try
            {
                BrokerResponse response = await brokerClient.SendAsync(new BrokerRequest
                {
                    Version = BrokerProtocol.Version,
                    Operation = "status"
                });

                if (response.Success)
                {
                    ApplyBrokerStatus(response);
                    brokerStatusUncertain = false;
                }
                else
                {
                    BrokerFailureCode failureCode = BrokerFailureCatalog.ClassifyResponseError(response.Error);
                    DiagnosticLog.WriteManagerBrokerFailure(DiagnosticEvent.BrokerStatusCheckFailure, failureCode);
                    DecodedActivationFailure decoded = ActivationFailureEnvelope.Decode(response.Error);
                    brokerStatusError = failureCode == BrokerFailureCode.BrokerReportedError
                        ? UiErrorLocalizer.Localize(decoded.MessageWithoutPrefix!)
                        : LocalizationManager.Get(BrokerFailureCatalog.GetLocalizationKey(failureCode));
                    brokerStatusUncertain = true;
                }
            }
            catch (Exception ex)
            {
                // A failed status request is not evidence that protection is off.
                // Preserve the last confirmed state; on first launch keep status unknown.
                if (UiErrorLocalizer.TryGetBrokerFailureCode(ex, out BrokerFailureCode failureCode))
                    DiagnosticLog.WriteManagerBrokerFailure(DiagnosticEvent.BrokerStatusCheckFailure, failureCode);
                else
                    DiagnosticLog.WriteManager(DiagnosticEvent.BrokerStatusCheckFailure, ex);
                brokerStatusError = UiErrorLocalizer.Localize(ex);
                brokerStatusUncertain = true;
            }
            finally { statusCheckInProgress = false; }
            if (brokerStatusUncertain)
                ScheduleBrokerStatusRetry();
            UpdateUI();
            if (enableActionWhenComplete)
                btnAction.IsEnabled = !brokerStatusUncertain;
        }

        private void ApplyBrokerStatus(BrokerResponse response)
        {
            isProtected = response.Active;
            _pauseUntil = response.State == "paused" ? response.PauseUntilUtc : null;
            needsDnsRestore = response.State == "attention";
            if (response.Active)
            {
                chkReportName.IsChecked = response.ReportClientInfo;
                chkSetupRouter.IsChecked = response.SetupRouter;
            }
            activeProfileId = response.ProfileId;
            hasBrokerStatus = true;
            brokerStatusError = null;
            brokerStatusUncertain = false;
            if (response.Active)
                SelectProfileById(activeProfileId);
            RefreshNetworkInformation();
            UpdateUI();
            UpdateBrokerStatusRetrySchedule();
        }

        private void UpdateBrokerStatusRetrySchedule()
        {
            if (BrokerStatusRetryPolicy.ShouldRetry(brokerStatusUncertain, needsDnsRestore))
            {
                ScheduleBrokerStatusRetry();
                return;
            }

            brokerStatusRetryPolicy.Reset();
            brokerStatusRetryTimer?.Stop();
            if (brokerStatusRetryTimer != null)
                brokerStatusRetryTimer.Interval = TimeSpan.FromSeconds(BrokerStatusRetryPolicy.InitialDelaySeconds);
        }

        private void ScheduleBrokerStatusRetry()
        {
            brokerStatusRetryTimer?.Stop();
            if (brokerStatusRetryTimer != null)
            {
                brokerStatusRetryTimer.Interval = brokerStatusRetryPolicy.RecordRetry();
                brokerStatusRetryTimer.Start();
            }
        }

        private void UpdateUI()
        {
            infoIndicator.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                hasBrokerStatus && isProtected && !brokerStatusUncertain && !needsDnsRestore && !_pauseUntil.HasValue
                    ? "StatusProtected" : "TextSecondary");
            UpdatePauseUi();
            UpdateTrayMenuState();
            cmbProfiles.IsEnabled = hasBrokerStatus && !brokerStatusUncertain && !_pauseUntil.HasValue &&
                !statusCheckInProgress && (!brokerActionInProgress || profileSwitchInProgress);
            btnAction.IsEnabled = !brokerStatusUncertain && !brokerActionInProgress && !profileSwitchInProgress && !statusCheckInProgress;
            if (needsDnsRestore)
            {
                lblStatus.Tag = "NeedsAttention";
                lblStatus.Text = LocalizationManager.Get("Code.Home.StatusNeedsAttention");
                lblStatus.SetResourceReference(TextBlock.ForegroundProperty, "StatusAttention");
                string priorProfile = string.IsNullOrWhiteSpace(activeProfileId)
                    ? string.Empty
                    : LocalizationManager.T("Code.Home.PreviousProfile", GetProfileDisplayName(activeProfileId));
                lblInfo.Text = LocalizationManager.Get("Code.Home.RecoveryNeeded") + priorProfile;
                btnAction.Content = LocalizationManager.Get("Code.Home.Deactivate");
                SetActionButtonTheme("ActionButtonDestructiveBackground", "ActionButtonDestructiveForeground");
                return;
            }
            if (!hasBrokerStatus)
            {
                lblStatus.Tag = null;
                lblStatus.Text = LocalizationManager.Get("Main.Ui.Home.UnknownStatus");
                lblStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                lblInfo.Text = string.IsNullOrWhiteSpace(brokerStatusError) ? LocalizationManager.Get("Main.Ui.Home.VerifyingService") : brokerStatusError;
                btnAction.Content = LocalizationManager.Get("Main.Ui.Home.Activate");
                SetActionButtonTheme("NextBlue", "ActionButtonForeground");
                return;
            }

            if (_pauseUntil.HasValue && !isProtected)
            {
                lblStatus.Tag = "Paused";
                lblStatus.Text = LocalizationManager.Get("Code.Home.StatusPaused");
                lblStatus.SetResourceReference(TextBlock.ForegroundProperty, "StatusAttention");
                lblInfo.Text = LocalizationManager.T("Code.Home.PausedUntil", _pauseUntil.Value.ToLocalTime().ToString("HH:mm", LocalizationManager.Instance.ActiveCulture));
                btnAction.Content = LocalizationManager.Get("Main.Ui.Home.Activate");
                SetActionButtonTheme("NextBlue", "ActionButtonForeground");
                return;
            }
            if (isProtected)
            {
                lblStatus.Tag = "Protected";
                lblStatus.Text = LocalizationManager.Get("Code.Home.StatusProtected");
                lblStatus.SetResourceReference(TextBlock.ForegroundProperty, "StatusProtected");
                if (profileSwitchInProgress)
                {
                    lblInfo.Text = LocalizationManager.T("Code.Home.SwitchingToProfile", switchingToProfile?.Name ?? LocalizationManager.Get("Code.Home.SelectedProfile"));
                    btnAction.Content = LocalizationManager.Get("Code.Home.ChangingProfile");
                    return;
                }
                string profileLabel = string.IsNullOrWhiteSpace(activeProfileId)
                    ? LocalizationManager.Get("Code.Home.ActiveProfileUnknown")
                    : LocalizationManager.Get("Main.Ui.Common.ActiveProfile") + " " + GetProfileDisplayName(activeProfileId);
                lblInfo.Text = string.IsNullOrWhiteSpace(brokerStatusError)
                    ? profileLabel
                    : profileLabel + Environment.NewLine + brokerStatusError;
                btnAction.Content = LocalizationManager.Get("Code.Home.Deactivate");
                SetActionButtonTheme("ActionButtonDestructiveBackground", "ActionButtonDestructiveForeground");
            }
            else
            {
                lblStatus.Tag = null;
                lblStatus.Text = LocalizationManager.Get("Code.Home.StatusUnprotected");
                lblStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                lblInfo.Text = string.IsNullOrWhiteSpace(brokerStatusError)
                    ? LocalizationManager.Get("Code.Home.ManagerInactive")
                    : LocalizationManager.Get("Code.Home.ManagerInactive") + Environment.NewLine + brokerStatusError;
                btnAction.Content = LocalizationManager.Get("Main.Ui.Home.Activate");
                SetActionButtonTheme("NextBlue", "ActionButtonForeground");
            }
        }

        private void SetActionButtonTheme(string backgroundResource, string foregroundResource)
        {
            btnAction.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, backgroundResource);
            btnAction.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, foregroundResource);
        }

        private void UpdateTrayMenuState()
        {
            if (_trayDisposed || _trayStatusMenuItem == null) return;

            string confirmedState = !hasBrokerStatus
                ? LocalizationManager.Get("Code.Tray.StateUnknown")
                : needsDnsRestore
                    ? LocalizationManager.Get("Code.Tray.StateNeedsDnsRestore")
                    : _pauseUntil.HasValue
                        ? LocalizationManager.T("Code.Tray.StatePausedUntil", _pauseUntil.Value.ToLocalTime().ToString("HH:mm", LocalizationManager.Instance.ActiveCulture))
                    : isProtected
                        ? LocalizationManager.T("Code.Tray.StateActive", GetProfileDisplayName(activeProfileId ?? string.Empty))
                        : LocalizationManager.Get("Code.Tray.StateInactive");
            if (brokerStatusUncertain)
                _trayStatusMenuItem.Text = hasBrokerStatus
                    ? LocalizationManager.T("Code.Tray.UnconfirmedWithLast", confirmedState)
                    : LocalizationManager.Get("Code.Tray.Unconfirmed");
            else if (statusCheckInProgress)
                _trayStatusMenuItem.Text = LocalizationManager.T("Code.Tray.CheckingWithLast", confirmedState);
            else if (profileSwitchInProgress)
                _trayStatusMenuItem.Text = LocalizationManager.T("Code.Tray.SwitchingToProfile", switchingToProfile?.Name ?? LocalizationManager.Get("Code.Home.SelectedProfile"));
            else if (brokerActionInProgress)
                _trayStatusMenuItem.Text = LocalizationManager.Get("Code.Tray.OperationInProgress");
            else
                _trayStatusMenuItem.Text = LocalizationManager.T("Code.Tray.State", confirmedState);

            bool canRunAction = hasBrokerStatus && !brokerStatusUncertain &&
                !brokerActionInProgress && !profileSwitchInProgress && !statusCheckInProgress;
            if (_trayEnableMenuItem != null)
                _trayEnableMenuItem.Enabled = canRunAction && !isProtected && !needsDnsRestore;
            if (_trayDisableMenuItem != null)
            {
                _trayDisableMenuItem.Text = needsDnsRestore ? LocalizationManager.Get("Code.Home.RestoreDns") : LocalizationManager.Get("Code.Tray.DeactivateNextDns");
                _trayDisableMenuItem.Enabled = canRunAction && (isProtected || needsDnsRestore || _pauseUntil.HasValue);
            }
            if (_trayRefreshStatusMenuItem != null)
                _trayRefreshStatusMenuItem.Enabled = !brokerActionInProgress && !profileSwitchInProgress && !statusCheckInProgress;
            if (_trayCheckUpdatesMenuItem != null)
                _trayCheckUpdatesMenuItem.Enabled = !updateCheckInProgress && !updateInstallInProgress;
        }

        private string GetProfileDisplayName(string profileId)
        {
            return profiles.FirstOrDefault(profile =>
                string.Equals(profile.ID, profileId, StringComparison.OrdinalIgnoreCase))?.Name ?? profileId;
        }

        private void BtnDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            try { new DiagnosticsWindow { Owner = this }.ShowDialog(); }
            catch (Exception exception)
            {
                DiagnosticLog.WriteManager(DiagnosticEvent.DiagnosticsViewFailure, exception);
                System.Windows.MessageBox.Show(LocalizationManager.Get("Code.Diagnostics.OpenFailed"), "NextDNS Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ManageProfiles_Click(object sender, RoutedEventArgs e)
        {
            mainTabs.SelectedIndex = 3;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                expLocalProfiles.IsExpanded = true;
                expLocalProfiles.BringIntoView();
                txtName.Focus();
            }), DispatcherPriority.Loaded);
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            string name = txtName.Text.Trim();
            string id = txtID.Text.Trim();
            if (name.Length == 0)
            {
                SetProfileFormMessage(LocalizationManager.Get("Code.Profile.EnterName"), "StatusAttention");
                txtName.Focus();
                return;
            }

            if (!IsValidProfileId(id))
            {
                SetProfileFormMessage(LocalizationManager.Get("Code.Profile.InvalidIdWithHexRange"), "StatusAttention");
                txtID.Focus();
                return;
            }

            if (profiles.Any(profile => string.Equals(profile.ID?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
            {
                SetProfileFormMessage(LocalizationManager.Get("Code.Profile.DuplicateId"), "StatusAttention");
                txtID.Focus();
                return;
            }

            if (profiles.Any(profile => string.Equals(profile.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                SetProfileFormMessage(LocalizationManager.Get("Code.Profile.DuplicateName"), "StatusAttention");
                txtName.Focus();
                return;
            }

            string previousSelectionId = (cmbProfiles.SelectedItem as Profile)?.ID;
            if (string.IsNullOrWhiteSpace(previousSelectionId) && isProtected)
                previousSelectionId = activeProfileId;
            List<Profile> updatedProfiles = profiles.ToList();
            updatedProfiles.Add(new Profile { Name = name, ID = id });
            if (!TrySaveProfiles(updatedProfiles)) return;

            profiles = updatedProfiles;
            RefreshCombo();
            suppressProfileSelectionChanged = true;
            try
            {
                cmbProfiles.SelectedItem = previousSelectionId is null
                    ? null
                    : profiles.FirstOrDefault(profile => string.Equals(profile.ID, previousSelectionId, StringComparison.OrdinalIgnoreCase));
            }
            finally { suppressProfileSelectionChanged = false; }

            txtName.Clear();
            txtID.Clear();
            SetProfileFormMessage(LocalizationManager.Get("Code.Profile.Added"), "StatusProtected");
        }

        private void BtnDel_Click(object sender, RoutedEventArgs e)
        {
            if (cmbProfiles.SelectedItem is not Profile selected) return;

            List<Profile> updatedProfiles = profiles
                .Where(profile => !ReferenceEquals(profile, selected))
                .ToList();
            if (updatedProfiles.Count == profiles.Count) return;
            if (!TrySaveProfiles(updatedProfiles)) return;

            profiles = updatedProfiles;
            RefreshCombo();
            SetProfileFormMessage(LocalizationManager.Get("Code.Profile.Deleted"), "TextSecondary");
        }

        private void LoadProfiles()
        {
            if (File.Exists(profilesFile))
            {
                try { profiles = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(profilesFile)) ?? new List<Profile>(); }
                catch (Exception exception)
                {
                    DiagnosticLog.WriteManager(DiagnosticEvent.ProfilesLoadFailure, exception);
                    profiles = new List<Profile>();
                }
            }
            RefreshCombo();
        }

        private bool TrySaveProfiles(List<Profile> profilesToSave)
        {
            string temporaryPath = profilesFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string json = JsonSerializer.Serialize(profilesToSave);
                File.WriteAllText(temporaryPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporaryPath, profilesFile, overwrite: true);
                return true;
            }
            catch (Exception exception)
            {
                DiagnosticLog.WriteManager(DiagnosticEvent.ProfilesSaveFailure, exception);
                SetProfileFormMessage(LocalizationManager.Get("Code.Profile.SaveFailed"), "StatusAttention");
                System.Windows.MessageBox.Show(
                    this,
                    LocalizationManager.T("Code.Profile.SaveFailedDetails", UiErrorLocalizer.Localize(exception)),
                    LocalizationManager.Get("Code.Profile.SaveFailedTitle"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private void SetProfileFormMessage(string message, string foregroundResource)
        {
            lblProfileMessage.Text = message;
            lblProfileMessage.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
        }

        private void RefreshCombo()
        {
            Profile previousSelection = cmbProfiles.SelectedItem as Profile;
            suppressProfileSelectionChanged = true;
            try
            {
                cmbProfiles.ItemsSource = null;
                cmbProfiles.ItemsSource = profiles;
                if (isProtected)
                    cmbProfiles.SelectedItem = profiles.FirstOrDefault(profile => string.Equals(profile.ID, activeProfileId, StringComparison.OrdinalIgnoreCase));
                else
                    cmbProfiles.SelectedItem = profiles.FirstOrDefault(profile => previousSelection != null && string.Equals(profile.ID, previousSelection.ID, StringComparison.OrdinalIgnoreCase))
                        ?? profiles.FirstOrDefault();
            }
            finally { suppressProfileSelectionChanged = false; }
        }

        private void SelectProfileById(string profileId, bool clearIfMissing = true)
        {
            if (string.IsNullOrWhiteSpace(profileId) && !clearIfMissing) return;
            Profile profile = profiles.FirstOrDefault(candidate => string.Equals(candidate.ID, profileId, StringComparison.OrdinalIgnoreCase));
            suppressProfileSelectionChanged = true;
            try { cmbProfiles.SelectedItem = profile; }
            finally { suppressProfileSelectionChanged = false; }
        }

        private static bool IsValidProfileId(string profileId) =>
            !string.IsNullOrWhiteSpace(profileId) && Regex.IsMatch(profileId, @"\A[0-9a-fA-F]{6}\z", RegexOptions.CultureInvariant);

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) { this.WindowState = WindowState.Minimized; }
        private void BtnClose_Click(object sender, RoutedEventArgs e) { this.Close(); }
    }
}
