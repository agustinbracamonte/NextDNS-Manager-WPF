using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Security.Principal;
using System.Threading.Tasks;
using NextDNSManager.Shared;

namespace NextDNSModernWPF
{
    public partial class App : System.Windows.Application
    {
        private SingleInstanceCoordinator? _singleInstance;
        private bool _primaryInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (UpdateLaunchCoordinator.IsHelperInvocation(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = RunUpdateLauncherAsync(e.Args);
                return;
            }

            SessionEnding += App_SessionEnding;
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += App_UnhandledException;
            TaskScheduler.UnobservedTaskException += App_UnobservedTaskException;

            try
            {
                bool background = Array.Exists(e.Args, arg => string.Equals(arg, "--background", StringComparison.Ordinal));
                _singleInstance = SingleInstanceCoordinator.Start(Dispatcher, RestoreMainWindow, !background);
                if (!_singleInstance.IsPrimary)
                {
                    Shutdown();
                    return;
                }
                _primaryInstance = true;
                DiagnosticLog.WriteManager(DiagnosticEvent.ManagerStarted);

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

        private async Task RunUpdateLauncherAsync(string[] arguments)
        {
            int exitCode = 1;
            try
            {
                exitCode = await UpdateLaunchCoordinator.RunHelperAsync(arguments);
            }
            catch
            {
                // The interactive instance reports helper handoff failures before it exits.
                exitCode = 1;
            }
            finally
            {
                Shutdown(exitCode);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            SystemEvents.PowerModeChanged -= PowerModeChanged;
            DispatcherUnhandledException -= App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= App_UnhandledException;
            TaskScheduler.UnobservedTaskException -= App_UnobservedTaskException;
            if (_primaryInstance)
                DiagnosticLog.WriteManager(DiagnosticEvent.ManagerShutdown);
            try
            {
                try { _singleInstance?.Dispose(); }
                catch (Exception exception)
                {
                    DiagnosticLog.WriteManager(DiagnosticEvent.ManagerShutdownFailure, exception);
                    throw;
                }
                finally { _singleInstance = null; }
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
            DiagnosticLog.WriteManager(DiagnosticEvent.WindowsSessionEnding);
            if (this.MainWindow is NextDNSModernWPF.MainWindow window)
                window.AllowCloseForSessionEnding();
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) =>
            DiagnosticLog.WriteManager(DiagnosticEvent.DispatcherUnhandledException, e.Exception);

        private void App_UnhandledException(object sender, UnhandledExceptionEventArgs e) =>
            DiagnosticLog.WriteManager(DiagnosticEvent.ProcessUnhandledException, e.ExceptionObject as Exception);

        private void App_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
            DiagnosticLog.WriteManager(DiagnosticEvent.UnobservedTaskException, e.Exception);

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
            catch (Exception ex)
            {
                DiagnosticLog.WriteManager(DiagnosticEvent.AutostartConfigurationFailure, ex);
            }
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

        private static void LogStartupFailure(Exception exception) =>
            DiagnosticLog.WriteManager(DiagnosticEvent.StartupFailure, exception);
    }
}
