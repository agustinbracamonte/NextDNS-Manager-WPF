namespace NextDNSManager.Shared;

internal enum ActivationFailureStage
{
    Unknown,
    RequestValidation,
    EngineServiceValidation,
    CliInstall,
    CliConfigure,
    CliStart,
    LocalDnsQuery,
    DnsApply,
    DnsRestore
}

internal enum ActivationFailureCategory
{
    Unknown,
    Access,
    Io,
    Network,
    TimeoutOrCancelled,
    InvalidData,
    InvalidArgument,
    InvalidOperation,
    WindowsError,
    Other
}

/// <summary>
/// Parsed activation-error metadata. The original response remains available in
/// memory for compatibility, while callers use MessageWithoutPrefix for display
/// and classification and the enum values for allowlisted diagnostics only.
/// </summary>
internal sealed record DecodedActivationFailure(
    string? OriginalResponseError,
    string? MessageWithoutPrefix,
    bool HasEnvelope,
    bool MetadataIsKnown,
    ActivationFailureStage Stage,
    ActivationFailureCategory Category);

internal static class ActivationFailureEnvelope
{
    private const string Prefix = "[NDMERR/ACTIVATE/";

    internal static string Encode(
        ActivationFailureStage stage,
        ActivationFailureCategory category,
        string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return $"{Prefix}{GetStageId(stage)}/{GetCategoryId(category)}] {message}";
    }

    internal static DecodedActivationFailure Decode(string? responseError)
    {
        if (responseError is null || !responseError.StartsWith(Prefix, StringComparison.Ordinal))
            return Legacy(responseError);

        int close = responseError.IndexOf("] ", Prefix.Length, StringComparison.Ordinal);
        if (close < 0)
            return Legacy(responseError);

        string tokens = responseError[Prefix.Length..close];
        int separator = tokens.IndexOf('/');
        if (separator <= 0 || separator == tokens.Length - 1 || tokens.IndexOf('/', separator + 1) >= 0)
            return Legacy(responseError);

        string stageToken = tokens[..separator];
        string categoryToken = tokens[(separator + 1)..];
        bool knownStage = TryParseStage(stageToken, out ActivationFailureStage stage);
        bool knownCategory = TryParseCategory(categoryToken, out ActivationFailureCategory category);
        string messageWithoutPrefix = responseError[(close + 2)..];

        return new DecodedActivationFailure(
            responseError,
            messageWithoutPrefix,
            HasEnvelope: true,
            MetadataIsKnown: knownStage && knownCategory,
            knownStage ? stage : ActivationFailureStage.Unknown,
            knownCategory ? category : ActivationFailureCategory.Unknown);
    }

    private static DecodedActivationFailure Legacy(string? responseError) => new(
        responseError,
        responseError,
        HasEnvelope: false,
        MetadataIsKnown: false,
        ActivationFailureStage.Unknown,
        ActivationFailureCategory.Unknown);

    private static bool TryParseStage(string token, out ActivationFailureStage stage)
    {
        stage = token switch
        {
            "REQUEST_VALIDATION" => ActivationFailureStage.RequestValidation,
            "ENGINE_SERVICE_VALIDATION" => ActivationFailureStage.EngineServiceValidation,
            "CLI_INSTALL" => ActivationFailureStage.CliInstall,
            "CLI_CONFIGURE" => ActivationFailureStage.CliConfigure,
            "CLI_START" => ActivationFailureStage.CliStart,
            "LOCAL_DNS_QUERY" => ActivationFailureStage.LocalDnsQuery,
            "DNS_APPLY" => ActivationFailureStage.DnsApply,
            "DNS_RESTORE" => ActivationFailureStage.DnsRestore,
            _ => ActivationFailureStage.Unknown
        };

        return stage != ActivationFailureStage.Unknown;
    }

    private static bool TryParseCategory(string token, out ActivationFailureCategory category)
    {
        category = token switch
        {
            "ACCESS" => ActivationFailureCategory.Access,
            "IO" => ActivationFailureCategory.Io,
            "NETWORK" => ActivationFailureCategory.Network,
            "TIMEOUT_OR_CANCELLED" => ActivationFailureCategory.TimeoutOrCancelled,
            "INVALID_DATA" => ActivationFailureCategory.InvalidData,
            "INVALID_ARGUMENT" => ActivationFailureCategory.InvalidArgument,
            "INVALID_OPERATION" => ActivationFailureCategory.InvalidOperation,
            "WINDOWS_ERROR" => ActivationFailureCategory.WindowsError,
            "OTHER" => ActivationFailureCategory.Other,
            _ => ActivationFailureCategory.Unknown
        };

        return category != ActivationFailureCategory.Unknown;
    }

    internal static string GetStageId(ActivationFailureStage stage) => stage switch
    {
        ActivationFailureStage.RequestValidation => "REQUEST_VALIDATION",
        ActivationFailureStage.EngineServiceValidation => "ENGINE_SERVICE_VALIDATION",
        ActivationFailureStage.CliInstall => "CLI_INSTALL",
        ActivationFailureStage.CliConfigure => "CLI_CONFIGURE",
        ActivationFailureStage.CliStart => "CLI_START",
        ActivationFailureStage.LocalDnsQuery => "LOCAL_DNS_QUERY",
        ActivationFailureStage.DnsApply => "DNS_APPLY",
        ActivationFailureStage.DnsRestore => "DNS_RESTORE",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    internal static string GetCategoryId(ActivationFailureCategory category) => category switch
    {
        ActivationFailureCategory.Access => "ACCESS",
        ActivationFailureCategory.Io => "IO",
        ActivationFailureCategory.Network => "NETWORK",
        ActivationFailureCategory.TimeoutOrCancelled => "TIMEOUT_OR_CANCELLED",
        ActivationFailureCategory.InvalidData => "INVALID_DATA",
        ActivationFailureCategory.InvalidArgument => "INVALID_ARGUMENT",
        ActivationFailureCategory.InvalidOperation => "INVALID_OPERATION",
        ActivationFailureCategory.WindowsError => "WINDOWS_ERROR",
        ActivationFailureCategory.Other => "OTHER",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}
