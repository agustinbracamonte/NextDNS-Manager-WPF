#nullable disable

using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace NextDNSModernWPF;

public partial class MainWindow
{
    private bool _activityDateControlsInitialized;
    private bool _syncingActivityDateControls;
    private bool _activityDateRangeInvalid;
    private bool _enforcingActivityDateRange;

    private void ActivityDateControls_Loaded(object sender, RoutedEventArgs e)
    {
        if (_activityDateControlsInitialized) return;

        _activityDateControlsInitialized = true;
        string[] hours = Enumerable.Range(0, 24)
            .Select(value => value.ToString("00", CultureInfo.InvariantCulture)).ToArray();
        string[] minutes = Enumerable.Range(0, 60)
            .Select(value => value.ToString("00", CultureInfo.InvariantCulture)).ToArray();

        _syncingActivityDateControls = true;
        try
        {
            cmbActivityFromHour.ItemsSource = hours;
            cmbActivityToHour.ItemsSource = hours;
            cmbActivityFromMinute.ItemsSource = minutes;
            cmbActivityToMinute.ItemsSource = minutes;
            cmbActivityFromHour.SelectedIndex = 0;
            cmbActivityToHour.SelectedIndex = 0;
            cmbActivityFromMinute.SelectedIndex = 0;
            cmbActivityToMinute.SelectedIndex = 0;
            lblActivityDateValidation.Text = string.Empty;
        }
        finally { _syncingActivityDateControls = false; }

        UpdateActivityTimeControlStates();
        SyncActivityDateFilters();
    }

    private void ActivityDateTime_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_activityDateControlsInitialized || _syncingActivityDateControls) return;

        if ((ReferenceEquals(sender, dpActivityFrom) && dpActivityFrom.SelectedDate is null) ||
            (ReferenceEquals(sender, dpActivityTo) && dpActivityTo.SelectedDate is null))
        {
            _syncingActivityDateControls = true;
            try
            {
                if (ReferenceEquals(sender, dpActivityFrom))
                {
                    cmbActivityFromHour.SelectedIndex = 0;
                    cmbActivityFromMinute.SelectedIndex = 0;
                }
                else
                {
                    cmbActivityToHour.SelectedIndex = 0;
                    cmbActivityToMinute.SelectedIndex = 0;
                }
            }
            finally { _syncingActivityDateControls = false; }
        }

        UpdateActivityTimeControlStates();
        SyncActivityDateFilters();
    }

    private void ActivityDateEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdateActivityTimeControlStates();

    private void ActivityDateValidationError(object sender, DatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
        string fieldName = ReferenceEquals(sender, dpActivityFrom) ? "Desde" : "Hasta";
        _activityDateRangeInvalid = true;
        if (lblActivityDateValidation is not null)
            lblActivityDateValidation.Text = $"La fecha de {fieldName} no es válida.";

        _syncingActivityDateControls = true;
        try
        {
            if (ReferenceEquals(sender, dpActivityFrom)) txtActivityFrom.Text = "invalid-date";
            else txtActivityTo.Text = "invalid-date";
        }
        finally { _syncingActivityDateControls = false; }

        if (btnRefreshActivity is not null) btnRefreshActivity.IsEnabled = false;
    }

    private void UpdateActivityTimeControlStates()
    {
        if (cmbActivityFromHour is null || cmbActivityFromMinute is null ||
            cmbActivityToHour is null || cmbActivityToMinute is null ||
            dpActivityFrom is null || dpActivityTo is null ||
            txtActivityFrom is null || txtActivityTo is null)
            return;

        bool canEditFromTime = txtActivityFrom.IsEnabled && dpActivityFrom.SelectedDate.HasValue;
        bool canEditToTime = txtActivityTo.IsEnabled && dpActivityTo.SelectedDate.HasValue;
        cmbActivityFromHour.IsEnabled = canEditFromTime;
        cmbActivityFromMinute.IsEnabled = canEditFromTime;
        cmbActivityToHour.IsEnabled = canEditToTime;
        cmbActivityToMinute.IsEnabled = canEditToTime;
    }

    private void ActivityRefreshButton_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_enforcingActivityDateRange || !_activityDateRangeInvalid || btnRefreshActivity is null || !btnRefreshActivity.IsEnabled)
            return;

        _enforcingActivityDateRange = true;
        try { btnRefreshActivity.IsEnabled = false; }
        finally { _enforcingActivityDateRange = false; }
    }

    private void LegacyActivityDate_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingActivityDateControls || !_activityDateControlsInitialized ||
            sender is not System.Windows.Controls.TextBox field || !string.IsNullOrWhiteSpace(field.Text))
            return;

        _syncingActivityDateControls = true;
        try
        {
            if (ReferenceEquals(field, txtActivityFrom))
            {
                dpActivityFrom.SelectedDate = null;
                cmbActivityFromHour.SelectedIndex = 0;
                cmbActivityFromMinute.SelectedIndex = 0;
            }
            else if (ReferenceEquals(field, txtActivityTo))
            {
                dpActivityTo.SelectedDate = null;
                cmbActivityToHour.SelectedIndex = 0;
                cmbActivityToMinute.SelectedIndex = 0;
            }
            lblActivityDateValidation.Text = string.Empty;
        }
        finally { _syncingActivityDateControls = false; }

        _activityDateRangeInvalid = false;
        UpdateActivityTimeControlStates();
    }

    private void SyncActivityDateFilters()
    {
        if (!_activityDateControlsInitialized || _syncingActivityDateControls ||
            txtActivityFrom is null || txtActivityTo is null || lblActivityDateValidation is null)
            return;

        string fromText = string.Empty;
        string toText = string.Empty;
        DateTimeOffset? fromInstant = null;
        DateTimeOffset? toInstant = null;
        string validation = string.Empty;

        if (!TryGetActivityDateUtc(dpActivityFrom, cmbActivityFromHour, cmbActivityFromMinute,
                "Desde", out fromText, out fromInstant, out validation))
        {
            fromText = "invalid-local-date";
        }
        else if (!TryGetActivityDateUtc(dpActivityTo, cmbActivityToHour, cmbActivityToMinute,
                     "Hasta", out toText, out toInstant, out validation))
        {
            toText = "invalid-local-date";
        }
        else if (fromInstant.HasValue && toInstant.HasValue && fromInstant.Value >= toInstant.Value)
        {
            validation = "Desde debe ser anterior a Hasta. El límite Hasta es exclusivo.";
            fromText = "invalid-date-range";
        }

        _activityDateRangeInvalid = validation.Length > 0;
        lblActivityDateValidation.Text = validation;

        _syncingActivityDateControls = true;
        try
        {
            txtActivityFrom.Text = fromText;
            txtActivityTo.Text = toText;
        }
        finally { _syncingActivityDateControls = false; }

        if (_activityDateRangeInvalid && btnRefreshActivity is not null)
            btnRefreshActivity.IsEnabled = false;
    }

    private static bool TryGetActivityDateUtc(DatePicker datePicker, System.Windows.Controls.ComboBox hourSelector,
        System.Windows.Controls.ComboBox minuteSelector, string fieldName, out string formattedUtc,
        out DateTimeOffset? instant, out string validation)
    {
        formattedUtc = string.Empty;
        instant = null;
        validation = string.Empty;

        if (datePicker.SelectedDate is not DateTime selectedDate)
            return true;

        if (!int.TryParse(hourSelector.SelectedItem as string, NumberStyles.None,
                CultureInfo.InvariantCulture, out int hour) ||
            !int.TryParse(minuteSelector.SelectedItem as string, NumberStyles.None,
                CultureInfo.InvariantCulture, out int minute))
        {
            validation = $"Selecciona una hora válida para {fieldName}.";
            return false;
        }

        DateTime localTime = DateTime.SpecifyKind(selectedDate.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
        TimeZoneInfo localZone = TimeZoneInfo.Local;
        if (localZone.IsInvalidTime(localTime))
        {
            validation = $"La hora local de {fieldName} no existe por el cambio de horario. Elige otra hora.";
            return false;
        }
        if (localZone.IsAmbiguousTime(localTime))
        {
            validation = $"La hora local de {fieldName} ocurre dos veces por el cambio de horario. Elige otra hora.";
            return false;
        }

        DateTime utcTime = TimeZoneInfo.ConvertTimeToUtc(localTime, localZone);
        instant = new DateTimeOffset(utcTime);
        formattedUtc = instant.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        return true;
    }
}
