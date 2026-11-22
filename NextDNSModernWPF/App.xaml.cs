using System;
using System.IO;
using System.Windows;

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
                _singleInstance = SingleInstanceCoordinator.Start(Dispatcher, RestoreMainWindow);
                if (!_singleInstance.IsPrimary)
                {
                    Shutdown();
                    return;
                }

                var window = new NextDNSModernWPF.MainWindow();
                this.MainWindow = window;
                window.Show();
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
