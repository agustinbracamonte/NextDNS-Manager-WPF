using System.Net;
using System.Security.Cryptography;
using System.Text;
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
            await TestUntrustedAssetUrlsAsync();
            TestChecksumManifestValidation();
            TestDownloadRedirectValidation();
            await TestDownloaderFollowsOnlyAllowedRedirectsAsync();
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
        await ExpectExceptionAsync<InvalidDataException>(
            () => CheckReleaseAsync(tag: "v1.10.0", assets: [Asset("NextDNS-Manager-1.10.0-Setup-x64.exe", state: "new", tag: "v1.10.0"), Asset("SHA256SUMS.txt", tag: "v1.10.0")]),
            "Reject an installer asset that is not uploaded.");
        await ExpectExceptionAsync<InvalidDataException>(
            () => CheckReleaseAsync(tag: "v1.10.0", assets: [Asset("NextDNS-Manager-1.10.0-Setup-x64.exe", size: 0, tag: "v1.10.0"), Asset("SHA256SUMS.txt", tag: "v1.10.0")]),
            "Reject an empty installer asset.");
        await ExpectExceptionAsync<InvalidDataException>(
            () => CheckReleaseAsync(tag: "v1.10.0", assets: [Asset("NextDNS-Manager-1.10.0-Setup-x64.exe", size: 128 * 1024 * 1024 + 1, tag: "v1.10.0"), Asset("SHA256SUMS.txt", tag: "v1.10.0")]),
            "Reject an installer above the 128 MiB limit.");
        Check(await CheckReleaseAsync(tag: "v1.10.0", assets: [Asset("NextDNS-Manager-1.10.0-Setup-x64.exe", tag: "v1.10.0")]) is null,
            "Do not offer an update when SHA256SUMS is missing.");
    }

    private static async Task TestUntrustedAssetUrlsAsync()
    {
        object[] badAssets =
        [
            Asset("NextDNS-Manager-1.10.0-Setup-x64.exe", tag: "v1.10.0", downloadUrl: "http://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/download/v1.10.0/NextDNS-Manager-1.10.0-Setup-x64.exe"),
            Asset("NextDNS-Manager-1.10.0-Setup-x64.exe", tag: "v1.10.0", downloadUrl: "https://github.com.evil.example/agustinbracamonte/NextDNS-Manager-WPF/releases/download/v1.10.0/NextDNS-Manager-1.10.0-Setup-x64.exe"),
            Asset("NextDNS-Manager-1.10.0-Setup-x64.exe", tag: "v1.10.0", downloadUrl: "https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/download/v1.9.0/NextDNS-Manager-1.10.0-Setup-x64.exe")
        ];

        foreach (object badAsset in badAssets)
        {
            await ExpectExceptionAsync<InvalidDataException>(
                () => CheckReleaseAsync(tag: "v1.10.0", assets: [badAsset, Asset("SHA256SUMS.txt", tag: "v1.10.0")]),
                "Reject a non-HTTPS, non-GitHub or mismatched release asset URL.");
        }
    }

    private static void TestChecksumManifestValidation()
    {
        const string expectedName = "NextDNS-Manager-1.10.0-Setup-x64.exe";
        byte[] expectedHash = SHA256.HashData(Encoding.ASCII.GetBytes("test installer bytes"));
        string hashText = Convert.ToHexString(expectedHash).ToLowerInvariant();
        byte[] manifest = Encoding.UTF8.GetBytes($"{hashText}  {expectedName}\r\n");
        Check(UpdateInstaller.ParseSha256Sums(manifest, expectedName).SequenceEqual(expectedHash),
            "Parse the single exact installer entry from SHA256SUMS.");

        ExpectException<InvalidDataException>(
            () => UpdateInstaller.ParseSha256Sums(Encoding.UTF8.GetBytes($"{hashText}  ..\\payload.exe"), expectedName),
            "Reject a checksum entry naming a different or path-like file.");
        ExpectException<InvalidDataException>(
            () => UpdateInstaller.ParseSha256Sums(Encoding.UTF8.GetBytes($"{hashText}  {expectedName}\n{hashText}  {expectedName}"), expectedName),
            "Reject extra checksum entries.");
        ExpectException<InvalidDataException>(
            () => UpdateInstaller.ParseSha256Sums(new byte[UpdateInstallerChecksumsMaxBytes + 1], expectedName),
            "Reject SHA256SUMS above 4 KiB.");
    }

    private static void TestDownloadRedirectValidation()
    {
        var original = new Uri("https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/download/v1.10.0/NextDNS-Manager-1.10.0-Setup-x64.exe");
        Check(UpdateInstaller.IsAllowedDownloadRedirect(original, new Uri("https://release-assets.githubusercontent.com/asset.exe?token=sample")),
            "Allow a redirect to the official GitHub release asset host over HTTPS.");
        Check(UpdateInstaller.IsAllowedDownloadRedirect(original, new Uri("https://objects.githubusercontent.com/asset.exe?token=sample")),
            "Allow a redirect to the official GitHub object host over HTTPS.");
        Check(!UpdateInstaller.IsAllowedDownloadRedirect(original, new Uri("http://release-assets.githubusercontent.com/asset.exe")),
            "Reject an HTTP redirect.");
        Check(!UpdateInstaller.IsAllowedDownloadRedirect(original, new Uri("https://release-assets.githubusercontent.com.evil.example/asset.exe")),
            "Reject a lookalike asset host.");
        Check(!UpdateInstaller.IsAllowedDownloadRedirect(original, new Uri("https://github.com/evil/payload.exe")),
            "Reject a redirect to a different GitHub path.");
    }

    private static async Task TestDownloaderFollowsOnlyAllowedRedirectsAsync()
    {
        byte[] payload = Encoding.ASCII.GetBytes("verified installer payload");
        var asset = new ReleaseAsset(
            "NextDNS-Manager-1.10.0-Setup-x64.exe",
            payload.Length,
            new Uri("https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/download/v1.10.0/NextDNS-Manager-1.10.0-Setup-x64.exe"));
        var handler = new QueueHttpMessageHandler(
            request =>
            {
                Check(request.RequestUri == asset.DownloadUrl,
                    "Start an installer download at its exact GitHub release URL.");
                return new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://release-assets.githubusercontent.com/asset.exe?token=test") }
                };
            },
            request =>
            {
                Check(request.RequestUri?.Host == "release-assets.githubusercontent.com",
                    "Follow a redirect only to the allowlisted GitHub asset host.");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            });

        using (var downloader = new UpdateInstaller(handler))
        {
            byte[] downloaded = await downloader.DownloadBoundedBytesAsync(asset, payload.Length, null, "test", CancellationToken.None);
            Check(downloaded.SequenceEqual(payload), "Stream the exact asset bytes after a trusted redirect.");
        }

        using var untrustedDownloader = new UpdateInstaller(new QueueHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evil.example/payload.exe") }
        }));
        await ExpectExceptionAsync<InvalidDataException>(
            () => untrustedDownloader.DownloadBoundedBytesAsync(asset, payload.Length, null, "test", CancellationToken.None),
            "Reject an untrusted redirect before requesting its destination.");

        using var oversizedDownloader = new UpdateInstaller(new QueueHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        await ExpectExceptionAsync<InvalidDataException>(
            () => oversizedDownloader.DownloadBoundedBytesAsync(asset, payload.Length - 1, null, "test", CancellationToken.None),
            "Reject a downloaded body above the expected asset length.");
    }

    private static async Task TestRequestUsesFixedEndpointAndHeadersAsync()
    {
        FakeHttpMessageHandler? handler = null;
        handler = new FakeHttpMessageHandler(_ => JsonResponse(ReleaseJson("v1.10.0")));
        using var checker = new UpdateChecker(handler);

        UpdateInfo? update = await checker.CheckAsync();

        Check(update?.ReleaseUrl == new Uri(LatestReleasePage),
            "Return the trusted GitHub release page for a newer version.");
        Check(update?.Installer.Name == "NextDNS-Manager-1.10.0-Setup-x64.exe" && update.Installer.DownloadUrl.AbsolutePath.EndsWith("/NextDNS-Manager-1.10.0-Setup-x64.exe", StringComparison.Ordinal),
            "Return the exact trusted installer release asset.");
        Check(update?.Checksums.Name == "SHA256SUMS.txt",
            "Require and return the release SHA256SUMS asset.");
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
            assets = assets ?? [
                Asset($"NextDNS-Manager-{tag.TrimStart('v')}-Setup-x64.exe", tag: tag),
                Asset("SHA256SUMS.txt", tag: tag)
            ]
        });
    }

    private const int UpdateInstallerChecksumsMaxBytes = 4 * 1024;

    private static object Asset(
        string name,
        string state = "uploaded",
        int size = 1,
        string tag = "v1.10.0",
        string? downloadUrl = null) => new
    {
        name,
        state,
        size,
        browser_download_url = downloadUrl ?? $"https://github.com/agustinbracamonte/NextDNS-Manager-WPF/releases/download/{tag}/{name}"
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

    private static void ExpectException<TException>(Action action, string description)
        where TException : Exception
    {
        try
        {
            action();
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

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        internal QueueHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
                throw new InvalidOperationException("Unexpected HTTP request.");
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }
}
