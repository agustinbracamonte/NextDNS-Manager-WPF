using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
                Console.WriteLine($"PASS: {_assertions} offline broker, journal, and profile-switch assertions.");
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
        TestRequestValidation();
        await TestProfileSwitchTransactionAsync().ConfigureAwait(false);
        TestProfileSwitchJournal();
        TestProfileSwitchSelection();
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
              actual.ProfileId == expected.ProfileId && actual.ReportClientInfo && actual.SetupRouter,
            "BrokerProtocol round-trips a valid newline-delimited request.");
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
        ExpectRequestInvalid("unsupported protocol version", new BrokerRequest { Version = BrokerProtocol.Version + 1 });
        ExpectRequestInvalid("status with profile parameter", new BrokerRequest { Operation = "status", ProfileId = "abcdef" });
        ExpectRequestInvalid("deactivate with report flag", new BrokerRequest { Operation = "deactivate", ReportClientInfo = true });
        ExpectRequestInvalid("deactivate with router flag", new BrokerRequest { Operation = "deactivate", SetupRouter = true });
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
        string.Equals(left.ProfileId, right.ProfileId, StringComparison.Ordinal);

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
