using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.ServiceProcess;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Pipes;
using NextDNSManager.Service;
using NextDNSManager.Shared;
using NextDNSModernWPF;

internal static class Program
{
    private static int _assertions;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                await RunOfflineTestsAsync().ConfigureAwait(false);
                Console.WriteLine($"PASS: {_assertions} offline broker, journal, profile-switch, and pause assertions.");
                return 0;
            }

            if (args.Length != 1)
                throw new ArgumentException("Use no arguments, --status, --cycle, or --malformed-live.");

            return args[0] switch
            {
                "--status" => await RunStatusAsync().ConfigureAwait(false),
                "--cycle" => await RunCycleAsync().ConfigureAwait(false),
                "--malformed-live" => await RunMalformedLiveAsync().ConfigureAwait(false),
                _ => throw new ArgumentException("Use no arguments, --status, --cycle, or --malformed-live.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            for (Exception? detail = ex.InnerException; detail != null; detail = detail.InnerException)
                Console.Error.WriteLine(detail.GetType().Name + ": " + detail.Message);
            return 1;
        }
    }

    private static async Task RunOfflineTestsAsync()
    {
        await TestProtocolRoundTripAsync().ConfigureAwait(false);
        await TestProtocolRejectionsAsync().ConfigureAwait(false);
        TestCliOwnershipLayout();
        TestRequestValidation();
        await TestServiceStartupCoordinatorAsync().ConfigureAwait(false);
        TestServiceStartupBackoff();
        TestServiceReconcilePolicy();
        TestServiceRemovalRecovery();
        await TestTemporaryPauseAsync().ConfigureAwait(false);
        TestDiagnosticServiceRecord();
        TestBrokerFailureCodes();
        TestBrokerStatusRetryPolicy();
        await TestActivationFailureDiagnosticsAsync().ConfigureAwait(false);
        await TestProfileSwitchTransactionAsync().ConfigureAwait(false);
        TestProfileSwitchJournal();
        TestProfileSwitchSelection();
    }

    private static void TestCliOwnershipLayout()
    {
        string bundledCli = Path.GetFullPath(NextDnsCliOwnershipPolicy.SourceExecutable);
        string expectedBundledCli = Path.GetFullPath(BrokerPolicy.CliExecutable);
        string serviceCli = NextDnsCliOwnershipPolicy.InstalledExecutable;

        Check(string.Equals(bundledCli, expectedBundledCli, StringComparison.OrdinalIgnoreCase),
            "CLI commands use the packaged, hash-pinned executable.");
        Check(string.Equals(serviceCli, @"C:\Program Files\nextdns\nextdns.exe", StringComparison.OrdinalIgnoreCase),
            "The owned CLI destination matches the upstream Windows installer path.");
        Check(!string.Equals(bundledCli, serviceCli, StringComparison.OrdinalIgnoreCase),
            "The packaged CLI source is distinct from the copy installed by the upstream service installer.");
        Check(NextDnsCliOwnershipPolicy.IsOwnedServiceImage("\"C:\\Program Files\\nextdns\\nextdns.exe\" run"),
            "The upstream NextDNS service image path and run argument are recognized.");
        Check(!NextDnsCliOwnershipPolicy.IsOwnedServiceImage($"\"{bundledCli}\" run"),
            "The packaged source path is not mistaken for the upstream-installed service copy.");
        Check(!NextDnsCliOwnershipPolicy.IsOwnedServiceImage($"\"{serviceCli} run"),
            "A malformed quoted service path is rejected.");
        Check(!NextDnsCliOwnershipPolicy.IsOwnedServiceImage($"\"{serviceCli}\" run --unexpected"),
            "Extra service arguments are rejected.");
    }

    private static async Task TestServiceStartupCoordinatorAsync()
    {
        int starts = 0;
        int delays = 0;
        await ServiceStartupCoordinator.EnsureRunningAsync(
            () => ServiceControllerStatus.Running,
            _ => { starts++; return Task.CompletedTask; },
            _ => { delays++; return Task.CompletedTask; },
            CancellationToken.None).ConfigureAwait(false);
        Check(starts == 0 && delays == 0,
            "an already-running service is left alone.");

        var pendingStatuses = new Queue<ServiceControllerStatus>([
            ServiceControllerStatus.StartPending,
            ServiceControllerStatus.StartPending,
            ServiceControllerStatus.Running
        ]);
        starts = 0;
        delays = 0;
        await ServiceStartupCoordinator.EnsureRunningAsync(
            () => pendingStatuses.Dequeue(),
            _ => { starts++; return Task.CompletedTask; },
            _ => { delays++; return Task.CompletedTask; },
            CancellationToken.None).ConfigureAwait(false);
        Check(starts == 0 && delays == 2,
            "StartPending is polled until Running without issuing a duplicate start.");

        ServiceControllerStatus status = ServiceControllerStatus.Stopped;
        starts = 0;
        delays = 0;
        await ServiceStartupCoordinator.EnsureRunningAsync(
            () => status,
            _ => { starts++; status = ServiceControllerStatus.StartPending; return Task.CompletedTask; },
            _ => { delays++; status = ServiceControllerStatus.Running; return Task.CompletedTask; },
            CancellationToken.None).ConfigureAwait(false);
        Check(starts == 1 && delays == 1,
            "a successful start request is polled through StartPending to Running.");

        status = ServiceControllerStatus.Stopped;
        starts = 0;
        delays = 0;
        await ServiceStartupCoordinator.EnsureRunningAsync(
            () => status,
            _ =>
            {
                starts++;
                status = ServiceControllerStatus.Running;
                throw new InvalidOperationException("another actor started the service first");
            },
            _ => { delays++; return Task.CompletedTask; },
            CancellationToken.None).ConfigureAwait(false);
        Check(starts == 1 && delays == 0,
            "a start failure caused by a concurrent successful start is accepted after rereading status.");

        status = ServiceControllerStatus.Stopped;
        starts = 0;
        delays = 0;
        await ServiceStartupCoordinator.EnsureRunningAsync(
            () => status,
            _ =>
            {
                starts++;
                status = ServiceControllerStatus.StartPending;
                throw new InvalidOperationException("another actor has a start in flight");
            },
            _ => { delays++; status = ServiceControllerStatus.Running; return Task.CompletedTask; },
            CancellationToken.None).ConfigureAwait(false);
        Check(starts == 1 && delays == 1,
            "a start failure caused by a concurrent pending transition is polled to Running.");

        var expectedFailure = new InvalidOperationException("injected service start failure");
        status = ServiceControllerStatus.Stopped;
        starts = 0;
        Exception? observedFailure = null;
        try
        {
            await ServiceStartupCoordinator.EnsureRunningAsync(
                () => status,
                _ => { starts++; throw expectedFailure; },
                _ => Task.CompletedTask,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            observedFailure = exception;
        }
        Check(starts == 1 && ReferenceEquals(observedFailure, expectedFailure),
            "a failed start is propagated when the service remains stopped.");

        using var canceledWait = new CancellationTokenSource();
        status = ServiceControllerStatus.StartPending;
        bool cancellationObserved = false;
        try
        {
            await ServiceStartupCoordinator.EnsureRunningAsync(
                () => status,
                _ => throw new InvalidOperationException("pending state must not start again"),
                _ => { canceledWait.Cancel(); return Task.CompletedTask; },
                canceledWait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationObserved = true;
        }
        Check(cancellationObserved,
            "cancellation interrupts service status polling.");
    }

    private static void TestServiceStartupBackoff()
    {
        int[] expectedDelays = [10, 20, 40, 60, 60];
        int delay = 5;
        foreach (int expected in expectedDelays)
        {
            delay = ServiceStartupBackoff.NextDelaySeconds(delay);
            Check(delay == expected,
                $"service retry backoff advances to {expected} seconds and respects the cap.");
        }

        Check(ServiceStartupBackoff.NextDelaySeconds(7, maximumSeconds: 10) == 10,
            "service retry backoff accepts a custom cap.");
        Check(ServiceStartupBackoff.NextDelaySeconds(int.MaxValue) == 60,
            "service retry backoff caps without integer overflow.");
    }

    private static void TestBrokerStatusRetryPolicy()
    {
        var policy = new BrokerStatusRetryPolicy();
        int[] expectedDelays = [3, 6, 12, 24, 48, 60, 60, 60];
        foreach (int expected in expectedDelays)
            Check(policy.RecordRetry() == TimeSpan.FromSeconds(expected),
                $"broker status retry delay advances to {expected} seconds and respects the cap.");

        Check(policy.ConsecutiveRetries == expectedDelays.Length,
            "broker status retry policy tracks consecutive retries.");
        policy.Reset();
        Check(policy.ConsecutiveRetries == 0 && policy.RecordRetry() == TimeSpan.FromSeconds(3),
            "a normal confirmed status resets automatic retry backoff.");
        Check(BrokerStatusRetryPolicy.ShouldRetry(statusUncertain: false, needsDnsRestore: true) &&
              BrokerStatusRetryPolicy.ShouldRetry(statusUncertain: true, needsDnsRestore: false) &&
              !BrokerStatusRetryPolicy.ShouldRetry(statusUncertain: false, needsDnsRestore: false),
            "attention and uncertain statuses retry, while a normal confirmed status stops retrying.");
        Check(BrokerStatusRetryPolicy.GetDelaySeconds(3, initialDelaySeconds: 7, maximumDelaySeconds: 10) == 10 &&
              BrokerStatusRetryPolicy.GetDelaySeconds(int.MaxValue) == BrokerStatusRetryPolicy.MaximumDelaySeconds,
            "broker status retry backoff accepts custom bounds and caps without integer overflow.");
    }

    private static void TestDiagnosticServiceRecord()
    {
        const string sensitiveMarker = "profile-SECRET 192.0.2.15 example.invalid";
        string record = DiagnosticLog.CreateServiceRecord(
            DiagnosticEvent.ServiceReconcileFailure,
            new InvalidOperationException(sensitiveMarker),
            suppressed: 7);

        Check(!record.Contains(sensitiveMarker, StringComparison.Ordinal) &&
              !record.Contains("192.0.2.15", StringComparison.Ordinal) &&
              !record.Contains("example.invalid", StringComparison.Ordinal),
            "service diagnostic records omit exception messages and sensitive request values.");
        Check(DiagnosticLog.TryParseServiceRecord(record, out DiagnosticEvent parsedEvent, out string category, out int suppressed) &&
              parsedEvent == DiagnosticEvent.ServiceReconcileFailure && category == "INVALID_OPERATION" && suppressed == 7,
            "service diagnostic records round-trip only allowlisted event and error categories.");
        Check(!DiagnosticLog.TryParseServiceRecord(record + "\t192.0.2.15", out _, out _, out _),
            "service diagnostic reader rejects appended free-form fields.");
    }

    private static void TestBrokerFailureCodes()
    {
        (string Message, BrokerFailureCode Code)[] cases =
        [
            ("El servicio auxiliar no está disponible. Repara la instalación de NextDNS Manager.", BrokerFailureCode.ServiceUnavailable),
            ("La versión del protocolo del broker no coincide con esta aplicación.", BrokerFailureCode.ProtocolMismatch),
            ("No se pudo acceder al broker de NextDNS. Repara la instalación desde el instalador y vuelve a intentarlo.", BrokerFailureCode.AccessDenied),
            ("No se pudo conectar con el broker de NextDNS. Repara la instalación desde el instalador y vuelve a intentarlo.", BrokerFailureCode.PipeUnavailable),
            ("No se pudo verificar el proceso del broker. Repara la instalación desde el instalador.", BrokerFailureCode.PipeProcessIdUnavailable),
            ("No se pudo verificar el servicio del broker. Repara la instalación desde el instalador.", BrokerFailureCode.ServiceManagerQueryFailed),
            ("No se encontró el servicio broker de NextDNS. Repara la instalación desde el instalador.", BrokerFailureCode.ServiceNotFound),
            ("El cliente oficial de NextDNS sigue instalado. Retíralo desde Windows antes de activar este administrador.", BrokerFailureCode.OfficialNextDnsServiceDetected),
            ("La conexión no pertenece al proceso en ejecución del servicio broker. Repara la instalación desde el instalador.", BrokerFailureCode.PipeServiceProcessMismatch),
            ("El broker no está configurado para ejecutarse como LocalSystem. Repara la instalación desde el instalador.", BrokerFailureCode.ServiceAccountUnexpected),
            ("La ruta configurada para el broker es inesperada. Repara la instalación desde el instalador.", BrokerFailureCode.ServicePathUnexpected),
            ("No se pudo inspeccionar el proceso del broker. Repara la instalación desde el instalador.", BrokerFailureCode.ProcessInspectionFailed),
            ("El servidor de la conexión no es el broker instalado. Repara la instalación desde el instalador.", BrokerFailureCode.PipeProcessPathMismatch),
            ("No se pudo leer la ruta del proceso del broker.", BrokerFailureCode.ProcessPathReadFailed),
            ("No se pudo leer el estado del servicio broker.", BrokerFailureCode.ServiceStatusReadFailed),
            ("No se pudo leer la configuración del servicio broker.", BrokerFailureCode.ServiceConfigurationReadFailed),
            ("Este usuario no está autorizado para controlar NextDNS.", BrokerFailureCode.UserNotAuthorized),
            ("No se pudo cambiar la pausa.", BrokerFailureCode.PauseActionUnconfirmed),
            ("El broker no pudo completar la operación.", BrokerFailureCode.GenericResponse),
            ("El broker no pudo cambiar de perfil.", BrokerFailureCode.ProfileChangeUnconfirmed),
            ("No se pudo consultar el estado del broker.", BrokerFailureCode.StatusQueryUnconfirmed),
            ("El broker no confirmó el perfil solicitado.", BrokerFailureCode.ActivationNotConfirmed),
            ("El broker no confirmó el perfil seleccionado.", BrokerFailureCode.ProfileSelectionNotConfirmed)
        ];

        var observedCodes = new HashSet<BrokerFailureCode>();
        foreach ((string message, BrokerFailureCode expected) in cases)
        {
            Check(BrokerFailureCatalog.TryClassifyKnownMessage(message, out BrokerFailureCode actual) && actual == expected,
                $"broker failure message maps to {BrokerFailureCatalog.GetDiagnosticId(expected)}.");
            Check(observedCodes.Add(expected), "each grouped broker failure has a distinct diagnostic code.");
        }

        Check(BrokerFailureCatalog.ClassifyResponseError(null) == BrokerFailureCode.ResponseErrorMissing &&
              BrokerFailureCatalog.ClassifyResponseError("  ") == BrokerFailureCode.ResponseErrorMissing,
            "missing broker error text maps to a fixed missing-detail code.");
        Check(BrokerFailureCatalog.ClassifyResponseError("El broker no pudo completar la operación.") == BrokerFailureCode.GenericResponse,
            "the legacy generic broker response gets its own diagnostic code.");

        const string privateMarker = "profile-SECRET 192.0.2.15 example.invalid";
        BrokerFailureCode reported = BrokerFailureCatalog.ClassifyResponseError("Fallo interno: " + privateMarker);
        BrokerResponseException preserved = BrokerFailureCatalog.CreateResponseException(
            "Fallo interno: " + privateMarker, "Localized safe message.");
        string safeRecord = DiagnosticLog.CreateManagerBrokerFailureRecord(
            DateTimeOffset.UnixEpoch, DiagnosticEvent.BrokerActionFailure, reported);
        Check(reported == BrokerFailureCode.BrokerReportedError &&
              preserved.ResponseError == "Fallo interno: " + privateMarker &&
              preserved.Message == "Localized safe message." &&
              preserved.FailureCode == BrokerFailureCode.BrokerReportedError &&
              safeRecord.Contains("BROKER_REPORTED_ERROR", StringComparison.Ordinal) &&
              !safeRecord.Contains(privateMarker, StringComparison.Ordinal) &&
              !safeRecord.Contains("192.0.2.15", StringComparison.Ordinal) &&
              !safeRecord.Contains("example.invalid", StringComparison.Ordinal),
            "broker failure logging uses only an allowlisted code and omits response content.");
        Check(BrokerProtocol.Version == 2,
            "diagnostic failure codes do not change the broker wire protocol version.");

        string translationsPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Localization", "broker-errors.json");
        using JsonDocument translations = JsonDocument.Parse(File.ReadAllText(translationsPath));
        foreach (string language in new[] { "es", "en", "fr", "pt", "de" })
        {
            JsonElement catalog = translations.RootElement.GetProperty(language);
            foreach (BrokerFailureCode code in Enum.GetValues<BrokerFailureCode>())
            {
                string key = BrokerFailureCatalog.GetLocalizationKey(code);
                Check(catalog.TryGetProperty(key, out JsonElement message) &&
                      message.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(message.GetString()),
                    $"{language} has a translated broker message for {BrokerFailureCatalog.GetDiagnosticId(code)}.");
            }
        }
    }

    private static async Task TestActivationFailureDiagnosticsAsync()
    {
        const string sensitiveMarker = "profile-SECRET 192.0.2.15 example.invalid";
        var injectedInstallFailure = new InvalidOperationException(sensitiveMarker);
        Exception? observed = null;

        using (BrokerOperationDiagnostics.BeginActivation(enabled: true))
        {
            try
            {
                await BrokerOperationDiagnostics.RunAsync(
                    ActivationFailureStage.CliInstall,
                    () => Task.FromException(injectedInstallFailure)).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                observed = error;
            }

            string brokerError = BrokerOperationDiagnostics.FormatBrokerError(injectedInstallFailure);
            DecodedActivationFailure decoded = ActivationFailureEnvelope.Decode(brokerError);
            string serviceRecord = DiagnosticLog.CreateServiceRecord(
                DiagnosticEvent.ServiceReconcileFailure,
                injectedInstallFailure,
                suppressed: 0);

            Check(ReferenceEquals(observed, injectedInstallFailure),
                "activation diagnostics rethrow the original injected exception without wrapping it.");
            Check(decoded.HasEnvelope && decoded.MetadataIsKnown &&
                  decoded.Stage == ActivationFailureStage.CliInstall &&
                  decoded.Category == ActivationFailureCategory.InvalidOperation &&
                  decoded.MessageWithoutPrefix == sensitiveMarker,
                "the existing broker error text carries a known stage/category envelope before its original message.");
            Check(!serviceRecord.Contains(sensitiveMarker, StringComparison.Ordinal) &&
                  !serviceRecord.Contains("NDMERR", StringComparison.Ordinal),
                "service logs omit both raw exception text and the response-only activation token.");

            var injectedTimeout = new OperationCanceledException("cancel-marker");
            try
            {
                await BrokerOperationDiagnostics.RunAsync(
                    ActivationFailureStage.DnsRestore,
                    () => Task.FromException(injectedTimeout)).ConfigureAwait(false);
            }
            catch (OperationCanceledException error)
            {
                Check(ReferenceEquals(error, injectedTimeout),
                    "activation diagnostics preserve the original cancellation exception.");
            }

            DecodedActivationFailure timeout = ActivationFailureEnvelope.Decode(
                BrokerOperationDiagnostics.FormatBrokerError(injectedTimeout));
            Check(timeout.MetadataIsKnown && timeout.Stage == ActivationFailureStage.DnsRestore &&
                  timeout.Category == ActivationFailureCategory.TimeoutOrCancelled &&
                  timeout.MessageWithoutPrefix == "La operación tardó demasiado. Consulta el estado antes de reintentar.",
                "a restoration timeout receives a fixed safe code and preserves the established timeout message.");

            var injectedApplyFailure = new IOException("apply-marker");
            var injectedRestoreFailure = new InvalidOperationException("restore-marker");
            Action failApply = () => throw injectedApplyFailure;
            Action failRestore = () => throw injectedRestoreFailure;
            try { BrokerOperationDiagnostics.RunSync(ActivationFailureStage.DnsApply, failApply); }
            catch (IOException) { }
            try { BrokerOperationDiagnostics.RunSync(ActivationFailureStage.DnsRestore, failRestore); }
            catch (InvalidOperationException) { }

            DecodedActivationFailure rollbackFailure = ActivationFailureEnvelope.Decode(
                BrokerOperationDiagnostics.FormatBrokerError(new AggregateException(injectedApplyFailure, injectedRestoreFailure)));
            Check(rollbackFailure.MetadataIsKnown && rollbackFailure.Stage == ActivationFailureStage.DnsRestore &&
                  rollbackFailure.Category == ActivationFailureCategory.InvalidOperation,
                "when both apply and restore fail, the token reports the restoration failure stage.");

            var injectedLongFailure = new IOException(new string('x', 5000));
            Action failLongQuery = () => throw injectedLongFailure;
            try { BrokerOperationDiagnostics.RunSync(ActivationFailureStage.LocalDnsQuery, failLongQuery); }
            catch (IOException) { }
            string truncatedError = BrokerOperationDiagnostics.FormatBrokerError(injectedLongFailure);
            DecodedActivationFailure truncated = ActivationFailureEnvelope.Decode(truncatedError);
            Check(truncatedError.Length <= 1600 && truncated.MetadataIsKnown &&
                  truncated.Stage == ActivationFailureStage.LocalDnsQuery,
                "bounded broker error formatting keeps the diagnostic envelope when truncating long messages.");
        }

        var nonActivationFailure = new InvalidOperationException("legacy-message");
        using (BrokerOperationDiagnostics.BeginActivation(enabled: false))
        {
            try
            {
                await BrokerOperationDiagnostics.RunAsync(
                    ActivationFailureStage.CliStart,
                    () => Task.FromException(nonActivationFailure)).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { }
        }

        Check(BrokerOperationDiagnostics.FormatBrokerError(nonActivationFailure) == "legacy-message",
            "status and pause failures retain their legacy response text without an activation token.");
    }

    private static async Task TestProfileSwitchTransactionAsync()
    {
        var successful = new FakeProfileSwitchActions();
        await ProfileSwitchTransaction.ExecuteAsync(successful, TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
        Check(successful.Calls.SequenceEqual(new[]
        {
            "journal-begin", "disable-dns", "configure-target", "restart-target", "verify-target",
            "set-target-ipv4", "set-target-ipv6", "journal-commit"
        }) && !successful.JournalPresent, "profile switch journals before DNS changes and clears the journal after target DNS is active.");

        var failedJournalWrite = new FakeProfileSwitchActions(ProfileSwitchFailurePoint.Begin);
        bool journalWriteRejected = false;
        try { await ProfileSwitchTransaction.ExecuteAsync(failedJournalWrite, TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false); }
        catch (InvalidOperationException) { journalWriteRejected = true; }
        Check(journalWriteRejected && failedJournalWrite.Calls.SequenceEqual(new[] { "journal-begin" }) && !failedJournalWrite.JournalPresent,
            "a journal write failure stops before any DNS or CLI mutation.");

        foreach (ProfileSwitchFailurePoint failurePoint in new[]
        {
            ProfileSwitchFailurePoint.DisableDns,
            ProfileSwitchFailurePoint.Configure,
            ProfileSwitchFailurePoint.RestartTarget,
            ProfileSwitchFailurePoint.Verify,
            ProfileSwitchFailurePoint.SetIpv4,
            ProfileSwitchFailurePoint.SetIpv6,
            ProfileSwitchFailurePoint.Commit,
            ProfileSwitchFailurePoint.CommitAfterDelete
        })
        {
            var failed = new FakeProfileSwitchActions(failurePoint);
            ProfileSwitchTransactionException failure = await CaptureProfileSwitchFailureAsync(
                () => ProfileSwitchTransaction.ExecuteAsync(failed, TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
            Check(failure.PreviousProfileRestored && failed.Calls.Last() == "rollback-clear-journal" && failed.RecoveryMarkers == 0 && !failed.JournalPresent,
                $"profile switch rolls back after {failurePoint} failure.");
        }

        foreach (ProfileSwitchFailurePoint rollbackFailure in new[]
        {
            ProfileSwitchFailurePoint.RollbackDns,
            ProfileSwitchFailurePoint.RollbackConfigure,
            ProfileSwitchFailurePoint.RollbackRestart,
            ProfileSwitchFailurePoint.RollbackVerify,
            ProfileSwitchFailurePoint.RollbackIpv4,
            ProfileSwitchFailurePoint.RollbackIpv6,
            ProfileSwitchFailurePoint.RollbackClear
        })
        {
            var failed = new FakeProfileSwitchActions(ProfileSwitchFailurePoint.Configure, rollbackFailurePoint: rollbackFailure);
            ProfileSwitchTransactionException failure = await CaptureProfileSwitchFailureAsync(
                () => ProfileSwitchTransaction.ExecuteAsync(failed, TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
            Check(!failure.PreviousProfileRestored && failed.RecoveryMarkers == 1 && failed.JournalPresent,
                $"rollback stage {rollbackFailure} keeps the durable journal for retry.");
        }

        using var callerCancellation = new CancellationTokenSource();
        var canceledApply = new FakeProfileSwitchActions(cancelCallerAfterDisable: callerCancellation);
        ProfileSwitchTransactionException canceled = await CaptureProfileSwitchFailureAsync(
            () => ProfileSwitchTransaction.ExecuteAsync(canceledApply, TimeSpan.FromSeconds(1), callerCancellation.Token)).ConfigureAwait(false);
        Check(canceled.PreviousProfileRestored && canceledApply.RestoreTokenWasIndependent,
            "profile rollback runs with an independent token after caller cancellation.");

        var timedOutRestore = new FakeProfileSwitchActions(ProfileSwitchFailurePoint.Configure, timeoutRollback: true);
        ProfileSwitchTransactionException rollbackTimeout = await CaptureProfileSwitchFailureAsync(
            () => ProfileSwitchTransaction.ExecuteAsync(timedOutRestore, TimeSpan.FromMilliseconds(50), CancellationToken.None)).ConfigureAwait(false);
        Check(!rollbackTimeout.PreviousProfileRestored && timedOutRestore.RecoveryMarkers == 1,
            "timed-out profile rollback marks state as requiring recovery.");
    }

    private static void TestProfileSwitchJournal()
    {
        ProfileSwitchJournal expected = ProfileSwitchJournal.Create("a1b2c3:false:true", "d4e5f6:true:false");
        Check(ProfileSwitchJournal.TryDeserialize(expected.Serialize(), out ProfileSwitchJournal? actual) && actual == expected,
            "profile-switch journal serialization preserves previous and target configuration.");
        Check(!ProfileSwitchJournal.TryDeserialize("{broken", out _) &&
              !ProfileSwitchJournal.TryDeserialize("{\"Version\":2,\"PreviousConfiguration\":\"a1b2c3:false:true\",\"TargetConfiguration\":\"d4e5f6:true:false\"}", out _),
            "malformed and future profile-switch journals are rejected for visible recovery.");
        foreach (string phase in new[] { ProfileSwitchJournal.ApplyingPhase, ProfileSwitchJournal.RollingBackPhase, ProfileSwitchJournal.CommittedPhase })
        {
            ProfileSwitchJournal saved = expected with { Phase = phase };
            Check(ProfileSwitchJournal.TryDeserialize(saved.Serialize(), out ProfileSwitchJournal? recovered) &&
                  recovered == saved && recovered.RequiresRollback == (phase != ProfileSwitchJournal.CommittedPhase),
                $"restart recovery preserves phase {phase} and selects the expected recovery action.");
        }
    }

    private static void TestProfileSwitchSelection()
    {
        Check(ProfileSwitchSelection.QueueLatest("A", "A", "B") == "A",
            "A→B followed by selecting A queues a return to the latest user choice.");
        Check(ProfileSwitchSelection.QueueLatest("C", "A", "B") == "C",
            "A→B followed by selecting C coalesces to C.");
        Check(ProfileSwitchSelection.QueueLatest("B", "A", "B") is null,
            "selecting the in-flight target cancels a previously queued profile.");
        Check(ProfileSwitchSelection.QueueLatest("A", "A", null) is null,
            "selecting the confirmed active profile does not start a redundant switch.");
    }

    private static async Task<ProfileSwitchTransactionException> CaptureProfileSwitchFailureAsync(Func<Task> operation)
    {
        Exception? failure = null;
        try { await operation().ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }

        if (failure is ProfileSwitchTransactionException transactionFailure)
            return transactionFailure;
        throw new InvalidOperationException("Expected a profile-switch transaction failure.", failure);
    }

    private static async Task TestProtocolRoundTripAsync()
    {
        using var stream = new MemoryStream();
        var expected = new BrokerRequest
        {
            Version = BrokerProtocol.Version,
            Operation = "activate",
            ProfileId = "aB12cD",
            ReportClientInfo = true,
            SetupRouter = true
        };

        await BrokerProtocol.WriteAsync(stream, expected, CancellationToken.None).ConfigureAwait(false);
        stream.Position = 0;
        BrokerRequest actual = await BrokerProtocol.ReadAsync<BrokerRequest>(stream, CancellationToken.None).ConfigureAwait(false);

        Check(actual.Version == expected.Version && actual.Operation == expected.Operation &&
              actual.ProfileId == expected.ProfileId && actual.ReportClientInfo && actual.SetupRouter && actual.PauseMinutes is null,
            "BrokerProtocol round-trips a valid newline-delimited request.");

        using var pauseStream = new MemoryStream();
        var expectedPause = new BrokerRequest { Operation = "pause", PauseMinutes = 15 };
        await BrokerProtocol.WriteAsync(pauseStream, expectedPause, CancellationToken.None).ConfigureAwait(false);
        pauseStream.Position = 0;
        BrokerRequest actualPause = await BrokerProtocol.ReadAsync<BrokerRequest>(pauseStream, CancellationToken.None).ConfigureAwait(false);
        Check(actualPause.Operation == "pause" && actualPause.PauseMinutes == 15,
            "BrokerProtocol round-trips the temporary pause duration.");

        DateTimeOffset pauseUntil = new(2026, 11, 25, 19, 30, 0, TimeSpan.Zero);
        using var responseStream = new MemoryStream();
        await BrokerProtocol.WriteAsync(responseStream, new BrokerResponse
        {
            Success = true, State = "paused", PauseUntilUtc = pauseUntil
        }, CancellationToken.None).ConfigureAwait(false);
        responseStream.Position = 0;
        BrokerResponse response = await BrokerProtocol.ReadAsync<BrokerResponse>(responseStream, CancellationToken.None).ConfigureAwait(false);
        Check(response.State == "paused" && response.PauseUntilUtc == pauseUntil,
            "BrokerProtocol round-trips the durable pause deadline in status.");
    }

    private static async Task TestProtocolRejectionsAsync()
    {
        await ExpectProtocolFailureAsync(
            "unknown JSON property",
            "{\"Version\":1,\"Operation\":\"status\",\"Unexpected\":true}\n",
            typeof(JsonException)).ConfigureAwait(false);
        await ExpectProtocolFailureAsync(
            "duplicate JSON property",
            "{\"Version\":1,\"Version\":1,\"Operation\":\"status\"}\n",
            typeof(JsonException)).ConfigureAwait(false);
        await ExpectProtocolFailureAsync(
            "malformed JSON",
            "{not-json}\n",
            typeof(JsonException)).ConfigureAwait(false);
        await ExpectProtocolFailureAsync(
            "truncated line",
            "{\"Version\":1,\"Operation\":\"status\"",
            typeof(IOException)).ConfigureAwait(false);

        using var oversized = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', BrokerProtocol.MaxMessageBytes + 8)));
        await ExpectFailureAsync(
            "oversized JSON line",
            () => BrokerProtocol.ReadAsync<BrokerRequest>(oversized, CancellationToken.None),
            typeof(JsonException)).ConfigureAwait(false);
    }

    private static async Task ExpectProtocolFailureAsync(string label, string payload, Type expectedException)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        await ExpectFailureAsync(label,
            () => BrokerProtocol.ReadAsync<BrokerRequest>(stream, CancellationToken.None),
            expectedException).ConfigureAwait(false);
    }

    private static void TestRequestValidation()
    {
        ExpectRequestValid("valid status request", new BrokerRequest { Operation = "status" });
        ExpectRequestValid("valid deactivate request", new BrokerRequest { Operation = "deactivate" });
        ExpectRequestValid("valid resume request", new BrokerRequest { Operation = "resume" });
        foreach (int duration in new[] { 5, 15, 30 })
            ExpectRequestValid("valid temporary pause duration", new BrokerRequest { Operation = "pause", PauseMinutes = duration });
        ExpectRequestValid("valid mixed-case profile ID", new BrokerRequest
        {
            Operation = "activate", ProfileId = "aB12cD", ReportClientInfo = true, SetupRouter = true
        });

        string?[] invalidProfileIds =
        [
            null,
            "",
            "abcde",
            "abcdefg",
            "abcdef\"",
            "abc def",
            "abcdef -auto-activate",
            "abcdef\n",
            "ébcdef",
            "ａｂｃｄｅｆ",
            "ab🙂ef"
        ];
        foreach (string? profileId in invalidProfileIds)
        {
            ExpectRequestInvalid("invalid profile ID", new BrokerRequest
            {
                Operation = "activate", ProfileId = profileId
            });
        }

        ExpectRequestInvalid("unknown operation", new BrokerRequest { Operation = "install" });
        ExpectRequestInvalid("null operation", new BrokerRequest { Operation = null! });
        ExpectRequestInvalid("protocol v1 request rejected", new BrokerRequest { Version = 1, Operation = "status" });
        ExpectRequestInvalid("unsupported protocol version", new BrokerRequest { Version = BrokerProtocol.Version + 1 });
        ExpectRequestInvalid("status with profile parameter", new BrokerRequest { Operation = "status", ProfileId = "abcdef" });
        ExpectRequestInvalid("deactivate with report flag", new BrokerRequest { Operation = "deactivate", ReportClientInfo = true });
        ExpectRequestInvalid("deactivate with router flag", new BrokerRequest { Operation = "deactivate", SetupRouter = true });
        ExpectRequestInvalid("deactivate with pause duration", new BrokerRequest { Operation = "deactivate", PauseMinutes = 5 });
        ExpectRequestInvalid("activate with pause duration", new BrokerRequest { Operation = "activate", ProfileId = "abcdef", PauseMinutes = 5 });
        ExpectRequestInvalid("pause without duration", new BrokerRequest { Operation = "pause" });
        foreach (int duration in new[] { 0, 1, 10, 60 })
            ExpectRequestInvalid("invalid temporary pause duration", new BrokerRequest { Operation = "pause", PauseMinutes = duration });
    }

    private static async Task TestTemporaryPauseAsync()
    {
        DateTimeOffset now = new(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(-3));
        foreach (int minutes in new[] { 5, 15, 30 })
        {
            DateTimeOffset deadline = TemporaryPausePolicy.CreateDeadlineUtc(minutes, now);
            Check(deadline == now.ToUniversalTime().AddMinutes(minutes),
                $"{minutes}-minute pause deadline is stored as an absolute UTC time.");
            Check(TemporaryPausePolicy.GetTimerDelay(deadline, now) == TimeSpan.FromMinutes(minutes),
                $"{minutes}-minute pause schedules its expiry timer.");
        }

        DateTimeOffset until = TemporaryPausePolicy.CreateDeadlineUtc(5, now);
        var active = new TemporaryPauseIntent(true, null);
        TemporaryPauseIntent paused = TemporaryPausePolicy.ApplyExplicitIntent(
            active, TemporaryPauseExplicitAction.Pause, until);
        Check(paused.Enabled && paused.PauseUntilUtc == until,
            "pausing preserves the wanted-active intent and records a deadline.");
        Check(TemporaryPausePolicy.GetReconcileAction(true, true, until, now) == TemporaryPauseReconcileAction.KeepPaused,
            "reconciliation holds protection off before its deadline.");
        Check(TemporaryPausePolicy.GetReconcileAction(true, true, until, until) == TemporaryPauseReconcileAction.Activate,
            "reconciliation resumes at the deadline.");
        Check(TemporaryPausePolicy.GetReconcileAction(false, true, until, now) == TemporaryPauseReconcileAction.Deactivate,
            "an explicit off intent wins over a pending pause.");
        Check(TemporaryPausePolicy.GetReconcileAction(true, false, null, now) == TemporaryPauseReconcileAction.Deactivate &&
              TemporaryPausePolicy.GetReconcileAction(true, false, until, now) == TemporaryPauseReconcileAction.Deactivate,
            "a missing or invalid saved configuration fails closed, even with a future pause deadline.");
        Check(TemporaryPausePolicy.GetReconcileAction(true, true, null, now) == TemporaryPauseReconcileAction.Activate,
            "a valid saved configuration allows active-state reconciliation.");
        Check(TemporaryPausePolicy.GetTimerDelay(null, now) == Timeout.InfiniteTimeSpan &&
              TemporaryPausePolicy.GetTimerDelay(until, until) == Timeout.InfiniteTimeSpan,
            "no pause or an elapsed pause leaves no repeated timer armed.");

        TemporaryPauseIntent resumed = TemporaryPausePolicy.ApplyExplicitIntent(
            paused, TemporaryPauseExplicitAction.Resume);
        TemporaryPauseIntent activated = TemporaryPausePolicy.ApplyExplicitIntent(
            paused, TemporaryPauseExplicitAction.Activate);
        TemporaryPauseIntent deactivated = TemporaryPausePolicy.ApplyExplicitIntent(
            paused, TemporaryPauseExplicitAction.Deactivate);
        Check(resumed.Enabled && resumed.PauseUntilUtc is null && activated.Enabled && activated.PauseUntilUtc is null,
            "explicit resume and activation cancel the pending deadline.");
        Check(!deactivated.Enabled && deactivated.PauseUntilUtc is null,
            "explicit deactivation cancels the deadline and remains off.");

        var order = new List<string>();
        DateTimeOffset? persistedDeadline = null;
        await ExpectFailureAsync("pause restore failure leaves a durable deadline", async () =>
        {
            await TemporaryPauseTransaction.PauseAsync(until,
                deadline => { order.Add("persist-deadline"); persistedDeadline = deadline; return Task.CompletedTask; },
                () => { order.Add("restore-dns"); throw new InvalidOperationException("injected restore failure"); }).ConfigureAwait(false);
        }, typeof(InvalidOperationException)).ConfigureAwait(false);
        Check(order.SequenceEqual(new[] { "persist-deadline", "restore-dns" }) && persistedDeadline == until,
            "pause persists its deadline before attempting DNS restoration.");
        Check(TemporaryPausePolicy.GetReconcileAction(true, true, persistedDeadline, now) == TemporaryPauseReconcileAction.KeepPaused,
            "a failed pause restoration remains safely paused and can be retried by the service.");

        order.Clear();
        persistedDeadline = until;
        bool wanted = true;
        await ExpectFailureAsync("explicit deactivation remains persisted after DNS failure", async () =>
        {
            await TemporaryPauseTransaction.DeactivateAsync(
                () => { order.Add("persist-off"); wanted = false; return Task.CompletedTask; },
                () => { order.Add("clear-deadline"); persistedDeadline = null; return Task.CompletedTask; },
                () => { order.Add("restore-dns"); throw new InvalidOperationException("injected restore failure"); }).ConfigureAwait(false);
        }, typeof(InvalidOperationException)).ConfigureAwait(false);
        Check(order.SequenceEqual(new[] { "persist-off", "clear-deadline", "restore-dns" }) && !wanted && persistedDeadline is null,
            "explicit deactivation persists off before DNS restoration and cannot auto-resume.");

        order.Clear();
        persistedDeadline = until;
        await ExpectFailureAsync("resume failure remains retryable", async () =>
        {
            await TemporaryPauseTransaction.ResumeAsync(
                () => { order.Add("persist-resume-due"); persistedDeadline = now; return Task.CompletedTask; },
                () => { order.Add("enable-dns"); throw new InvalidOperationException("injected enable failure"); },
                () => { order.Add("clear-deadline"); persistedDeadline = null; return Task.CompletedTask; }).ConfigureAwait(false);
        }, typeof(InvalidOperationException)).ConfigureAwait(false);
        Check(order.SequenceEqual(new[] { "persist-resume-due", "enable-dns" }) && persistedDeadline == now &&
              TemporaryPausePolicy.GetReconcileAction(true, true, persistedDeadline, now) == TemporaryPauseReconcileAction.Activate,
            "a failed resume keeps an expired durable marker for service retry.");

        order.Clear();
        await TemporaryPauseTransaction.ResumeAsync(
            () => { order.Add("persist-resume-due"); persistedDeadline = now; return Task.CompletedTask; },
            () => { order.Add("enable-dns"); return Task.CompletedTask; },
            () => { order.Add("clear-deadline"); persistedDeadline = null; return Task.CompletedTask; }).ConfigureAwait(false);
        Check(order.SequenceEqual(new[] { "persist-resume-due", "enable-dns", "clear-deadline" }) && persistedDeadline is null,
            "a successful resume clears the durable marker only after DNS is active.");
    }

    private static void TestServiceReconcilePolicy()
    {
        DateTimeOffset now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset retryAt = now.AddSeconds(10);
        Check(!ServiceReconcilePolicy.RetryGateElapsed(retryAt, now) &&
              ServiceReconcilePolicy.RetryGateElapsed(retryAt, retryAt),
            "a reconciliation retry remains gated until its scheduled time.");
        Check(ServiceReconcilePolicy.DebounceDelay(TimeSpan.Zero, retryAt, now) == TimeSpan.FromSeconds(10) &&
              ServiceReconcilePolicy.DebounceDelay(TimeSpan.FromSeconds(2), retryAt, now) == TimeSpan.FromSeconds(10) &&
              ServiceReconcilePolicy.DebounceDelay(TimeSpan.FromSeconds(2), null, now) == TimeSpan.FromSeconds(2),
            "pending signals and network debounce cannot shorten an active retry backoff.");
        Check(!ServiceReconcilePolicy.ShouldDrainPendingImmediately(succeeded: false, pending: true) &&
              ServiceReconcilePolicy.ShouldDrainPendingImmediately(succeeded: true, pending: true) &&
              !ServiceReconcilePolicy.ShouldDrainPendingImmediately(succeeded: true, pending: false),
            "only a successful reconciliation immediately drains coalesced signals.");
    }

    private static void TestServiceRemovalRecovery()
    {
        var calls = new List<string>();
        var removalFailure = new InvalidOperationException("injected DNS restoration failure");
        Exception? result = null;
        try
        {
            ServiceRemovalRecovery.Run(
                brokerExisted: true,
                removeOwnedClient: () => { calls.Add("remove"); throw removalFailure; },
                restartBroker: () => calls.Add("restart"));
        }
        catch (Exception error) { result = error; }

        Check(calls.SequenceEqual(new[] { "remove", "restart" }) &&
              result is InvalidOperationException && ReferenceEquals(result.InnerException, removalFailure),
            "failed removal restarts the existing broker and preserves the original failure.");

        calls.Clear();
        var restartFailure = new InvalidOperationException("injected broker start failure");
        result = null;
        try
        {
            ServiceRemovalRecovery.Run(
                brokerExisted: true,
                removeOwnedClient: () => { calls.Add("remove"); throw removalFailure; },
                restartBroker: () => { calls.Add("restart"); throw restartFailure; });
        }
        catch (Exception error) { result = error; }

        Check(calls.SequenceEqual(new[] { "remove", "restart" }) &&
              result is AggregateException aggregate && aggregate.InnerExceptions.Contains(removalFailure) &&
              aggregate.InnerExceptions.Contains(restartFailure),
            "a failed broker restart reports both failures while leaving recovery data untouched.");

        calls.Clear();
        result = null;
        try
        {
            ServiceRemovalRecovery.Run(
                brokerExisted: false,
                removeOwnedClient: () => { calls.Add("remove"); throw removalFailure; },
                restartBroker: () => calls.Add("restart"));
        }
        catch (Exception error) { result = error; }

        Check(calls.SequenceEqual(new[] { "remove" }) && ReferenceEquals(result, removalFailure),
            "failed removal does not start a broker that was not registered.");
    }

    private static async Task<int> RunStatusAsync()
    {
        PrintAndRequireNonElevated();
        BrokerResponse response = await new BrokerClient().SendAsync(new BrokerRequest { Operation = "status" }).ConfigureAwait(false);
        Require(response.Success, "The broker status request failed.");
        PrintStatus(response);
        return 0;
    }

    private static async Task<int> RunCycleAsync()
    {
        PrintAndRequireNonElevated();
        Dictionary<string, string[]> beforeDns = CaptureDnsSnapshot();
        Require(beforeDns.Count > 0, "No non-loopback network interface is up; cycle aborted without mutation.");
        Require(!ContainsLoopbackResolver(beforeDns), "DNS already points to a loopback resolver; cycle aborted without mutation.");
        BrokerClient client = new();
        BrokerResponse initialStatus = await client.SendAsync(new BrokerRequest { Operation = "status" }).ConfigureAwait(false);
        Require(initialStatus.Success, "Could not read broker status before the cycle.");
        if (initialStatus.Active || !string.Equals(initialStatus.State, "inactive", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The broker was not inactive at the start; cycle aborted without sending an activation request.");

        string profileId = ReadFirstValidProfileId();
        bool activationAttempted = false;
        bool activationSucceeded = false;
        Exception? activationFailure = null;
        Exception? cleanupFailure = null;

        try
        {
            activationAttempted = true;
            BrokerResponse activated = await client.SendAsync(new BrokerRequest
            {
                Operation = "activate",
                ProfileId = profileId
            }).ConfigureAwait(false);
            activationSucceeded = activated.Success;
            Require(activated.Success, "The broker rejected the activation request.");
            Require(activated.Active && string.Equals(activated.State, "active", StringComparison.OrdinalIgnoreCase),
                "The broker did not report active after activation.");
            Console.WriteLine("PASS: broker reported active.");
        }
        catch (Exception ex)
        {
            activationFailure = ex;
        }
        finally
        {
            try
            {
                bool needsCleanup = activationSucceeded;
                if (activationAttempted && !needsCleanup)
                {
                    BrokerResponse afterFailedActivation = await client.SendAsync(new BrokerRequest { Operation = "status" }).ConfigureAwait(false);
                    needsCleanup = afterFailedActivation.Success &&
                        (afterFailedActivation.Active || string.Equals(afterFailedActivation.State, "attention", StringComparison.OrdinalIgnoreCase));
                }

                if (needsCleanup)
                {
                    BrokerResponse deactivated = await client.SendAsync(new BrokerRequest { Operation = "deactivate" }).ConfigureAwait(false);
                    Require(deactivated.Success && !deactivated.Active &&
                            string.Equals(deactivated.State, "inactive", StringComparison.OrdinalIgnoreCase),
                        "The broker did not confirm a completed deactivation in cycle cleanup.");
                    Console.WriteLine("PASS: broker reported inactive after cleanup.");
                }
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }
        }

        if (activationFailure != null)
            throw new InvalidOperationException("Activation failed; cleanup was attempted in finally.", activationFailure);
        if (cleanupFailure != null)
            throw new InvalidOperationException("The mandatory deactivation cleanup failed.", cleanupFailure);

        int comparedInterfaces = await WaitForDnsSnapshotRestoredAsync(beforeDns, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        BrokerResponse finalStatus = await client.SendAsync(new BrokerRequest { Operation = "status" }).ConfigureAwait(false);
        Require(finalStatus.Success && !finalStatus.Active &&
                string.Equals(finalStatus.State, "inactive", StringComparison.OrdinalIgnoreCase),
            "The final broker status is not inactive.");
        Console.WriteLine($"PASS: DNS server snapshot restored on {comparedInterfaces} stable interface(s).");
        return 0;
    }

    private static async Task<int> RunMalformedLiveAsync()
    {
        PrintAndRequireNonElevated();
        BrokerClient client = new();
        BrokerResponse before = await client.SendAsync(new BrokerRequest { Operation = "status" }).ConfigureAwait(false);
        Require(before.Success, "Could not read broker status before malformed requests.");

        BrokerRequest[] invalidRequests =
        [
            new() { Operation = "activate", ProfileId = null },
            new() { Operation = "activate", ProfileId = "abcde" },
            new() { Operation = "activate", ProfileId = "abcdef\"" },
            new() { Operation = "activate", ProfileId = "abcdef -auto-activate" },
            new() { Operation = "activate", ProfileId = "ébcdef" },
            new() { Operation = "install" },
            new() { Operation = "deactivate", ProfileId = "abcdef" }
        ];

        foreach (BrokerRequest request in invalidRequests)
        {
            BrokerResponse response = await client.SendAsync(request).ConfigureAwait(false);
            Require(response.Version == BrokerProtocol.Version && !response.Success,
                "The live broker did not reject an invalid request.");
        }

        const string unknownProperty = "{\"Version\":1,\"Operation\":\"status\",\"ProfileId\":null,\"ReportClientInfo\":false,\"SetupRouter\":false,\"Unexpected\":true}";
        BrokerResponse unknownResponse = await SendRawVerifiedAsync(unknownProperty).ConfigureAwait(false);
        Require(unknownResponse.Version == BrokerProtocol.Version && !unknownResponse.Success,
            "The live broker did not reject JSON with an unknown property.");

        BrokerResponse after = await client.SendAsync(new BrokerRequest { Operation = "status" }).ConfigureAwait(false);
        Require(SameStatus(before, after), "Malformed requests changed the broker status.");
        Console.WriteLine($"PASS: broker rejected {invalidRequests.Length} invalid requests and unknown JSON; status stayed stable.");
        return 0;
    }

    private static async Task<BrokerResponse> SendRawVerifiedAsync(string json)
    {
        using var pipe = new NamedPipeClientStream(
            ".", BrokerProtocol.PipeName, PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.Synchronize, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        VerifyBrokerServer(pipe);

        byte[] payload = Encoding.UTF8.GetBytes(json + "\n");
        await pipe.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        return await BrokerProtocol.ReadAsync<BrokerResponse>(pipe, timeout.Token).ConfigureAwait(false);
    }

    private static void VerifyBrokerServer(NamedPipeClientStream pipe)
    {
        MethodInfo verifier = typeof(BrokerClient).GetMethod("VerifyServerIdentity", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The linked BrokerClient server verifier was not found.");
        try
        {
            verifier.Invoke(null, [pipe]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static string ReadFirstValidProfileId()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextDNSManager", "profiles.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("No profiles.json file was found for the current Windows user.");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The current user's profiles.json is not a JSON array.");

        foreach (JsonElement profile in document.RootElement.EnumerateArray())
        {
            if (profile.ValueKind != JsonValueKind.Object) continue;
            string? id = null;
            foreach (JsonProperty property in profile.EnumerateObject())
            {
                if (string.Equals(property.Name, "ID", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    id = property.Value.GetString();
                    break;
                }
            }
            if (id != null && Regex.IsMatch(id, "\\A[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant))
                return id;
        }

        throw new InvalidDataException("No six-character hexadecimal profile ID was found in profiles.json.");
    }

    private static Dictionary<string, string[]> CaptureDnsSnapshot()
    {
        var snapshot = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                networkInterface.OperationalStatus != OperationalStatus.Up)
                continue;

            string id = networkInterface.Id;
            string[] addresses = networkInterface.GetIPProperties().DnsAddresses
                .Select(address => address.ToString())
                .ToArray();
            snapshot[id] = addresses;
        }
        return snapshot;
    }

    private static int CompareStableDnsSnapshots(
        IReadOnlyDictionary<string, string[]> before,
        IReadOnlyDictionary<string, string[]> after)
    {
        int compared = 0;
        foreach ((string id, string[] beforeAddresses) in before)
        {
            if (!after.TryGetValue(id, out string[]? afterAddresses)) continue;
            compared++;
            if (!AddressListsEqual(beforeAddresses, afterAddresses))
                throw new InvalidOperationException("DNS server addresses changed after deactivation on a stable network interface.");
        }
        return compared;
    }

    private static async Task<int> WaitForDnsSnapshotRestoredAsync(
        IReadOnlyDictionary<string, string[]> before,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        Exception? lastMismatch = null;
        while (DateTime.UtcNow < deadline)
        {
            Dictionary<string, string[]> after = CaptureDnsSnapshot();
            try
            {
                int compared = CompareStableDnsSnapshots(before, after);
                if (compared > 0) return compared;
            }
            catch (InvalidOperationException ex)
            {
                lastMismatch = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }

        if (lastMismatch != null) throw lastMismatch;
        throw new InvalidOperationException("No network interface remained stable long enough to compare DNS snapshots.");
    }

    private static bool AddressListsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (!IPAddress.TryParse(left[index], out IPAddress? leftAddress) ||
                !IPAddress.TryParse(right[index], out IPAddress? rightAddress) ||
                !leftAddress.Equals(rightAddress))
                return false;
        }
        return true;
    }

    private static bool ContainsLoopbackResolver(IReadOnlyDictionary<string, string[]> snapshot) =>
        snapshot.Values.SelectMany(addresses => addresses).Any(address =>
            IPAddress.TryParse(address, out IPAddress? parsed) &&
            (IPAddress.IsLoopback(parsed) || parsed.Equals(IPAddress.IPv6Loopback)));

    private static bool SameStatus(BrokerResponse left, BrokerResponse right) =>
        left.Success == right.Success && left.Active == right.Active &&
        string.Equals(left.State, right.State, StringComparison.Ordinal) &&
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal) &&
        left.PauseUntilUtc == right.PauseUntilUtc;

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void PrintAndRequireNonElevated()
    {
        bool admin = IsAdministrator();
        Console.WriteLine($"Admin: {admin.ToString().ToLowerInvariant()}");
        Require(!admin, "Run live probes from the current non-elevated controller session.");
    }

    private static void PrintStatus(BrokerResponse response)
    {
        Console.WriteLine($"Success: {response.Success.ToString().ToLowerInvariant()}");
        Console.WriteLine($"Active: {response.Active.ToString().ToLowerInvariant()}");
        Console.WriteLine($"State: {response.State}");
        if (response.PauseUntilUtc is { } pauseUntil)
            Console.WriteLine($"Pause until (UTC): {pauseUntil.ToUniversalTime():O}");
        if (!string.IsNullOrWhiteSpace(response.Error)) Console.WriteLine($"Error: {response.Error}");
        // Deliberately omit ProfileId from all probe output.
    }

    private static void ExpectRequestValid(string label, BrokerRequest request)
    {
        NextDnsEngine.ValidateRequest(request);
        Check(true, label);
    }

    private static void ExpectRequestInvalid(string label, BrokerRequest request)
    {
        bool rejected = false;
        try { NextDnsEngine.ValidateRequest(request); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, label);
    }

    private static async Task ExpectFailureAsync(string label, Func<Task> operation, Type expectedException)
    {
        Exception? failure = null;
        try { await operation().ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        Check(failure != null && expectedException.IsAssignableFrom(failure.GetType()), label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + label);
        _assertions++;
    }

    private enum ProfileSwitchFailurePoint
    {
        None,
        Begin,
        DisableDns,
        Configure,
        RestartTarget,
        Verify,
        SetIpv4,
        SetIpv6,
        Commit,
        CommitAfterDelete,
        RollbackDns,
        RollbackConfigure,
        RollbackRestart,
        RollbackVerify,
        RollbackIpv4,
        RollbackIpv6,
        RollbackClear
    }

    private sealed class FakeProfileSwitchActions : IProfileSwitchActions
    {
        private readonly ProfileSwitchFailurePoint _failurePoint;
        private readonly ProfileSwitchFailurePoint _rollbackFailurePoint;
        private readonly bool _timeoutRollback;
        private readonly CancellationTokenSource? _cancelCallerAfterDisable;

        internal FakeProfileSwitchActions(
            ProfileSwitchFailurePoint failurePoint = ProfileSwitchFailurePoint.None,
            ProfileSwitchFailurePoint rollbackFailurePoint = ProfileSwitchFailurePoint.None,
            bool timeoutRollback = false,
            CancellationTokenSource? cancelCallerAfterDisable = null)
        {
            _failurePoint = failurePoint;
            _rollbackFailurePoint = rollbackFailurePoint;
            _timeoutRollback = timeoutRollback;
            _cancelCallerAfterDisable = cancelCallerAfterDisable;
        }

        internal List<string> Calls { get; } = new();
        internal int RecoveryMarkers { get; private set; }
        internal bool RestoreTokenWasIndependent { get; private set; }
        internal bool JournalPresent { get; private set; }

        public Task BeginAsync(CancellationToken cancellationToken)
        {
            Calls.Add("journal-begin");
            cancellationToken.ThrowIfCancellationRequested();
            if (_failurePoint == ProfileSwitchFailurePoint.Begin)
                throw new InvalidOperationException("injected journal write failure");
            JournalPresent = true;
            return Task.CompletedTask;
        }

        public Task DisableDnsAsync(CancellationToken cancellationToken)
        {
            Calls.Add("disable-dns");
            if (_failurePoint == ProfileSwitchFailurePoint.DisableDns)
                throw new InvalidOperationException("injected disable failure");
            _cancelCallerAfterDisable?.Cancel();
            return Task.CompletedTask;
        }

        public Task SetTargetConfigurationAsync(CancellationToken cancellationToken) =>
            StepAsync("configure-target", ProfileSwitchFailurePoint.Configure, cancellationToken);

        public Task RestartTargetServiceAsync(CancellationToken cancellationToken) =>
            StepAsync("restart-target", ProfileSwitchFailurePoint.RestartTarget, cancellationToken);

        public Task VerifyTargetResolverAsync(CancellationToken cancellationToken) =>
            StepAsync("verify-target", ProfileSwitchFailurePoint.Verify, cancellationToken);

        public Task EnableTargetDnsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("set-target-ipv4");
            if (_failurePoint == ProfileSwitchFailurePoint.SetIpv4)
                throw new InvalidOperationException("injected ipv4 failure");

            Calls.Add("set-target-ipv6");
            if (_failurePoint == ProfileSwitchFailurePoint.SetIpv6)
                throw new InvalidOperationException("injected ipv6 failure");

            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Calls.Add("journal-commit");
            cancellationToken.ThrowIfCancellationRequested();
            if (_failurePoint == ProfileSwitchFailurePoint.Commit)
                throw new InvalidOperationException("injected journal commit failure");
            JournalPresent = false;
            if (_failurePoint == ProfileSwitchFailurePoint.CommitAfterDelete)
                throw new InvalidOperationException("injected journal flush failure after delete");
            return Task.CompletedTask;
        }

        public Task BeginRollbackAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JournalPresent = true;
            return Task.CompletedTask;
        }

        public async Task DisableDnsForRollbackAsync(CancellationToken cancellationToken)
        {
            RestoreTokenWasIndependent = _cancelCallerAfterDisable?.IsCancellationRequested == true &&
                !cancellationToken.IsCancellationRequested;
            await RollbackStepAsync("rollback-disable-dns", ProfileSwitchFailurePoint.RollbackDns, cancellationToken).ConfigureAwait(false);
            if (_timeoutRollback)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        public Task SetPreviousConfigurationAsync(CancellationToken cancellationToken) =>
            RollbackStepAsync("rollback-configure", ProfileSwitchFailurePoint.RollbackConfigure, cancellationToken);

        public Task RestartPreviousServiceAsync(CancellationToken cancellationToken) =>
            RollbackStepAsync("rollback-restart", ProfileSwitchFailurePoint.RollbackRestart, cancellationToken);

        public Task VerifyPreviousResolverAsync(CancellationToken cancellationToken) =>
            RollbackStepAsync("rollback-verify", ProfileSwitchFailurePoint.RollbackVerify, cancellationToken);

        public async Task EnablePreviousDnsAsync(CancellationToken cancellationToken)
        {
            await RollbackStepAsync("rollback-ipv4", ProfileSwitchFailurePoint.RollbackIpv4, cancellationToken).ConfigureAwait(false);
            await RollbackStepAsync("rollback-ipv6", ProfileSwitchFailurePoint.RollbackIpv6, cancellationToken).ConfigureAwait(false);
        }

        public Task CompleteRollbackAsync(CancellationToken cancellationToken) =>
            RollbackStepAsync("rollback-clear-journal", ProfileSwitchFailurePoint.RollbackClear, cancellationToken, () => JournalPresent = false);

        public void MarkRecoveryRequired()
        {
            RecoveryMarkers++;
            JournalPresent = true;
        }

        private Task RollbackStepAsync(string name, ProfileSwitchFailurePoint failurePoint, CancellationToken cancellationToken, Action? onSuccess = null)
        {
            Calls.Add(name);
            cancellationToken.ThrowIfCancellationRequested();
            if (_rollbackFailurePoint == failurePoint)
                throw new InvalidOperationException("injected rollback failure");
            onSuccess?.Invoke();
            return Task.CompletedTask;
        }

        private Task StepAsync(string name, ProfileSwitchFailurePoint failurePoint, CancellationToken cancellationToken)
        {
            Calls.Add(name);
            cancellationToken.ThrowIfCancellationRequested();
            if (_failurePoint == failurePoint)
                throw new InvalidOperationException("injected stage failure");
            return Task.CompletedTask;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
