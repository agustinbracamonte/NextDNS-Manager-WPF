using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NextDNSModernWPF;

internal static class Program
{
    private const string SyntheticApiKey = "synthetic-probe-key";
    private static int _assertions;

    private static async Task<int> Main()
    {
        try
        {
            await TestLogResponseAndHeaderAsync();
            await TestAnalyticsDevicesPaginationAsync();
            await TestAnalyticsDevicesLimitAsync();
            await TestAnalyticsDevicesCursorLoopAsync();
            await TestAnalyticsDevicesFilterValidationAsync();
            await TestAnalyticsDevicesCancellationAsync();
            TestActivityAutoLoadPolicy();
            await TestLiveLogStreamAsync();
            await TestLiveLogStreamCancellationAsync();
            await TestLiveLogStreamApiErrorAsync();
            await TestActivityStreamReconnectPolicyAsync();
            await TestProfilePaginationQueryAsync();
            await TestListPaginationAsync();
            await TestAllowlistPaginationAsync();
            await TestListLimitErrorIsUsefulAndRedactedAsync();
            await TestBulkListAddPreservesExistingStateAsync();
            await TestBulkListAddCanExplicitlyReactivateAsync();
            await TestHttp200ErrorsAsync();
            TestDomainNormalizationAndImport();
            TestDpapiKeyStorage();

            Console.WriteLine($"PASS: {_assertions} NextDNS API assertions (simulated transport only).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static async Task TestLogResponseAndHeaderAsync()
    {
        const string response = """
        {
          "data": [
            {
              "timestamp": "2026-11-25T09:20:10.338Z",
              "domain": "sb.scorecardresearch.com",
              "root": "scorecardresearch.com",
              "tracker": "scorecard_research_beacon",
              "encrypted": false,
              "protocol": "UDP",
              "clientIp": "192.0.2.7",
              "status": "blocked",
              "reasons": [
                { "id": "blocklist:oisd", "name": "oisd" },
                { "id": "denylist", "name": "Denylist" }
              ]
            }
          ],
          "meta": {
            "pagination": { "cursor": "cursor-2" },
            "stream": { "id": "stream-id" }
          }
        }
        """;
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.OK, response));
        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        NextDnsPage<NextDnsLogEntry> page = await client.GetLogsAsync("abc123", new NextDnsLogsQuery
        {
            Limit = 10,
            From = "2026-11-25T00:00:00.000Z",
            To = "2026-11-26T00:00:00.000Z",
            Device = "device-1",
            Status = NextDnsLogStatus.Blocked,
            Search = "scorecard",
            Raw = true
        });

        Check(page.Items.Count == 1, "A log page is deserialized.");
        Check(page.Items[0].Domain == "sb.scorecardresearch.com", "The log domain is preserved.");
        Check(page.Items[0].TimestampLocal?.Offset == TimeZoneInfo.Local.GetUtcOffset(page.Items[0].Timestamp!.Value), "Log display uses the machine's local time zone.");
        Check(page.Items[0].TimestampLocal?.ToUniversalTime() == page.Items[0].Timestamp?.ToUniversalTime(), "Converting for display preserves the instant.");
        List<NextDnsLogReason>? reasons = page.Items[0].Reasons;
        Check(reasons is { Count: 2 } && reasons[0].Id == "blocklist:oisd" && reasons[0].Name == "oisd",
            "Log reasons deserialize as id/name objects.");
        Check(page.NextCursor == "cursor-2" && page.StreamId == "stream-id", "Log pagination and stream metadata are parsed.");

        Uri? requestUri = handler.Requests.Single().Uri;
        Check(requestUri is not null && requestUri.Host == "api.nextdns.io", "Requests target the fixed HTTPS API host.");
        Check(handler.Requests.Single().ApiKey == SyntheticApiKey, "The synthetic key is sent in X-Api-Key.");
        Check(requestUri!.Query.Contains("status=blocked", StringComparison.Ordinal) &&
              requestUri.Query.Contains("raw=1", StringComparison.Ordinal) &&
              requestUri.Query.Contains("search=scorecard", StringComparison.Ordinal) &&
              Uri.UnescapeDataString(requestUri.Query).Contains("from=2026-11-25T00:00:00.000Z", StringComparison.Ordinal) &&
              Uri.UnescapeDataString(requestUri.Query).Contains("to=2026-11-26T00:00:00.000Z", StringComparison.Ordinal) &&
              requestUri.Query.Contains("device=device-1", StringComparison.Ordinal),
            "Search, date, device, status and raw filters are retained in the log request.");
        Check(!requestUri.ToString().Contains(SyntheticApiKey, StringComparison.Ordinal), "The key never appears in the URL.");
    }

    private static void TestActivityAutoLoadPolicy()
    {
        Check(NextDnsActivityAutoLoadPolicy.ShouldQueueForCurrentVisit(
                activityVisible: true, apiConnected: true, profileSelected: true, filtersNeedApply: false,
                requestAlreadyIssuedForVisit: false, activityRequestInProgress: false),
            "Opening Activity with a connected profile queues a log request.");
        Check(!NextDnsActivityAutoLoadPolicy.ShouldQueueForCurrentVisit(
                activityVisible: false, apiConnected: true, profileSelected: true, filtersNeedApply: false,
                requestAlreadyIssuedForVisit: false, activityRequestInProgress: false),
            "Activity does not load while its page is hidden.");
        Check(!NextDnsActivityAutoLoadPolicy.ShouldQueueForCurrentVisit(
                activityVisible: true, apiConnected: false, profileSelected: true, filtersNeedApply: false,
                requestAlreadyIssuedForVisit: false, activityRequestInProgress: false),
            "Activity does not request logs without a connected API client.");
        Check(!NextDnsActivityAutoLoadPolicy.ShouldQueueForCurrentVisit(
                activityVisible: true, apiConnected: true, profileSelected: false, filtersNeedApply: false,
                requestAlreadyIssuedForVisit: false, activityRequestInProgress: false),
            "Activity does not request logs without a selected profile.");
        Check(!NextDnsActivityAutoLoadPolicy.ShouldQueueForCurrentVisit(
                activityVisible: true, apiConnected: true, profileSelected: true, filtersNeedApply: false,
                requestAlreadyIssuedForVisit: true, activityRequestInProgress: false),
            "Multiple tab events in one visit do not queue duplicate requests.");
        Check(NextDnsActivityAutoLoadPolicy.ShouldQueueForCurrentVisit(
                activityVisible: true, apiConnected: true, profileSelected: true, filtersNeedApply: false,
                requestAlreadyIssuedForVisit: false, activityRequestInProgress: false),
            "A new visit refreshes activity even when a previous visit loaded a query.");
        Check(!NextDnsActivityAutoLoadPolicy.ShouldQueueForCurrentVisit(
                activityVisible: true, apiConnected: true, profileSelected: true, filtersNeedApply: true,
                requestAlreadyIssuedForVisit: false, activityRequestInProgress: false),
            "Unapplied filter edits are preserved until the user applies them.");
        Check(!NextDnsActivityAutoLoadPolicy.ShouldQueueForCurrentVisit(
                activityVisible: true, apiConnected: true, profileSelected: true, filtersNeedApply: false,
                requestAlreadyIssuedForVisit: false, activityRequestInProgress: true),
            "An in-flight activity request is not duplicated.");
        Check(NextDnsActivityAutoLoadPolicy.ShouldStartQueuedLoad(
                activityVisible: true, apiConnected: true, profileSelected: true,
                filtersNeedApply: false, autoLoadPending: true, activityRequestInProgress: false,
                anotherApiOperationInProgress: false),
            "A queued initial request starts after the view and API become ready.");
        Check(!NextDnsActivityAutoLoadPolicy.ShouldStartQueuedLoad(
                activityVisible: true, apiConnected: true, profileSelected: true,
                filtersNeedApply: false, autoLoadPending: true, activityRequestInProgress: false,
                anotherApiOperationInProgress: true),
            "Activity waits for another API operation instead of duplicating or overlapping calls.");
    }

    private static async Task TestAnalyticsDevicesPaginationAsync()
    {
        var handler = new ScriptedHandler((request, call) =>
        {
            Check(request.Method == HttpMethod.Get && request.Uri!.AbsolutePath == "/profiles/abc123/analytics/devices",
                "Device discovery uses the documented analytics/devices endpoint.");
            string query = Uri.UnescapeDataString(request.Uri.Query);
            Check(query.Contains("limit=500", StringComparison.Ordinal),
                "Analytics device pages use the documented maximum page size.");
            Check(query.Contains("from=2026-11-25T00:00:00Z", StringComparison.Ordinal) &&
                  query.Contains("to=2026-11-26T00:00:00Z", StringComparison.Ordinal),
                "Analytics device date range filters are retained across pages.");

            if (call == 1)
            {
                Check(query == "?limit=500&from=2026-11-25T00:00:00Z&to=2026-11-26T00:00:00Z",
                    "The first analytics device request starts without a cursor.");
                return JsonResponse(HttpStatusCode.OK, """
                {
                  "data": [
                    { "id": "device-a", "name": "Desktop", "model": "Windows", "localIp": "192.0.2.10", "queries": 12345678901 },
                    { "id": "__UNIDENTIFIED__", "queries": 4 }
                  ],
                  "meta": { "pagination": { "cursor": "next+/cursor" } }
                }
                """);
            }

            Check(query == "?limit=500&from=2026-11-25T00:00:00Z&to=2026-11-26T00:00:00Z&cursor=next+/cursor",
                "The next analytics device request retains filters and adds the decoded cursor.");
            return JsonResponse(HttpStatusCode.OK, """
            {
              "data": [{ "id": "device-b", "name": "Phone", "model": "Android", "localIp": "192.0.2.11", "queries": 7 }],
              "meta": { "pagination": { "cursor": null } }
            }
            """);
        });

        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        IReadOnlyList<NextDnsAnalyticsDevice> devices = await client.GetAnalyticsDevicesAsync("abc123",
            from: "2026-11-25T00:00:00Z", to: "2026-11-26T00:00:00Z");
        Check(handler.Requests.Count == 2 && devices.Count == 3,
            "Analytics device discovery follows all cursor pages.");
        Check(devices[0].Id == "device-a" && devices[0].Name == "Desktop" && devices[0].Model == "Windows" &&
              devices[0].LocalIp == "192.0.2.10" && devices[0].Queries == 12345678901L,
            "Analytics device fields deserialize, including 64-bit query counts.");
        Check(devices[1].Id == "__UNIDENTIFIED__" && devices[1].Name is null && devices[1].Queries == 4,
            "Unidentified analytics devices are preserved even without optional metadata.");
        Check(devices[2].Id == "device-b", "Analytics devices from later pages remain in order.");
        Check(handler.Requests.All(request => !request.Uri.ToString().Contains(SyntheticApiKey, StringComparison.Ordinal)),
            "The API key never appears in analytics device URLs.");
    }

    private static async Task TestAnalyticsDevicesLimitAsync()
    {
        string oversizedData = "{ \"data\": [" + string.Join(",", Enumerable.Range(0, 10001)
            .Select(index => "{\"id\":\"device-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"}")) +
            "], \"meta\": { \"pagination\": { \"cursor\": null } } }";
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.OK, oversizedData));
        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        try
        {
            await client.GetAnalyticsDevicesAsync("abc123");
            throw new InvalidOperationException("An oversized analytics device response should be rejected.");
        }
        catch (NextDnsApiException exception)
        {
            Check(exception.Kind == NextDnsApiErrorKind.UnexpectedResponse,
                "Analytics device discovery stops at its total-entry safety bound.");
            Check(handler.Requests.Count == 1,
                "An oversized analytics device response does not trigger another page request.");
        }
    }

    private static async Task TestAnalyticsDevicesCursorLoopAsync()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.OK, """
        { "data": [{ "id": "device-a" }], "meta": { "pagination": { "cursor": "repeat" } } }
        """));
        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        try
        {
            await client.GetAnalyticsDevicesAsync("abc123");
            throw new InvalidOperationException("A repeated analytics device cursor should be rejected.");
        }
        catch (NextDnsApiException exception)
        {
            Check(exception.Kind == NextDnsApiErrorKind.UnexpectedResponse && handler.Requests.Count == 2,
                "Repeated analytics device cursors are detected before an infinite loop.");
        }
    }

    private static async Task TestAnalyticsDevicesFilterValidationAsync()
    {
        var handler = new ScriptedHandler((_, _) => throw new InvalidOperationException("Invalid filters must not send requests."));
        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        bool rejected = false;
        try { await client.GetAnalyticsDevicesAsync("abc123", from: "2026-11-25\n&limit=999999"); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected && handler.Requests.Count == 0,
            "Control characters in analytics filters are rejected before transport.");
    }

    private static async Task TestAnalyticsDevicesCancellationAsync()
    {
        var handler = new CancellableHandler();
        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        using var cancellation = new CancellationTokenSource();
        Task<IReadOnlyList<NextDnsAnalyticsDevice>> request = client.GetAnalyticsDevicesAsync(
            "abc123", cancellationToken: cancellation.Token);
        cancellation.Cancel();

        bool canceled = false;
        try { await request; }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && handler.RequestCount == 1,
            "Canceling analytics device discovery interrupts its in-flight transport request.");
    }

    private static async Task TestListPaginationAsync()
    {
        var handler = new ScriptedHandler((request, call) =>
        {
            Check(request.Uri!.AbsolutePath == "/profiles/abc123/denylist", "List requests use the denylist endpoint.");
            if (request.Uri.Query.Contains("limit=", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.BadRequest, """
                { "errors": [{ "code": "extraneous", "source": { "parameter": "limit" } }] }
                """);
            if (call == 1)
            {
                Check(request.Uri.Query.Length == 0, "The first denylist page does not send an unsupported limit parameter.");
                return JsonResponse(HttpStatusCode.OK, """
                { "data": [{ "id": "one.example", "active": true }], "meta": { "pagination": { "cursor": "next-cursor" } } }
                """);
            }

            Check(request.Uri.Query == "?cursor=next-cursor", "The next denylist request sends only the returned cursor.");
            return JsonResponse(HttpStatusCode.OK, """
            { "data": [{ "id": "two.example", "active": false }], "meta": { "pagination": { "cursor": null } } }
            """);
        });

        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        IReadOnlyList<NextDnsListEntry> entries = await client.GetListAsync("abc123", NextDnsListType.Denylist);
        Check(handler.Requests.Count == 2 && entries.Count == 2, "List retrieval follows both pages.");
        Check(entries[0].Id == "one.example" && entries[1].Id == "two.example" && !entries[1].Active,
            "List entries and active state are retained across pages.");
    }

    private static async Task TestAllowlistPaginationAsync()
    {
        var handler = new ScriptedHandler((request, call) =>
        {
            Check(request.Uri!.AbsolutePath == "/profiles/abc123/allowlist",
                "Allowlist retrieval uses the allowlist collection endpoint.");
            if (request.Uri.Query.Contains("limit=", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.BadRequest, """
                { "errors": [{ "code": "extraneous", "source": { "parameter": "limit" } }] }
                """);
            if (call == 1)
            {
                Check(request.Uri.Query.Length == 0, "The first allowlist page does not send an unsupported limit parameter.");
                return JsonResponse(HttpStatusCode.OK, """
                { "data": [{ "id": "allow-one.example", "active": true }], "meta": { "pagination": { "cursor": "allow-cursor" } } }
                """);
            }

            Check(request.Uri.Query == "?cursor=allow-cursor", "The next allowlist request sends only the returned cursor.");
            return JsonResponse(HttpStatusCode.OK, """
            { "data": [{ "id": "allow-two.example", "active": false }], "meta": { "pagination": { "cursor": null } } }
            """);
        });

        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        IReadOnlyList<NextDnsListEntry> entries = await client.GetListAsync("abc123", NextDnsListType.Allowlist);
        Check(handler.Requests.Count == 2 && entries.Count == 2, "Allowlist retrieval follows both cursor pages.");
        Check(entries[0].Id == "allow-one.example" && entries[1].Id == "allow-two.example" && !entries[1].Active,
            "Allowlist entries and active state are retained across pages.");
    }

    private static async Task TestListLimitErrorIsUsefulAndRedactedAsync()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.BadRequest, """
        { "errors": [{ "code": "extraneous", "detail": "Rejected synthetic-probe-key", "source": { "parameter": "limit" } }] }
        """));
        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        try
        {
            await client.GetListAsync("abc123", NextDnsListType.Denylist);
            throw new InvalidOperationException("A simulated unsupported list limit should be rejected.");
        }
        catch (NextDnsApiException exception)
        {
            Check(exception.Errors.Single().Code == "extraneous" && exception.Errors.Single().Source == "limit",
                "The list query error retains its code and parameter source.");
            Check(exception.Message.Contains("extraneous [limit]", StringComparison.Ordinal),
                "The displayed API error identifies the unsupported limit parameter.");
            Check(!exception.Message.Contains(SyntheticApiKey, StringComparison.Ordinal) &&
                  !exception.Errors.Any(error => (error.Code + error.Detail + error.Source).Contains(SyntheticApiKey, StringComparison.Ordinal)),
                "A list query error redacts the synthetic key from messages and metadata.");
        }
    }

    private static async Task TestProfilePaginationQueryAsync()
    {
        var handler = new ScriptedHandler((request, call) =>
        {
            Check(request.Method == HttpMethod.Get && request.Uri!.AbsolutePath == "/profiles",
                "API key verification requests the profile collection.");
            if (call == 1)
            {
                Check(request.Uri.Query.Length == 0,
                    "The first profile request has no undocumented query parameters.");
                return JsonResponse(HttpStatusCode.OK, """
                { "data": [{ "id": "profile-one", "name": "One" }], "meta": { "pagination": { "cursor": "profile-cursor" } } }
                """);
            }

            Check(request.Uri.Query == "?cursor=profile-cursor",
                "The next profile page sends only the cursor returned by the API.");
            return JsonResponse(HttpStatusCode.OK, """
            { "data": [{ "id": "profile-two", "name": "Two" }], "meta": { "pagination": { "cursor": null } } }
            """);
        });

        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        IReadOnlyList<NextDnsProfileRecord> profiles = await client.GetProfilesAsync();
        Check(handler.Requests.Count == 2 && profiles.Count == 2,
            "Profile collection pagination follows the server cursor.");
        Check(profiles[0].Id == "profile-one" && profiles[1].Id == "profile-two",
            "All profile pages are retained in order.");
    }

    private static async Task TestBulkListAddPreservesExistingStateAsync()
    {
        var handler = new ScriptedHandler((request, call) =>
        {
            if (call == 1)
            {
                Check(request.Method == HttpMethod.Get && request.Uri!.AbsolutePath == "/profiles/abc123/denylist",
                    "Bulk import reads the current list before writing.");
                return JsonResponse(HttpStatusCode.OK, """
                {
                  "data": [
                    { "id": "disabled.example", "active": false },
                    { "id": "enabled.example", "active": true }
                  ],
                  "meta": { "pagination": { "cursor": null } }
                }
                """);
            }

            Check(request.Method == HttpMethod.Post && request.Uri!.AbsolutePath == "/profiles/abc123/denylist",
                "Bulk import only POSTs a genuinely new entry by default.");
            Check(request.Body is not null && request.Body.Contains("\"id\":\"new.example\"", StringComparison.Ordinal),
                "Bulk import sends the new domain.");
            Check(request.Body is not null && !request.Body.Contains("disabled.example", StringComparison.Ordinal) &&
                  !request.Body.Contains("enabled.example", StringComparison.Ordinal),
                "Bulk import does not rewrite any existing domain by default.");
            return JsonResponse(HttpStatusCode.OK, "{ \"data\": { \"id\": \"new.example\", \"active\": true } }");
        });

        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        NextDnsBulkListResult result = await client.AddListEntriesAsync("abc123", NextDnsListType.Denylist,
            ["disabled.example", "enabled.example", "new.example"]);

        Check(result.Added.SequenceEqual(["new.example"]), "Bulk import adds only missing entries.");
        Check(result.Activated.Count == 0, "Bulk import does not reactivate disabled entries by default.");
        Check(result.SkippedExisting.Contains("disabled.example", StringComparer.OrdinalIgnoreCase) &&
              result.SkippedExisting.Contains("enabled.example", StringComparer.OrdinalIgnoreCase),
            "Both active and inactive existing entries are reported as skipped.");
        Check(handler.Requests.Count == 2, "Default bulk import sends no PATCH for existing entries.");
    }

    private static async Task TestBulkListAddCanExplicitlyReactivateAsync()
    {
        var handler = new ScriptedHandler((request, call) =>
        {
            if (call == 1)
                return JsonResponse(HttpStatusCode.OK, """
                {
                  "data": [{ "id": "disabled.example", "active": false }],
                  "meta": { "pagination": { "cursor": null } }
                }
                """);

            Check(request.Method == HttpMethod.Patch &&
                  request.Uri!.AbsolutePath == "/profiles/abc123/allowlist/disabled.example",
                "Explicit reactivation PATCHes only the requested inactive entry.");
            Check(request.Body is not null && request.Body.Contains("\"active\":true", StringComparison.Ordinal),
                "Explicit reactivation sends active=true.");
            return JsonResponse(HttpStatusCode.OK, "{ \"data\": { \"id\": \"disabled.example\", \"active\": true } }");
        });

        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        NextDnsBulkListResult result = await client.AddListEntriesAsync("abc123", NextDnsListType.Allowlist,
            ["disabled.example"], activateExisting: true);

        Check(result.Activated.SequenceEqual(["disabled.example"]), "Reactivation is recorded when explicitly requested.");
        Check(result.Added.Count == 0 && result.SkippedExisting.Count == 0,
            "Explicit reactivation does not report the disabled entry as added or skipped.");
        Check(handler.Requests.Count == 2, "Explicit reactivation uses one read and one PATCH.");
    }

    private static async Task TestLiveLogStreamAsync()
    {
        const string response = """
        : keepalive

        id: event-1
        event: log
        data: {
        data:   "timestamp": "2026-11-25T09:20:10.338Z",
        data:   "domain": "blocked.example",
        data:   "root": "example",
        data:   "status": "blocked",
        data:   "reasons": [{ "id": "denylist", "name": "Denylist" }]
        data: }

        id: event-2
        data: {"timestamp":"2026-11-25T09:21:10.338Z","domain":"allowed.example","status":"allowed","reasons":[]}

        """;
        var handler = new ScriptedHandler((_, _) => EventStreamResponse(response));
        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        var events = new List<NextDnsLogStreamEvent>();
        await foreach (NextDnsLogStreamEvent item in client.StreamLogsAsync("abc123", new NextDnsLogStreamQuery
        {
            Id = "resume/after",
            Device = "phone 1",
            Status = NextDnsLogStatus.Blocked,
            Search = "example",
            Raw = true
        }))
            events.Add(item);

        Check(events.Count == 2, "The SSE parser emits complete log events and ignores keepalive comments.");
        Check(events[0].Id == "event-1" && events[0].EventType == "log" &&
              events[0].Entry.Domain == "blocked.example", "The first SSE event ID, type, and log are parsed.");
        Check(events[0].Entry.Reasons?.Single().Name == "Denylist", "SSE log reasons deserialize as objects.");
        Check(events[1].Id == "event-2" && events[1].Entry.Domain == "allowed.example",
            "The second SSE event is parsed after the first event boundary.");

        RequestSnapshot request = handler.Requests.Single();
        Check(request.AcceptsEventStream, "The stream request advertises text/event-stream.");
        Check(request.Uri.AbsolutePath == "/profiles/abc123/logs/stream", "The request uses the documented live logs endpoint.");
        Check(request.Uri.Query.Contains("id=resume%2Fafter", StringComparison.Ordinal) &&
              request.Uri.Query.Contains("device=phone%201", StringComparison.Ordinal) &&
              request.Uri.Query.Contains("status=blocked", StringComparison.Ordinal) &&
              request.Uri.Query.Contains("search=example", StringComparison.Ordinal) &&
              request.Uri.Query.Contains("raw=1", StringComparison.Ordinal), "Resume ID and supported stream filters are encoded in the query.");
        Check(!request.Uri.ToString().Contains(SyntheticApiKey, StringComparison.Ordinal), "The stream API key never appears in the URL.");
    }

    private static async Task TestLiveLogStreamCancellationAsync()
    {
        const string firstEvent = "id: cancel-1\ndata: {\"timestamp\":\"2026-11-25T09:20:10.338Z\",\"domain\":\"cancel.example\",\"status\":\"default\",\"reasons\":[]}\n\n";
        var stream = new BlockingAfterFirstChunkStream(Encoding.UTF8.GetBytes(firstEvent));
        var handler = new ScriptedHandler((_, _) => EventStreamResponse(stream));
        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        using var cancellation = new CancellationTokenSource();
        IAsyncEnumerator<NextDnsLogStreamEvent> enumerator = client.StreamLogsAsync("abc123", cancellationToken: cancellation.Token).GetAsyncEnumerator();
        try
        {
            Check(await enumerator.MoveNextAsync(), "The live stream yields the first event before waiting for another.");
            Check(enumerator.Current.Id == "cancel-1", "The first event ID is available for reconnecting.");
            cancellation.Cancel();
            bool canceled = false;
            try { _ = await enumerator.MoveNextAsync(); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Caller cancellation stops an open SSE read.");
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
        Check(stream.WasDisposed, "Canceling or disposing the enumerator closes the response stream.");
    }

    private static async Task TestLiveLogStreamApiErrorAsync()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.OK, """
        { "errors": [{ "code": "unauthorized", "detail": "Stream rejected key synthetic-probe-key." }] }
        """));
        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        try
        {
            await foreach (NextDnsLogStreamEvent _ in client.StreamLogsAsync("abc123")) { }
            throw new InvalidOperationException("A JSON error response should not be treated as an empty stream.");
        }
        catch (NextDnsApiException exception)
        {
            Check(exception.Kind == NextDnsApiErrorKind.Rejected && exception.Errors.Single().Code == "unauthorized",
                "An HTTP 200 JSON error from the stream endpoint is surfaced as a typed API rejection.");
            Check(!exception.Message.Contains(SyntheticApiKey, StringComparison.Ordinal),
                "Stream API failures do not expose the key.");
            Check(!exception.Errors.Any(error =>
                    (error.Code + error.Detail + error.Source).Contains(SyntheticApiKey, StringComparison.Ordinal)),
                "Stream error metadata also redacts an echoed key.");
        }
    }

    private static async Task TestActivityStreamReconnectPolicyAsync()
    {
        Type windowType = typeof(MainWindow);
        MethodInfo reconnectDelayMethod = windowType.GetMethod("GetActivityStreamReconnectDelay",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The activity reconnect delay policy was not found.");
        int[] expectedSeconds = [1, 2, 4, 8, 16, 30, 30];
        for (int attempt = 0; attempt < expectedSeconds.Length; attempt++)
        {
            TimeSpan delay = (TimeSpan)reconnectDelayMethod.Invoke(null, [attempt])!;
            Check(delay == TimeSpan.FromSeconds(expectedSeconds[attempt]),
                $"Activity reconnect backoff is capped and grows exponentially (attempt {attempt}).");
        }

        MethodInfo resumeQueryMethod = windowType.GetMethod("ResumeActivityStreamQuery",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The activity stream resume helper was not found.");
        var originalQuery = new NextDnsLogStreamQuery
        {
            Id = "initial-id",
            Device = "device-1",
            Status = NextDnsLogStatus.Blocked,
            Search = "example",
            Raw = true
        };
        var resumedQuery = (NextDnsLogStreamQuery)resumeQueryMethod.Invoke(null, [originalQuery, "last-event-id"])!;
        Check(resumedQuery.Id == "last-event-id" && resumedQuery.Device == originalQuery.Device &&
              resumedQuery.Status == originalQuery.Status && resumedQuery.Search == originalQuery.Search &&
              resumedQuery.Raw == originalQuery.Raw,
            "Reconnecting updates the SSE cursor while preserving the active filters.");

        MethodInfo retryFailureMethod = windowType.GetMethod("IsRetryableActivityStreamFailure",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The activity stream retry policy was not found.");
        NextDnsApiException transient = await CaptureStreamApiFailureAsync(HttpStatusCode.ServiceUnavailable);
        NextDnsApiException permanent = await CaptureStreamApiFailureAsync(HttpStatusCode.Unauthorized);
        Check((bool)retryFailureMethod.Invoke(null, [transient])!,
            "Activity retries transient service failures.");
        Check(!(bool)retryFailureMethod.Invoke(null, [permanent])!,
            "Activity does not retry permanent authentication failures.");
    }

    private static async Task<NextDnsApiException> CaptureStreamApiFailureAsync(HttpStatusCode statusCode)
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(statusCode, "{ \"errors\": [{ \"code\": \"probe\" }] }"));
        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        try
        {
            await foreach (NextDnsLogStreamEvent _ in client.StreamLogsAsync("abc123")) { }
        }
        catch (NextDnsApiException exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected stream API failure for HTTP {(int)statusCode}.");
    }

    private static async Task TestHttp200ErrorsAsync()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.OK, """
        { "errors": [{ "code": "invalid", "detail": "Rejected key synthetic-probe-key", "source": { "pointer": "/synthetic-probe-key" } }] }
        """));
        using var client = new NextDnsApiClient(SyntheticApiKey, handler);
        try
        {
            await client.GetProfileAsync("abc123");
            throw new InvalidOperationException("A 200 response containing errors should throw.");
        }
        catch (NextDnsApiException exception)
        {
            Check(exception.Kind == NextDnsApiErrorKind.Rejected, "A 200 response with errors is treated as an API rejection.");
            Check(exception.Errors.Count == 1 && exception.Errors[0].Code == "invalid", "API error metadata is retained.");
            Check(!exception.Message.Contains(SyntheticApiKey, StringComparison.Ordinal), "API errors never expose the API key.");
            Check(exception.Message.Contains("/[redacted]", StringComparison.Ordinal),
                "The API error source remains useful in the message after key redaction.");
            Check(!exception.Errors.Any(error =>
                    (error.Code + error.Detail + error.Source).Contains(SyntheticApiKey, StringComparison.Ordinal)),
                "Error metadata redacts a key echoed in detail or source.");
        }
    }

    private static void TestDomainNormalizationAndImport()
    {
        Check(NextDnsDomain.Normalize(" *.BÜCHER.de ") == "xn--bcher-kva.de", "Unicode and wildcard input normalize to ASCII.");
        Check(NextDnsDomain.Normalize("https://EXAMPLE.com/path") == "example.com", "URL input normalizes to its host.");

        DomainImportResult result = NextDnsDomain.ParseImport(
            "example.com\nhttps://EXAMPLE.com/path\ninvalid_domain\n# comment\n");
        Check(result.Domains.SequenceEqual(["example.com"]), "Import preserves the first normalized occurrence.");
        Check(result.DuplicateLines.Count == 1 && result.DuplicateLines[0].LineNumber == 2,
            "Import reports duplicate lines.");
        Check(result.InvalidLines.Count == 1 && result.InvalidLines[0].LineNumber == 3,
            "Import reports invalid lines without discarding their location.");
    }

    private static void TestDpapiKeyStorage()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "nextdns-api-key-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string keyPath = Path.Combine(temporaryDirectory, "api-key.dpapi");
            var store = new ApiKeyStore(keyPath);
            store.Save(SyntheticApiKey);
            byte[] storedBytes = File.ReadAllBytes(keyPath);
            Check(!Encoding.UTF8.GetString(storedBytes).Contains(SyntheticApiKey, StringComparison.Ordinal),
                "The persisted key is not plaintext.");
            CryptographicOperations.ZeroMemory(storedBytes);
            Check(store.HasApiKey && store.Load() == SyntheticApiKey, "DPAPI storage round-trips for the current Windows user.");
            store.Delete();
            Check(!store.HasApiKey && store.Load() is null, "Deleting the stored key clears it.");
        }
        finally
        {
            string resolvedDirectory = Path.GetFullPath(temporaryDirectory);
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolvedDirectory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean a path outside the system temporary directory.");
            Directory.Delete(resolvedDirectory, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage EventStreamResponse(string body) => EventStreamResponse(
        new MemoryStream(Encoding.UTF8.GetBytes(body), writable: false));

    private static HttpResponseMessage EventStreamResponse(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream") { CharSet = "utf-8" };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + description);
        _assertions++;
    }

    private sealed class ScriptedHandler(Func<RequestSnapshot, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _callCount;
        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is missing.");
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "api.nextdns.io")
                throw new InvalidOperationException("The client attempted to send a request outside the fixed HTTPS API origin.");
            if (!request.Headers.TryGetValues("X-Api-Key", out IEnumerable<string>? values))
                throw new InvalidOperationException("The X-Api-Key header was missing.");

            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var snapshot = new RequestSnapshot(uri, values.Single(), request.Method, body);
            snapshot = snapshot with
            {
                AcceptsEventStream = request.Headers.Accept.Any(value =>
                    string.Equals(value.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            };
            Requests.Add(snapshot);
            return respond(snapshot, ++_callCount);
        }
    }

    private sealed class CancellableHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("A canceled synthetic request should not return a response.");
        }
    }

    private sealed record RequestSnapshot(Uri Uri, string ApiKey, HttpMethod Method, string? Body,
        bool AcceptsEventStream = false);

    private sealed class BlockingAfterFirstChunkStream(byte[] firstChunk) : Stream
    {
        private bool _sentChunk;
        public bool WasDisposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentChunk)
            {
                _sentChunk = true;
                int count = Math.Min(buffer.Length, firstChunk.Length);
                firstChunk.AsMemory(0, count).CopyTo(buffer);
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
