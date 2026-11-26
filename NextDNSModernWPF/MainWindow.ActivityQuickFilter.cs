using System.Windows;
using System.Windows.Controls;

namespace NextDNSModernWPF;

public partial class MainWindow
{
    private async void BlockedOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_apiOperationInProgress || _nextDnsApiClient is null || SelectedApiProfileId is null) return;
        bool wasLive = _activityStreamCancellation is not null;
        await StopActivityStreamAsync(LocalizationManager.Get("Code.Activity.FiltersChangedStreamStopped"));
        bool enable = (cmbActivityStatus.SelectedItem as ActivityStatusChoice)?.Status != NextDnsLogStatus.Blocked;
        _bindingActivityFilters = true;
        try { cmbActivityStatus.SelectedIndex = enable ? 3 : 0; }
        finally { _bindingActivityFilters = false; }
        btnBlockedOnly.Content = enable
            ? LocalizationManager.Get("Code.Activity.BlockedOnlyActive")
            : LocalizationManager.Get("Main.Ui.Activity.BlockedOnly");
        _activityQuery = null;
        _activityCursor = null;
        _activityStreamId = null;
        _recentStreamEventIds.Clear();
        _recentStreamEventIdSet.Clear();
        await LoadActivityPageAsync(loadMore: false);
        if (wasLive && activityPage.IsVisible && !_activityNeedsApply) await StartActivityStreamAsync();
    }
    private void ActivityPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || !int.TryParse(button.Tag?.ToString(), out int days)) return;
        ActivityDateControls_Loaded(sender, e);
        _syncingActivityDateControls = true;
        try
        {
            DateTime start = DateTime.Now.AddDays(-days);
            dpActivityFrom.SelectedDate = days == 0 ? null : start.Date;
            cmbActivityFromHour.SelectedIndex = days == 0 ? 0 : start.Hour;
            cmbActivityFromMinute.SelectedIndex = days == 0 ? 0 : start.Minute;
            dpActivityTo.SelectedDate = null;
            cmbActivityToHour.SelectedIndex = 0;
            cmbActivityToMinute.SelectedIndex = 0;
        }
        finally { _syncingActivityDateControls = false; }
        UpdateActivityTimeControlStates();
        SyncActivityDateFilters();
    }
}
