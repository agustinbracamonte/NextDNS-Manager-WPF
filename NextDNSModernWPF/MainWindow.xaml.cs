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
        private System.Drawing.Icon _trayIcon;
        private bool _allowClose;
        private bool _trayDisposed;

        public MainWindow()
        {
            ThemeManager.Initialize(System.Windows.Application.Current);
            InitializeComponent();
            InitializeNetworkInformation();
            cmbTheme.SelectionChanged += CmbTheme_SelectionChanged;
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
            SyncThemeSelection();
            lblInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            cmbProfiles.SelectionChanged += CmbProfiles_SelectionChanged;
            brokerStatusRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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
                _trayEnableMenuItem = new System.Windows.Forms.ToolStripMenuItem("Activar NextDNS", null,
                    async (s, e) => await PerformActionAsync("activate"));
                _trayDisableMenuItem = new System.Windows.Forms.ToolStripMenuItem("Desactivar NextDNS", null,
                    async (s, e) => await PerformActionAsync("deactivate"));
                _trayRefreshStatusMenuItem = new System.Windows.Forms.ToolStripMenuItem("Actualizar estado", null,
                    async (s, e) => await CheckCurrentStatusAsync());
                _trayCheckUpdatesMenuItem = new System.Windows.Forms.ToolStripMenuItem("Buscar actualizaciones", null,
                    async (s, e) => await CheckForUpdatesAsync(true));
                contextMenu.Opening += TrayContextMenu_Opening;
                contextMenu.Items.Add(_trayStatusMenuItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(_trayEnableMenuItem);
                contextMenu.Items.Add(_trayDisableMenuItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add(_trayRefreshStatusMenuItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add("Abrir", null, (s, e) => RestoreFromTray());
                contextMenu.Items.Add(_trayCheckUpdatesMenuItem);
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add("Salir (Ctrl+Q)", null, (s, e) => ExitApplication());
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
                if (update == null)
                {
                    if (manual) System.Windows.MessageBox.Show("No hay una versión más reciente publicada.", "Actualizaciones", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                availableUpdate = update;
                btnUpdate.Content = $"Descargar e instalar la versión {update.Version}";
                btnUpdate.Visibility = Visibility.Visible;
                string marker = Path.Combine(appDataFolder, "last-update-notified.txt");
                bool notified = false;
                try { notified = File.Exists(marker) && File.ReadAllText(marker).Trim() == update.Version; } catch (IOException) { }
                if (manual || !notified)
                {
                    _notifyIcon.BalloonTipTitle = "Actualización disponible";
                    _notifyIcon.BalloonTipText = $"NextDNS Manager {update.Version} ya está disponible. Haz clic para abrir la ventana y descargarla.";
                    _notifyIcon.ShowBalloonTip(8000);
                    try { File.WriteAllText(marker, update.Version); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                if (manual)
                {
                    RestoreFromTray();
                    System.Windows.MessageBox.Show($"La versión {update.Version} está disponible. Usa el botón de actualización en la ventana para descargar y verificar el instalador.", "Actualizaciones", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteManager(DiagnosticEvent.UpdateCheckFailure, ex);
                if (manual && !_trayDisposed)
                    System.Windows.MessageBox.Show("No se pudo buscar actualizaciones. Comprueba tu conexión e inténtalo de nuevo.\n" + ex.Message, "Actualizaciones", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            btnUpdate.Content = $"Descargando {update.Version}…";
            btnCancelUpdate.IsEnabled = true;
            updateProgressPanel.Visibility = Visibility.Visible;
            updateProgressBar.Value = 0;
            UpdateTrayMenuState();

            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                if (_trayDisposed)
                    return;

                updateProgressText.Text = value.TotalBytes > 0
                    ? $"{value.Stage} {FormatBytes(value.BytesReceived)} de {FormatBytes(value.TotalBytes)}"
                    : value.Stage;
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
                updateProgressText.Text = "Volviendo a comprobar la publicación…";
                installer.EnsureStillVerified();
                UpdateLaunchCoordinator.HandoffResult handoff = await UpdateLaunchCoordinator.HandOffToUnelevatedHelperAsync(installer);
                if (handoff == UpdateLaunchCoordinator.HandoffResult.Cancelled)
                {
                    updateProgressText.Text = "Instalación cancelada.";
                    return;
                }
                if (handoff != UpdateLaunchCoordinator.HandoffResult.Ready)
                {
                    System.Windows.MessageBox.Show(
                        this,
                        "No se pudo preparar o volver a verificar el instalador. No se ejecutó ningún archivo.",
                        "Actualización no iniciada",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                ExitApplication();
            }
            catch (OperationCanceledException)
            {
                if (!_trayDisposed)
                    updateProgressText.Text = "Descarga cancelada.";
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteManager(DiagnosticEvent.UpdateDownloadFailure, ex);
                if (!_trayDisposed)
                {
                    updateProgressText.Text = "No se pudo verificar la descarga.";
                    System.Windows.MessageBox.Show(
                        this,
                        "No se pudo descargar o verificar el instalador. No se ejecutó ningún archivo. Comprueba tu conexión e inténtalo de nuevo.",
                        "Actualizaciones",
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
                        btnUpdate.Content = $"Descargar e instalar la versión {availableUpdate.Version}";
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
            try
            {
                string operation = requestedOperation ?? (isProtected || needsDnsRestore ? "deactivate" : "activate");
                Profile selectedProfile = cmbProfiles.SelectedItem as Profile;
                string profileId = null;

                if (operation == "activate")
                {
                    if (selectedProfile == null)
                    {
                        System.Windows.MessageBox.Show("Selecciona un perfil.", "Perfil requerido", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    profileId = selectedProfile.ID;
                    if (!IsValidProfileId(profileId))
                    {
                        System.Windows.MessageBox.Show("El ID del perfil debe tener exactamente 6 caracteres hexadecimales.", "Perfil inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                        throw new InvalidOperationException("El broker no confirmó el perfil solicitado.");
                }
                else
                {
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Error) ? "El broker no pudo completar la operación." : response.Error);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteManager(DiagnosticEvent.BrokerActionFailure, ex);
                brokerStatusError = ex.Message;
                brokerStatusUncertain = true;
                brokerActionInProgress = false;
                await CheckCurrentStatusAsync();
                if (!_trayDisposed)
                    System.Windows.MessageBox.Show(ex.Message, "No se confirmó la operación", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                        System.Windows.MessageBox.Show("El ID del perfil debe tener exactamente 6 caracteres hexadecimales.", "Perfil inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                            throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Error) ? "El broker no pudo cambiar de perfil." : response.Error);
                        ApplyBrokerStatus(response);
                        if (!response.Active || !string.Equals(response.ProfileId, target.ID, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("El broker no confirmó el perfil seleccionado.");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.WriteManager(DiagnosticEvent.ProfileSwitchFailure, ex);
                        switchError = ex;
                        brokerStatusError = ex.Message;
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
                            System.Windows.MessageBox.Show(switchError.Message, "No se confirmó el cambio de perfil", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            if (!brokerStatusUncertain || brokerActionInProgress || profileSwitchInProgress) return;
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
                    brokerStatusRetryTimer?.Stop();
                }
                else
                {
                    brokerStatusError = string.IsNullOrWhiteSpace(response.Error) ? "No se pudo consultar el estado del broker." : response.Error;
                    brokerStatusUncertain = true;
                }
            }
            catch (Exception ex)
            {
                // A failed status request is not evidence that protection is off.
                // Preserve the last confirmed state; on first launch keep status unknown.
                DiagnosticLog.WriteManager(DiagnosticEvent.BrokerStatusCheckFailure, ex);
                brokerStatusError = ex.Message;
                brokerStatusUncertain = true;
            }
            finally { statusCheckInProgress = false; }
            if (brokerStatusUncertain)
                brokerStatusRetryTimer?.Start();
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
        }

        private void UpdateUI()
        {
            UpdatePauseUi();
            UpdateTrayMenuState();
            cmbProfiles.IsEnabled = hasBrokerStatus && !brokerStatusUncertain && !_pauseUntil.HasValue &&
                !statusCheckInProgress && (!brokerActionInProgress || profileSwitchInProgress);
            btnAction.IsEnabled = !brokerStatusUncertain && !brokerActionInProgress && !profileSwitchInProgress && !statusCheckInProgress;
            if (needsDnsRestore)
            {
                lblStatus.Text = "REQUIERE ATENCIÓN";
                lblStatus.SetResourceReference(TextBlock.ForegroundProperty, "StatusAttention");
                string priorProfile = string.IsNullOrWhiteSpace(activeProfileId)
                    ? string.Empty
                    : $" Perfil anterior: {GetProfileDisplayName(activeProfileId)}.";
                lblInfo.Text = "La protección requiere recuperación o hay una configuración DNS pendiente." + priorProfile;
                btnAction.Content = "RESTABLECER DNS";
                SetActionButtonTheme("NextBlue", "ActionButtonForeground");
                return;
            }
            if (!hasBrokerStatus)
            {
                lblStatus.Text = "ESTADO DESCONOCIDO";
                lblStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                lblInfo.Text = string.IsNullOrWhiteSpace(brokerStatusError) ? "Verificando servicio..." : brokerStatusError;
                btnAction.Content = "ACTIVAR";
                SetActionButtonTheme("NextBlue", "ActionButtonForeground");
                return;
            }

            if (_pauseUntil.HasValue && !isProtected)
            {
                lblStatus.Text = "PAUSADO";
                lblStatus.SetResourceReference(TextBlock.ForegroundProperty, "StatusAttention");
                lblInfo.Text = $"La protección se reanudará después de las {_pauseUntil.Value.ToLocalTime():HH:mm}. Reanúdala para cambiar de perfil.";
                btnAction.Content = "ACTIVAR";
                SetActionButtonTheme("NextBlue", "ActionButtonForeground");
                return;
            }
            if (isProtected)
            {
                lblStatus.Text = "PROTEGIDO";
                lblStatus.SetResourceReference(TextBlock.ForegroundProperty, "StatusProtected");
                if (profileSwitchInProgress)
                {
                    lblInfo.Text = $"Cambiando al perfil: {switchingToProfile?.Name ?? "seleccionado"}...";
                    btnAction.Content = "CAMBIANDO PERFIL";
                    return;
                }
                string profileLabel = string.IsNullOrWhiteSpace(activeProfileId)
                    ? "Perfil activo: no informado"
                    : $"Perfil activo: {GetProfileDisplayName(activeProfileId)}";
                lblInfo.Text = string.IsNullOrWhiteSpace(brokerStatusError)
                    ? profileLabel
                    : profileLabel + Environment.NewLine + brokerStatusError;
                btnAction.Content = "DESACTIVAR";
                SetActionButtonTheme("ActionButtonDestructiveBackground", "ActionButtonDestructiveForeground");
            }
            else
            {
                lblStatus.Text = "DESPROTEGIDO";
                lblStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                lblInfo.Text = string.IsNullOrWhiteSpace(brokerStatusError)
                    ? "NextDNS Manager no está activo"
                    : "NextDNS Manager no está activo" + Environment.NewLine + brokerStatusError;
                btnAction.Content = "ACTIVAR";
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
                ? "desconocido"
                : needsDnsRestore
                    ? "requiere restauración DNS"
                    : _pauseUntil.HasValue
                        ? $"pausado hasta {_pauseUntil.Value.ToLocalTime():HH:mm}"
                    : isProtected
                        ? $"activo · {GetProfileDisplayName(activeProfileId ?? string.Empty)}"
                        : "inactivo";
            if (brokerStatusUncertain)
                _trayStatusMenuItem.Text = hasBrokerStatus
                    ? $"Estado sin confirmar (último: {confirmedState})"
                    : "Estado sin confirmar";
            else if (statusCheckInProgress)
                _trayStatusMenuItem.Text = $"Consultando estado (último: {confirmedState})";
            else if (profileSwitchInProgress)
                _trayStatusMenuItem.Text = $"Cambiando perfil a {switchingToProfile?.Name ?? "seleccionado"}…";
            else if (brokerActionInProgress)
                _trayStatusMenuItem.Text = "Operación de NextDNS en curso…";
            else
                _trayStatusMenuItem.Text = $"Estado: {confirmedState}";

            bool canRunAction = hasBrokerStatus && !brokerStatusUncertain &&
                !brokerActionInProgress && !profileSwitchInProgress && !statusCheckInProgress;
            if (_trayEnableMenuItem != null)
                _trayEnableMenuItem.Enabled = canRunAction && !isProtected && !needsDnsRestore;
            if (_trayDisableMenuItem != null)
            {
                _trayDisableMenuItem.Text = needsDnsRestore ? "Restablecer DNS" : "Desactivar NextDNS";
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
                System.Windows.MessageBox.Show("No se pudo abrir el panel de diagnóstico.", "NextDNS Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                SetProfileFormMessage("Escribe un nombre para el perfil.", "StatusAttention");
                txtName.Focus();
                return;
            }

            if (!IsValidProfileId(id))
            {
                SetProfileFormMessage("El ID debe tener exactamente 6 caracteres hexadecimales (0–9, A–F).", "StatusAttention");
                txtID.Focus();
                return;
            }

            if (profiles.Any(profile => string.Equals(profile.ID?.Trim(), id, StringComparison.OrdinalIgnoreCase)))
            {
                SetProfileFormMessage("Ya existe un perfil con ese ID.", "StatusAttention");
                txtID.Focus();
                return;
            }

            if (profiles.Any(profile => string.Equals(profile.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                SetProfileFormMessage("Ya existe un perfil con ese nombre.", "StatusAttention");
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
            SetProfileFormMessage("Perfil agregado. El perfil activo no cambió.", "StatusProtected");
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
            SetProfileFormMessage("Perfil eliminado.", "TextSecondary");
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
                SetProfileFormMessage("No se pudo guardar. El perfil no se agregó ni se cambió en la lista.", "StatusAttention");
                System.Windows.MessageBox.Show(
                    this,
                    "No se guardaron los cambios de perfiles. La lista y el perfil activo no cambiaron.\n\n" + exception.Message,
                    "No se guardó el perfil",
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
