namespace NextDNSManager.Service;

internal interface IProfileSwitchActions
{
    Task DisableDnsAsync(CancellationToken cancellationToken);
    Task SetTargetConfigurationAsync(CancellationToken cancellationToken);
    Task RestartServiceAsync(CancellationToken cancellationToken);
    Task VerifyTargetResolverAsync(CancellationToken cancellationToken);
    Task EnableDnsAsync(CancellationToken cancellationToken);
    Task RestorePreviousAsync(CancellationToken cancellationToken);
    void MarkRecoveryRequired();
}

internal sealed class ProfileSwitchTransactionException : InvalidOperationException
{
    internal ProfileSwitchTransactionException(bool previousProfileRestored, Exception innerException)
        : base(previousProfileRestored
            ? "No se pudo aplicar el perfil solicitado. Se restauró el perfil anterior."
            : "No se pudo aplicar el perfil solicitado ni restaurar el perfil anterior. La protección puede estar desactivada; consulta el estado antes de volver a intentarlo.",
            innerException)
    {
        PreviousProfileRestored = previousProfileRestored;
    }

    internal bool PreviousProfileRestored { get; }
}

internal static class ProfileSwitchTransaction
{
    private static readonly TimeSpan RollbackTimeout = TimeSpan.FromSeconds(30);

    internal static Task ExecuteAsync(IProfileSwitchActions actions, CancellationToken cancellationToken) =>
        ExecuteAsync(actions, RollbackTimeout, cancellationToken);

    internal static async Task ExecuteAsync(
        IProfileSwitchActions actions,
        TimeSpan rollbackTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (rollbackTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(rollbackTimeout));

        try
        {
            await actions.DisableDnsAsync(cancellationToken).ConfigureAwait(false);
            await actions.SetTargetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            await actions.RestartServiceAsync(cancellationToken).ConfigureAwait(false);
            await actions.VerifyTargetResolverAsync(cancellationToken).ConfigureAwait(false);
            await actions.EnableDnsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception applyError) when (applyError is not OutOfMemoryException)
        {
            using var rollbackDeadline = new CancellationTokenSource(rollbackTimeout);
            try
            {
                await actions.RestorePreviousAsync(rollbackDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException)
            {
                Exception failure = new AggregateException(applyError, rollbackError);
                try
                {
                    actions.MarkRecoveryRequired();
                }
                catch (Exception stateError) when (stateError is not OutOfMemoryException)
                {
                    failure = new AggregateException(applyError, rollbackError, stateError);
                }

                throw new ProfileSwitchTransactionException(previousProfileRestored: false, failure);
            }

            throw new ProfileSwitchTransactionException(previousProfileRestored: true, applyError);
        }
    }
}
