using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Security.Principal;
using System.Threading.Tasks;

namespace NextDNSModernWPF
{
    public partial class App : System.Windows.Application
    {
        private SingleInstanceCoordinator? _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            SessionEnding += App_SessionEnding;

            try
            {
                bool background = Array.Exists(e.Args, arg => string.Equals(arg, "--background", StringComparison.Ordinal));
                _singleInstance = SingleInstanceCoordinator.Start(Dispatcher, RestoreMainWindow, !background);
                if (!_singleInstance.IsPrimary)
                {
                    Shutdown();
                    return;
                }

                ConfigureAutostart();
                SystemEvents.PowerModeChanged += PowerModeChanged;
                var window = new NextDNSModernWPF.MainWindow();
                this.MainWindow = window;
                window.Show();
                if (background)
                    window.Hide();
            }
            catch (Exception ex)
            {
                if (this.MainWindow is NextDNSModernWPF.MainWindow failedWindow)
                {
                    failedWindow.AllowCloseForSessionEnding();
                    try { failedWindow.Close(); }
                    catch { }
                }

                LogStartupFailure(ex);
                System.Windows.MessageBox.Show(
                    "No se pudo iniciar NextDNS Manager. " + ex.Message,
                    "NextDNS Manager",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SystemEvents.PowerModeChanged -= PowerModeChanged;
            try
            {
                _singleInstance?.Dispose();
                _singleInstance = null;
            }
            finally
            {
                base.OnExit(e);
            }
        }

        private void RestoreMainWindow()
        {
            if (this.MainWindow is NextDNSModernWPF.MainWindow window)
                window.RestoreFromTray();
        }

        private void App_SessionEnding(object? sender, SessionEndingCancelEventArgs e)
        {
            if (this.MainWindow is NextDNSModernWPF.MainWindow window)
                window.AllowCloseForSessionEnding();
        }

        private async void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (Dispatcher.HasShutdownStarted) return;
            await Dispatcher.InvokeAsync(async () =>
            {
                if (this.MainWindow is NextDNSModernWPF.MainWindow window)
                    await window.CheckCurrentStatusAsync();
            });
        }

        private static void ConfigureAutostart()
        {
            // Configure once per user. If disabled in Windows startup settings,
            // do not re-enable it each time the application is opened.
            try
            {
                string? path = Environment.ProcessPath;
                string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NextDNS Manager", "NextDNSModernWPF.exe");
                if (path == null || !string.Equals(path, expected, StringComparison.OrdinalIgnoreCase)) return;
                using RegistryKey settings = Registry.CurrentUser.CreateSubKey(@"Software\NextDNSManager");
                if ((int?)settings.GetValue("StartupConfigured") == 1) return;
                using RegistryKey run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                run.SetValue("NextDNS Manager", $"\"{path}\" --background", RegistryValueKind.String);
                ConfigureResumeTask(path);
                settings.SetValue("StartupConfigured", 1, RegistryValueKind.DWord);
            }
            catch (Exception ex) { LogStartupFailure(ex); }
        }

        private static void ConfigureResumeTask(string executable)
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? throw new InvalidOperationException("No se pudo identificar la sesión de Windows.");
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
            scheduler.Connect();
            dynamic folder = scheduler.GetFolder("\\");
            dynamic task = scheduler.NewTask(0);
            task.RegistrationInfo.Description = "Restaura el icono de NextDNS Manager al reanudar Windows.";
            task.Principal.UserId = sid;
            task.Principal.LogonType = 3; // interactive user token
            task.Principal.RunLevel = 0; // limited privileges
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.StartWhenAvailable = true;
            dynamic trigger = task.Triggers.Create(0); // event trigger
            trigger.Subscription = "<QueryList><Query Id='0' Path='System'><Select Path='System'>*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and (EventID=1)]]</Select></Query></QueryList>";
            trigger.Delay = "PT5S";
            dynamic action = task.Actions.Create(0);
            action.Path = executable;
            action.Arguments = "--background";
            action.WorkingDirectory = Path.GetDirectoryName(executable);
            folder.RegisterTaskDefinition("NextDNS Manager Resume " + sid, task, 6, null, null, 3, null);
        }

        private static void LogStartupFailure(Exception exception)
        {
            try
            {
                string logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "NextDNSManager");
                Directory.CreateDirectory(logDirectory);
                string logPath = Path.Combine(logDirectory, "startup.log");
                File.AppendAllText(
                    logPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] Startup failure{Environment.NewLine}" +
                    exception + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
                // Startup logging must not prevent the error dialog or shutdown.
            }
        }
    }
}
