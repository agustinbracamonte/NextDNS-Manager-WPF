using Microsoft.Win32;
using NextDNSManager.Shared;
using System.Globalization;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace NextDNSManager.Service;

internal sealed class NextDnsEngine
{
    private const string ProfileSwitchJournalValueName = "ProfileSwitchJournal";
    private const string PauseUntilValueName = "PauseUntilUtc";
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly DnsConfiguration _dns = new();
    internal event Action? PauseScheduleChanged;
    internal event Action? ReconciliationRequested;
    internal DateTimeOffset? PauseUntilUtcForScheduler => ReadPauseUntilUtc();

    internal async Task<BrokerResponse> ExecuteAsync(BrokerRequest request)
    {
        using IDisposable activationDiagnostics = BrokerOperationDiagnostics.BeginActivation(
            request.Operation == "activate");
        try
        {
            BrokerOperationDiagnostics.RunSync(
                ActivationFailureStage.RequestValidation,
                () => ValidateRequest(request));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _operations.WaitAsync(deadline.Token).ConfigureAwait(false);
            bool completed = false;
            try
            {
                if (request.Operation != "status" && HasProfileSwitchJournal())
                    await RecoverPendingProfileSwitchAsync(deadline.Token).ConfigureAwait(false);

                if (request.Operation == "activate")
                {
                    // An explicit activation cancels any pending timed resume first.
                    await TemporaryPauseTransaction.ActivateAsync(
                        () => PersistPauseUntilAsync(null),
                        () => EnableAsync(request, deadline.Token)).ConfigureAwait(false);
                }
                else if (request.Operation == "deactivate")
                {
                    // Persist the explicit off intent before removing a pause marker.
                    // Restore network configuration even if the resolver crashed.
                    // Disabling DNS never runs the CLI's reset-to-DHCP command.
                    await TemporaryPauseTransaction.DeactivateAsync(
                        PersistDisabledAsync,
                        () => PersistPauseUntilAsync(null),
                        () => _dns.DisableAsync(deadline.Token)).ConfigureAwait(false);
                }
                else if (request.Operation == "pause")
                    await PauseAsync(request.PauseMinutes!.Value, deadline.Token).ConfigureAwait(false);
                else if (request.Operation == "resume")
                    await ResumeAsync(deadline.Token, requireExpired: false).ConfigureAwait(false);
                else if (request.Operation == "status" &&
                         ReadSetting("Enabled") == "true" &&
                         ReadPauseUntilUtc() is { } pauseUntil && pauseUntil <= DateTimeOffset.UtcNow)
                    await ResumeAsync(deadline.Token, requireExpired: true).ConfigureAwait(false);

                BrokerResponse response = Status();
                completed = true;
                return response;
            }
            finally
            {
                _operations.Release();
                if (!completed) NotifyReconciliationRequested();
                NotifyPauseScheduleChanged();
            }
        }
        catch (Exception error) when (request.Operation == "activate" && error is not OutOfMemoryException)
        {
            BrokerOperationDiagnostics.MarkFailureIfMissing(error, ActivationFailureStage.EngineServiceValidation);
            throw;
        }
    }

    internal static void ValidateRequest(BrokerRequest request)
    {
        if (request.Version != BrokerProtocol.Version) throw new ArgumentException("La versión de la interfaz no coincide. Repara la instalación.");
        if (request.Operation is not ("status" or "activate" or "deactivate" or "pause" or "resume"))
            throw new ArgumentException("Operación no permitida.");
        if (request.Operation == "activate")
        {
            if (request.ProfileId == null || !Regex.IsMatch(request.ProfileId, "\\A[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant))
                throw new ArgumentException("El ID de perfil debe contener exactamente seis caracteres hexadecimales.");
            if (request.PauseMinutes != null) throw new ArgumentException("La activación no acepta duración de pausa.");
        }
        else if (request.Operation == "pause")
        {
            if (request.ProfileId != null || request.ReportClientInfo || request.SetupRouter)
                throw new ArgumentException("La pausa no acepta parámetros de configuración.");
            if (request.PauseMinutes is not (5 or 15 or 30))
                throw new ArgumentException("La pausa debe durar 5, 15 o 30 minutos.");
        }
        else if (request.ProfileId != null || request.ReportClientInfo || request.SetupRouter || request.PauseMinutes != null)
            throw new ArgumentException("La operación no acepta parámetros de configuración.");
    }

    private async Task PauseAsync(int minutes, CancellationToken token)
    {
        if (ReadPauseUntilUtc() is not null)
            throw new InvalidOperationException("Ya hay una pausa temporal pendiente.");
        if (ReadSetting("Enabled") != "true" || !ServiceExists("nextdns") || !BrokerPolicy.OwnsCli ||
            !IsCliRunning() || !_dns.IsActive)
            throw new InvalidOperationException("La protección debe estar activa para pausarla.");

        ValidateOwnedCli();
        if (!TryParseConfiguration(ReadSetting("Configuration"), out _, out _, out _))
            throw new InvalidOperationException("No hay un perfil válido guardado para reanudar la protección.");

        DateTimeOffset deadline = TemporaryPausePolicy.CreateDeadlineUtc(minutes, DateTimeOffset.UtcNow);
        var currentIntent = new TemporaryPauseIntent(Enabled: true, PauseUntilUtc: null);
        TemporaryPauseIntent pausedIntent = TemporaryPausePolicy.ApplyExplicitIntent(
            currentIntent, TemporaryPauseExplicitAction.Pause, deadline);

        // Flush the deadline before restoring any adapter so an interrupted
        // pause is reconciled after service restart or system resume.
        await TemporaryPauseTransaction.PauseAsync(
            pausedIntent.PauseUntilUtc!.Value,
            deadline => PersistPauseUntilAsync(deadline),
            () => _dns.DisableAsync(token)).ConfigureAwait(false);
    }

    private async Task ResumeAsync(CancellationToken token, bool requireExpired)
    {
        DateTimeOffset? pauseUntil = ReadPauseUntilUtc();
        if (pauseUntil is null)
        {
            if (requireExpired) return;
            throw new InvalidOperationException("No hay una pausa temporal pendiente.");
        }
        if (requireExpired && pauseUntil.Value > DateTimeOffset.UtcNow)
            return;

        bool enabled = ReadSetting("Enabled") == "true";
        var currentIntent = new TemporaryPauseIntent(enabled, pauseUntil);
        if (!enabled)
        {
            WritePauseUntilUtc(null);
            if (_dns.HasSnapshot) await _dns.DisableAsync(token).ConfigureAwait(false);
            return;
        }

        if (!TryParseConfiguration(ReadSetting("Configuration"), out string profile, out bool report, out bool router))
        {
            // Fail closed if the persisted resume configuration is unusable.
            SetWanted(false);
            WritePauseUntilUtc(null);
            if (_dns.HasSnapshot) await _dns.DisableAsync(token).ConfigureAwait(false);
            throw new InvalidOperationException("No se pudo reanudar porque el perfil guardado no es válido; la protección quedó desactivada.");
        }

        TemporaryPauseIntent resumedIntent = TemporaryPausePolicy.ApplyExplicitIntent(
            currentIntent, TemporaryPauseExplicitAction.Resume);
        BrokerRequest request = new()
        {
            Version = BrokerProtocol.Version,
            Operation = "activate",
            ProfileId = profile,
            ReportClientInfo = report,
            SetupRouter = router
        };
        DateTimeOffset resumeDue = requireExpired ? pauseUntil.Value : DateTimeOffset.UtcNow;
        await TemporaryPauseTransaction.ResumeAsync(
            () => PersistPauseUntilAsync(resumeDue),
            () => EnableAsync(request, token),
            () => PersistPauseUntilAsync(resumedIntent.PauseUntilUtc)).ConfigureAwait(false);
    }

    private void NotifyPauseScheduleChanged()
    {
        try { PauseScheduleChanged?.Invoke(); }
        catch { }
    }

    private void NotifyReconciliationRequested()
    {
        try { ReconciliationRequested?.Invoke(); }
        catch { }
    }

    private async Task EnableAsync(BrokerRequest request, CancellationToken token)
    {
        if (BrokerOperationDiagnostics.RunSync(
                ActivationFailureStage.EngineServiceValidation,
                () => ServiceExists("NextDNSService")))
            throw new InvalidOperationException("El cliente oficial de NextDNS sigue instalado. Retíralo desde Windows antes de activar este administrador.");
        bool exists = BrokerOperationDiagnostics.RunSync(
            ActivationFailureStage.EngineServiceValidation,
            () => ServiceExists("nextdns"));
        if (exists)
            BrokerOperationDiagnostics.RunSync(ActivationFailureStage.EngineServiceValidation, ValidateOwnedCli);

        string profile = request.ProfileId!.ToLowerInvariant();
        string signature = $"{profile}:{request.ReportClientInfo}:{request.SetupRouter}";
        string? previousConfiguration = BrokerOperationDiagnostics.RunSync(
            ActivationFailureStage.EngineServiceValidation,
            () => ReadSetting("Configuration"));
        bool changed = previousConfiguration != signature;
        bool cliWasRunning = exists && BrokerOperationDiagnostics.RunSync(
            ActivationFailureStage.EngineServiceValidation,
            IsCliRunning);
        bool wasActive = cliWasRunning && BrokerOperationDiagnostics.RunSync(
            ActivationFailureStage.EngineServiceValidation,
            () => _dns.IsActive);
        if (exists && !changed && wasActive)
        {
            SetWanted(true);
            return;
        }

        if (wasActive && changed)
        {
            if (!TryParseConfiguration(previousConfiguration, out string previousProfile, out bool previousReport, out bool previousRouter))
                throw new InvalidOperationException("No se puede cambiar el perfil con seguridad porque la configuración activa anterior no es válida.");

            var actions = new ActiveProfileSwitchActions(
                this,
                request,
                signature,
                previousConfiguration!,
                previousProfile,
                previousReport,
                previousRouter,
                ProfileSwitchJournal.Create(previousConfiguration!, signature));
            try
            {
                await ProfileSwitchTransaction.ExecuteAsync(actions, token).ConfigureAwait(false);
            }
            catch (ProfileSwitchTransactionException error) when (!error.PreviousProfileRestored)
            {
                BrokerOperationDiagnostics.MarkFailureAsPrimary(error, ActivationFailureStage.DnsRestore);
                throw;
            }
            return;
        }

        await ApplyConfigurationAsync(request, exists, changed, signature, token).ConfigureAwait(false);
    }

    private async Task ApplyConfigurationAsync(
        BrokerRequest request,
        bool exists,
        bool changed,
        string signature,
        CancellationToken token)
    {
        string profile = request.ProfileId!.ToLowerInvariant();
        bool wantedBefore = ReadSetting("Enabled") == "true";
        bool hasSnapshot = BrokerOperationDiagnostics.RunSync(
            ActivationFailureStage.DnsRestore,
            () => _dns.HasSnapshot);
        if (hasSnapshot && (changed || !BrokerOperationDiagnostics.RunSync(
                ActivationFailureStage.EngineServiceValidation,
                IsCliRunning)))
            await BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.DnsRestore,
                () => _dns.DisableAsync(token)).ConfigureAwait(false);

        string[] options = BuildCliOptions(profile, request.ReportClientInfo, request.SetupRouter);
        if (!exists)
        {
            // Claim the exact fixed location before provisioning. A failed
            // install can be repaired, but an existing foreign service is never
            // adopted or stopped just because its name happens to match.
            BrokerPolicy.OwnsCli = true;
            await BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.CliInstall,
                () => RunCliAsync(new[] { "install" }.Concat(options), token)).ConfigureAwait(false);
            BrokerOperationDiagnostics.RunSync(ActivationFailureStage.EngineServiceValidation, ValidateOwnedCli);
        }
        else if (changed)
        {
            await BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.CliConfigure,
                () => RunCliAsync(new[] { "config", "set" }.Concat(options), token)).ConfigureAwait(false);
            if (BrokerOperationDiagnostics.RunSync(ActivationFailureStage.EngineServiceValidation, IsCliRunning))
                await BrokerOperationDiagnostics.RunAsync(
                    ActivationFailureStage.CliStart,
                    () => RunCliAsync(new[] { "restart" }, token)).ConfigureAwait(false);
            else
                await BrokerOperationDiagnostics.RunAsync(
                    ActivationFailureStage.CliStart,
                    () => EnsureCliRunningAsync(token)).ConfigureAwait(false);
        }
        else if (!BrokerOperationDiagnostics.RunSync(ActivationFailureStage.EngineServiceValidation, IsCliRunning))
            await BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.CliStart,
                () => EnsureCliRunningAsync(token)).ConfigureAwait(false);

        await BrokerOperationDiagnostics.RunAsync(
            ActivationFailureStage.CliStart,
            () => WaitForCliRunningAsync(token)).ConfigureAwait(false);
        BrokerOperationDiagnostics.RunSync(ActivationFailureStage.EngineServiceValidation, ValidateOwnedCli);
        await BrokerOperationDiagnostics.RunAsync(
            ActivationFailureStage.LocalDnsQuery,
            () => VerifyResolverAsync(token)).ConfigureAwait(false);
        BrokerPolicy.SavedProfile = profile;
        WriteSetting("Configuration", signature);
        SetWanted(true);
        try
        {
            await BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.DnsApply,
                () => _dns.EnableAsync(token)).ConfigureAwait(false);
            SetWanted(true);
        }
        catch
        {
            using var restoreDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await BrokerOperationDiagnostics.RunAsync(
                    ActivationFailureStage.DnsRestore,
                    () => _dns.DisableAsync(restoreDeadline.Token)).ConfigureAwait(false);
            }
            finally { SetWanted(wantedBefore); }
            throw;
        }
    }

    private static async Task EnsureCliRunningAsync(CancellationToken token)
    {
        using var service = new ServiceController("nextdns");
        await ServiceStartupCoordinator.EnsureRunningAsync(
            () => { service.Refresh(); return service.Status; },
            cancellation => RunCliAsync(new[] { "start" }, cancellation),
            cancellation => Task.Delay(200, cancellation), token).ConfigureAwait(false);
    }

    private static void MarkProfileSwitchRecoveryRequired(ProfileSwitchJournal journal)
    {
        // Preserve the durable journal and prior profile. ReconcileAsync uses
        // that record after a network change or service restart.
        var errors = new List<Exception>();
        try { WriteProfileSwitchJournal(journal); }
        catch (Exception error) { errors.Add(error); }
        try { SetWanted(false); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0)
            throw new AggregateException("No se pudo guardar el estado de recuperación del cambio de perfil.", errors);
    }

    private static async Task WaitForCliRunningAsync(CancellationToken token)
    {
        while (!IsCliRunning())
            await Task.Delay(150, token).ConfigureAwait(false);
    }

    private static string[] BuildCliOptions(string profile, bool reportClientInfo, bool setupRouter) =>
    [
        "-profile=" + profile,
        "-auto-activate=false",
        "-report-client-info=" + reportClientInfo.ToString().ToLowerInvariant(),
        // Windows is a desktop host. The checkbox controls the listener;
        // router-specific platform integrations must not alter DNS here.
        "-setup-router=false",
        "-listen=" + (setupRouter ? "0.0.0.0:53" : "127.0.0.1:53"),
        "-listen=" + (setupRouter ? "[::]:53" : "[::1]:53")
    ];

    private static bool TryParseConfiguration(string? signature, out string profile, out bool reportClientInfo, out bool setupRouter)
    {
        profile = string.Empty;
        reportClientInfo = false;
        setupRouter = false;
        if (string.IsNullOrWhiteSpace(signature)) return false;

        string[] parts = signature.Split(':');
        if (parts.Length != 3 ||
            !Regex.IsMatch(parts[0], "\\A[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant) ||
            !bool.TryParse(parts[1], out reportClientInfo) ||
            !bool.TryParse(parts[2], out setupRouter))
        {
            return false;
        }

        profile = parts[0].ToLowerInvariant();
        return true;
    }

    private sealed class ActiveProfileSwitchActions : IProfileSwitchActions
    {
        private readonly NextDnsEngine _engine;
        private readonly BrokerRequest _targetRequest;
        private readonly string _targetSignature;
        private readonly string _previousSignature;
        private readonly string _previousProfile;
        private readonly bool _previousReportClientInfo;
        private readonly bool _previousSetupRouter;
        private readonly ProfileSwitchJournal _journal;

        internal ActiveProfileSwitchActions(
            NextDnsEngine engine,
            BrokerRequest targetRequest,
            string targetSignature,
            string previousSignature,
            string previousProfile,
            bool previousReportClientInfo,
            bool previousSetupRouter,
            ProfileSwitchJournal journal)
        {
            _engine = engine;
            _targetRequest = targetRequest;
            _targetSignature = targetSignature;
            _previousSignature = previousSignature;
            _previousProfile = previousProfile;
            _previousReportClientInfo = previousReportClientInfo;
            _previousSetupRouter = previousSetupRouter;
            _journal = journal;
        }

        public Task BeginAsync(CancellationToken cancellationToken)
        {
            return BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.EngineServiceValidation,
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteProfileSwitchJournal(_journal);
                    return Task.CompletedTask;
                });
        }

        public Task DisableDnsAsync(CancellationToken cancellationToken) =>
            BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.DnsRestore,
                () => _engine._dns.DisableAsync(cancellationToken));

        public Task SetTargetConfigurationAsync(CancellationToken cancellationToken) =>
            BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.CliConfigure,
                () => RunCliAsync(new[] { "config", "set" }.Concat(BuildCliOptions(
                    _targetRequest.ProfileId!.ToLowerInvariant(),
                    _targetRequest.ReportClientInfo,
                    _targetRequest.SetupRouter)), cancellationToken));

        public Task RestartTargetServiceAsync(CancellationToken cancellationToken) =>
            BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.CliStart,
                async () =>
                {
                    if (BrokerOperationDiagnostics.RunSync(ActivationFailureStage.EngineServiceValidation, IsCliRunning))
                        await RunCliAsync(new[] { "restart" }, cancellationToken).ConfigureAwait(false);
                    else
                        await EnsureCliRunningAsync(cancellationToken).ConfigureAwait(false);
                    await WaitForCliRunningAsync(cancellationToken).ConfigureAwait(false);
                });

        public Task VerifyTargetResolverAsync(CancellationToken cancellationToken) =>
            BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.EngineServiceValidation,
                async () =>
                {
                    ValidateOwnedCli();
                    await BrokerOperationDiagnostics.RunAsync(
                        ActivationFailureStage.LocalDnsQuery,
                        () => VerifyResolverAsync(cancellationToken)).ConfigureAwait(false);
                });

        public Task EnableTargetDnsAsync(CancellationToken cancellationToken) =>
            BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.DnsApply,
                async () =>
                {
                    BrokerPolicy.SavedProfile = _targetRequest.ProfileId!.ToLowerInvariant();
                    WriteSetting("Configuration", _targetSignature);
                    SetWanted(true);
                    await _engine._dns.EnableAsync(cancellationToken).ConfigureAwait(false);
                    SetWanted(true);
                });

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            return BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.EngineServiceValidation,
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteProfileSwitchJournal(_journal with { Phase = ProfileSwitchJournal.CommittedPhase });
                    try { ClearProfileSwitchJournal(); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        ServiceLog.Write(DiagnosticEvent.ServiceProfileSwitchCleanupPending, error);
                    }
                    return Task.CompletedTask;
                });
        }

        public Task BeginRollbackAsync(CancellationToken cancellationToken)
        {
            return BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.EngineServiceValidation,
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteProfileSwitchJournal(_journal with { Phase = ProfileSwitchJournal.RollingBackPhase });
                    return Task.CompletedTask;
                });
        }

        public Task DisableDnsForRollbackAsync(CancellationToken cancellationToken) =>
            BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.DnsRestore,
                async () =>
                {
                    SetWanted(false);
                    await _engine._dns.DisableAsync(cancellationToken).ConfigureAwait(false);
                });

        public Task SetPreviousConfigurationAsync(CancellationToken cancellationToken) =>
            BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.EngineServiceValidation,
                () =>
                {
                    ValidateOwnedCli();
                    return BrokerOperationDiagnostics.RunAsync(
                        ActivationFailureStage.CliConfigure,
                        () => RunCliAsync(new[] { "config", "set" }.Concat(BuildCliOptions(
                            _previousProfile, _previousReportClientInfo, _previousSetupRouter)), cancellationToken));
                });

        public Task RestartPreviousServiceAsync(CancellationToken cancellationToken) =>
            BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.CliStart,
                async () =>
                {
                    if (BrokerOperationDiagnostics.RunSync(ActivationFailureStage.EngineServiceValidation, IsCliRunning))
                        await RunCliAsync(new[] { "restart" }, cancellationToken).ConfigureAwait(false);
                    else
                        await EnsureCliRunningAsync(cancellationToken).ConfigureAwait(false);
                    await WaitForCliRunningAsync(cancellationToken).ConfigureAwait(false);
                });

        public Task VerifyPreviousResolverAsync(CancellationToken cancellationToken) =>
            BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.EngineServiceValidation,
                () =>
                {
                    ValidateOwnedCli();
                    return BrokerOperationDiagnostics.RunAsync(
                        ActivationFailureStage.LocalDnsQuery,
                        () => VerifyResolverAsync(cancellationToken));
                });

        public Task EnablePreviousDnsAsync(CancellationToken cancellationToken) =>
            BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.DnsRestore,
                async () =>
                {
                    BrokerPolicy.SavedProfile = _previousProfile;
                    WriteSetting("Configuration", _previousSignature);
                    SetWanted(true);
                    await _engine._dns.EnableAsync(cancellationToken).ConfigureAwait(false);
                    SetWanted(true);
                    if (!BrokerOperationDiagnostics.RunSync(ActivationFailureStage.EngineServiceValidation, IsCliRunning) ||
                        !_engine._dns.IsActive)
                        throw new InvalidOperationException("Windows no confirmó la restauración DNS del perfil anterior.");
                });

        public Task CompleteRollbackAsync(CancellationToken cancellationToken) =>
            BrokerOperationDiagnostics.RunAsync(
                ActivationFailureStage.EngineServiceValidation,
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ClearProfileSwitchJournal();
                    return Task.CompletedTask;
                });

        public void MarkRecoveryRequired() => MarkProfileSwitchRecoveryRequired(_journal);
    }

    private BrokerResponse Status()
    {
        if (HasProfileSwitchJournal())
        {
            ProfileSwitchJournal? journal = null;
            try { journal = ReadProfileSwitchJournal(); }
            catch (InvalidOperationException) { }
            string? priorConfiguration = journal?.PreviousConfiguration;
            string[] priorOptions = (priorConfiguration ?? "").Split(':');
            string? priorProfile = priorOptions.Length == 3 && Regex.IsMatch(priorOptions[0], "\\A[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant)
                ? priorOptions[0].ToLowerInvariant()
                : (Regex.IsMatch(BrokerPolicy.SavedProfile ?? "", "\\A[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant)
                    ? BrokerPolicy.SavedProfile!.ToLowerInvariant()
                    : null);
            string[] displayedOptions = (priorConfiguration ?? ReadSetting("Configuration") ?? "").Split(':');
            return new BrokerResponse
            {
                Success = true,
                Active = false,
                State = "attention",
                ProfileId = priorProfile,
                ReportClientInfo = displayedOptions.Length == 3 && bool.TryParse(displayedOptions[1], out bool priorReport) && priorReport,
                SetupRouter = displayedOptions.Length == 3 && bool.TryParse(displayedOptions[2], out bool priorRouter) && priorRouter
            };
        }

        bool active = false;
        if (ServiceExists("nextdns") && BrokerPolicy.OwnsCli)
        {
            ValidateOwnedCli();
            active = IsCliRunning() && _dns.IsActive;
        }
        bool wanted = ReadSetting("Enabled") == "true";
        DateTimeOffset? pauseUntil = ReadPauseUntilUtc();
        bool isPaused = wanted && pauseUntil is { } deadline && deadline > DateTimeOffset.UtcNow;
        bool pauseCompleted = isPaused && !active && !_dns.HasSnapshot;
        string[] options = (ReadSetting("Configuration") ?? "").Split(':');
        return new BrokerResponse
        {
            Success = true, Active = active,
            State = isPaused ? (pauseCompleted ? "paused" : "attention") :
                active ? "active" : (_dns.HasSnapshot ? "attention" : "inactive"),
            ProfileId = active || pauseCompleted ? BrokerPolicy.SavedProfile : null,
            ReportClientInfo = options.Length == 3 && bool.TryParse(options[1], out bool report) && report,
            SetupRouter = options.Length == 3 && bool.TryParse(options[2], out bool router) && router,
            PauseUntilUtc = isPaused ? pauseUntil : null
        };
    }

    internal async Task<bool> ReconcileAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await _operations.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { ServiceLog.Write(DiagnosticEvent.ServiceReconcileWaitFailure, ex); return false; }

        try
        {
            // Read the recovery marker and desired configuration under the
            // same gate as activation. Otherwise a queued network event could
            // capture the old profile, wait for a switch, and reactivate stale
            // settings after the new profile has committed.
            if (HasProfileSwitchJournal())
            {
                await RecoverPendingProfileSwitchAsync(deadline.Token).ConfigureAwait(false);
                return true;
            }

            DateTimeOffset? pauseUntil;
            try { pauseUntil = ReadPauseUntilUtc(); }
            catch (InvalidOperationException error)
            {
                // A corrupt pause record must never turn an intended pause
                // into an automatic activation. Fail closed and restore DNS.
                SetWanted(false);
                WritePauseUntilUtc(null);
                if (_dns.HasSnapshot)
                    await _dns.DisableAsync(deadline.Token).ConfigureAwait(false);
                throw new InvalidOperationException("El estado de pausa guardado no es válido; se desactivó la protección para recuperar DNS.", error);
            }

            bool enabled = ReadSetting("Enabled") == "true";
            string? config = ReadSetting("Configuration");
            bool configurationValid = TryParseConfiguration(config, out string profile, out bool report, out bool router);
            TemporaryPauseReconcileAction action = TemporaryPausePolicy.GetReconcileAction(
                enabled, configurationValid, pauseUntil, DateTimeOffset.UtcNow);
            if (action == TemporaryPauseReconcileAction.Deactivate)
            {
                await TemporaryPauseTransaction.DeactivateAsync(
                    PersistDisabledAsync,
                    () => PersistPauseUntilAsync(null),
                    () => _dns.DisableAsync(deadline.Token)).ConfigureAwait(false);
                if (enabled && !configurationValid)
                    ServiceLog.Write(DiagnosticEvent.ServiceReconcileFailure,
                        new InvalidOperationException("La configuración NextDNS guardada no es válida; DNS se restauró y la protección quedó desactivada."));
                return true;
            }
            if (action == TemporaryPauseReconcileAction.KeepPaused)
            {
                if (_dns.HasSnapshot) await _dns.DisableAsync(deadline.Token).ConfigureAwait(false);
                return true;
            }

            if (pauseUntil is not null)
            {
                await ResumeAsync(deadline.Token, requireExpired: true).ConfigureAwait(false);
                return true;
            }

            await EnableAsync(new BrokerRequest
            {
                Version = BrokerProtocol.Version,
                Operation = "activate",
                ProfileId = profile,
                ReportClientInfo = report,
                SetupRouter = router
            }, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) { ServiceLog.Write(DiagnosticEvent.ServiceReconcileFailure, ex); return false; }
        finally
        {
            _operations.Release();
            NotifyPauseScheduleChanged();
        }
    }

    internal async Task RemoveOwnedCliAsync(CancellationToken token)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (HasProfileSwitchJournal())
                await RecoverPendingProfileSwitchAsync(token).ConfigureAwait(false);
            SetWanted(false);
            WritePauseUntilUtc(null);
            await _dns.DisableAsync(token).ConfigureAwait(false);
            if (!ServiceExists("nextdns") || !BrokerPolicy.OwnsCli) return;
            ValidateOwnedCli();
            // CLI uninstall calls ResetDNS (DHCP) unconditionally on Windows.
            // Remove only our validated SCM registration, after restoring DNS.
            using (var service = new ServiceController("nextdns"))
            {
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }
            }
            await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), new[] { "delete", "nextdns" }, token).ConfigureAwait(false);
            while (ServiceExists("nextdns")) await Task.Delay(150, token).ConfigureAwait(false);
            BrokerPolicy.OwnsCli = false;
        }
        finally
        {
            _operations.Release();
            NotifyPauseScheduleChanged();
        }
    }

    private static async Task<string> RunCliAsync(IEnumerable<string> args, CancellationToken token)
    {
        using FileStream file = File.Open(NextDnsCliOwnershipPolicy.SourceExecutable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(BrokerPolicy.CliHash, StringComparison.Ordinal))
            throw new InvalidOperationException("El núcleo DNS no coincide con la versión verificada. Repara la instalación.");
        return await ProcessRunner.RunAsync(NextDnsCliOwnershipPolicy.SourceExecutable, args, token).ConfigureAwait(false);
    }

    internal static bool ServiceExists(string name)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key != null;
    }

    private static bool IsCliRunning()
    {
        if (!ServiceExists("nextdns")) return false;
        using var service = new ServiceController("nextdns");
        return service.Status == ServiceControllerStatus.Running;
    }

    private static void ValidateOwnedCli()
    {
        if (!BrokerPolicy.OwnsCli) throw new InvalidOperationException("Hay otro servicio CLI de NextDNS instalado. No se modificará desde este administrador.");
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\nextdns");
        string image = key?.GetValue("ImagePath") as string ?? "";
        if (!NextDnsCliOwnershipPolicy.IsOwnedServiceImage(image))
            throw new InvalidOperationException("El servicio nextdns apunta a un ejecutable o configuración ajenos. Se rechazó la operación.");
        using FileStream file = File.Open(NextDnsCliOwnershipPolicy.InstalledExecutable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(file)) != BrokerPolicy.CliHash)
            throw new InvalidOperationException("El núcleo del servicio instalado no coincide con la versión verificada.");
    }

    private static async Task VerifyResolverAsync(CancellationToken token)
    {
        // A running SCM state does not prove that DNS is responding. Send a
        // normal query directly to loopback before changing adapter settings.
        byte[] query = Convert.FromHexString("123401000001000000000000076E657874646E7302696F0000010001");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                using var udp = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetwork);
                udp.Connect(System.Net.IPAddress.Loopback, 53);
                await udp.SendAsync(query, deadline.Token).ConfigureAwait(false);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                var response = await udp.ReceiveAsync(attempt.Token).ConfigureAwait(false);
                if (response.Buffer.Length >= 12 && response.Buffer[0] == 0x12 && response.Buffer[1] == 0x34 &&
                    (response.Buffer[2] & 0x80) != 0 && (response.Buffer[3] & 0x0F) == 0) return;
            }
            catch (System.Net.Sockets.SocketException) { }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            await Task.Delay(150, deadline.Token).ConfigureAwait(false);
        }
    }

    private static void SetWanted(bool enabled) => WriteSetting("Enabled", enabled ? "true" : "false");
    private static string? ReadSetting(string name) { using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath); return key?.GetValue(name) as string; }
    private static void WriteSetting(string name, string value)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(BrokerPolicy.RegistryPath, true);
        key.SetValue(name, value, RegistryValueKind.String);
        key.Flush();
    }

    private static DateTimeOffset? ReadPauseUntilUtc()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath);
        object? stored = key?.GetValue(PauseUntilValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (stored is null) return null;
        if (stored is not string text ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
            throw new InvalidOperationException("El plazo de pausa guardado en el registro no es válido.");
        return parsed.ToUniversalTime();
    }

    private static void WritePauseUntilUtc(DateTimeOffset? deadline)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(BrokerPolicy.RegistryPath, true);
        if (deadline is null)
            key.DeleteValue(PauseUntilValueName, throwOnMissingValue: false);
        else
            key.SetValue(PauseUntilValueName, deadline.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
        key.Flush();
    }

    private Task PersistPauseUntilAsync(DateTimeOffset? deadline)
    {
        WritePauseUntilUtc(deadline);
        // Program the broker timer as soon as the durable deadline is saved,
        // before a DNS restore/re-enable operation can take time.
        // The observer only schedules work; it never mutates network state.
        NotifyPauseScheduleChanged();
        return Task.CompletedTask;
    }

    private static Task PersistDisabledAsync()
    {
        SetWanted(false);
        return Task.CompletedTask;
    }

    private async Task RecoverPendingProfileSwitchAsync(CancellationToken token)
    {
        ProfileSwitchJournal? journal;
        try { journal = ReadProfileSwitchJournal(); }
        catch (InvalidOperationException journalError)
        {
            SetWanted(false);
            try
            {
                await BrokerOperationDiagnostics.RunAsync(
                    ActivationFailureStage.DnsRestore,
                    () => _dns.DisableAsync(token)).ConfigureAwait(false);
            }
            catch (Exception dnsError) when (dnsError is not OutOfMemoryException)
            {
                throw new AggregateException(journalError, dnsError);
            }
            throw;
        }
        if (journal is null) return;

        if (!journal.RequiresRollback)
        {
            ClearProfileSwitchJournal();
            return;
        }

        if (!TryParseConfiguration(journal.PreviousConfiguration, out string previousProfile, out bool previousReport, out bool previousRouter) ||
            !TryParseConfiguration(journal.TargetConfiguration, out string targetProfile, out bool targetReport, out bool targetRouter))
            throw new InvalidOperationException("El journal del cambio de perfil no contiene una configuración válida; se conservó para revisión.");

        var targetRequest = new BrokerRequest
        {
            Version = BrokerProtocol.Version,
            Operation = "activate",
            ProfileId = targetProfile,
            ReportClientInfo = targetReport,
            SetupRouter = targetRouter
        };
        var actions = new ActiveProfileSwitchActions(
            this,
            targetRequest,
            journal.TargetConfiguration,
            journal.PreviousConfiguration,
            previousProfile,
            previousReport,
            previousRouter,
            journal);
        try
        {
            await ProfileSwitchTransaction.RestorePreviousAsync(actions, token).ConfigureAwait(false);
        }
        catch (Exception recoveryError) when (recoveryError is not OutOfMemoryException)
        {
            try { MarkProfileSwitchRecoveryRequired(journal); }
            catch (Exception stateError) when (stateError is not OutOfMemoryException)
            {
                throw new AggregateException(recoveryError, stateError);
            }
            throw;
        }
    }

    private static bool HasProfileSwitchJournal()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath);
        return key?.GetValueNames().Contains(ProfileSwitchJournalValueName, StringComparer.OrdinalIgnoreCase) == true;
    }

    private static ProfileSwitchJournal? ReadProfileSwitchJournal()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath);
        if (key is null || !key.GetValueNames().Contains(ProfileSwitchJournalValueName, StringComparer.OrdinalIgnoreCase)) return null;
        object? value = key.GetValue(ProfileSwitchJournalValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is string json && ProfileSwitchJournal.TryDeserialize(json, out ProfileSwitchJournal? journal)) return journal;
        throw new InvalidOperationException("El journal del cambio de perfil está dañado; se conservó para revisión.");
    }

    private static void WriteProfileSwitchJournal(ProfileSwitchJournal journal)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(BrokerPolicy.RegistryPath, true);
        key.SetValue(ProfileSwitchJournalValueName, journal.Serialize(), RegistryValueKind.String);
        key.Flush();
    }

    private static void ClearProfileSwitchJournal()
    {
        using RegistryKey key = Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath, true)
            ?? throw new InvalidOperationException("Falta la clave protegida para completar el cambio de perfil.");
        key.DeleteValue(ProfileSwitchJournalValueName, throwOnMissingValue: false);
        key.Flush();
    }
}
