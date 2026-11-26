using System.Windows;

namespace NextDNSModernWPF;

public partial class MainWindow
{
    private bool _profileSettingsLoadPending;
    private bool _profileSettingsLoadScheduled;

    private void ApiProfileSettings_Expanded(object sender, RoutedEventArgs e)
    {
        if (!_apiSettingsLoaded) QueueProfileSettingsLoad();
    }

    private void QueueProfileSettingsLoad()
    {
        _profileSettingsLoadPending = true;
        TryStartPendingProfileSettingsLoad();
    }

    private void TryStartPendingProfileSettingsLoad()
    {
        if (!_profileSettingsLoadPending || _profileSettingsLoadScheduled || _apiOperationInProgress ||
            expApiProfileSettings is null || !expApiProfileSettings.IsExpanded ||
            _nextDnsApiClient is null || SelectedApiProfileId is null) return;
        _profileSettingsLoadScheduled = true;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            _profileSettingsLoadScheduled = false;
            if (!_profileSettingsLoadPending || _apiOperationInProgress || !expApiProfileSettings.IsExpanded ||
                _nextDnsApiClient is null || SelectedApiProfileId is null) return;
            _profileSettingsLoadPending = false;
            await LoadApiSettingsAsync();
        }));
    }
}
