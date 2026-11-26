#nullable enable

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace NextDNSModernWPF;

public partial class MainWindow
{
    private void ComboBoxSurface_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox || !comboBox.IsEnabled ||
            IsWithinComboBoxItem(e.OriginalSource as DependencyObject, comboBox))
            return;

        comboBox.Focus();
        comboBox.IsDropDownOpen = !comboBox.IsDropDownOpen;
        e.Handled = true;
    }

    private static bool IsWithinComboBoxItem(DependencyObject? source, System.Windows.Controls.ComboBox comboBox)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.ComboBoxItem item &&
                System.Windows.Controls.ItemsControl.ItemsControlFromItemContainer(item) == comboBox)
                return true;
            if (ReferenceEquals(source, comboBox))
                return false;

            source = GetRoutedVisualParent(source);
        }

        return false;
    }

    private static DependencyObject? GetRoutedVisualParent(DependencyObject source)
    {
        if (source is FrameworkContentElement contentElement && contentElement.Parent is not null)
            return contentElement.Parent;
        if (source is FrameworkElement element && element.Parent is not null)
            return element.Parent;
        if (source is ContentElement content)
            return ContentOperations.GetParent(content);
        if (source is Visual || source is Visual3D)
            return VisualTreeHelper.GetParent(source);
        return LogicalTreeHelper.GetParent(source);
    }
}
