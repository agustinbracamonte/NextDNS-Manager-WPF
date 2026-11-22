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
                Console.WriteLine($"PASS: {_assertions} BrokerProtocol and request-validation assertions.");
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
