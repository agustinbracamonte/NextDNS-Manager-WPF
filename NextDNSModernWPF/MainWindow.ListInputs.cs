#nullable disable

using System;
using System.Windows;
using System.Windows.Controls;

namespace NextDNSModernWPF;

public partial class MainWindow
{
    private bool _syncingDomainInputs;
    private bool _syncingDomainAddButtons;
    private string _lastSubmittedDomain;
    private bool _lastSubmissionWasAllowed;

    private void ApiProfileDomainInputChanged(object sender, SelectionChangedEventArgs e)
    {
        // A draft belongs to the profile where it was entered, not the next profile.
        if (txtAllowedDomain is null || txtBlockedDomain is null || txtListDomain is null) return;
        _lastSubmittedDomain = null;
        txtAllowedDomain.Clear();
        txtBlockedDomain.Clear();
        txtListDomain.Clear();
        SyncDomainAddButtonStates();
    }

    private void AllowedDomainInput_TextChanged(object sender, TextChangedEventArgs e) =>
        CopyDomainInputToLegacyField(txtAllowedDomain?.Text);

    private void BlockedDomainInput_TextChanged(object sender, TextChangedEventArgs e) =>
        CopyDomainInputToLegacyField(txtBlockedDomain?.Text);

    private void CopyDomainInputToLegacyField(string value)
    {
        if (txtListDomain is null || _syncingDomainInputs) return;

        _syncingDomainInputs = true;
        try { txtListDomain.Text = value ?? string.Empty; }
        finally { _syncingDomainInputs = false; }
        SyncDomainAddButtonStates();
    }

    private void AllowedDomainAddRequested(object sender, RoutedEventArgs e) =>
        SubmitDomainInput(txtAllowedDomain?.Text, allowed: true, e);

    private void BlockedDomainAddRequested(object sender, RoutedEventArgs e) =>
        SubmitDomainInput(txtBlockedDomain?.Text, allowed: false, e);

    private void SubmitDomainInput(string value, bool allowed, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            lblListsMessage.Text = allowed
                ? "Escribe un dominio en el campo de Permitidos."
                : "Escribe un dominio en el campo de Bloqueados.";
            lblListsMessage.SetResourceReference(TextBlock.ForegroundProperty, "StatusAttention");
            e.Handled = true;
            return;
        }

        _lastSubmittedDomain = value;
        _lastSubmissionWasAllowed = allowed;
        CopyDomainInputToLegacyField(value);
    }

    private void LegacyListDomain_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncingDomainInputs && string.IsNullOrEmpty(txtListDomain.Text) &&
            !string.IsNullOrEmpty(_lastSubmittedDomain))
        {
            if (_lastSubmissionWasAllowed && string.Equals(txtAllowedDomain.Text, _lastSubmittedDomain, StringComparison.Ordinal))
                txtAllowedDomain.Clear();
            else if (!_lastSubmissionWasAllowed && string.Equals(txtBlockedDomain.Text, _lastSubmittedDomain, StringComparison.Ordinal))
                txtBlockedDomain.Clear();

            _lastSubmittedDomain = null;
        }

        SyncDomainAddButtonStates();
    }

    private void LegacyListDomain_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SyncDomainAddButtonStates();

    private void DomainAddButton_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SyncDomainAddButtonStates();

    private void SyncDomainAddButtonStates()
    {
        if (_syncingDomainAddButtons || txtListDomain is null || txtAllowedDomain is null ||
            txtBlockedDomain is null || btnAddAllowedDomain is null || btnAddBlockedDomain is null)
            return;

        _syncingDomainAddButtons = true;
        try
        {
            bool apiReady = txtListDomain.IsEnabled;
            btnAddAllowedDomain.IsEnabled = apiReady && !string.IsNullOrWhiteSpace(txtAllowedDomain.Text);
            btnAddBlockedDomain.IsEnabled = apiReady && !string.IsNullOrWhiteSpace(txtBlockedDomain.Text);
        }
        finally { _syncingDomainAddButtons = false; }
    }
}
