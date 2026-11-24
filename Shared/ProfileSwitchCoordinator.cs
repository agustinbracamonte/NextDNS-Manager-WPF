namespace NextDNSManager.Shared;

/// <summary>Serializes active-profile changes and keeps only the latest selection.</summary>
internal sealed class ProfileSwitchCoordinator
{
    private readonly Func<string?> _getActiveProfileId;
    private readonly Func<string, Task> _applyProfileAsync;
    private string? _pendingProfileId;
    private string? _inFlightProfileId;
    private bool _running;

    internal ProfileSwitchCoordinator(Func<string?> getActiveProfileId, Func<string, Task> applyProfileAsync)
    {
        _getActiveProfileId = getActiveProfileId ?? throw new ArgumentNullException(nameof(getActiveProfileId));
        _applyProfileAsync = applyProfileAsync ?? throw new ArgumentNullException(nameof(applyProfileAsync));
    }

    internal bool IsRunning => _running;
    internal bool HasPending => _pendingProfileId != null;
    internal string? InFlightProfileId => _inFlightProfileId;

    internal void QueueLatest(string? selectedProfileId)
    {
        _pendingProfileId = ProfileSwitchSelection.QueueLatest(
            selectedProfileId,
            _getActiveProfileId(),
            _inFlightProfileId);
    }

    internal void ClearPending() => _pendingProfileId = null;

    internal async Task DrainAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            while (_pendingProfileId != null)
            {
                string target = _pendingProfileId;
                _pendingProfileId = null;
                if (string.Equals(target, _getActiveProfileId(), StringComparison.OrdinalIgnoreCase))
                    continue;

                _inFlightProfileId = target;
                try { await _applyProfileAsync(target).ConfigureAwait(false); }
                finally { _inFlightProfileId = null; }
            }
        }
        finally { _running = false; }
    }
}
