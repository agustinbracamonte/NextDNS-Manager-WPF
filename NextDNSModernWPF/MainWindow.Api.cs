#nullable enable

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NextDNSModernWPF;

public partial class MainWindow
{
    private const int ApiLogPageSize = 100;
    private const int MaximumActivityItems = 1000;
    private const int MaximumRememberedStreamIds = 2000;
    private const long MaximumImportFileBytes = 2 * 1024 * 1024;
    private readonly ApiKeyStore _apiKeyStore = new();
    private readonly ObservableCollection<NextDnsListEntry> _allowedDomains = new();
    private readonly ObservableCollection<NextDnsListEntry> _blockedDomains = new();
    private readonly ObservableCollection<NextDnsLogEntry> _dnsActivity = new();
    private readonly Dictionary<string, string> _activityDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentStreamEventIds = new();
    private readonly HashSet<string> _recentStreamEventIdSet = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _prettyJson = new() { WriteIndented = true };
    private NextDnsApiClient? _nextDnsApiClient;
    private byte[]? _verifiedApiKeyHash;
    private ApiListImportPreview? _domainImportPreview;
    private string? _previewedImportText;
    private string? _previewedImportProfileId;
    private NextDnsListType? _previewedImportListType;
    private bool _previewPreflightComplete;
    private NextDnsLogsQuery? _activityQuery;
    private string? _activityCursor;
    private string? _activityStreamId;
    private bool _activityNeedsApply;
    private bool _activityAutoLoadPending;
    private bool _activityLoadInProgress;
    private bool _activityAutoLoadIssuedForVisit;
    private CancellationTokenSource? _activityStreamCancellation;
    private Task? _activityStreamTask;
    private CancellationTokenSource? _activityPageLoadCancellation;
    private string? _activityStreamStopMessage;
    private int _activityStreamEventCount;
    private bool? _originalLogsEnabled;
    private bool? _originalDropIp;
    private bool? _originalDropDomain;
    private bool? _originalBlockPageEnabled;
    private bool _apiSettingsLoaded;
    private bool _apiUiInitialized;
    private bool _apiOperationInProgress;
    private bool _bindingApiProfiles;
    private bool _bindingActivityDevices;
    private bool _bindingActivityFilters;
    private bool _refreshingLocalizedChoices;
    private bool _responsiveWindowSized;
    private bool _apiKeyVisible;
    private bool _apiKeyVisibilityChanging;

    private string CurrentApiKeyText => _apiKeyVisible ? txtApiKeyVisible.Text : pwdApiKey.Password;

    private void MainWindow_ApplyResponsiveSize(object sender, RoutedEventArgs e) => ApplyResponsiveWindowBounds(initial: true);

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_responsiveWindowSized)
            Dispatcher.BeginInvoke(new Action(() => ApplyResponsiveWindowBounds(initial: false)));
    }

    private void ApplyResponsiveWindowBounds(bool initial)
    {
        Rect workArea = SystemParameters.WorkArea;
        double maxWidth = Math.Max(1, workArea.Width - 32);
        double maxHeight = Math.Max(1, workArea.Height - 32);
        MinWidth = Math.Min(680, maxWidth);
        MinHeight = Math.Min(560, maxHeight);
        MaxWidth = maxWidth;
        MaxHeight = maxHeight;

        if (initial)
        {
            Width = Math.Min(800, maxWidth);
            Height = Math.Min(560, maxHeight);
        }
        else
        {
            Width = Math.Min(Math.Max(Width, MinWidth), maxWidth);
            Height = Math.Min(Math.Max(Height, MinHeight), maxHeight);
        }

        double minLeft = workArea.Left + 16;
        double maxLeft = Math.Max(minLeft, workArea.Right - Width - 16);
        double minTop = workArea.Top + 16;
        double maxTop = Math.Max(minTop, workArea.Bottom - Height - 16);
        if (initial)
        {
            Left = minLeft + (maxLeft - minLeft) / 2;
            Top = minTop + (maxTop - minTop) / 2;
        }
        else
        {
            Left = Math.Clamp(Left, minLeft, maxLeft);
            Top = Math.Clamp(Top, minTop, maxTop);
        }

        _responsiveWindowSized = true;
    }

    private sealed record ApiProfileChoice(string Id, string Name);
    private sealed record ImportListChoice(string Name, NextDnsListType Type);
    private sealed record ApiListImportPreview(IReadOnlyList<NextDnsListEntry> Entries, IReadOnlyList<string> Issues,
        int InvalidCount, int DuplicateCount, bool ImportedFromStateFile);
    private sealed record ActivityStatusChoice(string Name, NextDnsLogStatus? Status);
    private sealed record ActivityDeviceChoice(string Name, string? Id);
    private sealed class LogsSettingData
    {
        public bool? Enabled { get; set; }
        public LogDropSettingData? Drop { get; set; }
    }
    private sealed class LogDropSettingData
    {
        public bool? Ip { get; set; }
        public bool? Domain { get; set; }
    }
    private sealed class BlockPageSettingData
    {
        public bool? Enabled { get; set; }
    }

    internal void InitializeApiUi()
    {
        if (_apiUiInitialized) return;
        _apiUiInitialized = true;

        lstAllowedDomains.ItemsSource = _allowedDomains;
        lstBlockedDomains.ItemsSource = _blockedDomains;
        lstDnsActivity.ItemsSource = _dnsActivity;
        cmbApiProfile.DisplayMemberPath = nameof(ApiProfileChoice.Name);
        cmbApiProfile.SelectedValuePath = nameof(ApiProfileChoice.Id);
        cmbActivityDevice.DisplayMemberPath = nameof(ActivityDeviceChoice.Name);
        cmbActivityDevice.SelectedValuePath = nameof(ActivityDeviceChoice.Id);
        cmbActivityStatus.DisplayMemberPath = nameof(ActivityStatusChoice.Name);
        cmbActivityStatus.SelectedValuePath = nameof(ActivityStatusChoice.Status);
        cmbImportListTarget.DisplayMemberPath = nameof(ImportListChoice.Name);
        cmbImportListTarget.SelectedValuePath = nameof(ImportListChoice.Type);

        cmbImportListTarget.ItemsSource = new[]
        {
            new ImportListChoice(LocalizationManager.Get("Code.Lists.AllowlistLabel"), NextDnsListType.Allowlist),
            new ImportListChoice(LocalizationManager.Get("Code.Lists.BlocklistLabel"), NextDnsListType.Denylist)
        };
        cmbImportListTarget.SelectedIndex = 0;
        cmbActivityStatus.ItemsSource = new[]
        {
            new ActivityStatusChoice(LocalizationManager.Get("Code.Activity.AllResults"), null),
            new ActivityStatusChoice(LocalizationManager.Get("Code.Activity.StatusDefault"), NextDnsLogStatus.Default),
            new ActivityStatusChoice(LocalizationManager.Get("Code.Activity.StatusError"), NextDnsLogStatus.Error),
            new ActivityStatusChoice(LocalizationManager.Get("Code.Activity.StatusBlocked"), NextDnsLogStatus.Blocked),
            new ActivityStatusChoice(LocalizationManager.Get("Code.Activity.StatusAllowed"), NextDnsLogStatus.Allowed)
        };
        cmbActivityStatus.SelectedIndex = 0;
        cmbActivityDevice.ItemsSource = new[] { new ActivityDeviceChoice(LocalizationManager.Get("Code.Activity.AllDevices"), null) };
        cmbActivityDevice.SelectedIndex = 0;

        btnVerifyApiKey.Click += BtnVerifyApiKey_Click;
        btnSaveApiKey.Click += BtnSaveApiKey_Click;
        btnDeleteApiKey.Click += BtnDeleteApiKey_Click;
        cmbApiProfile.SelectionChanged += ApiProfile_SelectionChanged;
        btnAddAllowedDomain.Click += BtnAddAllowedDomain_Click;
        btnAddBlockedDomain.Click += BtnAddBlockedDomain_Click;
        btnRefreshDomainLists.Click += BtnRefreshDomainLists_Click;
        btnExportAllowedList.Click += (_, _) => ExportList(NextDnsListType.Allowlist);
        btnExportBlockedList.Click += (_, _) => ExportList(NextDnsListType.Denylist);
        btnImportAllowedFile.Click += (_, _) => OpenImportFileForList(NextDnsListType.Allowlist);
        btnImportBlockedFile.Click += (_, _) => OpenImportFileForList(NextDnsListType.Denylist);
        btnOpenImportFile.Click += BtnOpenImportFile_Click;
        btnPreviewDomainImport.Click += BtnPreviewDomainImport_Click;
        btnImportDomainList.Click += BtnImportDomainList_Click;
        txtBulkDomainImport.TextChanged += BulkDomainImport_TextChanged;
        cmbImportListTarget.SelectionChanged += ImportListTarget_SelectionChanged;
        lstAllowedDomains.SelectionChanged += ApiListSelection_Changed;
        lstBlockedDomains.SelectionChanged += ApiListSelection_Changed;
        txtListDomain.TextChanged += ListDomain_TextChanged;
        btnRefreshActivity.Click += BtnRefreshActivity_Click;
        btnLoadMoreActivity.Click += BtnLoadMoreActivity_Click;
        btnToggleActivityStream.Click += BtnToggleActivityStream_Click;
        activityPage.IsVisibleChanged += ActivityPage_IsVisibleChanged;
        cmbActivityDevice.SelectionChanged += ActivityFilter_Changed;
        cmbActivityStatus.SelectionChanged += ActivityFilter_Changed;
        txtActivitySearch.TextChanged += ActivityFilter_Changed;
        txtActivityFrom.TextChanged += ActivityFilter_Changed;
        txtActivityTo.TextChanged += ActivityFilter_Changed;
        btnLoadApiSettings.Click += BtnLoadApiSettings_Click;
        btnApplyApiSettings.Click += BtnApplyApiSettings_Click;
        chkApiLogsEnabled.Checked += ApiSettingCheckBox_Changed;
        chkApiLogsEnabled.Unchecked += ApiSettingCheckBox_Changed;
        chkApiLogsEnabled.Indeterminate += ApiSettingCheckBox_Changed;
        chkApiDropIp.Checked += ApiSettingCheckBox_Changed;
        chkApiDropIp.Unchecked += ApiSettingCheckBox_Changed;
        chkApiDropIp.Indeterminate += ApiSettingCheckBox_Changed;
        chkApiDropDomain.Checked += ApiSettingCheckBox_Changed;
        chkApiDropDomain.Unchecked += ApiSettingCheckBox_Changed;
        chkApiDropDomain.Indeterminate += ApiSettingCheckBox_Changed;
        chkApiBlockPageEnabled.Checked += ApiSettingCheckBox_Changed;
        chkApiBlockPageEnabled.Unchecked += ApiSettingCheckBox_Changed;
        chkApiBlockPageEnabled.Indeterminate += ApiSettingCheckBox_Changed;
        pwdApiKey.PasswordChanged += ApiKeyPassword_Changed;
        txtApiKeyVisible.TextChanged += ApiKeyVisible_TextChanged;
        btnToggleApiKeyVisibility.Click += ApiKeyVisibility_Click;
        mainTabs.SelectionChanged += MainTabs_SelectionChanged;
        IsVisibleChanged += MainWindow_ApiIsVisibleChanged;
        Deactivated += MainWindow_ApiDeactivated;
        Closed += MainWindow_ApiClosed;

        UpdateApiControlStates();
        if (_apiKeyStore.HasApiKey)
            _ = RestoreStoredApiKeyAsync();
        else
            SetApiConnectionStatus(LocalizationManager.Get("Code.Api.NotConnectedSaveKey"), "TextSecondary");
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, mainTabs)) return;
        int selectedIndex = mainTabs.SelectedIndex;
        if (selectedIndex != 3)
            ConcealApiKeyInput();

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!IsLoaded || mainTabs.SelectedIndex != selectedIndex) return;
            if (mainTabs.ItemContainerGenerator.ContainerFromIndex(selectedIndex) is TabItem selectedTab)
                selectedTab.Focus();

            switch (selectedIndex)
            {
                case 0: homeScrollViewer.ScrollToTop(); break;
                case 1: listsScrollViewer.ScrollToTop(); break;
                case 2:
                    activityScrollViewer.ScrollToTop();
                    QueueActivityAutoLoadIfNeeded();
                    break;
                case 3: settingsScrollViewer.ScrollToTop(); break;
            }
        }));
    }

    private void MainWindow_ApiIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible)
            ConcealApiKeyInput();
    }

    private void MainWindow_ApiDeactivated(object? sender, EventArgs e) => ConcealApiKeyInput();

    private void RefreshApiLocalizedChoices()
    {
        if (!_apiUiInitialized) return;

        NextDnsListType? selectedList = (cmbImportListTarget.SelectedItem as ImportListChoice)?.Type;
        NextDnsLogStatus? selectedStatus = (cmbActivityStatus.SelectedItem as ActivityStatusChoice)?.Status;
        string? selectedDeviceId = (cmbActivityDevice.SelectedItem as ActivityDeviceChoice)?.Id;
        string? selectedDeviceName = selectedDeviceId is null ? null : _activityDevices.GetValueOrDefault(selectedDeviceId);

        _refreshingLocalizedChoices = true;
        try
        {
            cmbImportListTarget.ItemsSource = new[]
            {
                new ImportListChoice(LocalizationManager.Get("Code.Lists.AllowlistLabel"), NextDnsListType.Allowlist),
                new ImportListChoice(LocalizationManager.Get("Code.Lists.BlocklistLabel"), NextDnsListType.Denylist)
            };
            cmbImportListTarget.SelectedItem = cmbImportListTarget.Items
                .OfType<ImportListChoice>().FirstOrDefault(choice => choice.Type == selectedList) ?? cmbImportListTarget.Items[0];

            cmbActivityStatus.ItemsSource = new[]
            {
                new ActivityStatusChoice(LocalizationManager.Get("Code.Activity.AllResults"), null),
                new ActivityStatusChoice(LocalizationManager.Get("Code.Activity.StatusDefault"), NextDnsLogStatus.Default),
                new ActivityStatusChoice(LocalizationManager.Get("Code.Activity.StatusError"), NextDnsLogStatus.Error),
                new ActivityStatusChoice(LocalizationManager.Get("Code.Activity.StatusBlocked"), NextDnsLogStatus.Blocked),
                new ActivityStatusChoice(LocalizationManager.Get("Code.Activity.StatusAllowed"), NextDnsLogStatus.Allowed)
            };
            cmbActivityStatus.SelectedItem = cmbActivityStatus.Items
                .OfType<ActivityStatusChoice>().FirstOrDefault(choice => choice.Status == selectedStatus) ?? cmbActivityStatus.Items[0];
            RebuildActivityDeviceChoices(selectedDeviceId, selectedDeviceName);
        }
        finally { _refreshingLocalizedChoices = false; }
        SetApiKeyVisibilityLabel();
        UpdateActivityEmptyDescription();
    }

    private void RefreshLocalizedCodeMessages()
    {
        if (lblApiConnectionStatus is not null)
            lblApiConnectionStatus.Text = LocalizationManager.TranslateExistingText(lblApiConnectionStatus.Text);
        if (lblListsMessage is not null)
            lblListsMessage.Text = LocalizationManager.TranslateExistingText(lblListsMessage.Text);
        if (lblActivityMessage is not null)
            lblActivityMessage.Text = LocalizationManager.TranslateExistingText(lblActivityMessage.Text);
        if (lblApiSettingsStatus is not null)
            lblApiSettingsStatus.Text = LocalizationManager.TranslateExistingText(lblApiSettingsStatus.Text);
        if (lblBulkImportResult is not null)
            lblBulkImportResult.Text = LocalizationManager.TranslateExistingText(lblBulkImportResult.Text);
        if (lblImportPreviewSummary is not null)
            lblImportPreviewSummary.Text = LocalizationManager.TranslateExistingText(lblImportPreviewSummary.Text);
        if (lblImportIssues is not null)
            lblImportIssues.Text = LocalizationManager.TranslateExistingText(lblImportIssues.Text);
        if (lblActivityDateValidation is not null)
            lblActivityDateValidation.Text = LocalizationManager.TranslateExistingText(lblActivityDateValidation.Text);
        if (lblProfileMessage is not null)
            lblProfileMessage.Text = LocalizationManager.TranslateExistingText(lblProfileMessage.Text);
        if (updateProgressText is not null)
            updateProgressText.Text = LocalizationManager.TranslateExistingText(updateProgressText.Text);
        if (_dnsSummary is not null)
            _dnsSummary.Text = LocalizationManager.TranslateExistingText(_dnsSummary.Text);
        if (btnUpdate is not null && !string.IsNullOrEmpty(btnUpdate.Content as string))
            btnUpdate.Content = LocalizationManager.TranslateExistingText((string)btnUpdate.Content);

        if (lstImportIssues.ItemsSource is IEnumerable<string> importIssues)
            lstImportIssues.ItemsSource = importIssues.Select(LocalizationManager.TranslateExistingText).ToArray();

        if (btnBlockedOnly is not null)
            btnBlockedOnly.Content = cmbActivityStatus.SelectedItem is ActivityStatusChoice { Status: NextDnsLogStatus.Blocked }
                ? LocalizationManager.Get("Code.Activity.BlockedOnlyActive")
                : LocalizationManager.Get("Main.Ui.Activity.BlockedOnly");
        if (btnToggleActivityStream is not null)
            btnToggleActivityStream.Content = LocalizationManager.Get(_activityStreamCancellation is not null
                ? "Code.Activity.StopLive"
                : "Main.Ui.Activity.Live");

        UpdateActivityEmptyDescription();
    }

    private async Task RestoreStoredApiKeyAsync()
    {
        try
        {
            string? key = _apiKeyStore.Load();
            if (string.IsNullOrEmpty(key)) return;
            SetApiConnectionStatus(LocalizationManager.Get("Code.Api.VerifyingSavedKey"), "TextSecondary");
            await VerifyAndConnectApiKeyAsync(key, saveAfterVerification: false);
        }
        catch (Exception exception)
        {
            SetApiConnectionStatus(LocalizationManager.T("Code.Api.LoadSavedKeyFailed", UiErrorLocalizer.Localize(exception)), "StatusAttention");
        }
        finally
        {
            UpdateApiControlStates();
        }
    }

    private async void BtnVerifyApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (_apiOperationInProgress) return;
        string key = CurrentApiKeyText;
        try
        {
            if (string.IsNullOrWhiteSpace(key))
                key = _apiKeyStore.Load() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                SetApiConnectionStatus(LocalizationManager.Get("Code.Api.EnterOrSaveKey"), "StatusAttention");
                return;
            }

            await VerifyAndConnectApiKeyAsync(key, saveAfterVerification: false);
        }
        catch (Exception exception)
        {
            SetApiConnectionStatus(LocalizationManager.T("Code.Api.VerifyKeyFailed", UiErrorLocalizer.Localize(exception)), "StatusAttention");
        }
        finally
        {
            UpdateApiControlStates();
        }
    }

    private void BtnSaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (_apiOperationInProgress) return;
        string key = CurrentApiKeyText;
        if (!IsCurrentKeyVerified(key))
        {
            SetApiConnectionStatus(LocalizationManager.Get("Code.Api.VerifyBeforeSave"), "StatusAttention");
            return;
        }

        try
        {
            _apiKeyStore.Save(key);
            ClearApiKeyInput();
            SetApiConnectionStatus(LocalizationManager.Get("Code.Api.KeySavedProtected"), "StatusProtected");
        }
        catch (Exception exception)
        {
            SetApiConnectionStatus(LocalizationManager.T("Code.Api.SaveKeyFailed", UiErrorLocalizer.Localize(exception)), "StatusAttention");
        }
        finally
        {
            UpdateApiControlStates();
        }
    }

    private async void BtnDeleteApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (_apiOperationInProgress) return;
        System.Windows.MessageBoxResult confirm = System.Windows.MessageBox.Show(
            this,
            LocalizationManager.Get("Code.Api.DeleteKeyConfirmation"),
            LocalizationManager.Get("Code.Api.DeleteKeyTitle"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        _apiOperationInProgress = true;
        if (_apiKeyVisible)
            ConcealApiKeyInput();
        UpdateApiControlStates();
        NextDnsApiClient? previousClient = _nextDnsApiClient;
        bool disconnected = false;
        try
        {
            _apiKeyStore.Delete();
            _nextDnsApiClient = null;
            disconnected = true;
            ClearVerifiedApiKeyHash();
            ClearApiKeyInput();
            _bindingApiProfiles = true;
            try { cmbApiProfile.ItemsSource = null; }
            finally { _bindingApiProfiles = false; }
            ClearApiProfileData();
            await StopActivityStreamAsync(LocalizationManager.Get("Code.Api.KeyDeletedStreamStopped"));
            previousClient?.Dispose();
            previousClient = null;
            SetApiConnectionStatus(LocalizationManager.Get("Code.Api.KeyDeletedDisconnected"), "TextSecondary");
        }
        catch (Exception exception)
        {
            SetApiConnectionStatus(LocalizationManager.T("Code.Api.DeleteKeyFailed", UiErrorLocalizer.Localize(exception)), "StatusAttention");
        }
        finally
        {
            if (disconnected) previousClient?.Dispose();
            _apiOperationInProgress = false;
            UpdateApiControlStates();
        }
    }

    private async Task<bool> VerifyAndConnectApiKeyAsync(string apiKey, bool saveAfterVerification)
    {
        if (_apiOperationInProgress) return false;
        string normalizedKey;
        try { normalizedKey = NextDnsApiClient.ValidateApiKey(apiKey); }
        catch (ArgumentException exception)
        {
            string sessionNote = _nextDnsApiClient is null
                ? LocalizationManager.Get("Code.Api.NoActiveSessionSuffix")
                : LocalizationManager.Get("Code.Api.PreviousSessionStillConnectedSuffix");
            SetApiConnectionStatus(LocalizationManager.T("Code.Api.InvalidKey", UiErrorLocalizer.Localize(exception), sessionNote), "StatusAttention");
            return false;
        }

        _apiOperationInProgress = true;
        if (_apiKeyVisible)
            ConcealApiKeyInput();
        UpdateApiControlStates();
        bool hadPreviousSession = _nextDnsApiClient is not null;
        NextDnsApiClient? previousClient = _nextDnsApiClient;
        _nextDnsApiClient = null;
        ClearVerifiedApiKeyHash();
        _bindingApiProfiles = true;
        try { cmbApiProfile.ItemsSource = null; }
        finally { _bindingApiProfiles = false; }
        ClearApiProfileData();
        SetApiConnectionStatus(LocalizationManager.Get("Code.Api.VerifyingKeyWithNextDns"), "TextSecondary");

        NextDnsApiClient? candidate = null;
        bool connected = false;
        try
        {
            await StopActivityStreamAsync(LocalizationManager.Get("Code.Api.AnotherCredentialStreamStopped"));
            previousClient?.Dispose();
            previousClient = null;

            candidate = new NextDnsApiClient(normalizedKey);
            IReadOnlyList<NextDnsProfileRecord> profiles = await candidate.GetProfilesAsync();
            if (saveAfterVerification)
                _apiKeyStore.Save(normalizedKey);

            _nextDnsApiClient = candidate;
            candidate = null;
            ReplaceVerifiedApiKeyHash(normalizedKey);
            IReadOnlyList<ApiProfileChoice> choices = profiles
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
                .GroupBy(profile => profile.Id!, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ApiProfileChoice(group.Key, string.IsNullOrWhiteSpace(group.First().Name) ? group.Key : group.First().Name!))
                .ToArray();

            _bindingApiProfiles = true;
            try
            {
                cmbApiProfile.ItemsSource = choices;
                cmbApiProfile.SelectedItem = choices.FirstOrDefault();
            }
            finally { _bindingApiProfiles = false; }
            connected = true;

            string status = choices.Count == 0
                ? LocalizationManager.Get("Code.Api.VerifiedNoProfiles")
                : LocalizationManager.T("Code.Api.ConnectedProfileCount", choices.Count);
            SetApiConnectionStatus(status, "StatusProtected");
        }
        catch (Exception exception)
        {
            candidate?.Dispose();
            string sessionNote = hadPreviousSession
                ? LocalizationManager.Get("Code.Api.PreviousSessionDisconnectedSuffix")
                : LocalizationManager.Get("Code.Api.NoActiveSessionSuffix");
            SetApiConnectionStatus(LocalizationManager.T("Code.Api.VerifyKeyFailedWithSession", UiErrorLocalizer.Localize(exception), sessionNote), "StatusAttention");
        }
        finally
        {
            previousClient?.Dispose();
            candidate?.Dispose();
            _apiOperationInProgress = false;
            UpdateApiControlStates();
        }

        if (connected && SelectedApiProfileId is not null)
        {
            await RefreshApiListsAsync();
            QueueActivityAutoLoadIfNeeded();
        }
        return connected;
    }

    private async void ApiProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bindingApiProfiles) return;
        QueueProfileSettingsLoad();
        if (_apiOperationInProgress)
        {
            CancelActivityPageLoad();
            return;
        }
        _apiOperationInProgress = true;
        UpdateApiControlStates();
        string? selectedProfileId = SelectedApiProfileId;
        InvalidateImportPreview();
        lstAllowedDomains.SelectedItem = null;
        lstBlockedDomains.SelectedItem = null;
        ClearApiProfileData();
        try
        {
        await StopActivityStreamAsync(LocalizationManager.Get("Code.Api.ProfileChangedStreamStopped"));
        }
        finally
        {
            _apiOperationInProgress = false;
            UpdateApiControlStates();
        }
        if (_nextDnsApiClient is not null && selectedProfileId is not null &&
            string.Equals(selectedProfileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase))
        {
            _ = RefreshApiListsAsync();
            QueueActivityAutoLoadIfNeeded();
        }
    }

    private async void BtnAddAllowedDomain_Click(object sender, RoutedEventArgs e) =>
        await AddDomainAsync(NextDnsListType.Allowlist);

    private async void BtnAddBlockedDomain_Click(object sender, RoutedEventArgs e) =>
        await AddDomainAsync(NextDnsListType.Denylist);

    private async Task AddDomainAsync(NextDnsListType listType)
    {
        string domain;
        try { domain = NextDnsDomain.Normalize(txtListDomain.Text); }
        catch (ArgumentException exception)
        {
            SetListsMessage(UiErrorLocalizer.Localize(exception), "StatusAttention");
            return;
        }

        await ExecuteListWriteAsync(async (client, profileId) =>
        {
            await client.AddListEntryAsync(profileId, listType, domain, active: true);
            txtListDomain.Clear();
            SetListsMessage(LocalizationManager.T("Code.Lists.Added", domain), "StatusProtected");
        });
    }

    private async void AllowedDomainSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NextDnsListEntry entry } || !_allowedDomains.Contains(entry)) return;
        await ToggleDomainAsync(NextDnsListType.Allowlist, entry);
    }

    private async void BlockedDomainSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NextDnsListEntry entry } || !_blockedDomains.Contains(entry)) return;
        await ToggleDomainAsync(NextDnsListType.Denylist, entry);
    }

    private async void AllowedDomainRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NextDnsListEntry entry } || !_allowedDomains.Contains(entry)) return;
        await RemoveDomainAsync(NextDnsListType.Allowlist, entry);
    }

    private async void BlockedDomainRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NextDnsListEntry entry } || !_blockedDomains.Contains(entry)) return;
        await RemoveDomainAsync(NextDnsListType.Denylist, entry);
    }

    private async Task RemoveDomainAsync(NextDnsListType listType, NextDnsListEntry? entry)
    {
        if (entry is null || !IsCurrentListEntry(listType, entry) || _nextDnsApiClient is null ||
            string.IsNullOrWhiteSpace(SelectedApiProfileId) || _apiOperationInProgress) return;
        System.Windows.MessageBoxResult confirm = System.Windows.MessageBox.Show(
            this,
            LocalizationManager.T("Code.Lists.DeleteConfirmation", entry.Id),
            LocalizationManager.Get("Code.Lists.DeleteTitle"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        if (!IsCurrentListEntry(listType, entry)) return;
        await ExecuteListWriteAsync(async (client, profileId) =>
        {
            await client.DeleteListEntryAsync(profileId, listType, entry.Id);
            SetListsMessage(LocalizationManager.T("Code.Lists.Deleted", entry.Id), "StatusProtected");
        });
    }

    private async Task ToggleDomainAsync(NextDnsListType listType, NextDnsListEntry? entry)
    {
        if (entry is null || !IsCurrentListEntry(listType, entry) || _nextDnsApiClient is null ||
            string.IsNullOrWhiteSpace(SelectedApiProfileId) || _apiOperationInProgress) return;
        bool nextState = !entry.Active;
        await ExecuteListWriteAsync(async (client, profileId) =>
        {
            await client.UpdateListEntryAsync(profileId, listType, entry.Id, active: nextState);
            SetListsMessage(LocalizationManager.T(nextState ? "Code.Lists.Activated" : "Code.Lists.Deactivated", entry.Id), "StatusProtected");
        });
    }

    private bool IsCurrentListEntry(NextDnsListType listType, NextDnsListEntry entry) =>
        (listType == NextDnsListType.Allowlist ? _allowedDomains : _blockedDomains).Any(candidate =>
            ReferenceEquals(candidate, entry) && !string.IsNullOrWhiteSpace(candidate.Id));

    private async void BtnRefreshDomainLists_Click(object sender, RoutedEventArgs e) => await RefreshApiListsAsync();

    private async Task ExecuteListWriteAsync(Func<NextDnsApiClient, string, Task> operation)
    {
        NextDnsApiClient? client = _nextDnsApiClient;
        string? profileId = SelectedApiProfileId;
        if (client is null || string.IsNullOrWhiteSpace(profileId) || _apiOperationInProgress) return;

        bool succeeded = false;
        _apiOperationInProgress = true;
        UpdateApiControlStates();
        try
        {
            await operation(client, profileId);
            succeeded = true;
        }
        catch (Exception exception)
        {
            SetListsMessage(LocalizationManager.T("Code.Lists.WriteFailed", UiErrorLocalizer.Localize(exception)), "StatusAttention");
        }
        finally
        {
            _apiOperationInProgress = false;
            UpdateApiControlStates();
        }

        if (succeeded && ReferenceEquals(client, _nextDnsApiClient) &&
            string.Equals(profileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase))
            await RefreshApiListsAsync();
    }

    private async Task RefreshApiListsAsync()
    {
        NextDnsApiClient? client = _nextDnsApiClient;
        string? profileId = SelectedApiProfileId;
        if (client is null || string.IsNullOrWhiteSpace(profileId) || _apiOperationInProgress) return;

        _apiOperationInProgress = true;
        UpdateApiControlStates();
        try
        {
            Task<IReadOnlyList<NextDnsListEntry>> allowTask = client.GetListAsync(profileId, NextDnsListType.Allowlist);
            Task<IReadOnlyList<NextDnsListEntry>> denyTask = client.GetListAsync(profileId, NextDnsListType.Denylist);
            await Task.WhenAll(allowTask, denyTask);
            if (!ReferenceEquals(client, _nextDnsApiClient) ||
                !string.Equals(profileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase)) return;

            IReadOnlyList<NextDnsListEntry> allowed = await allowTask;
            IReadOnlyList<NextDnsListEntry> blocked = await denyTask;
            ReplaceCollection(_allowedDomains, allowed);
            ReplaceCollection(_blockedDomains, blocked);
            lblAllowListCount.Text = allowed.Count.ToString(CultureInfo.InvariantCulture);
            lblBlockListCount.Text = blocked.Count.ToString(CultureInfo.InvariantCulture);
            pnlAllowedEmpty.Visibility = allowed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            pnlBlockedEmpty.Visibility = blocked.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SetListsMessage(LocalizationManager.T("Code.Lists.Refreshed", allowed.Count, blocked.Count), "TextSecondary");
        }
        catch (Exception exception)
        {
            SetListsMessage(LocalizationManager.T("Code.Lists.ReadFailed", UiErrorLocalizer.Localize(exception)), "StatusAttention");
        }
        finally
        {
            _apiOperationInProgress = false;
            UpdateApiControlStates();
        }
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (T value in values) target.Add(value);
    }

    private void ApiListSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateApiControlStates();

    private void ListDomain_TextChanged(object sender, TextChangedEventArgs e) => UpdateApiControlStates();

    private async void BtnOpenImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = LocalizationManager.Get("Code.Import.OpenFileTitle"),
            Filter = LocalizationManager.Get("Code.Import.OpenFileFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var file = new FileInfo(dialog.FileName);
            if (file.Length > MaximumImportFileBytes)
                throw new InvalidOperationException(LocalizationManager.Get("Code.Import.FileTooLarge"));
            txtBulkDomainImport.Text = await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8);
            SetBulkImportResult(LocalizationManager.Get("Code.Import.FileLoaded"), "TextSecondary");
        }
        catch (Exception exception)
        {
            SetBulkImportResult(LocalizationManager.T("Code.Import.ReadFileFailed", UiErrorLocalizer.Localize(exception)), "StatusAttention");
        }
    }

    private void OpenImportFileForList(NextDnsListType listType)
    {
        cmbImportListTarget.SelectedValue = listType;
        expListImport.IsExpanded = true;
        btnOpenImportFile.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    }

    private async void BtnPreviewDomainImport_Click(object sender, RoutedEventArgs e)
    {
        NextDnsApiClient? client = _nextDnsApiClient;
        string? profileId = SelectedApiProfileId;
        ImportListChoice? target = cmbImportListTarget.SelectedItem as ImportListChoice;
        if (client is null || profileId is null || target is null || _apiOperationInProgress) return;

        ApiListImportPreview preview = ParseListImport(txtBulkDomainImport.Text);
        _domainImportPreview = preview;
        _previewedImportText = txtBulkDomainImport.Text;
        _previewedImportProfileId = profileId;
        _previewedImportListType = target.Type;
        _previewPreflightComplete = false;
        lstImportPreview.ItemsSource = preview.Entries;
        lstImportIssues.ItemsSource = preview.Issues;
        int activeCount = preview.Entries.Count(entry => entry.Active);
        lblImportPreviewSummary.Text = LocalizationManager.T("Code.Import.PreviewSummary",
            preview.Entries.Count, activeCount, preview.Entries.Count - activeCount, preview.InvalidCount, preview.DuplicateCount);
        lblImportIssues.Text = preview.Issues.Count == 0
            ? LocalizationManager.Get("Code.Import.NoIssues")
            : LocalizationManager.Get("Code.Import.ReviewIssues");
        SetBulkImportResult(string.Empty, "TextSecondary");
        if (preview.Entries.Count == 0)
        {
            UpdateApiControlStates();
            return;
        }

        _apiOperationInProgress = true;
        UpdateApiControlStates();
        try
        {
            IReadOnlyList<NextDnsListEntry> existing = await client.GetListAsync(profileId, target.Type);
            if (!ReferenceEquals(client, _nextDnsApiClient) ||
                !string.Equals(profileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase) ||
                target.Type != (cmbImportListTarget.SelectedItem as ImportListChoice)?.Type ||
                !string.Equals(_previewedImportText, txtBulkDomainImport.Text, StringComparison.Ordinal)) return;
            var existingIds = new HashSet<string>(existing.Select(entry => entry.Id), StringComparer.OrdinalIgnoreCase);
            int alreadyPresent = preview.Entries.Count(entry => existingIds.Contains(entry.Id));
            int newEntries = preview.Entries.Count - alreadyPresent;
            lblImportPreviewSummary.Text += LocalizationManager.T("Code.Import.PreviewSummaryExisting", newEntries, alreadyPresent);
            _previewPreflightComplete = true;
        }
        catch (Exception exception)
        {
            lblImportIssues.Text = LocalizationManager.T("Code.Import.CompareFailed", UiErrorLocalizer.Localize(exception));
        }
        finally
        {
            _apiOperationInProgress = false;
            UpdateApiControlStates();
        }
    }

    private static ApiListImportPreview ParseListImport(string text)
    {
        string trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            DomainImportResult parsed = NextDnsDomain.ParseImport(text);
            IReadOnlyList<string> issues = parsed.InvalidLines
                .Select(issue => LocalizationManager.T("Code.Import.LineIssue", issue.LineNumber, issue.Input, UiErrorLocalizer.Localize(issue.Reason)))
                .Concat(parsed.DuplicateLines.Select(issue => LocalizationManager.T("Code.Import.LineIssue", issue.LineNumber, issue.Input, UiErrorLocalizer.Localize(issue.Reason))))
                .ToArray();
            return new ApiListImportPreview(
                parsed.Domains.Select(domain => new NextDnsListEntry { Id = domain, Active = true }).ToArray(),
                issues,
                parsed.InvalidLines.Count,
                parsed.DuplicateLines.Count,
                ImportedFromStateFile: false);
        }

        var entries = new List<NextDnsListEntry>();
        var issuesFromFile = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int invalidCount = 0;
        int duplicateCount = 0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return new ApiListImportPreview(Array.Empty<NextDnsListEntry>(), new[] { LocalizationManager.Get("Code.Import.JsonArrayRequired") }, 1, 0, true);

            int index = 0;
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                index++;
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("active", out JsonElement activeElement) ||
                    activeElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    invalidCount++;
                    issuesFromFile.Add(LocalizationManager.T("Code.Import.EntryMissingFields", index));
                    continue;
                }

                string input = idElement.GetString() ?? string.Empty;
                try
                {
                    string domain = NextDnsDomain.Normalize(input);
                    if (!seen.Add(domain))
                    {
                        duplicateCount++;
                        issuesFromFile.Add(LocalizationManager.T("Code.Import.DuplicateEntry", index, domain));
                        continue;
                    }
                    entries.Add(new NextDnsListEntry { Id = domain, Active = activeElement.GetBoolean() });
                }
                catch (ArgumentException exception)
                {
                    invalidCount++;
                    issuesFromFile.Add(LocalizationManager.T("Code.Import.EntryInvalid", index, UiErrorLocalizer.Localize(exception)));
                }
            }
        }
        catch (JsonException exception)
        {
            long line = (exception.LineNumber ?? 0) + 1;
            long column = (exception.BytePositionInLine ?? 0) + 1;
            return new ApiListImportPreview(Array.Empty<NextDnsListEntry>(), new[] { LocalizationManager.T("Code.Import.InvalidJsonAtPosition", line, column) }, 1, 0, true);
        }

        return new ApiListImportPreview(entries, issuesFromFile, invalidCount, duplicateCount, ImportedFromStateFile: true);
    }

    private void BulkDomainImport_TextChanged(object sender, TextChangedEventArgs e)
    {
        InvalidateImportPreview();
        UpdateApiControlStates();
    }

    private void ImportListTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cmbImportListTarget is null || _refreshingLocalizedChoices) return;
        InvalidateImportPreview();
        UpdateApiControlStates();
    }

    private void InvalidateImportPreview()
    {
        _domainImportPreview = null;
        _previewedImportText = null;
        _previewedImportProfileId = null;
        _previewedImportListType = null;
        _previewPreflightComplete = false;
        if (lstImportPreview is not null) lstImportPreview.ItemsSource = null;
        if (lstImportIssues is not null) lstImportIssues.ItemsSource = null;
        if (lblImportPreviewSummary is not null) lblImportPreviewSummary.Text = LocalizationManager.Get("Code.Import.PreviewInvalidated");
        if (lblImportIssues is not null) lblImportIssues.Text = string.Empty;
    }

    private async void BtnImportDomainList_Click(object sender, RoutedEventArgs e)
    {
        ApiListImportPreview? preview = _domainImportPreview;
        NextDnsApiClient? client = _nextDnsApiClient;
        ImportListChoice? target = cmbImportListTarget.SelectedItem as ImportListChoice;
        string? profileId = SelectedApiProfileId;
        if (client is null || preview is null || target is null || profileId is null || !_previewPreflightComplete ||
            !string.Equals(_previewedImportText, txtBulkDomainImport.Text, StringComparison.Ordinal) ||
            !string.Equals(_previewedImportProfileId, profileId, StringComparison.OrdinalIgnoreCase) ||
            _previewedImportListType != target.Type || preview.Entries.Count == 0 || _apiOperationInProgress)
            return;

        _apiOperationInProgress = true;
        UpdateApiControlStates();
        SetBulkImportResult(LocalizationManager.Get("Code.Import.ImportingNewEntries"), "TextSecondary");
        int added = 0;
        int skippedExisting = 0;
        int notAttempted = 0;
        var failures = new List<string>();
        try
        {
            IReadOnlyList<NextDnsListEntry> latest = await client.GetListAsync(profileId, target.Type);
            var existingIds = new HashSet<string>(latest.Select(entry => entry.Id), StringComparer.OrdinalIgnoreCase);
            NextDnsListEntry[] newEntries = preview.Entries.Where(entry => !existingIds.Contains(entry.Id)).ToArray();
            skippedExisting = preview.Entries.Count - newEntries.Length;
            for (int index = 0; index < newEntries.Length; index++)
            {
                NextDnsListEntry entry = newEntries[index];
                SetBulkImportResult(LocalizationManager.T("Code.Import.AddingEntryProgress", index + 1, newEntries.Length), "TextSecondary");
                try
                {
                    await client.AddListEntryAsync(profileId, target.Type, entry.Id, active: entry.Active);
                    added++;
                }
                catch (Exception exception)
                {
                    failures.Add(LocalizationManager.T("Code.Import.FailedEntry", entry.Id, UiErrorLocalizer.Localize(exception)));
                    notAttempted = newEntries.Length - index - 1;
                    break;
                }
            }

            _previewPreflightComplete = false;
            SetBulkImportResult(
                LocalizationManager.T("Code.Import.ResultSummary", added, skippedExisting, failures.Count, notAttempted),
                failures.Count == 0 ? "StatusProtected" : "StatusAttention");
            if (failures.Count > 0)
            {
                lstImportIssues.ItemsSource = preview.Issues.Concat(failures).ToArray();
                lblImportIssues.Text = LocalizationManager.Get("Code.Import.ExistingEntriesUnchangedWarning");
            }
            else
            {
                lblImportIssues.Text = LocalizationManager.Get("Code.Import.SafeImportExistingEntriesUnchanged");
            }
        }
        catch (Exception exception)
        {
            SetBulkImportResult(LocalizationManager.T("Code.Import.CompleteFailed", UiErrorLocalizer.Localize(exception)), "StatusAttention");
        }
        finally
        {
            _apiOperationInProgress = false;
            UpdateApiControlStates();
        }

        if (ReferenceEquals(client, _nextDnsApiClient) &&
            string.Equals(profileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase))
            await RefreshApiListsAsync();
    }

    private void ExportList(NextDnsListType listType)
    {
        IReadOnlyList<NextDnsListEntry> entries = listType == NextDnsListType.Allowlist
            ? _allowedDomains.ToArray()
            : _blockedDomains.ToArray();
        string listName = LocalizationManager.Get(listType == NextDnsListType.Allowlist ? "Code.Lists.Allowlist" : "Code.Lists.Blocklist");
        if (entries.Count == 0)
        {
            SetListsMessage(LocalizationManager.T("Code.Export.EmptyList", listName), "TextSecondary");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationManager.Get("Code.Export.Title"),
            FileName = $"nextdns-{(listType == NextDnsListType.Allowlist ? "allowlist" : "denylist")}.json",
            Filter = LocalizationManager.Get("Code.Export.Filter"),
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var exportData = entries.Select(entry => new { id = entry.Id, active = entry.Active }).ToArray();
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(exportData, _prettyJson), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetListsMessage(LocalizationManager.T("Code.Export.Completed", entries.Count, dialog.FileName), "StatusProtected");
        }
        catch (Exception exception)
        {
            SetListsMessage(LocalizationManager.T("Code.Export.Failed", UiErrorLocalizer.Localize(exception)), "StatusAttention");
        }
    }

    private async void BtnRefreshActivity_Click(object sender, RoutedEventArgs e)
    {
        await StopActivityStreamAsync(LocalizationManager.Get("Code.Activity.RefreshStoppedStream"));
        await LoadActivityPageAsync(loadMore: false);
    }

    private async void BtnLoadMoreActivity_Click(object sender, RoutedEventArgs e) => await LoadActivityPageAsync(loadMore: true);

    private async void BtnToggleActivityStream_Click(object sender, RoutedEventArgs e)
    {
        if (_activityStreamCancellation is not null)
        {
            await StopActivityStreamAsync(LocalizationManager.Get("Code.Activity.StreamStoppedByUser"));
            return;
        }
        await StartActivityStreamAsync();
    }

    private async Task StartActivityStreamAsync()
    {
        NextDnsApiClient? client = _nextDnsApiClient;
        string? profileId = SelectedApiProfileId;
        if (client is null || profileId is null || _apiOperationInProgress || _activityStreamCancellation is not null) return;
        if (!string.IsNullOrWhiteSpace(txtActivityFrom.Text) || !string.IsNullOrWhiteSpace(txtActivityTo.Text))
        {
            SetActivityMessage(LocalizationManager.Get("Code.Activity.LiveDateFiltersUnavailable"), "StatusAttention");
            return;
        }

        if (_activityQuery is null)
            await LoadActivityPageAsync(loadMore: false);
        if (!ReferenceEquals(client, _nextDnsApiClient) ||
            !string.Equals(profileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase) ||
            _apiOperationInProgress || _activityStreamCancellation is not null)
            return;

        NextDnsLogsQuery currentQuery;
        try { currentQuery = BuildActivityQuery(); }
        catch (Exception exception)
        {
            SetActivityMessage(UiErrorLocalizer.Localize(exception), "StatusAttention");
            return;
        }

        var streamQuery = new NextDnsLogStreamQuery
        {
            Id = _activityStreamId,
            Device = currentQuery.Device,
            Status = currentQuery.Status,
            Search = currentQuery.Search
        };
        var cancellation = new CancellationTokenSource();
        _activityStreamCancellation = cancellation;
        _activityStreamStopMessage = null;
        _activityStreamEventCount = 0;
        UpdateApiControlStates();
        SetActivityMessage(LocalizationManager.Get("Code.Activity.ConnectingLive"), "TextSecondary");
        _activityStreamTask = ReadActivityStreamAsync(client, profileId, streamQuery, cancellation);
    }

    private async Task ReadActivityStreamAsync(
        NextDnsApiClient client,
        string profileId,
        NextDnsLogStreamQuery query,
        CancellationTokenSource cancellation)
    {
        string? endMessage = null;
        string endForeground = "TextSecondary";
        int retryAttempt = 0;
        NextDnsLogStreamQuery reconnectQuery = query;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    await foreach (NextDnsLogStreamEvent streamEvent in client
                        .StreamLogsAsync(profileId, reconnectQuery, cancellation.Token)
                        .ConfigureAwait(false))
                    {
                        reconnectQuery = ResumeActivityStreamQuery(reconnectQuery, streamEvent.Id);
                        await Dispatcher.InvokeAsync(() => ApplyActivityStreamEvent(client, profileId, streamEvent));
                        retryAttempt = 0;
                    }

                    if (cancellation.IsCancellationRequested) break;
                    endMessage = LocalizationManager.Get("Code.Activity.LiveConnectionClosed");
                    endForeground = "StatusAttention";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (IsRetryableActivityStreamFailure(exception))
                {
                    endMessage = LocalizationManager.T("Code.Activity.LiveConnectionLost", UiErrorLocalizer.Localize(exception));
                    endForeground = "StatusAttention";
                }
                catch (Exception exception)
                {
                    endMessage = LocalizationManager.T("Code.Activity.StreamInterrupted", UiErrorLocalizer.Localize(exception));
                    endForeground = "StatusAttention";
                    break;
                }

                if (cancellation.IsCancellationRequested) break;

                TimeSpan delay = GetActivityStreamReconnectDelay(retryAttempt);
                retryAttempt = Math.Min(retryAttempt + 1, 5);
                string reconnectMessage = LocalizationManager.T("Code.Activity.Reconnecting", endMessage, delay.TotalSeconds);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_activityStreamCancellation, cancellation) &&
                        ReferenceEquals(client, _nextDnsApiClient) &&
                        string.Equals(profileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase))
                        SetActivityMessage(reconnectMessage, "StatusAttention");
                });

                try { await Task.Delay(delay, cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            }
        }
        catch (Exception exception)
        {
            endMessage = LocalizationManager.T("Code.Activity.StreamInterrupted", UiErrorLocalizer.Localize(exception));
            endForeground = "StatusAttention";
        }
        finally
        {
            if (cancellation.IsCancellationRequested)
            {
                endMessage = _activityStreamStopMessage ?? LocalizationManager.Get("Code.Activity.StreamStopped");
                endForeground = "TextSecondary";
            }

            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        bool ownsCurrentStream = ReferenceEquals(_activityStreamCancellation, cancellation);
                        bool sameProfile = ReferenceEquals(client, _nextDnsApiClient) &&
                            string.Equals(profileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase);
                        if (ownsCurrentStream)
                        {
                            _activityStreamCancellation = null;
                            _activityStreamTask = null;
                            _activityStreamStopMessage = null;
                            UpdateApiControlStates();
                        }
                        if (ownsCurrentStream && sameProfile && !string.IsNullOrWhiteSpace(endMessage))
                            SetActivityMessage(endMessage, endForeground);
                    });
                }
                catch (TaskCanceledException) { }
                catch (InvalidOperationException) { }
            }
            cancellation.Dispose();
        }
    }

    private static NextDnsLogStreamQuery ResumeActivityStreamQuery(
        NextDnsLogStreamQuery query,
        string? lastEventId) =>
        string.IsNullOrEmpty(lastEventId) ? query : query with { Id = lastEventId };

    private static bool IsRetryableActivityStreamFailure(Exception exception) =>
        exception is NextDnsApiException
        {
            Kind: NextDnsApiErrorKind.Network or NextDnsApiErrorKind.Timeout or
                NextDnsApiErrorKind.RateLimited or NextDnsApiErrorKind.ServiceUnavailable
        };

    private static TimeSpan GetActivityStreamReconnectDelay(int retryAttempt)
    {
        int boundedAttempt = Math.Clamp(retryAttempt, 0, 5);
        int seconds = boundedAttempt == 5 ? 30 : 1 << boundedAttempt;
        return TimeSpan.FromSeconds(seconds);
    }

    private void ApplyActivityStreamEvent(NextDnsApiClient client, string profileId, NextDnsLogStreamEvent streamEvent)
    {
        if (!ReferenceEquals(client, _nextDnsApiClient) ||
            !string.Equals(profileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase) ||
            _activityStreamCancellation?.IsCancellationRequested == true)
            return;

        if (!string.IsNullOrEmpty(streamEvent.Id))
        {
            _activityStreamId = streamEvent.Id;
            if (!_recentStreamEventIdSet.Add(streamEvent.Id)) return;
            _recentStreamEventIds.Enqueue(streamEvent.Id);
            while (_recentStreamEventIds.Count > MaximumRememberedStreamIds)
                _recentStreamEventIdSet.Remove(_recentStreamEventIds.Dequeue());
        }

        _dnsActivity.Insert(0, streamEvent.Entry);
        while (_dnsActivity.Count > MaximumActivityItems)
            _dnsActivity.RemoveAt(_dnsActivity.Count - 1);
        pnlActivityEmpty.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(streamEvent.Entry.Device?.Id) &&
            !_activityDevices.ContainsKey(streamEvent.Entry.Device.Id))
            MergeActivityDevices(new[] { streamEvent.Entry });
        _activityStreamEventCount++;
        SetActivityMessage(LocalizationManager.T("Code.Activity.LiveCount", _activityStreamEventCount, _dnsActivity.Count), "StatusProtected");
    }

    private async Task StopActivityStreamAsync(string message)
    {
        CancellationTokenSource? cancellation = _activityStreamCancellation;
        if (cancellation is null) return;
        _activityStreamStopMessage = message;
        cancellation.Cancel();
        Task? streamTask = _activityStreamTask;
        if (streamTask is null) return;
        try { await streamTask; }
        catch { /* The stream reader reports its own errors and always clears its state. */ }
    }

    private void CancelActivityStream(string message)
    {
        if (_activityStreamCancellation is null) return;
        _activityStreamStopMessage = message;
        _activityStreamCancellation.Cancel();
    }

    private void CancelActivityPageLoad()
    {
        try { _activityPageLoadCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ActivityPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!activityPage.IsVisible)
        {
            _activityAutoLoadPending = false;
            _activityAutoLoadIssuedForVisit = false;
            CancelActivityPageLoad();
            CancelActivityStream(LocalizationManager.Get("Code.Activity.LivePausedOnLeave"));
            return;
        }

        QueueActivityAutoLoadIfNeeded();
    }

    private void QueueActivityAutoLoadIfNeeded()
    {
        // A canceled request is still unwinding asynchronously. If the user
        // re-enters Activity before its finally block runs, queue the new
        // visit now and let TryStartPendingActivityAutoLoad start it once the
        // previous request releases the shared operation slot.
        bool currentLoadIsCanceled = _activityLoadInProgress &&
            _activityPageLoadCancellation?.IsCancellationRequested == true;
        bool shouldQueue = NextDnsActivityAutoLoadPolicy.ShouldQueueForCurrentVisit(
            activityVisible: activityPage.IsVisible,
            apiConnected: _nextDnsApiClient is not null,
            profileSelected: SelectedApiProfileId is not null,
            filtersNeedApply: _activityNeedsApply,
            requestAlreadyIssuedForVisit: _activityAutoLoadIssuedForVisit,
            activityRequestInProgress: _activityLoadInProgress && !currentLoadIsCanceled);
        if (!shouldQueue) return;

        _activityAutoLoadIssuedForVisit = true;
        _activityAutoLoadPending = true;
        TryStartPendingActivityAutoLoad();
    }

    private void TryStartPendingActivityAutoLoad()
    {
        if (!_activityAutoLoadPending) return;

        bool canStart = NextDnsActivityAutoLoadPolicy.ShouldStartQueuedLoad(
            activityVisible: activityPage.IsVisible,
            apiConnected: _nextDnsApiClient is not null,
            profileSelected: SelectedApiProfileId is not null,
            filtersNeedApply: _activityNeedsApply,
            autoLoadPending: _activityAutoLoadPending,
            activityRequestInProgress: _activityLoadInProgress,
            anotherApiOperationInProgress: _apiOperationInProgress);

        if (canStart)
        {
            _activityAutoLoadPending = false;
            _ = LoadActivityPageAsync(loadMore: false);
            return;
        }

        if (!activityPage.IsVisible || _nextDnsApiClient is null || SelectedApiProfileId is null ||
            _activityNeedsApply)
            _activityAutoLoadPending = false;
    }

    private void UpdateActivityEmptyDescription()
    {
        if (lblActivityEmptyDescription is null) return;
        lblActivityEmptyDescription.Text = _nextDnsApiClient is null
            ? LocalizationManager.Get("Main.Ui.Lists.ConnectApi")
            : SelectedApiProfileId is null
                ? LocalizationManager.Get("Code.Activity.SelectProfileEmpty")
                : _activityNeedsApply
                    ? LocalizationManager.Get("Code.Activity.ApplyFiltersEmpty")
                    : _activityLoadInProgress || _activityAutoLoadPending
                        ? LocalizationManager.Get("Code.Activity.LoadingProfileQueries")
                        : _activityQuery is not null
                            ? LocalizationManager.Get("Code.Activity.NoQueriesForFilters")
                            : LocalizationManager.Get("Code.Activity.QueriesAppearHere");
    }

    private async Task LoadActivityPageAsync(bool loadMore)
    {
        NextDnsApiClient? client = _nextDnsApiClient;
        string? profileId = SelectedApiProfileId;
        if (client is null || string.IsNullOrWhiteSpace(profileId) || _apiOperationInProgress) return;

        NextDnsLogsQuery query;
        try
        {
            if (loadMore)
            {
                if (_activityQuery is null || string.IsNullOrEmpty(_activityCursor)) return;
                query = _activityQuery with { Cursor = _activityCursor };
            }
            else
            {
                query = BuildActivityQuery();
            }
        }
        catch (Exception exception)
        {
            SetActivityMessage(UiErrorLocalizer.Localize(exception), "StatusAttention");
            return;
        }

        var pageLoadCancellation = new CancellationTokenSource();
        _activityPageLoadCancellation = pageLoadCancellation;
        CancellationToken cancellationToken = pageLoadCancellation.Token;
        _activityLoadInProgress = true;
        _activityAutoLoadPending = false;
        _activityAutoLoadIssuedForVisit = true;
        _apiOperationInProgress = true;
        UpdateApiControlStates();
        try
        {
            NextDnsPage<NextDnsLogEntry> page;
            IReadOnlyList<NextDnsAnalyticsDevice> analyticsDevices = Array.Empty<NextDnsAnalyticsDevice>();
            Exception? analyticsDevicesError = null;
            if (loadMore)
            {
                page = await client.GetLogsAsync(profileId, query, cancellationToken);
            }
            else
            {
                Task<NextDnsPage<NextDnsLogEntry>> logsTask = client.GetLogsAsync(profileId, query, cancellationToken);
                Task<IReadOnlyList<NextDnsAnalyticsDevice>> devicesTask = client.GetAnalyticsDevicesAsync(
                    profileId, query.From, query.To, cancellationToken);

                try
                {
                    page = await logsTask;
                }
                catch
                {
                    try { await devicesTask; }
                    catch { /* Observe the paired request before propagating the log failure or cancellation. */ }
                    throw;
                }

                try
                {
                    analyticsDevices = await devicesTask;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    analyticsDevicesError = exception;
                }
            }

            if (!ReferenceEquals(client, _nextDnsApiClient) ||
                !string.Equals(profileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase) ||
                cancellationToken.IsCancellationRequested) return;

            if (!loadMore)
            {
                _dnsActivity.Clear();
                _activityDevices.Clear();
                _activityStreamId = page.StreamId;
                _recentStreamEventIds.Clear();
                _recentStreamEventIdSet.Clear();
                _activityQuery = query with { Cursor = null };
                _activityNeedsApply = false;
                MergeAnalyticsActivityDevices(analyticsDevices, query.Device);
            }
            int remaining = MaximumActivityItems - _dnsActivity.Count;
            foreach (NextDnsLogEntry entry in page.Items.Take(Math.Max(remaining, 0))) _dnsActivity.Add(entry);
            _activityCursor = _dnsActivity.Count < MaximumActivityItems ? page.NextCursor : null;
            MergeActivityDevices(page.Items, query.Device);
            pnlActivityEmpty.Visibility = _dnsActivity.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            string activityMessage = _dnsActivity.Count == 0
                ? LocalizationManager.Get("Code.Activity.NoQueriesFound")
                : _dnsActivity.Count >= MaximumActivityItems
                    ? LocalizationManager.T("Code.Activity.MaximumShown", MaximumActivityItems)
                    : LocalizationManager.T(string.IsNullOrEmpty(_activityCursor)
                        ? "Code.Activity.ShowingCountNoMore"
                        : "Code.Activity.ShowingCountHasMore", _dnsActivity.Count);
            if (analyticsDevicesError is not null)
                activityMessage += LocalizationManager.T("Code.Activity.DeviceCatalogUpdateFailed", UiErrorLocalizer.Localize(analyticsDevicesError));
            SetActivityMessage(activityMessage, analyticsDevicesError is null ? "TextSecondary" : "StatusAttention");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A profile, filter, visibility, or window change canceled this request intentionally.
        }
        catch (Exception exception)
        {
            SetActivityMessage(LocalizationManager.T("Code.Activity.LoadLogsFailed", UiErrorLocalizer.Localize(exception)), "StatusAttention");
        }
        finally
        {
            if (ReferenceEquals(_activityPageLoadCancellation, pageLoadCancellation))
                _activityPageLoadCancellation = null;
            pageLoadCancellation.Dispose();
            _activityLoadInProgress = false;
            _apiOperationInProgress = false;
            UpdateApiControlStates();
        }
    }

    private NextDnsLogsQuery BuildActivityQuery()
    {
        string? from = NormalizeLogDate(txtActivityFrom.Text, LocalizationManager.Get("Code.Activity.DateFrom"));
        string? to = NormalizeLogDate(txtActivityTo.Text, LocalizationManager.Get("Code.Activity.DateTo"));
        if (from is not null && to is not null &&
            DateTimeOffset.Parse(from, CultureInfo.InvariantCulture) > DateTimeOffset.Parse(to, CultureInfo.InvariantCulture))
            throw new ArgumentException(LocalizationManager.Get("Code.Activity.QueryRangeInvalid"));

        ActivityDeviceChoice? device = cmbActivityDevice.SelectedItem as ActivityDeviceChoice;
        ActivityStatusChoice? status = cmbActivityStatus.SelectedItem as ActivityStatusChoice;
        string search = txtActivitySearch.Text.Trim();
        if (search.Length > 512) throw new ArgumentException(LocalizationManager.Get("Code.Activity.SearchTooLong"));
        return new NextDnsLogsQuery
        {
            From = from,
            To = to,
            Device = string.IsNullOrWhiteSpace(device?.Id) ? null : device.Id,
            Status = status?.Status,
            Search = string.IsNullOrWhiteSpace(search) ? null : search,
            Limit = ApiLogPageSize,
            Sort = NextDnsLogSort.Descending
        };
    }

    private static string? NormalizeLogDate(string input, string fieldName)
    {
        string value = input.Trim();
        if (value.Length == 0) return null;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            throw new ArgumentException(LocalizationManager.T("Code.Activity.InvalidUtcDate", fieldName));
        return parsed.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private void MergeAnalyticsActivityDevices(IEnumerable<NextDnsAnalyticsDevice> devices, string? preferredId = null)
    {
        ActivityDeviceChoice? currentChoice = cmbActivityDevice.SelectedItem as ActivityDeviceChoice;
        string? selectedId = preferredId ?? currentChoice?.Id;
        string? selectedName = currentChoice?.Name;
        foreach (NextDnsAnalyticsDevice device in devices)
        {
            string? id = device.Id;
            if (string.IsNullOrWhiteSpace(id) ||
                string.Equals(id, "__UNIDENTIFIED__", StringComparison.OrdinalIgnoreCase)) continue;
            string name = string.IsNullOrWhiteSpace(device.Name) ? id : device.Name!;
            _activityDevices[id] = name;
        }

        RebuildActivityDeviceChoices(selectedId, selectedName);
    }

    private void MergeActivityDevices(IEnumerable<NextDnsLogEntry> entries, string? preferredId = null)
    {
        ActivityDeviceChoice? currentChoice = cmbActivityDevice.SelectedItem as ActivityDeviceChoice;
        string? selectedId = preferredId ?? currentChoice?.Id;
        string? selectedName = currentChoice?.Name;
        foreach (NextDnsLogEntry entry in entries)
        {
            string? id = entry.Device?.Id;
            if (string.IsNullOrWhiteSpace(id) ||
                string.Equals(id, "__UNIDENTIFIED__", StringComparison.OrdinalIgnoreCase)) continue;
            string name = string.IsNullOrWhiteSpace(entry.Device?.Name) ? id : entry.Device!.Name!;
            _activityDevices[id] = name;
        }

        RebuildActivityDeviceChoices(selectedId, selectedName);
    }

    private void RebuildActivityDeviceChoices(string? selectedId, string? selectedName)
    {
        var options = new List<ActivityDeviceChoice> { new(LocalizationManager.Get("Code.Activity.AllDevices"), null) };
        options.Add(new ActivityDeviceChoice(LocalizationManager.Get("Main.Ui.Common.UnknownDevice"), "__UNIDENTIFIED__"));
        options.AddRange(_activityDevices.OrderBy(pair => pair.Value, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => new ActivityDeviceChoice(pair.Value, pair.Key)));
        if (!string.IsNullOrWhiteSpace(selectedId) &&
            !string.Equals(selectedId, "__UNIDENTIFIED__", StringComparison.OrdinalIgnoreCase) &&
            !_activityDevices.ContainsKey(selectedId))
            options.Add(new ActivityDeviceChoice(string.IsNullOrWhiteSpace(selectedName) ? selectedId : selectedName, selectedId));

        _bindingActivityDevices = true;
        try
        {
            cmbActivityDevice.ItemsSource = options;
            cmbActivityDevice.SelectedItem = options.FirstOrDefault(option =>
                string.Equals(option.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        }
        finally { _bindingActivityDevices = false; }
    }

    private async void ActivityFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_bindingActivityDevices || _bindingActivityFilters || _refreshingLocalizedChoices || !_apiUiInitialized) return;
        CancelActivityPageLoad();
        await StopActivityStreamAsync(LocalizationManager.Get("Code.Activity.FiltersChangedStreamStopped"));
        _activityQuery = null;
        _activityCursor = null;
        _activityStreamId = null;
        _activityNeedsApply = true;
        _activityAutoLoadPending = false;
        _recentStreamEventIds.Clear();
        _recentStreamEventIdSet.Clear();
        _dnsActivity.Clear();
        pnlActivityEmpty.Visibility = Visibility.Visible;
        SetActivityMessage(LocalizationManager.Get("Code.Activity.FiltersChangedApplyAgain"), "TextSecondary");
        UpdateApiControlStates();
    }

    private async void BtnLoadApiSettings_Click(object sender, RoutedEventArgs e) => await LoadApiSettingsAsync();

    private async Task LoadApiSettingsAsync()
    {
        NextDnsApiClient? client = _nextDnsApiClient;
        string? profileId = SelectedApiProfileId;
        if (client is null || profileId is null || _apiOperationInProgress) return;

        _apiOperationInProgress = true;
        UpdateApiControlStates();
        SetApiSettingsStatus(LocalizationManager.Get("Code.ApiSettings.Reading"), "TextSecondary");
        try
        {
            Task<LogsSettingData> logsTask = client.GetSettingAsync<LogsSettingData>(profileId, NextDnsSetting.Logs);
            Task<BlockPageSettingData> blockPageTask = client.GetSettingAsync<BlockPageSettingData>(profileId, NextDnsSetting.BlockPage);
            await Task.WhenAll(logsTask, blockPageTask);
            if (!ReferenceEquals(client, _nextDnsApiClient) ||
                !string.Equals(profileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase)) return;

            LogsSettingData? logs = await logsTask;
            BlockPageSettingData? blockPage = await blockPageTask;
            _originalLogsEnabled = logs?.Enabled;
            _originalDropIp = logs?.Drop?.Ip;
            _originalDropDomain = logs?.Drop?.Domain;
            _originalBlockPageEnabled = blockPage?.Enabled;
            chkApiLogsEnabled.IsChecked = _originalLogsEnabled;
            chkApiDropIp.IsChecked = _originalDropIp;
            chkApiDropDomain.IsChecked = _originalDropDomain;
            chkApiBlockPageEnabled.IsChecked = _originalBlockPageEnabled;
            chkApiLogsEnabled.IsThreeState = !_originalLogsEnabled.HasValue;
            chkApiDropIp.IsThreeState = !_originalDropIp.HasValue;
            chkApiDropDomain.IsThreeState = !_originalDropDomain.HasValue;
            chkApiBlockPageEnabled.IsThreeState = !_originalBlockPageEnabled.HasValue;
            _apiSettingsLoaded = true;
            pnlApiSettingsValues.Visibility = Visibility.Visible;
            SetApiSettingsStatus(LocalizationManager.Get("Code.ApiSettings.Read"), "TextSecondary");
        }
        catch (Exception exception)
        {
            _apiSettingsLoaded = false;
            pnlApiSettingsValues.Visibility = Visibility.Collapsed;
            SetApiSettingsStatus(LocalizationManager.T("Code.ApiSettings.ReadFailed", UiErrorLocalizer.Localize(exception)), "StatusAttention");
        }
        finally
        {
            _apiOperationInProgress = false;
            UpdateApiControlStates();
        }
    }

    private async void BtnApplyApiSettings_Click(object sender, RoutedEventArgs e)
    {
        NextDnsApiClient? client = _nextDnsApiClient;
        string? profileId = SelectedApiProfileId;
        if (client is null || profileId is null || _apiOperationInProgress || !_apiSettingsLoaded) return;

        var logChanges = new Dictionary<string, object>();
        if (HasChanged(_originalLogsEnabled, chkApiLogsEnabled.IsChecked))
            logChanges["enabled"] = chkApiLogsEnabled.IsChecked!.Value;
        var dropChanges = new Dictionary<string, bool>();
        if (HasChanged(_originalDropIp, chkApiDropIp.IsChecked))
            dropChanges["ip"] = chkApiDropIp.IsChecked!.Value;
        if (HasChanged(_originalDropDomain, chkApiDropDomain.IsChecked))
            dropChanges["domain"] = chkApiDropDomain.IsChecked!.Value;
        if (dropChanges.Count > 0) logChanges["drop"] = dropChanges;
        bool patchLogs = logChanges.Count > 0;
        bool patchBlockPage = HasChanged(_originalBlockPageEnabled, chkApiBlockPageEnabled.IsChecked);
        if (!patchLogs && !patchBlockPage) return;

        var changes = new List<string>();
        if (HasChanged(_originalLogsEnabled, chkApiLogsEnabled.IsChecked))
            changes.Add(LocalizationManager.Get(chkApiLogsEnabled.IsChecked == true
                ? "Code.ApiSettings.EnableDnsLogs"
                : "Code.ApiSettings.DisableDnsLogs"));
        if (HasChanged(_originalDropIp, chkApiDropIp.IsChecked))
            changes.Add(LocalizationManager.Get(chkApiDropIp.IsChecked == true
                ? "Code.ApiSettings.StopSavingClientIps"
                : "Code.ApiSettings.SaveClientIps"));
        if (HasChanged(_originalDropDomain, chkApiDropDomain.IsChecked))
            changes.Add(LocalizationManager.Get(chkApiDropDomain.IsChecked == true
                ? "Code.ApiSettings.StopSavingQueriedDomains"
                : "Code.ApiSettings.SaveQueriedDomains"));
        if (patchBlockPage)
            changes.Add(LocalizationManager.Get(chkApiBlockPageEnabled.IsChecked == true
                ? "Code.ApiSettings.ShowBlockPage"
                : "Code.ApiSettings.HideBlockPage"));

        System.Windows.MessageBoxResult confirmation = System.Windows.MessageBox.Show(
            this,
            LocalizationManager.T("Code.ApiSettings.Confirmation", string.Join("\n• ", changes)),
            LocalizationManager.Get("Code.ApiSettings.ConfirmationTitle"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirmation != System.Windows.MessageBoxResult.Yes) return;

        int applied = 0;
        _apiOperationInProgress = true;
        UpdateApiControlStates();
        try
        {
            if (patchLogs)
            {
                await client.PatchSettingAsync<JsonElement>(profileId, NextDnsSetting.Logs, logChanges);
                applied++;
            }
            if (patchBlockPage)
            {
                await client.PatchSettingAsync<JsonElement>(profileId, NextDnsSetting.BlockPage,
                    new { enabled = chkApiBlockPageEnabled.IsChecked!.Value });
                applied++;
            }
            SetApiSettingsStatus(LocalizationManager.Get("Code.ApiSettings.Saved"), "StatusProtected");
        }
        catch (Exception exception)
        {
            string message = applied > 0
                ? LocalizationManager.T("Code.ApiSettings.PartiallySaved", UiErrorLocalizer.Localize(exception))
                : LocalizationManager.T("Code.ApiSettings.SaveFailed", UiErrorLocalizer.Localize(exception));
            SetApiSettingsStatus(message, "StatusAttention");
        }
        finally
        {
            _apiOperationInProgress = false;
            UpdateApiControlStates();
        }

        if (applied > 0)
            await LoadApiSettingsAsync();
    }

    private void ApiSettingCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateApiControlStates();

    private static bool HasChanged(bool? original, bool? current) => current.HasValue && current != original;

    private void ApiKeyPassword_Changed(object sender, RoutedEventArgs e) => UpdateApiControlStates();

    private void ApiKeyVisible_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_apiKeyVisibilityChanging)
            UpdateApiControlStates();
    }

    private void ApiKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (_apiKeyVisible)
        {
            ConcealApiKeyInput();
            return;
        }

        _apiKeyVisibilityChanging = true;
        try
        {
            string key = pwdApiKey.Password;
            txtApiKeyVisible.Text = key;
            pwdApiKey.Clear();
            pwdApiKey.Visibility = Visibility.Collapsed;
            txtApiKeyVisible.Visibility = Visibility.Visible;
            _apiKeyVisible = true;
            SetApiKeyVisibilityLabel();
            txtApiKeyVisible.Focus();
            txtApiKeyVisible.CaretIndex = txtApiKeyVisible.Text.Length;
        }
        finally
        {
            _apiKeyVisibilityChanging = false;
        }

        UpdateApiControlStates();
    }

    private void ConcealApiKeyInput()
    {
        if (!_apiKeyVisible) return;
        _apiKeyVisibilityChanging = true;
        try
        {
            string key = txtApiKeyVisible.Text;
            pwdApiKey.Password = key;
            txtApiKeyVisible.Clear();
            txtApiKeyVisible.Visibility = Visibility.Collapsed;
            pwdApiKey.Visibility = Visibility.Visible;
            _apiKeyVisible = false;
            SetApiKeyVisibilityLabel();
        }
        finally
        {
            _apiKeyVisibilityChanging = false;
        }

        UpdateApiControlStates();
    }

    private void ClearApiKeyInput()
    {
        _apiKeyVisibilityChanging = true;
        try
        {
            txtApiKeyVisible.Clear();
            pwdApiKey.Clear();
            _apiKeyVisible = false;
            txtApiKeyVisible.Visibility = Visibility.Collapsed;
            pwdApiKey.Visibility = Visibility.Visible;
            SetApiKeyVisibilityLabel();
        }
        finally
        {
            _apiKeyVisibilityChanging = false;
        }

        UpdateApiControlStates();
    }

    private void SetApiKeyVisibilityLabel()
    {
        string description = LocalizationManager.Get(_apiKeyVisible
            ? "Code.Api.HideKey"
            : "Main.Ui.Settings.ShowApiKey");
        ApiKeyVisibilityGlyph.Text = _apiKeyVisible ? "\uE8AC" : "\uE890";
        btnToggleApiKeyVisibility.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(btnToggleApiKeyVisibility, description);
    }

    private bool IsCurrentKeyVerified(string key)
    {
        if (_verifiedApiKeyHash is null || string.IsNullOrWhiteSpace(key)) return false;
        try
        {
            string normalized = NextDnsApiClient.ValidateApiKey(key);
            byte[] candidateHash = ComputeApiKeyHash(normalized);
            try { return CryptographicOperations.FixedTimeEquals(candidateHash, _verifiedApiKeyHash); }
            finally { CryptographicOperations.ZeroMemory(candidateHash); }
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void ReplaceVerifiedApiKeyHash(string key)
    {
        ClearVerifiedApiKeyHash();
        _verifiedApiKeyHash = ComputeApiKeyHash(key);
    }

    private static byte[] ComputeApiKeyHash(string key)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void ClearVerifiedApiKeyHash()
    {
        if (_verifiedApiKeyHash is null) return;
        CryptographicOperations.ZeroMemory(_verifiedApiKeyHash);
        _verifiedApiKeyHash = null;
    }

    private void UpdateApiControlStates()
    {
        if (!_apiUiInitialized) return;
        bool busy = _apiOperationInProgress;
        string apiKeyText = CurrentApiKeyText;
        bool connected = _nextDnsApiClient is not null;
        bool hasProfile = connected && !string.IsNullOrWhiteSpace(SelectedApiProfileId);
        ImportListChoice? importTarget = cmbImportListTarget.SelectedItem as ImportListChoice;
        bool previewMatches = _previewPreflightComplete && _domainImportPreview is not null && importTarget is not null &&
            string.Equals(_previewedImportText, txtBulkDomainImport.Text, StringComparison.Ordinal) &&
            string.Equals(_previewedImportProfileId, SelectedApiProfileId, StringComparison.OrdinalIgnoreCase) &&
            _previewedImportListType == importTarget.Type;

        pwdApiKey.IsEnabled = !busy;
        txtApiKeyVisible.IsEnabled = !busy;
        btnToggleApiKeyVisibility.IsEnabled = !busy && apiKeyText.Length > 0;
        btnVerifyApiKey.IsEnabled = !busy && (apiKeyText.Length > 0 || _apiKeyStore.HasApiKey);
        btnSaveApiKey.IsEnabled = !busy && apiKeyText.Length > 0 && IsCurrentKeyVerified(apiKeyText);
        btnDeleteApiKey.IsEnabled = !busy && (_apiKeyStore.HasApiKey || connected);
        cmbApiProfile.IsEnabled = !busy && connected;

        txtListDomain.IsEnabled = !busy && hasProfile;
        btnAddAllowedDomain.IsEnabled = !busy && hasProfile && !string.IsNullOrWhiteSpace(txtListDomain.Text);
        btnAddBlockedDomain.IsEnabled = btnAddAllowedDomain.IsEnabled;
        lstAllowedDomains.IsEnabled = !busy && hasProfile;
        lstBlockedDomains.IsEnabled = !busy && hasProfile;
        btnRefreshDomainLists.IsEnabled = !busy && hasProfile;
        btnExportAllowedList.IsEnabled = !busy && _allowedDomains.Count > 0;
        btnExportBlockedList.IsEnabled = !busy && _blockedDomains.Count > 0;

        cmbImportListTarget.IsEnabled = !busy && hasProfile;
        btnOpenImportFile.IsEnabled = !busy && hasProfile;
        btnPreviewDomainImport.IsEnabled = !busy && hasProfile && importTarget is not null;
        btnImportDomainList.IsEnabled = !busy && hasProfile && previewMatches && _domainImportPreview!.Entries.Count > 0;

        cmbActivityDevice.IsEnabled = !busy && hasProfile;
        cmbActivityStatus.IsEnabled = !busy && hasProfile;
        txtActivitySearch.IsEnabled = !busy && hasProfile;
        txtActivityFrom.IsEnabled = !busy && hasProfile;
        txtActivityTo.IsEnabled = !busy && hasProfile;
        btnToggleActivityStream.IsEnabled = !busy && hasProfile;
        btnToggleActivityStream.Content = LocalizationManager.Get(_activityStreamCancellation is not null
            ? "Code.Activity.StopLive"
            : "Main.Ui.Activity.Live");
        btnRefreshActivity.IsEnabled = !busy && hasProfile && _activityStreamCancellation is null;
        btnLoadMoreActivity.IsEnabled = !busy && hasProfile && _activityStreamCancellation is null &&
            _dnsActivity.Count < MaximumActivityItems && !string.IsNullOrEmpty(_activityCursor);

        btnLoadApiSettings.IsEnabled = !busy && hasProfile;
        chkApiLogsEnabled.IsEnabled = !busy && hasProfile && _apiSettingsLoaded && _originalLogsEnabled.HasValue;
        chkApiDropIp.IsEnabled = !busy && hasProfile && _apiSettingsLoaded && _originalDropIp.HasValue;
        chkApiDropDomain.IsEnabled = !busy && hasProfile && _apiSettingsLoaded && _originalDropDomain.HasValue;
        chkApiBlockPageEnabled.IsEnabled = !busy && hasProfile && _apiSettingsLoaded && _originalBlockPageEnabled.HasValue;
        btnApplyApiSettings.IsEnabled = !busy && hasProfile && _apiSettingsLoaded && ApiSettingsChanged();

        UpdateActivityEmptyDescription();
        TryStartPendingActivityAutoLoad();
        TryStartPendingProfileSettingsLoad();
    }

    private bool ApiSettingsChanged() =>
        HasChanged(_originalLogsEnabled, chkApiLogsEnabled.IsChecked) ||
        HasChanged(_originalDropIp, chkApiDropIp.IsChecked) ||
        HasChanged(_originalDropDomain, chkApiDropDomain.IsChecked) ||
        HasChanged(_originalBlockPageEnabled, chkApiBlockPageEnabled.IsChecked);

    private string? SelectedApiProfileId => cmbApiProfile.SelectedValue as string;

    private void ClearApiProfileData()
    {
        CancelActivityPageLoad();
        CancelActivityStream(LocalizationManager.Get("Code.Api.ProfileSessionClearedStreamStopped"));
        _allowedDomains.Clear();
        _blockedDomains.Clear();
        _dnsActivity.Clear();
        _activityDevices.Clear();
        lblAllowListCount.Text = "—";
        lblBlockListCount.Text = "—";
        pnlAllowedEmpty.Visibility = Visibility.Visible;
        pnlBlockedEmpty.Visibility = Visibility.Visible;
        pnlActivityEmpty.Visibility = Visibility.Visible;
        _activityQuery = null;
        _activityCursor = null;
        _activityStreamId = null;
        _activityNeedsApply = false;
        _activityAutoLoadPending = false;
        _activityAutoLoadIssuedForVisit = false;
        _recentStreamEventIds.Clear();
        _recentStreamEventIdSet.Clear();
        _apiSettingsLoaded = false;
        _originalLogsEnabled = null;
        _originalDropIp = null;
        _originalDropDomain = null;
        _originalBlockPageEnabled = null;
        _bindingActivityFilters = true;
        _bindingActivityDevices = true;
        try
        {
            cmbActivityDevice.ItemsSource = new[] { new ActivityDeviceChoice(LocalizationManager.Get("Code.Activity.AllDevices"), null) };
            cmbActivityDevice.SelectedIndex = 0;
            cmbActivityStatus.SelectedIndex = 0;
            txtActivitySearch.Clear();
            txtActivityFrom.Clear();
            txtActivityTo.Clear();
            chkApiLogsEnabled.IsChecked = null;
            chkApiDropIp.IsChecked = null;
            chkApiDropDomain.IsChecked = null;
            chkApiBlockPageEnabled.IsChecked = null;
            chkApiLogsEnabled.IsThreeState = true;
            chkApiDropIp.IsThreeState = true;
            chkApiDropDomain.IsThreeState = true;
            chkApiBlockPageEnabled.IsThreeState = true;
        }
        finally
        {
            _bindingActivityDevices = false;
            _bindingActivityFilters = false;
        }
        SetListsMessage(LocalizationManager.Get(_nextDnsApiClient is null
            ? "Main.Ui.Lists.ConnectApi"
            : "Code.Lists.SelectProfile"), "TextSecondary");
        SetActivityMessage(string.Empty, "TextSecondary");
        InvalidateImportPreview();
        pnlApiSettingsValues.Visibility = Visibility.Collapsed;
        SetApiSettingsStatus(LocalizationManager.Get("Code.ApiSettings.NotReadYet"), "TextSecondary");
        UpdateApiControlStates();
    }

    private void SetApiConnectionStatus(string message, string foregroundResource)
    {
        if (lblApiConnectionStatus is null) return;
        lblApiConnectionStatus.Text = message;
        lblApiConnectionStatus.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
    }

    private void SetListsMessage(string message, string foregroundResource)
    {
        if (lblListsMessage is null) return;
        lblListsMessage.Text = message;
        lblListsMessage.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
    }

    private void SetActivityMessage(string message, string foregroundResource)
    {
        if (lblActivityMessage is null) return;
        lblActivityMessage.Text = message;
        lblActivityMessage.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
    }

    private void SetApiSettingsStatus(string message, string foregroundResource)
    {
        if (lblApiSettingsStatus is null) return;
        lblApiSettingsStatus.Text = message;
        lblApiSettingsStatus.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
    }

    private void SetBulkImportResult(string message, string foregroundResource)
    {
        if (lblBulkImportResult is null) return;
        lblBulkImportResult.Text = message;
        lblBulkImportResult.SetResourceReference(TextBlock.ForegroundProperty, foregroundResource);
    }

    private void MainWindow_ApiClosed(object? sender, EventArgs e)
    {
        ClearApiKeyInput();
        CancelActivityPageLoad();
        NextDnsApiClient? client = _nextDnsApiClient;
        _nextDnsApiClient = null;
        CancellationTokenSource? cancellation = _activityStreamCancellation;
        Task? streamTask = _activityStreamTask;
        if (cancellation is not null)
        {
            _activityStreamStopMessage = LocalizationManager.Get("Code.Api.WindowClosedStreamStopped");
            cancellation.Cancel();
            if (streamTask is not null)
            {
                _ = streamTask.ContinueWith(
                    _ =>
                    {
                        cancellation.Dispose();
                        client?.Dispose();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                cancellation.Dispose();
                client?.Dispose();
            }
        }
        else
        {
            client?.Dispose();
        }
        ClearVerifiedApiKeyHash();
        Deactivated -= MainWindow_ApiDeactivated;
        IsVisibleChanged -= MainWindow_ApiIsVisibleChanged;
        mainTabs.SelectionChanged -= MainTabs_SelectionChanged;
        Closed -= MainWindow_ApiClosed;
    }
}
