using System.ServiceProcess;

namespace NextDNSManager.Service;

/// <summary>
/// Coordinates service startup while keeping status reads, startup, and polling injectable.
/// </summary>
internal static class ServiceStartupCoordinator
{
    /// <summary>
    /// Ensures that a service reaches the Running state. Pending states are polled without
    /// issuing another start request. A failed start is treated as a race only when a fresh
    /// status read shows that the service is already running or changing state.
    /// </summary>
    internal static async Task EnsureRunningAsync(
        Func<ServiceControllerStatus> readStatus,
        Func<CancellationToken, Task> start,
        Func<CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readStatus);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(delay);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ServiceControllerStatus status = readStatus();
            if (status == ServiceControllerStatus.Running)
                return;

            if (IsPending(status))
            {
                await delay(cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await start(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ServiceControllerStatus statusAfterStartFailure = readStatus();
                if (statusAfterStartFailure == ServiceControllerStatus.Running)
                    return;

                if (!IsPending(statusAfterStartFailure))
                    throw;
            }

            await delay(cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsPending(ServiceControllerStatus status) => status is
        ServiceControllerStatus.StartPending or
        ServiceControllerStatus.StopPending or
        ServiceControllerStatus.ContinuePending or
        ServiceControllerStatus.PausePending;
}
