using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace NextDNSModernWPF;

internal sealed record ReleaseAsset(string Name, long Size, Uri DownloadUrl);
internal sealed record UpdateInfo(
    string Version,
    Uri ReleaseUrl,
    ReleaseAsset Installer,
    ReleaseAsset Checksums);

/// <summary>
/// Checks GitHub's official latest-release API. It validates exact release
/// asset identities; downloading and launching are handled by UpdateInstaller.
/// </summary>
internal sealed class UpdateChecker : IDisposable
{
    private const int MaximumResponseBytes = 128 * 1024;
    internal const long MaximumInstallerBytes = 128L * 1024 * 1024;
    internal const int MaximumChecksumsBytes = 4 * 1024;
    internal const string RepositoryOwner = "agustinbracamonte";
    internal const string RepositoryName = "NextDNS-Manager-WPF";
    private static readonly Uri LatestReleaseApiUri = new(
        "https://api.github.com/repos/agustinbracamonte/NextDNS-Manager-WPF/releases/latest",
        UriKind.Absolute);

    private static readonly HttpClient SharedHttpClient = CreateHttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    });

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    internal UpdateChecker()
    {
        _httpClient = SharedHttpClient;
    }

    /// <summary>Creates a checker with a caller-supplied handler, primarily for deterministic testing.</summary>
    internal UpdateChecker(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _httpClient = CreateHttpClient(handler);
        _ownsHttpClient = true;
    }

    internal async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NextDNS-Manager-WPF", "1.0"));

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        int statusCode = (int)response.StatusCode;
        if (statusCode >= 300 && statusCode < 400)
            throw new HttpRequestException("La API de GitHub devolvió una redirección no permitida.", null, response.StatusCode);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("No se pudo consultar la última versión publicada en GitHub.", null, response.StatusCode);

        byte[] json = await ReadBoundedResponseAsync(response, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = ParseJson(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("La respuesta de GitHub no tiene el formato esperado.");

        bool draft = ReadRequiredBoolean(root, "draft");
        bool prerelease = ReadRequiredBoolean(root, "prerelease");
        if (draft || prerelease)
            return null;

        string tag = ReadRequiredString(root, "tag_name");
        if (!SemanticVersion.TryParseReleaseTag(tag, out SemanticVersion releaseVersion))
            return null;

        string htmlUrl = ReadRequiredString(root, "html_url");
        if (!IsTrustedReleaseUrl(htmlUrl, tag, out Uri? releaseUrl))
            throw new InvalidDataException("La URL de la publicación de GitHub no es válida.");

        string version = releaseVersion.ToString();
        if (!TryReadReleaseAssets(root, tag, version, out ReleaseAsset? installer, out ReleaseAsset? checksums))
            return null;

        SemanticVersion currentVersion = GetCurrentVersion();
        if (releaseVersion.CompareTo(currentVersion) <= 0)
            return null;

        return new UpdateInfo(version, releaseUrl!, installer!, checksums!);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaximumResponseBytes)
            throw new InvalidDataException("La respuesta de GitHub supera el tamaño permitido.");

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: Math.Min(MaximumResponseBytes, 16 * 1024));
        byte[] chunk = new byte[8192];

        while (true)
        {
            int remaining = MaximumResponseBytes + 1 - checked((int)buffer.Length);
            int bytesRead = await stream.ReadAsync(
                chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                cancellationToken).ConfigureAwait(false);

            if (bytesRead == 0)
                return buffer.ToArray();

            if (buffer.Length + bytesRead > MaximumResponseBytes)
                throw new InvalidDataException("La respuesta de GitHub supera el tamaño permitido.");

            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }
    }

    private static JsonDocument ParseJson(byte[] json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("La respuesta de GitHub no contiene JSON válido.", exception);
        }
    }

    private static bool ReadRequiredBoolean(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False))
        {
            throw new InvalidDataException("La respuesta de GitHub no tiene el formato esperado.");
        }

        return value.GetBoolean();
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException("La respuesta de GitHub no tiene el formato esperado.");
        }

        return value.GetString()!;
    }

    private static bool IsTrustedReleaseUrl(string value, string tag, out Uri? releaseUrl)
    {
        releaseUrl = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? candidate) ||
            !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !candidate.IsDefaultPort ||
            candidate.UserInfo.Length != 0 ||
            candidate.Query.Length != 0 ||
            candidate.Fragment.Length != 0)
        {
            return false;
        }

        string expectedPath = $"/agustinbracamonte/NextDNS-Manager-WPF/releases/tag/{tag}";
        if (!string.Equals(candidate.AbsolutePath, expectedPath, StringComparison.Ordinal))
            return false;

        releaseUrl = candidate;
        return true;
    }

    private static bool TryReadReleaseAssets(
        JsonElement root,
        string tag,
        string version,
        out ReleaseAsset? installer,
        out ReleaseAsset? checksums)
    {
        installer = null;
        checksums = null;
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return false;

        string installerName = $"NextDNS-Manager-{version}-Setup-x64.exe";
        const string checksumsName = "SHA256SUMS.txt";
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object || !TryReadString(asset, "name", out string? name))
                continue;

            bool isInstaller = string.Equals(name, installerName, StringComparison.Ordinal);
            bool isChecksums = string.Equals(name, checksumsName, StringComparison.Ordinal);
            if (!isInstaller && !isChecksums)
                continue;

            long maximumSize = isInstaller ? MaximumInstallerBytes : MaximumChecksumsBytes;
            if (!TryReadString(asset, "state", out string? state) ||
                !string.Equals(state, "uploaded", StringComparison.Ordinal) ||
                !asset.TryGetProperty("size", out JsonElement sizeValue) ||
                sizeValue.ValueKind != JsonValueKind.Number ||
                !sizeValue.TryGetInt64(out long size) ||
                size <= 0 ||
                size > maximumSize ||
                !TryReadString(asset, "browser_download_url", out string? downloadUrl) ||
                !IsTrustedAssetUrl(downloadUrl!, tag, name!, out Uri? uri))
            {
                throw new InvalidDataException("La publicación contiene un instalador o manifiesto SHA-256 no válido.");
            }

            var releaseAsset = new ReleaseAsset(name!, size, uri!);
            if (isInstaller)
            {
                if (installer is not null)
                    throw new InvalidDataException("La publicación contiene más de un instalador con el nombre esperado.");
                installer = releaseAsset;
            }
            else
            {
                if (checksums is not null)
                    throw new InvalidDataException("La publicación contiene más de un manifiesto SHA-256.");
                checksums = releaseAsset;
            }
        }

        return installer is not null && checksums is not null;
    }

    private static bool IsTrustedAssetUrl(string value, string tag, string assetName, out Uri? assetUrl)
    {
        assetUrl = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? candidate) ||
            !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !candidate.IsDefaultPort ||
            candidate.UserInfo.Length != 0 ||
            candidate.Query.Length != 0 ||
            candidate.Fragment.Length != 0)
        {
            return false;
        }

        string expectedPath = $"/{RepositoryOwner}/{RepositoryName}/releases/download/{tag}/{assetName}";
        if (!string.Equals(candidate.AbsolutePath, expectedPath, StringComparison.Ordinal))
            return false;

        assetUrl = candidate;
        return true;
    }

    private static bool TryReadString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString();
        return value is not null;
    }

    private static SemanticVersion GetCurrentVersion()
    {
        Assembly assembly = typeof(UpdateChecker).Assembly;
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int metadataStart = informationalVersion.IndexOf('+');
            string versionText = metadataStart >= 0
                ? informationalVersion[..metadataStart]
                : informationalVersion;

            if (Version.TryParse(versionText, out Version? parsedInformationalVersion))
                return SemanticVersion.FromAssemblyVersion(parsedInformationalVersion);
        }

        Version? assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is not null)
            return SemanticVersion.FromAssemblyVersion(assemblyVersion);

        throw new InvalidOperationException("No se pudo determinar la versión instalada de NextDNS Manager.");
    }

    private readonly record struct SemanticVersion(string Major, string Minor, string Patch) : IComparable<SemanticVersion>
    {
        internal static bool TryParseReleaseTag(string tag, out SemanticVersion version)
        {
            version = default;
            if (tag.Length < 6 || tag[0] != 'v')
                return false;

            ReadOnlySpan<char> value = tag.AsSpan(1);
            int firstSeparator = value.IndexOf('.');
            if (firstSeparator <= 0)
                return false;

            int secondSeparatorRelative = value[(firstSeparator + 1)..].IndexOf('.');
            if (secondSeparatorRelative <= 0)
                return false;

            int secondSeparator = firstSeparator + 1 + secondSeparatorRelative;
            ReadOnlySpan<char> major = value[..firstSeparator];
            ReadOnlySpan<char> minor = value[(firstSeparator + 1)..secondSeparator];
            ReadOnlySpan<char> patch = value[(secondSeparator + 1)..];

            if (!IsCanonicalNumber(major) || !IsCanonicalNumber(minor) || !IsCanonicalNumber(patch))
                return false;

            version = new SemanticVersion(major.ToString(), minor.ToString(), patch.ToString());
            return true;
        }

        internal static SemanticVersion FromAssemblyVersion(Version version)
        {
            string major = version.Major.ToString(CultureInfo.InvariantCulture);
            string minor = version.Minor.ToString(CultureInfo.InvariantCulture);
            int patch = version.Build < 0 ? 0 : version.Build;
            return new SemanticVersion(major, minor, patch.ToString(CultureInfo.InvariantCulture));
        }

        public int CompareTo(SemanticVersion other)
        {
            int result = CompareNumber(Major, other.Major);
            if (result != 0) return result;

            result = CompareNumber(Minor, other.Minor);
            return result != 0 ? result : CompareNumber(Patch, other.Patch);
        }

        public override string ToString() => $"{Major}.{Minor}.{Patch}";

        private static bool IsCanonicalNumber(ReadOnlySpan<char> value)
        {
            if (value.IsEmpty || (value.Length > 1 && value[0] == '0'))
                return false;

            foreach (char character in value)
            {
                if (character is < '0' or > '9')
                    return false;
            }

            return true;
        }

        private static int CompareNumber(string left, string right)
        {
            if (left.Length != right.Length)
                return left.Length.CompareTo(right.Length);

            return string.Compare(left, right, StringComparison.Ordinal);
        }
    }
}
