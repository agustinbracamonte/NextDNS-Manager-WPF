#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        private static readonly Version MinimumSupportedCliVersion = new Version(1, 50, 2);
        private const string ExpectedCliSha256 = "5A603C759BC4A854A6AAE46CADB91AE00A95BA5BB0591B985F49A95597856B8E";
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

                if (!IsRunningAsAdmin()) { /* Warning silencioso */ }

                LoadProfiles();
                Task.Run(() => CheckCurrentStatus());
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
                    using (var sourceIcon = new System.Drawing.Icon(iconStream))
                        trayIcon = (System.Drawing.Icon)sourceIcon.Clone();
                }
                catch { trayIcon = (System.Drawing.Icon)System.Drawing.SystemIcons.Shield.Clone(); }

                notifyIcon.Icon = trayIcon;
                notifyIcon.Text = "NextDNS Manager";
                notifyIcon.DoubleClick += NotifyIcon_DoubleClick;

                contextMenu.Items.Add("Abrir", null, (s, e) => RestoreFromTray());
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
            try { if (notifyIcon != null) notifyIcon.ContextMenuStrip = null; } catch { }
            try { notifyIcon?.Dispose(); } catch { }
            try { contextMenu?.Dispose(); } catch { }
            try { trayIcon?.Dispose(); } catch { }
        }

        private async void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            if (!IsRunningAsAdmin())
            {
                System.Windows.MessageBox.Show(
                    "Ejecuta NextDNS Manager como administrador para modificar el servicio.",
                    "Permisos requeridos", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            btnAction.IsEnabled = false;

            if (isProtected)
            {
                string errorMsg = await Task.Run(() =>
                {
                    string uninstallResult = RunSilentCommand("uninstall");
                    if (uninstallResult != "OK") return "No se pudo desinstalar el servicio NextDNS:\n" + uninstallResult;

                    CliCommandResult status = RunCliCommand("status");
                    string statusText = string.Join(Environment.NewLine,
                        new[] { status.StandardOutput, status.StandardError }
                            .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
                    if (status.ExitCode != 0 || !string.Equals(statusText, "not installed", StringComparison.OrdinalIgnoreCase))
                        return "El comando terminó, pero no se pudo confirmar que el servicio haya quedado desinstalado. Estado: " + statusText;

                    return "OK";
                });

                if (errorMsg == "OK") isProtected = false;
                else
                {
                    System.Windows.MessageBox.Show(errorMsg, "Fallo", MessageBoxButton.OK, MessageBoxImage.Error);
                    isProtected = await Task.Run(() => IsCliServiceRunning());
                }
            }
            else
            {
                if (cmbProfiles.SelectedItem == null) { System.Windows.MessageBox.Show("Selecciona un perfil"); btnAction.IsEnabled = true; return; }
                string profileName = cmbProfiles.SelectedItem.ToString();
                string profileId = profiles.FirstOrDefault(p => p.Name == profileName)?.ID;
                if (string.IsNullOrEmpty(profileId)) { btnAction.IsEnabled = true; return; }
                bool reportInfo = chkReportName.IsChecked == true;
                bool setupRouter = chkSetupRouter.IsChecked == true;

                string errorMsg = await Task.Run(() => {
                    // CORRECCIÓN 2: Verificar antes si existe el exe auxiliar
                    string exePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nextdns.exe");
                    if (!File.Exists(exePath)) return "Falta el archivo 'nextdns.exe' en la carpeta del programa.\nPor favor instálelo correctamente.";

                    if (HasWindowsService("NextDNSService"))
                        return "Se detectó instalado el cliente oficial de NextDNS para Windows (servicio NextDNSService). No se puede activar este CLI en paralelo porque ambos pueden competir por DNS. Desinstala primero el otro cliente desde Windows o usa exclusivamente el cliente oficial.";

                    string cliVersionError = ValidateCliVersion();
                    if (!string.IsNullOrWhiteSpace(cliVersionError)) return cliVersionError;

                    string args = $"install -config {profileId} -auto-activate";
                    if (reportInfo) args += " -report-client-info";
                    if (setupRouter) args += " -setup-router";

                    string errInstall = RunSilentCommand(args);
                    if (errInstall != "OK") return "Error al instalar:\n" + errInstall;

                    CliCommandResult status = RunCliCommand("status");
                    string statusText = string.Join(Environment.NewLine,
                        new[] { status.StandardOutput, status.StandardError }
                            .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
                    if (status.ExitCode != 0 || !string.Equals(statusText, "running", StringComparison.OrdinalIgnoreCase))
                        return "El instalador terminó, pero el servicio no confirmó estado activo. Estado: " + statusText;

                    string serviceBinaryError = ValidateInstalledServiceBinary();
                    if (!string.IsNullOrWhiteSpace(serviceBinaryError)) return serviceBinaryError;

                    return "OK";
                });

                if (errorMsg == "OK") isProtected = true;
                else
                {
                    System.Windows.MessageBox.Show(errorMsg, "Fallo", MessageBoxButton.OK, MessageBoxImage.Error);
                    isProtected = await Task.Run(() => IsCliServiceRunning());
                }
            }
            UpdateUI();
            btnAction.IsEnabled = true;
        }

        private sealed class CliCommandResult
        {
            public int ExitCode { get; }
            public string StandardOutput { get; }
            public string StandardError { get; }

            public CliCommandResult(int exitCode, string standardOutput, string standardError)
            {
                ExitCode = exitCode;
                StandardOutput = standardOutput;
                StandardError = standardError;
            }
        }

        private CliCommandResult RunCliCommand(string arguments)
        {
            string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nextdns.exe");
            if (!File.Exists(exePath)) throw new FileNotFoundException("Falta el archivo nextdns.exe.", exePath);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using (Process process = Process.Start(psi))
            {
                if (process == null) throw new InvalidOperationException("No se pudo iniciar nextdns.exe.");
                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                Task.WaitAll(stdoutTask, stderrTask);
                return new CliCommandResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
            }
        }

        private string ValidateCliVersion()
        {
            try
            {
                CliCommandResult result = RunCliCommand("version");
                string output = string.Join(Environment.NewLine,
                    new[] { result.StandardOutput, result.StandardError }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));

                if (result.ExitCode != 0)
                    return "No se pudo verificar la versión del núcleo NextDNS." + Environment.NewLine + output;

                Match match = Regex.Match(output, @"(?m)^\s*nextdns version (?<version>\d+\.\d+\.\d+)\s*$", RegexOptions.IgnoreCase);
                if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out Version installedVersion))
                    return "No se pudo leer la versión del núcleo NextDNS. Actualiza nextdns.exe a v1.50.2 o posterior.";

                if (installedVersion.CompareTo(MinimumSupportedCliVersion) < 0)
                {
                    return $"El núcleo NextDNS {installedVersion} está desactualizado. Se requiere v{MinimumSupportedCliVersion} o posterior, que incluye la corrección del bucle de CPU. Actualiza nextdns.exe desde https://github.com/nextdns/nextdns/releases/latest.";
                }

                return null;
            }
            catch (Exception ex)
            {
                return "No se pudo verificar la versión del núcleo NextDNS: " + ex.Message;
            }
        }

        private string RunSilentCommand(string arguments)
        {
            try
            {
                CliCommandResult result = RunCliCommand(arguments);
                if (result.ExitCode == 0) return "OK";

                string output = string.Join(Environment.NewLine,
                    new[] { result.StandardError, result.StandardOutput }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                return string.IsNullOrWhiteSpace(output) ? $"Error: {result.ExitCode}" : output.Trim();
            }
            catch (Exception ex) { return ex.Message; }
        }

        private void UpdateUI()
        {
            Dispatcher.Invoke(() =>
            {
                if (isProtected)
                {
                    lblStatus.Text = "PROTEGIDO";
                    lblStatus.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#40A745"));
                    lblInfo.Text = $"Perfil activo: {cmbProfiles.SelectedItem}";
                    btnAction.Content = "DESACTIVAR";
                    btnAction.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E"));
                    btnAction.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#D32F2F"));
                }
                else
                {
                    lblStatus.Text = "DESPROTEGIDO";
                    lblStatus.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A0A0A0"));
                    lblInfo.Text = "El tráfico no está encriptado";
                    btnAction.Content = "ACTIVAR";
                    btnAction.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2F80ED"));
                    btnAction.Foreground = System.Windows.Media.Brushes.White;
                }
            });
        }

        private void CheckCurrentStatus()
        {
            isProtected = IsCliServiceRunning();
            UpdateUI();
        }

        private bool IsCliServiceRunning()
        {
            try
            {
                CliCommandResult status = RunCliCommand("status");
                string statusText = string.Join(Environment.NewLine,
                    new[] { status.StandardOutput, status.StandardError }
                        .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
                return status.ExitCode == 0 && string.Equals(statusText, "running", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasWindowsService(string serviceName)
        {
            using (RegistryKey serviceKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false))
            {
                return serviceKey != null;
            }
        }

        private static string GetWindowsServiceImagePath(string serviceName)
        {
            using (RegistryKey serviceKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: false))
            {
                return serviceKey?.GetValue("ImagePath") as string;
            }
        }

        private string ValidateInstalledServiceBinary()
        {
            string imagePath = GetWindowsServiceImagePath("nextdns");
            Match imageMatch = Regex.Match(imagePath ?? string.Empty, @"^\s*""(?<path>[^""]+\.exe)""(?:\s|$)", RegexOptions.IgnoreCase);
            if (!imageMatch.Success)
                return "No se pudo verificar la ruta del ejecutable registrado en el servicio nextdns.";

            string expectedPath = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nextdns", "nextdns.exe"));
            string installedPath;
            try
            {
                installedPath = Path.GetFullPath(imageMatch.Groups["path"].Value);
            }
            catch (Exception ex)
            {
                return "La ruta registrada para el servicio nextdns no es válida: " + ex.Message;
            }

            if (!string.Equals(installedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                return "El servicio nextdns apunta a una ruta inesperada. No se confirmó qué núcleo está ejecutando: " + installedPath;

            if (!File.Exists(installedPath))
                return "No se encontró el ejecutable configurado para el servicio nextdns: " + installedPath;

            try
            {
                using (FileStream stream = File.OpenRead(installedPath))
                {
                    string installedHash = Convert.ToHexString(SHA256.HashData(stream));
                    if (!string.Equals(installedHash, ExpectedCliSha256, StringComparison.OrdinalIgnoreCase))
                        return "El servicio nextdns quedó instalado con un ejecutable distinto de la versión oficial verificada v1.50.2.";
                }
            }
            catch (Exception ex)
            {
                return "No se pudo validar el ejecutable activo del servicio nextdns: " + ex.Message;
            }

            return null;
        }

        private bool IsRunningAsAdmin()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
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
