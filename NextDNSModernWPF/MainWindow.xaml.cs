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
        private DispatcherTimer updateTimer;
        private DispatcherTimer brokerStatusRetryTimer;
        private bool updateCheckInProgress;
        private UpdateInfo availableUpdate;
        private System.Windows.Forms.NotifyIcon _notifyIcon;
        private System.Windows.Forms.ContextMenuStrip _trayContextMenu;
        private System.Drawing.Icon _trayIcon;
        private bool _allowClose;
        private bool _trayDisposed;

        public MainWindow()
        {
            InitializeComponent();
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

                LoadProfiles();
                btnAction.IsEnabled = false;
                _ = CheckCurrentStatusAsync(enableActionWhenComplete: true);
                updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
                updateTimer.Tick += UpdateTimer_Tick;
                updateTimer.Start();
                _ = CheckForUpdatesAsync(false);
            }
            catch
            {
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

                contextMenu.Items.Add("Abrir", null, (s, e) => RestoreFromTray());
                contextMenu.Items.Add("Buscar actualizaciones", null, async (s, e) => await CheckForUpdatesAsync(true));
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
            cmbProfiles.SelectionChanged -= CmbProfiles_SelectionChanged;
            DisposeTrayIcon();
            base.OnClosed(e);
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

            try { if (notifyIcon != null) notifyIcon.Visible = false; } catch { }
            try { if (notifyIcon != null) notifyIcon.DoubleClick -= NotifyIcon_DoubleClick; } catch { }
            try { if (notifyIcon != null) notifyIcon.BalloonTipClicked -= NotifyIcon_BalloonTipClicked; } catch { }
            try { if (notifyIcon != null) notifyIcon.ContextMenuStrip = null; } catch { }
            try { notifyIcon?.Dispose(); } catch { }
            try { contextMenu?.Dispose(); } catch { }
            try { trayIcon?.Dispose(); } catch { }
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
                btnUpdate.Content = $"Disponible la versión {update.Version}";
                btnUpdate.Visibility = Visibility.Visible;
                string marker = Path.Combine(appDataFolder, "last-update-notified.txt");
                bool notified = false;
                try { notified = File.Exists(marker) && File.ReadAllText(marker).Trim() == update.Version; } catch (IOException) { }
                if (manual || !notified)
                {
                    _notifyIcon.BalloonTipTitle = "Actualización disponible";
                    _notifyIcon.BalloonTipText = $"NextDNS Manager {update.Version} ya está disponible. Haz clic para abrir GitHub.";
                    _notifyIcon.ShowBalloonTip(8000);
                    try { File.WriteAllText(marker, update.Version); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                if (manual)
                    System.Windows.MessageBox.Show($"La versión {update.Version} está disponible. Selecciona el aviso en la ventana para abrir las descargas.", "Actualizaciones", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (manual && !_trayDisposed)
                    System.Windows.MessageBox.Show("No se pudo buscar actualizaciones. Comprueba tu conexión e inténtalo de nuevo.\n" + ex.Message, "Actualizaciones", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { updateCheckInProgress = false; }
        }

        private void BtnUpdate_Click(object sender, RoutedEventArgs e) => OpenReleases();
        private void NotifyIcon_BalloonTipClicked(object sender, EventArgs e) { if (availableUpdate != null) OpenReleases(); }

        private void OpenReleases()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("No se pudo abrir la página de versiones. " + ex.Message, "Actualizaciones", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            if (brokerActionInProgress || profileSwitchInProgress) return;
            btnAction.IsEnabled = false;
            brokerActionInProgress = true;
            try
            {
                string operation = isProtected || needsDnsRestore ? "deactivate" : "activate";
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
                btnAction.IsEnabled = true;
                UpdateUI();
            }
        }

        private void CmbProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (suppressProfileSelectionChanged) return;

            Profile selected = cmbProfiles.SelectedItem as Profile;
            if (brokerStatusUncertain)
            {
                SelectProfileById(activeProfileId);
                return;
            }
            if (!isProtected || !hasBrokerStatus || needsDnsRestore || selected == null)
                return;
            if (string.Equals(selected.ID, activeProfileId, StringComparison.OrdinalIgnoreCase))
                return;

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
                brokerStatusError = ex.Message;
                brokerStatusUncertain = true;
            }
            finally { statusCheckInProgress = false; }
            if (brokerStatusUncertain)
                brokerStatusRetryTimer?.Start();
            UpdateUI();
            if (enableActionWhenComplete)
                btnAction.IsEnabled = true;
        }

        private void ApplyBrokerStatus(BrokerResponse response)
        {
            isProtected = response.Active;
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
            UpdateUI();
        }

        private void UpdateUI()
        {
            cmbProfiles.IsEnabled = !brokerActionInProgress || profileSwitchInProgress;
            if (needsDnsRestore)
            {
                lblStatus.Text = "REQUIERE ATENCIÓN";
                lblStatus.Foreground = new SolidColorBrush(System.Windows.Media.Colors.Orange);
                string priorProfile = string.IsNullOrWhiteSpace(activeProfileId)
                    ? string.Empty
                    : $" Perfil anterior: {GetProfileDisplayName(activeProfileId)}.";
                lblInfo.Text = "La protección requiere recuperación o hay una configuración DNS pendiente." + priorProfile;
                btnAction.Content = "RESTABLECER DNS";
                return;
            }
            if (!hasBrokerStatus)
            {
                lblStatus.Text = "ESTADO DESCONOCIDO";
                lblStatus.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A0A0A0"));
                lblInfo.Text = string.IsNullOrWhiteSpace(brokerStatusError) ? "Verificando servicio..." : brokerStatusError;
                btnAction.Content = "ACTIVAR";
                btnAction.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2F80ED"));
                btnAction.Foreground = System.Windows.Media.Brushes.White;
                return;
            }

            if (isProtected)
            {
                lblStatus.Text = "PROTEGIDO";
                lblStatus.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#40A745"));
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
                btnAction.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E"));
                btnAction.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D32F2F"));
            }
            else
            {
                lblStatus.Text = "DESPROTEGIDO";
                lblStatus.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A0A0A0"));
                lblInfo.Text = string.IsNullOrWhiteSpace(brokerStatusError)
                    ? "NextDNS Manager no está activo"
                    : "NextDNS Manager no está activo" + Environment.NewLine + brokerStatusError;
                btnAction.Content = "ACTIVAR";
                btnAction.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2F80ED"));
                btnAction.Foreground = System.Windows.Media.Brushes.White;
            }
        }

        private string GetProfileDisplayName(string profileId)
        {
            return profiles.FirstOrDefault(profile =>
                string.Equals(profile.ID, profileId, StringComparison.OrdinalIgnoreCase))?.Name ?? profileId;
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(txtName.Text) && !string.IsNullOrWhiteSpace(txtID.Text))
            {
                Profile profile = new Profile { Name = txtName.Text.Trim(), ID = txtID.Text.Trim() };
                profiles.Add(profile);
                SaveProfiles();
                RefreshCombo();
                cmbProfiles.SelectedItem = profile;
                txtName.Clear(); txtID.Clear();
            }
        }

        private void BtnDel_Click(object sender, RoutedEventArgs e)
        {
            if (cmbProfiles.SelectedItem != null)
            {
                Profile selected = cmbProfiles.SelectedItem as Profile;
                profiles.Remove(selected);
                SaveProfiles();
                RefreshCombo();
            }
        }

        private void LoadProfiles()
        {
            if (File.Exists(profilesFile))
            {
                try { profiles = JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(profilesFile)) ?? new List<Profile>(); }
                catch { profiles = new List<Profile>(); }
            }
            RefreshCombo();
        }

        private void SaveProfiles() { string json = JsonSerializer.Serialize(profiles); File.WriteAllText(profilesFile, json); }

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
