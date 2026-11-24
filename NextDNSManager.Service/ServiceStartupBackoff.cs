namespace NextDNSManager.Service;

/// <summary>
/// Pure exponential backoff calculation for retry scheduling.
/// </summary>
internal static class ServiceStartupBackoff
{
    /// <summary>
    /// Doubles the current delay and caps it at the supplied maximum.
    /// </summary>
    internal static int NextDelaySeconds(int currentSeconds, int maximumSeconds = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSeconds);

        return (int)Math.Min((long)currentSeconds * 2, maximumSeconds);
    }
}
