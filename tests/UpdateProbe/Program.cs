using System.Net;
using System.Text.Json;
using NextDNSModernWPF;

internal static class Program
{
    private const string LatestReleaseEndpoint =
        "https://api.github.com/repos/agustinbracamonte/NextDNS-Manager-WPF/releases/latest";
    private const string LatestReleasePage =
        "https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/tag/v1.10.0";
    private static int _assertions;

    private static async Task<int> Main()
    {
        try
        {
            await TestVersionEligibilityAsync();
            await TestUntrustedReleaseUrlsAsync();
            await TestRedirectRejectedAsync();
            await TestMalformedAndOversizedBodiesRejectedAsync();
            await TestDraftAndPrereleaseIgnoredAsync();
            await TestInstallerAssetValidationAsync();
            await TestRequestUsesFixedEndpointAndHeadersAsync();

            Console.WriteLine($"PASS: {_assertions} update-check assertions.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static async Task TestVersionEligibilityAsync()
    {
        Check(await CheckReleaseAsync(tag: "v1.0") is null,
            "Legacy two-part v1.0 is not an eligible update tag.");
        Check(await CheckReleaseAsync(tag: "v1.3.0") is null,
            "A release equal to the installed 1.3.0 version is not an update.");
        Check(await CheckReleaseAsync(tag: "v1.2.0") is null,
            "A release older than the installed 1.3.0 version is not an update.");

        UpdateInfo? newer = await CheckReleaseAsync(tag: "v1.10.0");
        Check(newer is not null && newer.Version == "1.10.0",
            "Numeric SemVer comparison treats 1.10.0 as newer than 1.3.0.");
    }

    private static async Task TestUntrustedReleaseUrlsAsync()
    {
        string[] urls =
        [
            "https://evil.example/agustinbracamonte/NextDNS-Manager-WPF/releases/tag/v1.10.0",
            "https://github.com.evil.example/agustinbracamonte/NextDNS-Manager-WPF/releases/tag/v1.10.0",
            "http://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/tag/v1.10.0",
            "https://user@github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/tag/v1.10.0"
        ];

        foreach (string url in urls)
        {
            await ExpectExceptionAsync<InvalidDataException>(
                () => CheckReleaseAsync(tag: "v1.10.0", htmlUrl: url),
                $"Reject untrusted release URL: {url}");
        }
    }

    private static async Task TestRedirectRejectedAsync()
    {
        using var checker = new UpdateChecker(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evil.example/payload") }
        }));

        await ExpectExceptionAsync<HttpRequestException>(
            () => checker.CheckAsync(),
            "Reject an HTTP redirect from the fixed GitHub API endpoint.");
    }

    private static async Task TestMalformedAndOversizedBodiesRejectedAsync()
    {
        await ExpectExceptionAsync<InvalidDataException>(
            () => CheckRawResponseAsync("{ invalid json"),
            "Reject malformed JSON.");

        string oversized = new('x', 128 * 1024 + 1);
        await ExpectExceptionAsync<InvalidDataException>(
            () => CheckRawResponseAsync(oversized),
            "Reject a response larger than 128 KiB.");
    }

    private static async Task TestDraftAndPrereleaseIgnoredAsync()
    {
        Check(await CheckReleaseAsync(tag: "v1.10.0", draft: true) is null,
            "Ignore draft releases.");
        Check(await CheckReleaseAsync(tag: "v1.10.0", prerelease: true) is null,
            "Ignore prerelease releases.");
    }

    private static async Task TestInstallerAssetValidationAsync()
    {
        Check(await CheckReleaseAsync(tag: "v1.10.0", assets: []) is null,
            "Do not offer an update when the expected installer asset is missing.");
        Check(await CheckReleaseAsync(tag: "v1.10.0", assets: [Asset("NextDNS-Manager-1.10.0-Setup-x64.zip")]) is null,
            "Reject an asset with a different name.");
        Check(await CheckReleaseAsync(tag: "v1.10.0", assets: [Asset("NextDNS-Manager-1.10.0-Setup-x64.exe", state: "new")]) is null,
            "Reject an installer asset that is not uploaded.");
        Check(await CheckReleaseAsync(tag: "v1.10.0", assets: [Asset("NextDNS-Manager-1.10.0-Setup-x64.exe", size: 0)]) is null,
            "Reject an empty installer asset.");
    }

    private static async Task TestRequestUsesFixedEndpointAndHeadersAsync()
    {
        FakeHttpMessageHandler? handler = null;
        handler = new FakeHttpMessageHandler(_ => JsonResponse(ReleaseJson("v1.10.0")));
        using var checker = new UpdateChecker(handler);

        UpdateInfo? update = await checker.CheckAsync();

        Check(update?.ReleaseUrl == new Uri(LatestReleasePage),
            "Return the trusted GitHub release page for a newer version.");
        Check(handler.RequestUri == new Uri(LatestReleaseEndpoint),
            "Request the fixed absolute GitHub latest-release API endpoint.");
        Check(handler.Method == HttpMethod.Get,
            "Use GET for the public release metadata check.");
        Check(handler.Accept.Contains("application/vnd.github+json", StringComparer.OrdinalIgnoreCase),
            "Send GitHub's JSON Accept header.");
        Check(handler.UserAgent.Any(value => value.StartsWith("NextDNS-Manager-WPF/", StringComparison.Ordinal)),
            "Send the application User-Agent header.");
    }

    private static async Task<UpdateInfo?> CheckReleaseAsync(
        string tag,
        string? htmlUrl = null,
        bool draft = false,
        bool prerelease = false,
        IReadOnlyList<object>? assets = null)
    {
        string body = ReleaseJson(tag, htmlUrl, draft, prerelease, assets);
        return await CheckRawResponseAsync(body);
    }

    private static async Task<UpdateInfo?> CheckRawResponseAsync(string body)
    {
        using var checker = new UpdateChecker(new FakeHttpMessageHandler(_ => JsonResponse(body)));
        return await checker.CheckAsync();
    }

    private static string ReleaseJson(
        string tag,
        string? htmlUrl = null,
        bool draft = false,
        bool prerelease = false,
        IReadOnlyList<object>? assets = null)
    {
        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            html_url = htmlUrl ?? $"https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/tag/{tag}",
            assets = assets ?? [Asset($"NextDNS-Manager-{tag.TrimStart('v')}-Setup-x64.exe")]
        });
    }

    private static object Asset(string name, string state = "uploaded", int size = 1) => new
    {
        name,
        state,
        size
    };

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body)
    };

    private static async Task ExpectExceptionAsync<TException>(Func<Task> action, string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            Check(true, description);
            return;
        }

        Check(false, description);
    }

    private static void Check(bool condition, string description)
    {
        _assertions++;
        if (!condition)
            throw new InvalidOperationException(description);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        internal FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        internal Uri? RequestUri { get; private set; }
        internal HttpMethod? Method { get; private set; }
        internal List<string> Accept { get; } = [];
        internal List<string> UserAgent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            Accept.AddRange(request.Headers.Accept.Select(header => header.MediaType ?? ""));
            UserAgent.AddRange(request.Headers.UserAgent.Select(header => header.ToString()));
            return Task.FromResult(_respond(request));
        }
    }
}
