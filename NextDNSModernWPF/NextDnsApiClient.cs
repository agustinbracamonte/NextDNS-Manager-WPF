using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NextDNSModernWPF;

public enum NextDnsListType
{
    Allowlist,
    Denylist
}

public enum NextDnsProfileSection
{
    Security,
    Privacy,
    ParentalControl,
    Settings
}

public enum NextDnsSetting
{
    Logs,
    BlockPage,
    Performance,
    Web3,
    Csam
}

public enum NextDnsLogStatus
{
    Default,
    Error,
    Blocked,
    Allowed
}

public enum NextDnsLogSort
{
    Ascending,
    Descending
}

public enum NextDnsApiErrorKind
{
    Rejected,
    Unauthorized,
    Forbidden,
    NotFound,
    RateLimited,
    ServiceUnavailable,
    Network,
    Timeout,
    UnexpectedRedirect,
    UnexpectedResponse
}

public sealed record NextDnsApiError(string Code, string? Detail, string? Source);

public sealed class NextDnsApiException : Exception
{
    public NextDnsApiErrorKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }
    public IReadOnlyList<NextDnsApiError> Errors { get; }

    internal NextDnsApiException(
        NextDnsApiErrorKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        IReadOnlyList<NextDnsApiError>? errors = null)
        : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
        Errors = errors ?? Array.Empty<NextDnsApiError>();
    }
}

public sealed class NextDnsProfileRecord
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Other profile fields returned by NextDNS, retained without narrowing the API schema.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class NextDnsListEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("active")]
    public bool Active { get; set; } = true;
}

public sealed class NextDnsLogDevice
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("localIp")]
    public string? LocalIp { get; set; }
}

public sealed class NextDnsAnalyticsDevice
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("localIp")]
    public string? LocalIp { get; set; }

    [JsonPropertyName("queries")]
    public long? Queries { get; set; }
}

public sealed class NextDnsLogReason
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class NextDnsLogEntry
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; set; }

    [JsonPropertyName("domain")]
    public string? Domain { get; set; }

    [JsonPropertyName("root")]
    public string? Root { get; set; }

    [JsonPropertyName("tracker")]
    public string? Tracker { get; set; }

    [JsonPropertyName("encrypted")]
    public bool? Encrypted { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("clientIp")]
    public string? ClientIp { get; set; }

    [JsonPropertyName("client")]
    public string? Client { get; set; }

    [JsonPropertyName("device")]
    public NextDnsLogDevice? Device { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("reasons")]
    public List<NextDnsLogReason>? Reasons { get; set; }
}

public sealed record NextDnsPage<T>(IReadOnlyList<T> Items, string? NextCursor, string? StreamId = null);

public sealed record NextDnsLogsQuery
{
    public string? From { get; init; }
    public string? To { get; init; }
    public NextDnsLogSort Sort { get; init; } = NextDnsLogSort.Descending;
    public int Limit { get; init; } = 100;
    public string? Cursor { get; init; }
    public string? Device { get; init; }
    public NextDnsLogStatus? Status { get; init; }
    public string? Search { get; init; }
    public bool Raw { get; init; }
}

public sealed record NextDnsLogStreamQuery
{
    /// <summary>Last event ID to resume from, as returned by a previous stream event.</summary>
    public string? Id { get; init; }
    public string? Device { get; init; }
    public NextDnsLogStatus? Status { get; init; }
    public string? Search { get; init; }
    public bool Raw { get; init; }
}

public sealed record NextDnsLogStreamEvent(string? Id, string EventType, NextDnsLogEntry Entry);

public sealed record NextDnsLogHistory(
    IReadOnlyList<NextDnsLogEntry> Items,
    string? NextCursor,
    string? StreamId,
    bool IsComplete);

public sealed record NextDnsDomainImportIssue(int LineNumber, string Input, string Reason);

public sealed class DomainImportResult
{
    public IReadOnlyList<string> Domains { get; }
    public IReadOnlyList<NextDnsDomainImportIssue> InvalidLines { get; }
    public IReadOnlyList<NextDnsDomainImportIssue> DuplicateLines { get; }

    internal DomainImportResult(
        IReadOnlyList<string> domains,
        IReadOnlyList<NextDnsDomainImportIssue> invalidLines,
        IReadOnlyList<NextDnsDomainImportIssue> duplicateLines)
    {
        Domains = domains;
        InvalidLines = invalidLines;
        DuplicateLines = duplicateLines;
    }
}

public sealed record NextDnsBulkListIssue(string Input, string Message, string? ErrorCode = null);

public sealed record NextDnsBulkListResult(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Activated,
    IReadOnlyList<string> SkippedExisting,
    IReadOnlyList<NextDnsBulkListIssue> Invalid,
    IReadOnlyList<NextDnsBulkListIssue> Failed,
    int NotAttempted);

/// <summary>Normalizes domain inputs to the lowercase ASCII format accepted by NextDNS lists.</summary>
public static class NextDnsDomain
{
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("La entrada está vacía.", nameof(input));

        string candidate = input.Trim();
        if (candidate.Length > 2048 || candidate.Any(char.IsControl))
            throw new ArgumentException("La entrada es demasiado larga o contiene caracteres de control.", nameof(input));

        if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("La URL no es válida.", nameof(input));
            candidate = uri.Host;
        }
        else if (candidate.Contains('/') || candidate.Contains('\\') || candidate.Contains('?') || candidate.Contains('#'))
        {
            throw new ArgumentException("Usa un dominio o una URL HTTP/HTTPS.", nameof(input));
        }

        candidate = candidate.Trim();
        if (candidate.StartsWith("*.", StringComparison.Ordinal)) candidate = candidate[2..];
        else if (candidate.StartsWith('.')) candidate = candidate[1..];

        if (candidate.EndsWith("..", StringComparison.Ordinal))
            throw new ArgumentException("El dominio tiene un punto final inválido.", nameof(input));
        if (candidate.EndsWith('.')) candidate = candidate[..^1];
        if (candidate.Length == 0)
            throw new ArgumentException("El dominio está vacío.", nameof(input));

        string ascii;
        try { ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(candidate).ToLowerInvariant(); }
        catch (ArgumentException)
        {
            throw new ArgumentException("El dominio no tiene un formato válido.", nameof(input));
        }

        if (ascii.Length > 253 || IPAddress.TryParse(ascii, out _))
            throw new ArgumentException("Se requiere un nombre de dominio, no una dirección IP.", nameof(input));

        string[] labels = ascii.Split('.');
        foreach (string label in labels)
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                !Regex.IsMatch(label, @"\A[a-z0-9-]+\z", RegexOptions.CultureInvariant))
                throw new ArgumentException("El dominio contiene una etiqueta inválida.", nameof(input));
        }

        return ascii;
    }

    /// <summary>Reads one domain or URL per line, ignores blanks and full-line comments, and reports invalid or duplicate lines.</summary>
    public static DomainImportResult ParseImport(string? text)
    {
        var domains = new List<string>();
        var invalid = new List<NextDnsDomainImportIssue>();
        var duplicates = new List<NextDnsDomainImportIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n');

        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            try
            {
                string domain = Normalize(line);
                if (!seen.Add(domain))
                    duplicates.Add(new NextDnsDomainImportIssue(index + 1, line, "Ya aparece en la importación."));
                else
                    domains.Add(domain);
            }
            catch (ArgumentException exception)
            {
                invalid.Add(new NextDnsDomainImportIssue(index + 1, line, exception.Message));
            }
        }

        return new DomainImportResult(domains, invalid, duplicates);
    }
}

/// <summary>
/// Small typed client for the NextDNS profile API. API keys are sent only in
/// the X-Api-Key header over HTTPS and are never included in URLs or exceptions.
/// </summary>
public sealed class NextDnsApiClient : IDisposable
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private const int MaximumPaginationPages = 1000;
    private const int MaximumListEntries = 5000;
    private const int AnalyticsDevicesPageLimit = 500;
    private const int MaximumAnalyticsDeviceEntries = 10000;
    private const int MaximumAnalyticsDevicePages = 100;
    private const int MaximumSseLineCharacters = 64 * 1024;
    private const int MaximumSseEventCharacters = 1024 * 1024;
    private static readonly Uri ApiRoot = new("https://api.nextdns.io/", UriKind.Absolute);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    /// <param name="handler">Optional transport handler. The client owns and disposes it.</param>
    public NextDnsApiClient(string apiKey, HttpMessageHandler? handler = null)
    {
        _apiKey = ValidateApiKey(apiKey);
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = ApiRoot,
            Timeout = TimeSpan.FromSeconds(60)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NextDNS-Manager-WPF", "1.0"));
    }

    public async Task<IReadOnlyList<NextDnsProfileRecord>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<NextDnsProfileRecord>();
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        for (int pageIndex = 0; pageIndex < MaximumPaginationPages; pageIndex++)
        {
            // The profile collection endpoint does not document a `limit` query argument.
            // Start with the bare collection URL and only send a cursor if the server provides one.
            NextDnsPage<NextDnsProfileRecord> page = await GetPageAsync<NextDnsProfileRecord>("profiles", cursor, cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Items);
            cursor = page.NextCursor;
            if (string.IsNullOrEmpty(cursor)) return result;
            if (!seenCursors.Add(cursor)) throw UnexpectedResponse("La paginación de perfiles devolvió un cursor repetido.");
        }
        throw UnexpectedResponse("La respuesta de NextDNS contiene demasiadas páginas.");
    }

    public Task<NextDnsProfileRecord> GetProfileAsync(string profileId, CancellationToken cancellationToken = default) =>
        SendDataAsync<NextDnsProfileRecord>(HttpMethod.Get, ProfilePath(profileId), null, cancellationToken);

    public Task<NextDnsProfileRecord> CreateProfileAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Indica un nombre de perfil.", nameof(name));
        return SendDataAsync<NextDnsProfileRecord>(HttpMethod.Post, "profiles", new { name = name.Trim() }, cancellationToken);
    }

    public Task<NextDnsProfileRecord> CreateProfileAsync<T>(T profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return SendDataAsync<NextDnsProfileRecord>(HttpMethod.Post, "profiles", profile, cancellationToken);
    }

    public Task<NextDnsProfileRecord> UpdateProfileAsync<T>(string profileId, T patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return SendDataAsync<NextDnsProfileRecord>(HttpMethod.Patch, ProfilePath(profileId), patch, cancellationToken);
    }

    public Task DeleteProfileAsync(string profileId, CancellationToken cancellationToken = default) =>
        SendVoidAsync(HttpMethod.Delete, ProfilePath(profileId), null, cancellationToken);

    public async Task<IReadOnlyList<NextDnsListEntry>> GetListAsync(
        string profileId,
        NextDnsListType listType,
        CancellationToken cancellationToken = default)
    {
        string path = ProfileListPath(profileId, listType);
        var result = new List<NextDnsListEntry>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (int pageIndex = 0; pageIndex < MaximumPaginationPages; pageIndex++)
        {
            // NextDNS rejects `limit` on the allowlist and denylist collection endpoints
            // (`extraneous`, source.parameter=limit). Cursor pagination remains supported.
            NextDnsPage<NextDnsListEntry> page = await GetPageAsync<NextDnsListEntry>(path, cursor, cancellationToken).ConfigureAwait(false);
            result.AddRange(page.Items);
            if (result.Count > MaximumListEntries)
                throw UnexpectedResponse("La lista recibida supera el límite documentado de NextDNS.");
            cursor = page.NextCursor;
            if (string.IsNullOrEmpty(cursor)) return result;
            if (!seenCursors.Add(cursor)) throw UnexpectedResponse("La paginación de la lista devolvió un cursor repetido.");
        }
        throw UnexpectedResponse("La respuesta de NextDNS contiene demasiadas páginas.");
    }

    public async Task AddListEntryAsync(
        string profileId,
        NextDnsListType listType,
        string domain,
        bool active = true,
        CancellationToken cancellationToken = default)
    {
        string normalized = NextDnsDomain.Normalize(domain);
        await SendVoidAsync(HttpMethod.Post, ProfileListPath(profileId, listType),
            new NextDnsListEntry { Id = normalized, Active = active }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a batch without replacing the existing list. Existing entries are
    /// preserved by default; set <paramref name="activateExisting"/> only when
    /// reactivating inactive entries is intended. Invalid and duplicate inputs
    /// are returned separately instead of failing the whole import. Each write is
    /// a separate POST/PATCH, so unrelated concurrent edits are not erased.
    /// </summary>
    public async Task<NextDnsBulkListResult> AddListEntriesAsync(
        string profileId,
        NextDnsListType listType,
        IEnumerable<string> domains,
        CancellationToken cancellationToken = default,
        bool activateExisting = false)
    {
        ArgumentNullException.ThrowIfNull(domains);
        var normalized = new List<string>();
        var invalid = new List<NextDnsBulkListIssue>();
        var skipped = new List<string>();
        var seenInput = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? input in domains)
        {
            try
            {
                string domain = NextDnsDomain.Normalize(input ?? string.Empty);
                if (!seenInput.Add(domain))
                {
                    skipped.Add(domain);
                    continue;
                }
                normalized.Add(domain);
            }
            catch (ArgumentException exception)
            {
                invalid.Add(new NextDnsBulkListIssue(input ?? string.Empty, exception.Message));
            }
        }

        IReadOnlyList<NextDnsListEntry> existingEntries = await GetListAsync(profileId, listType, cancellationToken).ConfigureAwait(false);
        var existing = existingEntries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        foreach (string domain in normalized)
        {
            if (existing.TryGetValue(domain, out NextDnsListEntry? entry) &&
                (entry.Active || !activateExisting))
                skipped.Add(domain);
        }

        int missingCount = normalized.Count(domain => !existing.ContainsKey(domain));
        int writesRequired = normalized.Count(domain =>
            !existing.TryGetValue(domain, out NextDnsListEntry? entry) || (activateExisting && !entry.Active));
        if (existingEntries.Count + missingCount > MaximumListEntries)
        {
            invalid.Add(new NextDnsBulkListIssue(string.Empty,
                $"La importación excedería el límite de {MaximumListEntries} entradas de NextDNS."));
            return new NextDnsBulkListResult(Array.Empty<string>(), Array.Empty<string>(), skipped, invalid,
                Array.Empty<NextDnsBulkListIssue>(), writesRequired);
        }

        var added = new List<string>();
        var activated = new List<string>();
        var failed = new List<NextDnsBulkListIssue>();
        int notAttempted = 0;
        bool stopAfterFailure = false;
        foreach (string domain in normalized)
        {
            if (existing.TryGetValue(domain, out NextDnsListEntry? entry) &&
                (entry.Active || !activateExisting)) continue;
            if (stopAfterFailure)
            {
                notAttempted++;
                continue;
            }

            try
            {
                if (entry is not null)
                {
                    await UpdateListEntryAsync(profileId, listType, domain, active: true, cancellationToken).ConfigureAwait(false);
                    activated.Add(domain);
                }
                else
                {
                    await AddListEntryAsync(profileId, listType, domain, active: true, cancellationToken).ConfigureAwait(false);
                    added.Add(domain);
                }
            }
            catch (NextDnsApiException exception)
            {
                failed.Add(new NextDnsBulkListIssue(domain, exception.Message, exception.Errors.FirstOrDefault()?.Code));
                stopAfterFailure = exception.Kind is NextDnsApiErrorKind.Unauthorized or NextDnsApiErrorKind.Forbidden or
                    NextDnsApiErrorKind.RateLimited or NextDnsApiErrorKind.ServiceUnavailable or
                    NextDnsApiErrorKind.Network or NextDnsApiErrorKind.Timeout or
                    NextDnsApiErrorKind.UnexpectedRedirect or NextDnsApiErrorKind.UnexpectedResponse;
            }
        }

        return new NextDnsBulkListResult(added, activated, skipped, invalid, failed, notAttempted);
    }

    public async Task UpdateListEntryAsync(
        string profileId,
        NextDnsListType listType,
        string domain,
        bool active,
        CancellationToken cancellationToken = default)
    {
        string normalized = NextDnsDomain.Normalize(domain);
        string path = ProfileListPath(profileId, listType) + "/" + EscapeSegment(normalized);
        await SendVoidAsync(HttpMethod.Patch, path, new { active }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteListEntryAsync(
        string profileId,
        NextDnsListType listType,
        string domain,
        CancellationToken cancellationToken = default)
    {
        string normalized = NextDnsDomain.Normalize(domain);
        string path = ProfileListPath(profileId, listType) + "/" + EscapeSegment(normalized);
        await SendVoidAsync(HttpMethod.Delete, path, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the complete list. Use only when the caller intends to discard entries not supplied here.</summary>
    public async Task ReplaceListAsync(
        string profileId,
        NextDnsListType listType,
        IEnumerable<NextDnsListEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var normalized = new List<NextDnsListEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NextDnsListEntry entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            string domain = NextDnsDomain.Normalize(entry.Id);
            if (!seen.Add(domain)) throw new ArgumentException("La lista contiene dominios duplicados.", nameof(entries));
            normalized.Add(new NextDnsListEntry { Id = domain, Active = entry.Active });
            if (normalized.Count > MaximumListEntries)
                throw new ArgumentException($"La lista supera el límite de {MaximumListEntries} entradas.", nameof(entries));
        }

        await SendVoidAsync(HttpMethod.Put, ProfileListPath(profileId, listType), normalized, cancellationToken).ConfigureAwait(false);
    }

    public Task<T> GetSectionAsync<T>(string profileId, NextDnsProfileSection section, CancellationToken cancellationToken = default) =>
        SendDataAsync<T>(HttpMethod.Get, ProfileSectionPath(profileId, section), null, cancellationToken);

    public Task<T> PatchSectionAsync<T>(string profileId, NextDnsProfileSection section, object patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return SendDataAsync<T>(HttpMethod.Patch, ProfileSectionPath(profileId, section), patch, cancellationToken);
    }

    public Task<T> GetSettingAsync<T>(string profileId, NextDnsSetting setting, CancellationToken cancellationToken = default) =>
        SendDataAsync<T>(HttpMethod.Get, ProfileSettingPath(profileId, setting), null, cancellationToken);

    public Task<T> PatchSettingAsync<T>(string profileId, NextDnsSetting setting, object patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return SendDataAsync<T>(HttpMethod.Patch, ProfileSettingPath(profileId, setting), patch, cancellationToken);
    }

    /// <summary>
    /// Gets all devices represented by profile device analytics. Each request
    /// uses the documented 500 item limit and follows pagination cursors. The
    /// result is bounded to protect against malformed or unbounded responses.
    /// </summary>
    public async Task<IReadOnlyList<NextDnsAnalyticsDevice>> GetAnalyticsDevicesAsync(
        string profileId,
        string? from = null,
        string? to = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAnalyticsDevicesFilters(from, to);
        var result = new List<NextDnsAnalyticsDevice>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;

        for (int pageIndex = 0; pageIndex < MaximumAnalyticsDevicePages; pageIndex++)
        {
            string path = ProfilePath(profileId) + "/analytics/devices" + BuildAnalyticsDevicesQuery(from, to, cursor);
            NextDnsPage<NextDnsAnalyticsDevice> page = await SendPageAsync<NextDnsAnalyticsDevice>(path, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count > MaximumAnalyticsDeviceEntries - result.Count)
                throw UnexpectedResponse("La respuesta analytics/devices supera el máximo seguro de dispositivos.");
            result.AddRange(page.Items);

            cursor = page.NextCursor;
            if (string.IsNullOrEmpty(cursor)) return result;
            if (!seenCursors.Add(cursor))
                throw UnexpectedResponse("La paginación de analytics/devices devolvió un cursor repetido.");
        }

        throw UnexpectedResponse("La respuesta analytics/devices contiene demasiadas páginas.");
    }

    public Task<NextDnsPage<NextDnsLogEntry>> GetLogsAsync(
        string profileId,
        NextDnsLogsQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new NextDnsLogsQuery();
        ValidateLogsQuery(query);
        string path = ProfilePath(profileId) + "/logs" + BuildLogsQuery(query);
        return SendPageAsync<NextDnsLogEntry>(path, cancellationToken);
    }

    /// <summary>
    /// Streams new log events from NextDNS using server-sent events. The
    /// caller's cancellation token closes the HTTP response and underlying
    /// stream; pass the last returned event ID in a later query to resume.
    /// </summary>
    public async IAsyncEnumerable<NextDnsLogStreamEvent> StreamLogsAsync(
        string profileId,
        NextDnsLogStreamQuery? query = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        query ??= new NextDnsLogStreamQuery();
        ValidateLogsStreamQuery(query);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string path = ProfilePath(profileId) + "/logs/stream" + BuildLogsStreamQuery(query);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            throw new NextDnsApiException(NextDnsApiErrorKind.Timeout, "La conexión de logs en vivo a NextDNS tardó demasiado en iniciar.");
        }
        catch (HttpRequestException)
        {
            throw new NextDnsApiException(NextDnsApiErrorKind.Network, "No se pudo conectar con el stream de logs de NextDNS.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode ||
                !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                await ThrowStreamResponseErrorAsync(response, cancellationToken, _apiKey).ConfigureAwait(false);

            Stream source;
            try { source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { throw new NextDnsApiException(NextDnsApiErrorKind.Network, "No se pudo abrir el stream de logs de NextDNS."); }

            await using (source.ConfigureAwait(false))
            using (var lineReader = new SseLineReader(source))
            {
                var dataLines = new List<string>();
                int eventDataCharacters = 0;
                bool hasData = false;
                string? lastEventId = query.Id;
                string? eventName = null;

                while (true)
                {
                    string? line;
                    try { line = await lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (TaskCanceledException) { throw new NextDnsApiException(NextDnsApiErrorKind.Timeout, "La conexión de logs en vivo a NextDNS expiró."); }
                    catch (IOException) { throw new NextDnsApiException(NextDnsApiErrorKind.Network, "Se interrumpió el stream de logs de NextDNS."); }
                    catch (DecoderFallbackException) { throw UnexpectedResponse("El stream de logs de NextDNS contiene texto UTF-8 inválido."); }

                    if (line is null) break;
                    if (line.Length == 0)
                    {
                        NextDnsLogStreamEvent? completedEvent = ParseLogStreamEvent(lastEventId, eventName, dataLines, hasData);
                        dataLines.Clear();
                        eventDataCharacters = 0;
                        hasData = false;
                        eventName = null;
                        if (completedEvent is not null) yield return completedEvent;
                        continue;
                    }
                    if (line[0] == ':') continue;

                    int separatorIndex = line.IndexOf(':');
                    string field = separatorIndex >= 0 ? line[..separatorIndex] : line;
                    string value = separatorIndex >= 0 ? line[(separatorIndex + 1)..] : string.Empty;
                    if (value.StartsWith(' ')) value = value[1..];
                    switch (field)
                    {
                        case "data":
                            eventDataCharacters += value.Length + 1;
                            if (eventDataCharacters > MaximumSseEventCharacters)
                                throw UnexpectedResponse("Un evento del stream de logs de NextDNS supera el tamaño permitido.");
                            dataLines.Add(value);
                            hasData = true;
                            break;
                        case "id":
                            if (!value.Contains('\0')) lastEventId = value;
                            break;
                        case "event":
                            eventName = value;
                            break;
                        // SSE comments are keepalives. Unknown fields, including retry, are ignored.
                    }
                }

                NextDnsLogStreamEvent? finalEvent = ParseLogStreamEvent(lastEventId, eventName, dataLines, hasData);
                if (finalEvent is not null) yield return finalEvent;
            }
        }
    }

    /// <summary>Fetches logs up to a caller-selected bound. The returned cursor indicates whether more data remains.</summary>
    public async Task<NextDnsLogHistory> GetAllLogsAsync(
        string profileId,
        NextDnsLogsQuery? query = null,
        int maximumItems = 10000,
        CancellationToken cancellationToken = default)
    {
        if (maximumItems < 1) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        query ??= new NextDnsLogsQuery();
        ValidateLogsQuery(query);
        var result = new List<NextDnsLogEntry>(Math.Min(maximumItems, 10000));
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = query.Cursor;
        string? streamId = null;
        for (int pageIndex = 0; pageIndex < MaximumPaginationPages; pageIndex++)
        {
            NextDnsLogsQuery pageQuery = query with { Cursor = cursor, Limit = Math.Min(query.Limit, 1000) };
            NextDnsPage<NextDnsLogEntry> page = await GetLogsAsync(profileId, pageQuery, cancellationToken).ConfigureAwait(false);
            streamId ??= page.StreamId;
            int remaining = maximumItems - result.Count;
            result.AddRange(page.Items.Take(remaining));
            cursor = page.NextCursor;
            if (string.IsNullOrEmpty(cursor)) return new NextDnsLogHistory(result, null, streamId, true);
            if (result.Count >= maximumItems) return new NextDnsLogHistory(result, cursor, streamId, false);
            if (!seenCursors.Add(cursor)) throw UnexpectedResponse("La paginación de logs devolvió un cursor repetido.");
        }

        throw UnexpectedResponse("La respuesta de NextDNS contiene demasiadas páginas de logs.");
    }

    public Task ClearLogsAsync(string profileId, CancellationToken cancellationToken = default) =>
        SendVoidAsync(HttpMethod.Delete, ProfilePath(profileId) + "/logs", null, cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }

    internal static string ValidateApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("La API key de NextDNS no puede estar vacía.", nameof(apiKey));
        string normalized = apiKey.Trim();
        if (normalized.Length > 4096 || normalized.Any(character => character < 0x21 || character > 0x7e))
            throw new ArgumentException("La API key tiene caracteres no permitidos para un encabezado HTTP.", nameof(apiKey));
        return normalized;
    }

    private async Task<T> SendDataAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendJsonAsync(method, path, body, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data))
            throw UnexpectedResponse("La respuesta de NextDNS no contiene el campo de datos esperado.");
        try
        {
            T? value = data.Deserialize<T>(JsonOptions);
            if (value is null) throw UnexpectedResponse("NextDNS devolvió datos vacíos.");
            return value;
        }
        catch (JsonException)
        {
            throw UnexpectedResponse("La respuesta de NextDNS tiene un formato inesperado.");
        }
    }

    private async Task SendVoidAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using JsonDocument _ = await SendJsonAsync(method, path, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<NextDnsPage<T>> GetPageAsync<T>(string path, string? cursor, CancellationToken cancellationToken)
    {
        string query = string.IsNullOrEmpty(cursor)
            ? string.Empty
            : (path.Contains('?') ? "&" : "?") + "cursor=" + Uri.EscapeDataString(cursor);
        return await SendPageAsync<T>(path + query, cancellationToken).ConfigureAwait(false);
    }

    private async Task<NextDnsPage<T>> SendPageAsync<T>(string path, CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendJsonAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            throw UnexpectedResponse("La respuesta paginada de NextDNS no tiene el formato esperado.");

        List<T>? items;
        try { items = data.Deserialize<List<T>>(JsonOptions); }
        catch (JsonException) { throw UnexpectedResponse("La lista recibida de NextDNS tiene un formato inesperado."); }
        if (items is null) throw UnexpectedResponse("NextDNS devolvió una lista vacía con formato incorrecto.");

        string? cursor = null;
        string? streamId = null;
        if (root.TryGetProperty("meta", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (meta.TryGetProperty("pagination", out JsonElement pagination) && pagination.ValueKind == JsonValueKind.Object &&
                pagination.TryGetProperty("cursor", out JsonElement cursorElement) && cursorElement.ValueKind == JsonValueKind.String)
                cursor = cursorElement.GetString();
            if (meta.TryGetProperty("stream", out JsonElement stream) && stream.ValueKind == JsonValueKind.Object &&
                stream.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String)
                streamId = idElement.GetString();
        }
        return new NextDnsPage<T>(items, cursor, streamId);
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        byte[]? responseBytes = null;
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            responseBytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            JsonDocument document;
            if (responseBytes.Length == 0)
            {
                if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, Array.Empty<NextDnsApiError>(), _apiKey);
                document = JsonDocument.Parse("{}");
            }
            else
            {
                try { document = JsonDocument.Parse(responseBytes.ToArray()); }
                catch (JsonException)
                {
                    if (!response.IsSuccessStatusCode) throw CreateApiException(response.StatusCode, Array.Empty<NextDnsApiError>(), _apiKey);
                    throw UnexpectedResponse("NextDNS devolvió una respuesta que no es JSON válido.");
                }
            }

            IReadOnlyList<NextDnsApiError> errors = ParseErrors(document.RootElement);
            if (!response.IsSuccessStatusCode || errors.Count > 0)
            {
                document.Dispose();
                throw CreateApiException(response.StatusCode, errors, _apiKey);
            }
            return document;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            throw new NextDnsApiException(NextDnsApiErrorKind.Timeout, "La solicitud a NextDNS tardó demasiado.");
        }
        catch (HttpRequestException)
        {
            throw new NextDnsApiException(NextDnsApiErrorKind.Network, "No se pudo conectar con la API de NextDNS.");
        }
        catch (IOException)
        {
            throw new NextDnsApiException(NextDnsApiErrorKind.Network, "No se pudo leer la respuesta de NextDNS.");
        }
        finally
        {
            if (responseBytes is not null) CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using Stream source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[32 * 1024];
        try
        {
            int total = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > MaximumResponseBytes)
                    throw new NextDnsApiException(NextDnsApiErrorKind.UnexpectedResponse, "La respuesta de NextDNS supera el tamaño permitido.");
                destination.Write(buffer, 0, read);
            }
            return destination.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (destination.TryGetBuffer(out ArraySegment<byte> storedBytes))
                CryptographicOperations.ZeroMemory(storedBytes.AsSpan());
        }
    }

    private static async Task ThrowStreamResponseErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        string apiKey)
    {
        byte[] responseBytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<NextDnsApiError> errors = Array.Empty<NextDnsApiError>();
        try
        {
            if (responseBytes.Length > 0)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(responseBytes.ToArray());
                    errors = ParseErrors(document.RootElement);
                }
                catch (JsonException) { }
            }

            if (!response.IsSuccessStatusCode || errors.Count > 0)
                throw CreateApiException(response.StatusCode, errors, apiKey);
            throw UnexpectedResponse("NextDNS no devolvió un stream SSE de logs válido.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    private static NextDnsLogStreamEvent? ParseLogStreamEvent(
        string? lastEventId,
        string? eventName,
        IReadOnlyList<string> dataLines,
        bool hasData)
    {
        if (!hasData) return null;
        string payload = string.Join("\n", dataLines);
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw UnexpectedResponse("Un evento del stream de logs de NextDNS no es un objeto JSON.");
            NextDnsLogEntry? entry = document.RootElement.Deserialize<NextDnsLogEntry>(JsonOptions);
            if (entry is null) throw UnexpectedResponse("NextDNS devolvió un evento de log vacío.");
            return new NextDnsLogStreamEvent(lastEventId, string.IsNullOrEmpty(eventName) ? "message" : eventName, entry);
        }
        catch (JsonException)
        {
            throw UnexpectedResponse("NextDNS devolvió un evento de logs con formato JSON inesperado.");
        }
    }

    private static IReadOnlyList<NextDnsApiError> ParseErrors(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("errors", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<NextDnsApiError>();

        var errors = new List<NextDnsApiError>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string code = ReadSafeString(item, "code") ?? "unknown";
            string? detail = ReadSafeString(item, "detail");
            string? source = null;
            if (item.TryGetProperty("source", out JsonElement sourceElement) && sourceElement.ValueKind == JsonValueKind.Object)
                source = ReadSafeString(sourceElement, "parameter") ?? ReadSafeString(sourceElement, "pointer");
            errors.Add(new NextDnsApiError(code, detail, source));
        }
        return errors;
    }

    private static string? ReadSafeString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String) return null;
        string result = value.GetString() ?? string.Empty;
        result = new string(result.Where(character => !char.IsControl(character)).Take(500).ToArray());
        return result.Length == 0 ? null : result;
    }

    private static NextDnsApiException CreateApiException(
        HttpStatusCode statusCode,
        IReadOnlyList<NextDnsApiError> errors,
        string apiKey)
    {
        IReadOnlyList<NextDnsApiError> sanitizedErrors = errors
            .Select(error => new NextDnsApiError(
                RedactApiKey(error.Code, apiKey),
                error.Detail is null ? null : RedactApiKey(error.Detail, apiKey),
                error.Source is null ? null : RedactApiKey(error.Source, apiKey)))
            .ToArray();

        int numericStatusCode = (int)statusCode;
        NextDnsApiErrorKind kind = numericStatusCode switch
        {
            401 => NextDnsApiErrorKind.Unauthorized,
            403 => NextDnsApiErrorKind.Forbidden,
            404 => NextDnsApiErrorKind.NotFound,
            429 => NextDnsApiErrorKind.RateLimited,
            400 or 409 or 422 => NextDnsApiErrorKind.Rejected,
            >= 500 => NextDnsApiErrorKind.ServiceUnavailable,
            >= 300 and < 400 => NextDnsApiErrorKind.UnexpectedRedirect,
            _ => NextDnsApiErrorKind.Rejected
        };

        string message = kind switch
        {
            NextDnsApiErrorKind.Unauthorized => "La API key de NextDNS no es válida o fue revocada.",
            NextDnsApiErrorKind.Forbidden => "La API key no tiene permiso para realizar esta operación.",
            NextDnsApiErrorKind.NotFound => "No se encontró el perfil o recurso solicitado.",
            NextDnsApiErrorKind.RateLimited => "NextDNS limitó temporalmente las solicitudes. Inténtalo de nuevo más tarde.",
            NextDnsApiErrorKind.ServiceUnavailable => "La API de NextDNS no está disponible temporalmente.",
            NextDnsApiErrorKind.UnexpectedRedirect => "La API de NextDNS respondió con una redirección inesperada.",
            _ => "NextDNS rechazó la solicitud."
        };

        if (kind == NextDnsApiErrorKind.Rejected && sanitizedErrors.Count > 0)
        {
            string details = string.Join("; ", sanitizedErrors.Select(error =>
            {
                string summary = error.Code;
                if (!string.IsNullOrEmpty(error.Source)) summary += " [" + error.Source + "]";
                if (error.Detail is not null) summary += ": " + error.Detail;
                return summary;
            }));
            if (details.Length > 0) message += " " + details;
        }
        return new NextDnsApiException(kind, message, statusCode, sanitizedErrors);
    }

    private static string RedactApiKey(string value, string apiKey) =>
        string.IsNullOrEmpty(apiKey) ? value : value.Replace(apiKey, "[redacted]", StringComparison.Ordinal);

    private static NextDnsApiException UnexpectedResponse(string message) =>
        new(NextDnsApiErrorKind.UnexpectedResponse, message);

    private static string ProfilePath(string profileId) => "profiles/" + EscapeProfileId(profileId);

    private static string ProfileListPath(string profileId, NextDnsListType listType) =>
        ProfilePath(profileId) + "/" + ListPath(listType);

    private static string ProfileSectionPath(string profileId, NextDnsProfileSection section) =>
        ProfilePath(profileId) + "/" + (section switch
        {
            NextDnsProfileSection.Security => "security",
            NextDnsProfileSection.Privacy => "privacy",
            NextDnsProfileSection.ParentalControl => "parentalControl",
            NextDnsProfileSection.Settings => "settings",
            _ => throw new ArgumentOutOfRangeException(nameof(section))
        });

    private static string ProfileSettingPath(string profileId, NextDnsSetting setting) =>
        ProfilePath(profileId) + "/settings/" + (setting switch
        {
            NextDnsSetting.Logs => "logs",
            NextDnsSetting.BlockPage => "blockPage",
            NextDnsSetting.Performance => "performance",
            NextDnsSetting.Web3 => "web3",
            NextDnsSetting.Csam => "csam",
            _ => throw new ArgumentOutOfRangeException(nameof(setting))
        });

    private static string ListPath(NextDnsListType listType) => listType switch
    {
        NextDnsListType.Allowlist => "allowlist",
        NextDnsListType.Denylist => "denylist",
        _ => throw new ArgumentOutOfRangeException(nameof(listType))
    };

    private static string EscapeProfileId(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || profileId.Length > 128 ||
            !Regex.IsMatch(profileId, @"\A[a-zA-Z0-9_-]+\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("El ID de perfil de NextDNS no es válido.", nameof(profileId));
        return Uri.EscapeDataString(profileId);
    }

    private static string EscapeSegment(string value) => Uri.EscapeDataString(value);

    private static void ValidateLogsQuery(NextDnsLogsQuery query)
    {
        if (query.Limit is < 10 or > 1000) throw new ArgumentOutOfRangeException(nameof(query), "El límite de logs debe estar entre 10 y 1000.");
        if (!Enum.IsDefined(query.Sort) || (query.Status.HasValue && !Enum.IsDefined(query.Status.Value)))
            throw new ArgumentOutOfRangeException(nameof(query), "El orden o estado de logs no es válido.");
        if (query.From?.Length > 128 || query.To?.Length > 128 || query.Device?.Length > 256 ||
            query.Search?.Length > 512 || query.Cursor?.Length > 1024)
            throw new ArgumentException("Un filtro de logs supera el tamaño permitido.", nameof(query));
        if ((query.From ?? string.Empty).Any(char.IsControl) || (query.To ?? string.Empty).Any(char.IsControl) ||
            (query.Device ?? string.Empty).Any(char.IsControl) || (query.Search ?? string.Empty).Any(char.IsControl) ||
            (query.Cursor ?? string.Empty).Any(char.IsControl))
            throw new ArgumentException("Los filtros de logs no pueden contener caracteres de control.", nameof(query));
    }

    private static void ValidateAnalyticsDevicesFilters(string? from, string? to)
    {
        if (from?.Length > 128 || to?.Length > 128)
            throw new ArgumentException("Un filtro de analytics supera el tamaño permitido.");
        if ((from ?? string.Empty).Any(char.IsControl) || (to ?? string.Empty).Any(char.IsControl))
            throw new ArgumentException("Los filtros de analytics no pueden contener caracteres de control.");
    }

    private static void ValidateLogsStreamQuery(NextDnsLogStreamQuery query)
    {
        if (query.Status.HasValue && !Enum.IsDefined(query.Status.Value))
            throw new ArgumentOutOfRangeException(nameof(query), "El estado de logs no es válido.");
        if (query.Id?.Length > 1024 || query.Device?.Length > 256 || query.Search?.Length > 512)
            throw new ArgumentException("Un filtro del stream de logs supera el tamaño permitido.", nameof(query));
        if ((query.Id ?? string.Empty).Any(char.IsControl) || (query.Device ?? string.Empty).Any(char.IsControl) ||
            (query.Search ?? string.Empty).Any(char.IsControl))
            throw new ArgumentException("Los filtros del stream de logs no pueden contener caracteres de control.", nameof(query));
    }

    private static string BuildLogsQuery(NextDnsLogsQuery query)
    {
        var values = new List<string>
        {
            "limit=" + query.Limit.ToString(CultureInfo.InvariantCulture),
            "sort=" + (query.Sort == NextDnsLogSort.Ascending ? "asc" : "desc")
        };
        AddQuery(values, "from", query.From);
        AddQuery(values, "to", query.To);
        AddQuery(values, "cursor", query.Cursor);
        AddQuery(values, "device", query.Device);
        AddQuery(values, "search", query.Search);
        if (query.Status.HasValue)
            AddQuery(values, "status", query.Status.Value.ToString().ToLowerInvariant());
        if (query.Raw) values.Add("raw=1");
        return "?" + string.Join("&", values);
    }

    private static string BuildAnalyticsDevicesQuery(string? from, string? to, string? cursor)
    {
        var values = new List<string>
        {
            "limit=" + AnalyticsDevicesPageLimit.ToString(CultureInfo.InvariantCulture)
        };
        AddQuery(values, "from", from);
        AddQuery(values, "to", to);
        AddQuery(values, "cursor", cursor);
        return "?" + string.Join("&", values);
    }

    private static string BuildLogsStreamQuery(NextDnsLogStreamQuery query)
    {
        var values = new List<string>();
        AddQuery(values, "id", query.Id);
        AddQuery(values, "device", query.Device);
        AddQuery(values, "search", query.Search);
        if (query.Status.HasValue)
            AddQuery(values, "status", query.Status.Value.ToString().ToLowerInvariant());
        if (query.Raw) values.Add("raw=1");
        return values.Count == 0 ? string.Empty : "?" + string.Join("&", values);
    }

    private static void AddQuery(List<string> values, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value)) values.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
    }

    private sealed class SseLineReader : IDisposable
    {
        private readonly StreamReader _reader;
        private readonly char[] _buffer = new char[4096];
        private int _index;
        private int _count;
        private bool _skipLineFeed;

        internal SseLineReader(Stream source)
        {
            _reader = new StreamReader(source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        }

        internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_index >= _count)
                {
                    _count = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _index = 0;
                    if (_count == 0) return line.Length == 0 ? null : line.ToString();
                }

                char character = _buffer[_index++];
                if (_skipLineFeed)
                {
                    _skipLineFeed = false;
                    if (character == '\n') continue;
                }
                if (character == '\r')
                {
                    _skipLineFeed = true;
                    return line.ToString();
                }
                if (character == '\n') return line.ToString();
                line.Append(character);
                if (line.Length > MaximumSseLineCharacters)
                    throw UnexpectedResponse("Una línea del stream de logs de NextDNS supera el tamaño permitido.");
            }
        }

        public void Dispose() => _reader.Dispose();
    }
}
