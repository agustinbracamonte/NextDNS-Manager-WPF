namespace NextDNSManager.Shared;

/// <summary>Coalesces profile changes while an active switch is in flight.</summary>
internal static class ProfileSwitchSelection
{
    /// <summary>
    /// Returns the latest queued ID, or null when the selection matches the
    /// currently active/in-flight target and should cancel any queued change.
    /// </summary>
    internal static string? QueueLatest(string? selectedId, string? activeId, string? inFlightId)
    {
        string? currentId = string.IsNullOrWhiteSpace(inFlightId) ? activeId : inFlightId;
        return string.Equals(selectedId, currentId, StringComparison.OrdinalIgnoreCase) ? null : selectedId;
    }
}
