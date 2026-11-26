using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace NextDNSModernWPF;

public partial class MainWindow
{
    private void WindowMargin_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        System.Windows.Controls.ScrollViewer? viewer = mainTabs.SelectedIndex switch
        {
            0 => homeScrollViewer,
            1 => listsScrollViewer,
            2 => activityScrollViewer,
            3 => settingsScrollViewer,
            _ => null
        };
        if (viewer is null || !viewer.IsVisible) return;
        var origin = viewer.TranslatePoint(new System.Windows.Point(0, 0), this);
        var pointer = e.GetPosition(this);
        bool inContentHeight = pointer.Y >= origin.Y && pointer.Y <= origin.Y + viewer.ActualHeight;
        bool inRightMargin = pointer.X >= origin.X + viewer.ActualWidth && pointer.X <= ActualWidth - 4;
        bool inPageArea = (mainTabs.SelectedIndex == 2 || mainTabs.SelectedIndex == 3) &&
            pointer.X >= 4 && pointer.X <= ActualWidth - 4;
        if (!inContentHeight || (!inRightMargin && !inPageArea)) return;
        double lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        double amount = lines < 0 ? viewer.ViewportHeight : lines * 16;
        double target = Math.Clamp(viewer.VerticalOffset - e.Delta / 120.0 * amount, 0, viewer.ScrollableHeight);
        // At the outer edge, leave the event available to the list of records so
        // that its own scrollbar remains usable after reaching the page boundary.
        if (inPageArea && !inRightMargin && Math.Abs(target - viewer.VerticalOffset) < 0.01) return;
        viewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }
}
