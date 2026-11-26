namespace NextDNSManager.Service;

/// <summary>Pure retry scheduling rules used by the broker's reconciliation worker.</summary>
internal static class ServiceReconcilePolicy
{
    internal static bool ShouldDrainPendingImmediately(bool succeeded, bool pending) => succeeded && pending;

    internal static bool RetryGateElapsed(DateTimeOffset? retryNotBeforeUtc, DateTimeOffset now) =>
        retryNotBeforeUtc is null || retryNotBeforeUtc.Value <= now;

    internal static TimeSpan DebounceDelay(
        TimeSpan requestedDelay,
        DateTimeOffset? retryNotBeforeUtc,
        DateTimeOffset now)
    {
        if (retryNotBeforeUtc is not { } retryAt) return requestedDelay;
        TimeSpan remaining = retryAt - now;
        return remaining > requestedDelay ? remaining : requestedDelay;
    }
}
