namespace NextDNSManager.Shared;

/// <summary>Calculates bounded delays for automatic broker status retries.</summary>
internal sealed class BrokerStatusRetryPolicy
{
    internal const int InitialDelaySeconds = 3;
    internal const int MaximumDelaySeconds = 60;

    private int _consecutiveRetries;

    internal int ConsecutiveRetries => _consecutiveRetries;

    /// <summary>Records an unconfirmed or attention status and returns the next retry delay.</summary>
    internal TimeSpan RecordRetry()
    {
        if (_consecutiveRetries < int.MaxValue)
            _consecutiveRetries++;

        return TimeSpan.FromSeconds(GetDelaySeconds(_consecutiveRetries));
    }

    /// <summary>Clears the retry streak after a normal status is confirmed.</summary>
    internal void Reset() => _consecutiveRetries = 0;

    internal static bool ShouldRetry(bool statusUncertain, bool needsDnsRestore) =>
        statusUncertain || needsDnsRestore;

    /// <summary>Returns the exponential delay for a one-based consecutive retry count.</summary>
    internal static int GetDelaySeconds(
        int consecutiveRetries,
        int initialDelaySeconds = InitialDelaySeconds,
        int maximumDelaySeconds = MaximumDelaySeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consecutiveRetries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialDelaySeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDelaySeconds);

        long delay = Math.Min(initialDelaySeconds, maximumDelaySeconds);
        for (int retry = 1; retry < consecutiveRetries && delay < maximumDelaySeconds; retry++)
            delay = Math.Min(delay * 2, maximumDelaySeconds);

        return (int)delay;
    }
}
