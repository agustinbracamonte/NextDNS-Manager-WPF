using System.Net;
using System.Net.Sockets;
using NextDNSManager.Service;
using NextDNSManager.Shared;

internal static class Program
{
    private static int _assertions;

    private static async Task<int> Main()
    {
        try
        {
            TestActivationFailureEnvelope();
            TestKnownFailureClassificationAfterPrefixRemoval();
            TestAllowlistedStageCategoryDiagnostics();
            TestActivationFailureSummaries();
            TestTcpRowLayouts();
            await TestPreflightReportAwaitingAsync().ConfigureAwait(false);
            TestDnsListenerFilteringAndOwnServiceExclusion();
            TestIncompleteScanRemainsIndeterminate();
            Console.WriteLine($"PASS: {_assertions} offline activation preflight assertions; no sockets or DNS settings changed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            return 1;
        }
    }

    private static void TestActivationFailureEnvelope()
    {
        const string rawMessage = "El servicio oficial de NextDNS sigue instalado.";
        string generated = GenerateServiceActivationError(
            ActivationFailureStage.EngineServiceValidation,
            rawMessage);
        DecodedActivationFailure decoded = ActivationFailureEnvelope.Decode(generated);

        Check(decoded.HasEnvelope && decoded.MetadataIsKnown,
            "service-generated envelope parses only as known stage and category enums.");
        Check(decoded.Stage == ActivationFailureStage.EngineServiceValidation &&
              decoded.Category == ActivationFailureCategory.InvalidOperation,
            "service-generated stage/category survive parsing as enums.");
        Check(decoded.OriginalResponseError == generated && decoded.MessageWithoutPrefix == rawMessage,
            "the original error remains available while the display message is prefix-free.");

        DecodedActivationFailure legacy = ActivationFailureEnvelope.Decode(rawMessage);
        Check(!legacy.HasEnvelope && !legacy.MetadataIsKnown &&
              legacy.MessageWithoutPrefix == rawMessage && legacy.OriginalResponseError == rawMessage,
            "legacy response text stays intact without protocol changes.");
    }

    private static void TestKnownFailureClassificationAfterPrefixRemoval()
    {
        const string known = "El cliente oficial de NextDNS sigue instalado. Retíralo desde Windows antes de activar este administrador.";
        string prefixed = ActivationFailureEnvelope.Encode(
            ActivationFailureStage.EngineServiceValidation,
            ActivationFailureCategory.InvalidOperation,
            known);

        Check(BrokerFailureCatalog.ClassifyResponseError(prefixed) == BrokerFailureCode.OfficialNextDnsServiceDetected,
            "a known cause is still classified after the activation prefix is removed.");

        BrokerResponseException exception = BrokerFailureCatalog.CreateResponseException(prefixed, "Safe localized message.");
        Check(exception.ResponseError == prefixed && exception.ActivationFailure.MessageWithoutPrefix == known &&
              exception.FailureCode == BrokerFailureCode.OfficialNextDnsServiceDetected,
            "the parsed exception keeps the original response while preserving known failure classification.");
    }

    private static void TestAllowlistedStageCategoryDiagnostics()
    {
        const string privateMarker = "profile SECRET 192.0.2.15 process C:\\private\\thing.exe";
        string prefixed = ActivationFailureEnvelope.Encode(
            ActivationFailureStage.DnsApply,
            ActivationFailureCategory.Network,
            privateMarker);
        DecodedActivationFailure decoded = ActivationFailureEnvelope.Decode(prefixed);
        BrokerFailureCode failureCode = BrokerFailureCatalog.ClassifyResponseError(prefixed);
        string record = DiagnosticLog.CreateManagerActivationFailureRecord(
            DateTimeOffset.UnixEpoch,
            DiagnosticEvent.BrokerActionFailure,
            failureCode,
            decoded.Stage,
            decoded.Category);

        Check(record.Contains("BROKER_REPORTED_ERROR_ACTIVATE_DNS_APPLY_NETWORK", StringComparison.Ordinal),
            "activation logs are built from allowlisted failure/stage/category codes.");
        Check(!record.Contains(privateMarker, StringComparison.Ordinal) &&
              !record.Contains("192.0.2.15", StringComparison.Ordinal) &&
              !record.Contains("thing.exe", StringComparison.Ordinal),
            "activation logs omit response text, addresses, and paths.");

        DecodedActivationFailure unknown = ActivationFailureEnvelope.Decode(
            "[NDMERR/ACTIVATE/DNS_APPLY/NETWORK_EXTRA] safe-looking text");
        Check(unknown.HasEnvelope && !unknown.MetadataIsKnown &&
              unknown.Stage == ActivationFailureStage.DnsApply &&
              unknown.Category == ActivationFailureCategory.Unknown,
            "unknown category identifiers cannot become allowlisted metadata.");
        ExpectThrows<ArgumentOutOfRangeException>(
            () => DiagnosticLog.CreateManagerActivationFailureRecord(
                DateTimeOffset.UnixEpoch,
                DiagnosticEvent.BrokerActionFailure,
                BrokerFailureCode.BrokerReportedError,
                unknown.Stage,
                unknown.Category),
            "unknown enum values cannot be serialized as diagnostic IDs.");

        DecodedActivationFailure malicious = ActivationFailureEnvelope.Decode(
            "[NDMERR/ACTIVATE/DNS_APPLY/NETWORK/EXTRA] private text");
        Check(!malicious.HasEnvelope && !malicious.MetadataIsKnown &&
              malicious.MessageWithoutPrefix == "[NDMERR/ACTIVATE/DNS_APPLY/NETWORK/EXTRA] private text",
            "malformed multi-token prefixes are not stripped or accepted.");
    }

    private static void TestActivationFailureSummaries()
    {
        const string privateMessage = "profile SECRET 192.0.2.15 C:\\private\\thing.exe";
        string[] languages = ["es", "en", "fr", "pt", "de"];
        string[] summaries = languages.Select(language =>
            ActivationFailureSummaryFormatter.Format(
                ActivationFailureStage.CliStart,
                ActivationFailureCategory.Network,
                language) ?? throw new InvalidOperationException("A supported language had no activation summary.")).ToArray();

        Check(summaries.Length == 5 && summaries.Distinct(StringComparer.Ordinal).Count() == 5,
            "the privacy-safe activation stage/category summary is translated into all supported languages.");
        Check(summaries.All(summary => !summary.Contains(privateMessage, StringComparison.Ordinal) &&
                                       !summary.Contains("192.0.2.15", StringComparison.Ordinal) &&
                                       !summary.Contains("thing.exe", StringComparison.Ordinal)),
            "the user-facing activation summary is derived from enums and omits private exception details.");
        Check(ActivationFailureSummaryFormatter.Format(
                  ActivationFailureStage.Unknown,
                  ActivationFailureCategory.Network,
                  "es") is null,
            "unknown activation stages are not displayed as trusted summaries.");
    }

    private static void TestTcpRowLayouts()
    {
        DnsTcpRowLayout ipv4 = DnsPortTableLayout.GetTcpRowLayout(AddressFamily.InterNetwork);
        DnsTcpRowLayout ipv6 = DnsPortTableLayout.GetTcpRowLayout(AddressFamily.InterNetworkV6);

        Check(ipv4 == new DnsTcpRowLayout(24, 4, 8, 20),
            "the IPv4 TCP owner row uses its local address, port, and PID offsets.");
        Check(ipv6 == new DnsTcpRowLayout(56, 0, 20, 52),
            "the IPv6 TCP owner row uses the local IPv6 address and local port offsets.");
        ExpectThrows<ArgumentOutOfRangeException>(
            () => DnsPortTableLayout.GetTcpRowLayout(AddressFamily.Unspecified),
            "unsupported TCP owner table address families are rejected.");
    }

    private static async Task TestPreflightReportAwaitingAsync()
    {
        var expected = new ActivationPreflightReport(
            ListenerInspectionComplete: true,
            PossibleDnsListeners: Array.Empty<DnsPortFinding>(),
            ManagerBrokerStatus: PreflightServiceStatus.Running,
            NextDnsServiceStatus: PreflightServiceStatus.Running,
            OfficialServiceStatus: PreflightServiceStatus.NotInstalled,
            NextDnsCliFileStatus: PreflightFileStatus.Present,
            NetworkAdapterInspectionCompleted: true,
            HasOperationalNetworkAdapter: true);
        var capture = new TaskCompletionSource<ActivationPreflightReport?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ActivationPreflightCaptureResult> awaited = ActivationPreflightCapturePolicy.AwaitReportAsync(
            capture.Task,
            TimeSpan.FromSeconds(1));
        capture.SetResult(expected);

        ActivationPreflightCaptureResult completed = await awaited.ConfigureAwait(false);
        Check(ReferenceEquals(completed.Report, expected) && !completed.TimedOut,
            "the UI can await a pending preflight capture rather than silently dropping its result.");

        ActivationPreflightCaptureResult failed = await ActivationPreflightCapturePolicy.AwaitReportAsync(
            Task.FromException<ActivationPreflightReport?>(new IOException("private capture failure")),
            TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Check(failed.Report is null && !failed.TimedOut,
            "a failed local diagnostic returns no report and cannot replace the activation failure.");

        ActivationPreflightCaptureResult timeout = await ActivationPreflightCapturePolicy.AwaitReportAsync(
            new TaskCompletionSource<ActivationPreflightReport?>().Task,
            TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
        Check(timeout.Report is null && timeout.TimedOut,
            "a slow preflight stays bounded and is reported as incomplete instead of blocking the activation error indefinitely.");
    }

    private static void TestDnsListenerFilteringAndOwnServiceExclusion()
    {
        const uint ownPid = 300;
        DnsPortListener[] input =
        [
            new(DnsTransportProtocol.Tcp, IPAddress.Loopback, 53, ownPid),
            new(DnsTransportProtocol.Tcp, IPAddress.Any, 53, 400),
            new(DnsTransportProtocol.Tcp, IPAddress.IPv6Any, 53, 401),
            new(DnsTransportProtocol.Udp, IPAddress.Any, 53, 405),
            new(DnsTransportProtocol.Udp, IPAddress.IPv6Loopback, 53, 402),
            new(DnsTransportProtocol.Udp, IPAddress.Parse("192.0.2.44"), 53, 403),
            new(DnsTransportProtocol.Udp, IPAddress.Loopback, 5353, 404)
        ];
        var snapshot = new ActivationPreflightSnapshot(
            DnsPortTable.All,
            input,
            ownPid,
            PreflightServiceStatus.Running,
            PreflightServiceStatus.Running,
            PreflightServiceStatus.NotInstalled,
            PreflightFileStatus.Present,
            NetworkAdapterInspectionCompleted: true,
            HasOperationalNetworkAdapter: true);

        ActivationPreflightReport report = ActivationPreflightAnalyzer.Analyze(snapshot);
        Check(report.ListenerInspectionComplete && report.PossibleDnsListeners.Count == 4,
            "TCP/UDP IPv4/IPv6 wildcard or loopback port 53 findings remain after filtering.");
        Check(!report.PossibleDnsListeners.Any(finding =>
                finding.Protocol == DnsTransportProtocol.Tcp &&
                finding.AddressFamily == AddressFamily.InterNetwork &&
                finding.Scope == DnsListenerScope.Loopback),
            "a verified own NextDNS service PID is excluded.");
        Check(report.PossibleDnsListeners.Any(finding =>
                finding.Protocol == DnsTransportProtocol.Tcp &&
                finding.AddressFamily == AddressFamily.InterNetworkV6 &&
                finding.Scope == DnsListenerScope.Wildcard),
            "IPv6 wildcard listeners are detected without exposing the address.");
        Check(report.PossibleDnsListeners.Any(finding =>
                finding.Protocol == DnsTransportProtocol.Udp &&
                finding.AddressFamily == AddressFamily.InterNetwork &&
                finding.Scope == DnsListenerScope.Wildcard),
            "IPv4 wildcard UDP listeners are detected.");
        Check(report.PossibleDnsListeners.Any(finding =>
                finding.Protocol == DnsTransportProtocol.Udp &&
                finding.AddressFamily == AddressFamily.InterNetworkV6 &&
                finding.Scope == DnsListenerScope.Loopback),
            "IPv6 loopback UDP listeners are detected.");
        Check(ActivationPreflightAnalyzer.ChooseHint(report) == ActivationPreflightHint.PossibleDnsPortUse,
            "the user-facing summary selects one cautious port hint instead of listing all findings.");

        ActivationPreflightReport unverifiedOwner = ActivationPreflightAnalyzer.Analyze(snapshot with
        {
            TrustedNextDnsProcessId = null
        });
        Check(unverifiedOwner.PossibleDnsListeners.Count == 5,
            "an unverified process is kept as a possible listener instead of being attributed or excluded.");
    }

    private static void TestIncompleteScanRemainsIndeterminate()
    {
        var snapshot = new ActivationPreflightSnapshot(
            DnsPortTable.TcpIPv4 | DnsPortTable.UdpIPv4,
            Array.Empty<DnsPortListener>(),
            TrustedNextDnsProcessId: null,
            PreflightServiceStatus.Unknown,
            PreflightServiceStatus.Unknown,
            PreflightServiceStatus.Unknown,
            PreflightFileStatus.Unknown,
            NetworkAdapterInspectionCompleted: false,
            HasOperationalNetworkAdapter: false);

        ActivationPreflightReport report = ActivationPreflightAnalyzer.Analyze(snapshot);
        Check(!report.ListenerInspectionComplete && report.PossibleDnsListeners.Count == 0,
            "incomplete IPv4/IPv6 and TCP/UDP reads do not claim that no listener exists.");
        Check(ActivationPreflightAnalyzer.ChooseHint(report) == ActivationPreflightHint.None,
            "fully indeterminate checks produce no extra message or popup.");

        ActivationPreflightReport serviceStopped = ActivationPreflightAnalyzer.Analyze(snapshot with
        {
            CompletedTables = DnsPortTable.All,
            ManagerBrokerStatus = PreflightServiceStatus.Running,
            NextDnsServiceStatus = PreflightServiceStatus.Stopped,
            OfficialServiceStatus = PreflightServiceStatus.NotInstalled,
            NextDnsCliFileStatus = PreflightFileStatus.Present,
            NetworkAdapterInspectionCompleted = true,
            HasOperationalNetworkAdapter = true
        });
        Check(ActivationPreflightAnalyzer.ChooseHint(serviceStopped) == ActivationPreflightHint.NextDnsServiceNotReady,
            "when there is no listener, a stopped engine service becomes the single hint.");

        ActivationPreflightReport noListener = ActivationPreflightAnalyzer.Analyze(snapshot with
        {
            CompletedTables = DnsPortTable.All,
            ManagerBrokerStatus = PreflightServiceStatus.Running,
            NextDnsServiceStatus = PreflightServiceStatus.Running,
            OfficialServiceStatus = PreflightServiceStatus.NotInstalled,
            NextDnsCliFileStatus = PreflightFileStatus.Present,
            NetworkAdapterInspectionCompleted = true,
            HasOperationalNetworkAdapter = true
        });
        Check(ActivationPreflightAnalyzer.ChooseHint(noListener) == ActivationPreflightHint.NoLoopbackOrWildcardListenerObserved,
            "a complete empty scan remains caveated against driver/filter interference.");
    }

    private static string GenerateServiceActivationError(ActivationFailureStage stage, string message)
    {
        Exception? captured = null;
        using (BrokerOperationDiagnostics.BeginActivation(enabled: true))
        {
            try
            {
                BrokerOperationDiagnostics.RunSync(stage, (Action)(() => throw new InvalidOperationException(message)));
            }
            catch (Exception exception)
            {
                captured = exception;
            }
        }

        return BrokerOperationDiagnostics.FormatBrokerError(captured ??
            throw new InvalidOperationException("The service probe did not capture an activation failure."));
    }

    private static void Check(bool condition, string description)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("Assertion failed: " + description);
    }

    private static void ExpectThrows<T>(Action action, string description) where T : Exception
    {
        _assertions++;
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Assertion failed: " + description);
    }
}
