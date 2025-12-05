#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
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
        private System.Windows.Forms.NotifyIcon _notifyIcon;

        public MainWindow()
        {
            InitializeComponent();

            // Configurar ruta del JSON
            if (!Directory.Exists(appDataFolder)) Directory.CreateDirectory(appDataFolder);
            profilesFile = Path.Combine(appDataFolder, "profiles.json");

            try { this.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/escudo_dns.ico")); } catch { }
            InitializeTrayIcon();

            if (!IsRunningAsAdmin()) { /* Warning silencioso */ }

            LoadProfiles();
            Task.Run(() => CheckCurrentStatus());
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            try
            {
                using (Stream iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/escudo_dns.ico")).Stream)
                {
                    _notifyIcon.Icon = new System.Drawing.Icon(iconStream);
                }
            }
            catch { _notifyIcon.Icon = System.Drawing.SystemIcons.Shield; }

            _notifyIcon.Visible = true;
            _notifyIcon.Text = "NextDNS Manager";
            _notifyIcon.DoubleClick += (s, args) => { this.Show(); this.WindowState = WindowState.Normal; this.Activate(); };

            var contextMenu = new System.Windows.Forms.ContextMenuStrip();
            contextMenu.Items.Add("Abrir", null, (s, e) => { this.Show(); this.WindowState = WindowState.Normal; this.Activate(); });
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Salir (Quit)", null, (s, e) => { ExitApplication(); });
            _notifyIcon.ContextMenuStrip = contextMenu;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            this.Hide();
        }

        private void ExitApplication()
        {
            _notifyIcon.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        private async void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            btnAction.IsEnabled = false;

            if (isProtected)
            {
                await Task.Run(() => { RunSilentCommand("stop"); RunSilentCommand("uninstall"); });
                isProtected = false;
            }
            else
            {
                if (cmbProfiles.SelectedItem == null) { System.Windows.MessageBox.Show("Selecciona un perfil"); btnAction.IsEnabled = true; return; }
                string profileName = cmbProfiles.SelectedItem.ToString();
                string profileId = profiles.FirstOrDefault(p => p.Name == profileName)?.ID;
                if (string.IsNullOrEmpty(profileId)) { btnAction.IsEnabled = true; return; }
                bool reportInfo = chkReportName.IsChecked == true;

                string errorMsg = await Task.Run(() => {
                    // CORRECCIÓN 2: Verificar antes si existe el exe auxiliar
                    string exePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nextdns.exe");
                    if (!File.Exists(exePath)) return "Falta el archivo 'nextdns.exe' en la carpeta del programa.\nPor favor instálelo correctamente.";

                    RunSilentCommand("stop"); RunSilentCommand("uninstall");
                    string args = $"install -config {profileId} -auto-activate";
                    if (reportInfo) args += " -report-client-info -setup-router";

                    string errInstall = RunSilentCommand(args);
                    if (errInstall != "OK") return "Error al instalar:\n" + errInstall;

                    string errStart = RunSilentCommand("start");
                    if (errStart != "OK" && !errStart.Contains("already running")) return "Error al iniciar:\n" + errStart;
                    return "OK";
                });

                if (errorMsg == "OK") isProtected = true;
                else { System.Windows.MessageBox.Show(errorMsg, "Fallo", MessageBoxButton.OK, MessageBoxImage.Error); isProtected = false; }
            }
            UpdateUI();
            btnAction.IsEnabled = true;
        }

        private string RunSilentCommand(string arguments)
        {
            try
            {
                string exePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nextdns.exe");
                if (!File.Exists(exePath)) return "Falta nextdns.exe"; // Doble check

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exePath; psi.Arguments = arguments; psi.UseShellExecute = false;
                psi.CreateNoWindow = true; psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardError = true; psi.RedirectStandardOutput = true;
                Process p = Process.Start(psi);
                string errorOutput = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode == 0) return "OK";
                return string.IsNullOrWhiteSpace(errorOutput) ? $"Error: {p.ExitCode}" : errorOutput;
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

        private void CheckCurrentStatus() { isProtected = false; UpdateUI(); }

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