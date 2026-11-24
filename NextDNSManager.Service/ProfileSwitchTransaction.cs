using System.Text.Json;

namespace NextDNSManager.Service;

internal interface IProfileSwitchActions
{
    Task BeginAsync(CancellationToken cancellationToken);
    Task DisableDnsAsync(CancellationToken cancellationToken);
    Task SetTargetConfigurationAsync(CancellationToken cancellationToken);
    Task RestartTargetServiceAsync(CancellationToken cancellationToken);
    Task VerifyTargetResolverAsync(CancellationToken cancellationToken);
    Task EnableTargetDnsAsync(CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task BeginRollbackAsync(CancellationToken cancellationToken);
    Task DisableDnsForRollbackAsync(CancellationToken cancellationToken);
    Task SetPreviousConfigurationAsync(CancellationToken cancellationToken);
    Task RestartPreviousServiceAsync(CancellationToken cancellationToken);
    Task VerifyPreviousResolverAsync(CancellationToken cancellationToken);
    Task EnablePreviousDnsAsync(CancellationToken cancellationToken);
    Task CompleteRollbackAsync(CancellationToken cancellationToken);
    void MarkRecoveryRequired();
}

internal sealed class ProfileSwitchTransactionException : InvalidOperationException
{
    internal ProfileSwitchTransactionException(bool previousProfileRestored, Exception innerException)
        : base(previousProfileRestored
            ? "No se pudo aplicar el perfil solicitado. Se restauró el perfil anterior."
            : "No se pudo aplicar el perfil solicitado ni restaurar el perfil anterior. La protección requiere recuperación; conserva el perfil anterior y reinténtalo cuando vuelva la conexión.",
            innerException)
    {
        PreviousProfileRestored = previousProfileRestored;
    }

    internal bool PreviousProfileRestored { get; }
}

/// <summary>
/// Durable record written before DNS is changed. Its presence means startup
/// recovery must restore the prior configuration before accepting new work.
/// </summary>
internal sealed record ProfileSwitchJournal(int Version, string Phase, string PreviousConfiguration, string TargetConfiguration)
{
    internal const int CurrentVersion = 1;
    internal const string ApplyingPhase = "applying";
    internal const string CommittedPhase = "committed";
    internal const string RollingBackPhase = "rolling-back";

    internal static ProfileSwitchJournal Create(string previousConfiguration, string targetConfiguration) =>
        new(CurrentVersion, ApplyingPhase, previousConfiguration, targetConfiguration);

    internal bool RequiresRollback => !string.Equals(Phase, CommittedPhase, StringComparison.Ordinal);

    internal string Serialize() => JsonSerializer.Serialize(this);

    internal static bool TryDeserialize(string? json, out ProfileSwitchJournal? journal)
    {
        journal = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            ProfileSwitchJournal? candidate = JsonSerializer.Deserialize<ProfileSwitchJournal>(json);
            if (candidate is null || candidate.Version != CurrentVersion ||
                candidate.Phase is not (ApplyingPhase or CommittedPhase or RollingBackPhase) ||
                string.IsNullOrWhiteSpace(candidate.PreviousConfiguration) ||
                string.IsNullOrWhiteSpace(candidate.TargetConfiguration))
                return false;
            journal = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
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

        // This durable write must finish before DNS or the CLI is mutated.
        await actions.BeginAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await actions.DisableDnsAsync(cancellationToken).ConfigureAwait(false);
            await actions.SetTargetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            await actions.RestartTargetServiceAsync(cancellationToken).ConfigureAwait(false);
            await actions.VerifyTargetResolverAsync(cancellationToken).ConfigureAwait(false);
            await actions.EnableTargetDnsAsync(cancellationToken).ConfigureAwait(false);
            await actions.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception applyError) when (applyError is not OutOfMemoryException)
        {
            using var rollbackDeadline = new CancellationTokenSource(rollbackTimeout);
            try
            {
                await RestorePreviousAsync(actions, rollbackDeadline.Token).ConfigureAwait(false);
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

    internal static async Task RestorePreviousAsync(IProfileSwitchActions actions, CancellationToken cancellationToken)
    {
        await actions.BeginRollbackAsync(cancellationToken).ConfigureAwait(false);
        await actions.DisableDnsForRollbackAsync(cancellationToken).ConfigureAwait(false);
        await actions.SetPreviousConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await actions.RestartPreviousServiceAsync(cancellationToken).ConfigureAwait(false);
        await actions.VerifyPreviousResolverAsync(cancellationToken).ConfigureAwait(false);
        await actions.EnablePreviousDnsAsync(cancellationToken).ConfigureAwait(false);
        await actions.CompleteRollbackAsync(cancellationToken).ConfigureAwait(false);
    }
}
