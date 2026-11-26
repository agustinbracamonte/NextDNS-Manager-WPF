using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NextDNSModernWPF;

internal static class DemoCaptureRunner
{
    private static readonly (ThemeVariant Theme, string FileName)[] Captures =
    {
        (ThemeVariant.Light, "manager-light.png"),
        (ThemeVariant.Dark, "manager-dark.png")
    };

    internal static async Task CaptureAsync(string outputDirectory)
    {
        System.Windows.Application application = System.Windows.Application.Current ??
            throw new InvalidOperationException("No hay una aplicación WPF activa.");
        string targetDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(targetDirectory);

        foreach ((ThemeVariant theme, string fileName) in Captures)
        {
            application.Resources.MergedDictionaries.Clear();
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    theme == ThemeVariant.Light
                        ? "/NextDNSModernWPF;component/Themes/Light.xaml"
                        : "/NextDNSModernWPF;component/Themes/Dark.xaml",
                    UriKind.Relative)
            });

            var window = new MainWindow(demoCaptureMode: true, demoTheme: theme);
            application.MainWindow = window;
            try
            {
                await CaptureWindowAsync(window, Path.Combine(targetDirectory, fileName));
            }
            finally
            {
                if (window.IsLoaded)
                    window.CloseAfterDemoCapture();
            }
        }
    }

    private static async Task CaptureWindowAsync(Window window, string outputPath)
    {
        var contentRendered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler onContentRendered = (_, _) => contentRendered.TrySetResult(true);
        window.ContentRendered += onContentRendered;

        try
        {
            window.Show();
            await contentRendered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);

            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            int pixelWidth = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            bitmap.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(output);
        }
        finally
        {
            window.ContentRendered -= onContentRendered;
        }
    }
}
