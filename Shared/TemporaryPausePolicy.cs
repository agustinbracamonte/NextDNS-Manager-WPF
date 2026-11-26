namespace NextDNSManager.Shared;

/// <summary>Pure time and intent rules for the broker's durable temporary pause.</summary>
internal static class TemporaryPausePolicy
{
    internal static bool IsAllowedDuration(int minutes) => minutes is 5 or 15 or 30;

    internal static DateTimeOffset CreateDeadlineUtc(int minutes, DateTimeOffset now)
    {
        if (!IsAllowedDuration(minutes))
            throw new ArgumentOutOfRangeException(nameof(minutes), "La pausa debe durar 5, 15 o 30 minutos.");

        return now.ToUniversalTime().AddMinutes(minutes);
    }

    internal static TemporaryPauseIntent ApplyExplicitIntent(
        TemporaryPauseIntent current,
        TemporaryPauseExplicitAction action,
        DateTimeOffset? pauseUntilUtc = null)
    {
        return action switch
        {
            TemporaryPauseExplicitAction.Activate => new TemporaryPauseIntent(true, null),
            TemporaryPauseExplicitAction.Deactivate => new TemporaryPauseIntent(false, null),
            TemporaryPauseExplicitAction.Pause when current.Enabled && pauseUntilUtc is not null =>
                new TemporaryPauseIntent(true, pauseUntilUtc.Value.ToUniversalTime()),
            TemporaryPauseExplicitAction.Resume when current.Enabled && current.PauseUntilUtc is not null =>
                new TemporaryPauseIntent(true, null),
            TemporaryPauseExplicitAction.Pause => throw new InvalidOperationException("La protección debe estar activa para pausarla."),
            TemporaryPauseExplicitAction.Resume => throw new InvalidOperationException("No hay una pausa pendiente para reanudar."),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    internal static TemporaryPauseReconcileAction GetReconcileAction(
        bool enabled,
        bool configurationValid,
        DateTimeOffset? pauseUntilUtc,
        DateTimeOffset now)
    {
        // A desired active state without a usable profile must never leave
        // loopback DNS configured with a resolver the broker cannot restore.
        if (!enabled || !configurationValid) return TemporaryPauseReconcileAction.Deactivate;
        return pauseUntilUtc is { } until && until.ToUniversalTime() > now.ToUniversalTime()
            ? TemporaryPauseReconcileAction.KeepPaused
            : TemporaryPauseReconcileAction.Activate;
    }

    internal static TimeSpan GetTimerDelay(DateTimeOffset? pauseUntilUtc, DateTimeOffset now)
    {
        if (pauseUntilUtc is null) return Timeout.InfiniteTimeSpan;
        TimeSpan remaining = pauseUntilUtc.Value.ToUniversalTime() - now.ToUniversalTime();
        // An elapsed deadline is reconciled by the timer callback, startup,
        // resume, or a failed request. Do not arm a zero-delay timer again if
        // reactivation fails; the service's bounded retry schedule owns that.
        return remaining > TimeSpan.Zero ? remaining : Timeout.InfiniteTimeSpan;
    }
}

/// <summary>
/// Keeps persistence steps ahead of network changes. Delegate injection lets
/// offline tests exercise failure ordering without touching the real network.
/// </summary>
internal static class TemporaryPauseTransaction
{
    internal static async Task PauseAsync(
        DateTimeOffset deadline,
        Func<DateTimeOffset, Task> persistDeadline,
        Func<Task> restoreDns)
    {
        await persistDeadline(deadline).ConfigureAwait(false);
        await restoreDns().ConfigureAwait(false);
    }

    internal static async Task ActivateAsync(Func<Task> clearPause, Func<Task> enableDns)
    {
        await clearPause().ConfigureAwait(false);
        await enableDns().ConfigureAwait(false);
    }

    internal static async Task DeactivateAsync(
        Func<Task> persistDisabled,
        Func<Task> clearPause,
        Func<Task> restoreDns)
    {
        await persistDisabled().ConfigureAwait(false);
        await clearPause().ConfigureAwait(false);
        await restoreDns().ConfigureAwait(false);
    }

    internal static async Task ResumeAsync(
        Func<Task> persistResumeDue,
        Func<Task> enableDns,
        Func<Task> clearPause)
    {
        await persistResumeDue().ConfigureAwait(false);
        await enableDns().ConfigureAwait(false);
        await clearPause().ConfigureAwait(false);
    }
}

internal readonly record struct TemporaryPauseIntent(bool Enabled, DateTimeOffset? PauseUntilUtc);

internal enum TemporaryPauseExplicitAction
{
    Activate,
    Deactivate,
    Pause,
    Resume
}

internal enum TemporaryPauseReconcileAction
{
    Deactivate,
    KeepPaused,
    Activate
}
