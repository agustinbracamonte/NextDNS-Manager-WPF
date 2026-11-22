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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NextDNSManager.Shared;

namespace NextDNSModernWPF
{
    public class Profile { public string Name { get; set; } public string ID { get; set; } }

    public partial class MainWindow : Window
    {
        private List<Profile> profiles = new List<Profile>();

        // CORRECCIÓN 1: Guardar configuración en %AppData% para que no ensucie la carpeta del programa
        private string appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextDNSManager");
        private string profilesFile;

        private bool isProtected = false;
        private bool hasBrokerStatus;
        private bool needsDnsRestore;
        private string activeProfileId;
        private string brokerStatusError;
        private readonly BrokerClient brokerClient = new BrokerClient();
        private readonly UpdateChecker updateChecker = new UpdateChecker();
        private DispatcherTimer updateTimer;
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
            btnAction.IsEnabled = false;
            try
            {
                string operation = isProtected || needsDnsRestore ? "deactivate" : "activate";
                string profileId = null;

                if (operation == "activate")
                {
                    if (cmbProfiles.SelectedItem == null)
                    {
                        System.Windows.MessageBox.Show("Selecciona un perfil.", "Perfil requerido", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    string profileName = cmbProfiles.SelectedItem.ToString();
                    profileId = profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.Ordinal))?.ID;
                    if (string.IsNullOrWhiteSpace(profileId))
                    {
                        System.Windows.MessageBox.Show("El perfil seleccionado no tiene un ID válido.", "Perfil inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    if (!Regex.IsMatch(profileId, @"\A[0-9a-fA-F]{6}\z", RegexOptions.CultureInvariant))
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
                }
                else
                {
                    brokerStatusError = string.IsNullOrWhiteSpace(response.Error) ? "El broker no pudo completar la operación." : response.Error;
                    System.Windows.MessageBox.Show(
                        brokerStatusError,
                        "Fallo",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    await CheckCurrentStatusAsync();
                }
            }
            catch (Exception ex)
            {
                brokerStatusError = ex.Message;
                System.Windows.MessageBox.Show(ex.Message, "Broker no disponible", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnAction.IsEnabled = true;
                UpdateUI();
            }
        }

        internal async Task CheckCurrentStatusAsync(bool enableActionWhenComplete = false)
        {
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
                }
                else
                {
                    brokerStatusError = string.IsNullOrWhiteSpace(response.Error) ? "No se pudo consultar el estado del broker." : response.Error;
                }
            }
            catch (Exception ex)
            {
                // A failed status request is not evidence that protection is off.
                // Preserve the last confirmed state; on first launch keep status unknown.
                brokerStatusError = ex.Message;
            }
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
            UpdateUI();
        }

        private void UpdateUI()
        {
            if (needsDnsRestore)
            {
                lblStatus.Text = "REQUIERE ATENCIÓN";
                lblStatus.Foreground = new SolidColorBrush(System.Windows.Media.Colors.Orange);
                lblInfo.Text = "Hay una configuración DNS pendiente de restaurar.";
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
                profiles.Add(new Profile { Name = txtName.Text, ID = txtID.Text });
                SaveProfiles(); RefreshCombo(); cmbProfiles.SelectedItem = txtName.Text; txtName.Clear(); txtID.Clear();
            }
        }

        private void BtnDel_Click(object sender, RoutedEventArgs e)
        {
            if (cmbProfiles.SelectedItem != null)
            {
                var item = cmbProfiles.SelectedItem.ToString(); profiles.RemoveAll(p => p.Name == item); SaveProfiles(); RefreshCombo();
            }
        }

        private void LoadProfiles()
        {
            if (File.Exists(profilesFile)) { try { string json = File.ReadAllText(profilesFile); profiles = JsonSerializer.Deserialize<List<Profile>>(json); RefreshCombo(); } catch { profiles = new List<Profile>(); } }
        }

        private void SaveProfiles() { string json = JsonSerializer.Serialize(profiles); File.WriteAllText(profilesFile, json); }

        private void RefreshCombo()
        {
            cmbProfiles.Items.Clear(); foreach (var p in profiles) cmbProfiles.Items.Add(p.Name);
            if (cmbProfiles.Items.Count > 0) cmbProfiles.SelectedIndex = 0;
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e) { this.WindowState = WindowState.Minimized; }
        private void BtnClose_Click(object sender, RoutedEventArgs e) { this.Close(); }
    }
}
