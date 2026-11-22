using System;
using System.Reflection;
using System.Windows;

namespace LifecycleProbe
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var app = new ProbeApplication
            {
                Reason = args.Length > 0 && args[0] == "Logoff"
                    ? ReasonSessionEnding.Logoff : ReasonSessionEnding.Shutdown
            };
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            int exitCode = app.Run();
            return app.Failed ? 1 : exitCode;
        }
    }

    // Exercise the real SessionEnding handler without ending the Windows session.
    internal sealed class ProbeApplication : NextDNSModernWPF.App
    {
        internal ReasonSessionEnding Reason { get; set; }
        internal bool Failed { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (MainWindow == null)
            {
                Console.Error.WriteLine("FAIL: close the running Manager before this probe.");
                Failed = true;
                Shutdown(2);
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var window = MainWindow;
                    bool closed = false;
                    window.Closed += (sender, args) => closed = true;
                    // WPF creates these args internally; the probe does not send
                    // WM_QUERYENDSESSION or request a real logoff/shutdown.
                    var ending = (SessionEndingCancelEventArgs)Activator.CreateInstance(
                        typeof(SessionEndingCancelEventArgs), BindingFlags.Instance | BindingFlags.NonPublic,
                        null, new object[] { Reason }, null);
                    OnSessionEnding(ending);
                    if (ending.Cancel) throw new InvalidOperationException("Session ending was canceled.");
                    window.Close();
                    if (!closed) throw new InvalidOperationException("Window remained alive after session ending.");
                    Console.WriteLine($"PASS: {Reason} closes the real Manager window.");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    Failed = true;
                    Shutdown(1);
                }
            }));
        }
    }
}
