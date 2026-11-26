#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace NextDNSModernWPF;

public static class BackdropRuntimePolicy
{
    public static bool UseLayeredWindow => !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);
    public static Thickness GlassFrameThickness => UseLayeredWindow ? new Thickness(0) : new Thickness(-1);
    public static CornerRadius SurfaceCornerRadius => UseLayeredWindow ? new CornerRadius(10) : new CornerRadius(0);
    public static Thickness SurfaceBorderThickness => UseLayeredWindow ? new Thickness(1) : new Thickness(0);
}

public partial class MainWindow
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmSystemBackdropNone = 1;
    private const int DwmSystemBackdropTransientWindow = 3;
    private bool _systemBackdropReady;
    private HwndSource? _backdropSource;

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmMargins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref DwmMargins margins);

    private void MainWindow_BackdropSourceInitialized(object? sender, EventArgs e)
    {
        if (BackdropRuntimePolicy.UseLayeredWindow)
            return;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _backdropSource = HwndSource.FromHwnd(hwnd);
        if (_backdropSource?.CompositionTarget is not HwndTarget target)
        {
            UseOpaqueBackdropFallback(null);
            return;
        }

        System.Windows.Media.Color previousBackground = target.BackgroundColor;
        target.BackgroundColor = Colors.Transparent;

        try
        {
            int backdropType = DwmSystemBackdropTransientWindow;
            int backdropResult = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
            DwmMargins margins = new() { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            int frameResult = DwmExtendFrameIntoClientArea(hwnd, ref margins);

            if (backdropResult >= 0 && frameResult >= 0)
            {
                _systemBackdropReady = true;
                WindowSurfaceBorder.SetResourceReference(Border.BackgroundProperty, "WindowSurfaceBackdrop");
                WindowSurfaceBorder.CornerRadius = new CornerRadius(0);
                WindowSurfaceBorder.BorderThickness = new Thickness(0);
                StateChanged += MainWindow_BackdropStateChanged;
                return;
            }

            RestoreDwmFrame(hwnd);
            target.BackgroundColor = previousBackground;
            UseOpaqueBackdropFallback(target);
        }
        catch (DllNotFoundException)
        {
            target.BackgroundColor = previousBackground;
            UseOpaqueBackdropFallback(target);
        }
        catch (EntryPointNotFoundException)
        {
            target.BackgroundColor = previousBackground;
            UseOpaqueBackdropFallback(target);
        }
        catch (InvalidOperationException)
        {
            target.BackgroundColor = previousBackground;
            UseOpaqueBackdropFallback(target);
        }
    }

    private void MainWindow_BackdropStateChanged(object? sender, EventArgs e)
    {
        if (_systemBackdropReady && _backdropSource is not null)
            ApplyExtendedFrame(_backdropSource.Handle);
    }

    private void ApplyExtendedFrame(IntPtr hwnd)
    {
        try
        {
            DwmMargins margins = new() { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) < 0)
            {
                _systemBackdropReady = false;
                StateChanged -= MainWindow_BackdropStateChanged;
                UseOpaqueBackdropFallback(_backdropSource?.CompositionTarget as HwndTarget);
            }
        }
        catch (DllNotFoundException)
        {
            _systemBackdropReady = false;
            StateChanged -= MainWindow_BackdropStateChanged;
            UseOpaqueBackdropFallback(_backdropSource?.CompositionTarget as HwndTarget);
        }
        catch (EntryPointNotFoundException)
        {
            _systemBackdropReady = false;
            StateChanged -= MainWindow_BackdropStateChanged;
            UseOpaqueBackdropFallback(_backdropSource?.CompositionTarget as HwndTarget);
        }
    }

    private void RestoreDwmFrame(IntPtr hwnd)
    {
        try
        {
            int backdropType = DwmSystemBackdropNone;
            _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
            DwmMargins margins = new();
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private void UseOpaqueBackdropFallback(HwndTarget? target)
    {
        SetResourceReference(BackgroundProperty, "WindowSurfaceFallback");
        if (WindowSurfaceBorder is not null)
        {
            WindowSurfaceBorder.SetResourceReference(Border.BackgroundProperty, "WindowSurfaceFallback");
            WindowSurfaceBorder.CornerRadius = new CornerRadius(10);
            WindowSurfaceBorder.BorderThickness = new Thickness(1);
        }

        if (target is not null && TryFindResource("WindowSurfaceFallback") is SolidColorBrush fallback)
            target.BackgroundColor = fallback.Color;
    }
}
