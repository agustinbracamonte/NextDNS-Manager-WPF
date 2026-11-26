using System;
using System.Collections.Generic;

namespace NextDNSManager.Shared;

/// <summary>
/// Stable, non-sensitive identifiers for broker failures. These identifiers are
/// diagnostic-only; they do not change the broker pipe protocol.
/// </summary>
internal enum BrokerFailureCode
{
    ServiceUnavailable,
    ProtocolMismatch,
    AccessDenied,
    PipeUnavailable,
    PipeProcessIdUnavailable,
    ServiceManagerQueryFailed,
    ServiceNotFound,
    PipeServiceProcessMismatch,
    ServiceAccountUnexpected,
    ServicePathUnexpected,
    ProcessInspectionFailed,
    PipeProcessPathMismatch,
    ProcessPathReadFailed,
    ServiceStatusReadFailed,
    ServiceConfigurationReadFailed,
    UserNotAuthorized,
    PauseActionUnconfirmed,
    ProfileChangeUnconfirmed,
    StatusQueryUnconfirmed,
    ActivationNotConfirmed,
    ProfileSelectionNotConfirmed,
    ResponseErrorMissing,
    GenericResponse,
    BrokerReportedError
}

internal static class BrokerFailureCatalog
{
    private static readonly IReadOnlyDictionary<string, BrokerFailureCode> KnownMessages =
        new Dictionary<string, BrokerFailureCode>(StringComparer.Ordinal)
        {
            ["El servicio auxiliar no está disponible. Repara la instalación de NextDNS Manager."] = BrokerFailureCode.ServiceUnavailable,
            ["La versión del protocolo del broker no coincide con esta aplicación."] = BrokerFailureCode.ProtocolMismatch,
            ["No se pudo acceder al broker de NextDNS. Repara la instalación desde el instalador y vuelve a intentarlo."] = BrokerFailureCode.AccessDenied,
            ["No se pudo conectar con el broker de NextDNS. Repara la instalación desde el instalador y vuelve a intentarlo."] = BrokerFailureCode.PipeUnavailable,
            ["No se pudo verificar el proceso del broker. Repara la instalación desde el instalador."] = BrokerFailureCode.PipeProcessIdUnavailable,
            ["No se pudo verificar el servicio del broker. Repara la instalación desde el instalador."] = BrokerFailureCode.ServiceManagerQueryFailed,
            ["No se encontró el servicio broker de NextDNS. Repara la instalación desde el instalador."] = BrokerFailureCode.ServiceNotFound,
            ["La conexión no pertenece al proceso en ejecución del servicio broker. Repara la instalación desde el instalador."] = BrokerFailureCode.PipeServiceProcessMismatch,
            ["El broker no está configurado para ejecutarse como LocalSystem. Repara la instalación desde el instalador."] = BrokerFailureCode.ServiceAccountUnexpected,
            ["La ruta configurada para el broker es inesperada. Repara la instalación desde el instalador."] = BrokerFailureCode.ServicePathUnexpected,
            ["No se pudo inspeccionar el proceso del broker. Repara la instalación desde el instalador."] = BrokerFailureCode.ProcessInspectionFailed,
            ["El servidor de la conexión no es el broker instalado. Repara la instalación desde el instalador."] = BrokerFailureCode.PipeProcessPathMismatch,
            ["No se pudo leer la ruta del proceso del broker."] = BrokerFailureCode.ProcessPathReadFailed,
            ["No se pudo leer el estado del servicio broker."] = BrokerFailureCode.ServiceStatusReadFailed,
            ["No se pudo leer la configuración del servicio broker."] = BrokerFailureCode.ServiceConfigurationReadFailed,
            ["Este usuario no está autorizado para controlar NextDNS."] = BrokerFailureCode.UserNotAuthorized,
            ["No se pudo cambiar la pausa."] = BrokerFailureCode.PauseActionUnconfirmed,
            ["El broker no pudo completar la operación."] = BrokerFailureCode.GenericResponse,
            ["El broker no pudo cambiar de perfil."] = BrokerFailureCode.ProfileChangeUnconfirmed,
            ["No se pudo consultar el estado del broker."] = BrokerFailureCode.StatusQueryUnconfirmed,
            ["El broker no confirmó el perfil solicitado."] = BrokerFailureCode.ActivationNotConfirmed,
            ["El broker no confirmó el perfil seleccionado."] = BrokerFailureCode.ProfileSelectionNotConfirmed
        };

    internal static bool TryClassifyKnownMessage(string? message, out BrokerFailureCode code)
    {
        if (!string.IsNullOrWhiteSpace(message) && KnownMessages.TryGetValue(message, out code))
            return true;

        code = default;
        return false;
    }

    internal static BrokerFailureCode ClassifyResponseError(string? responseError)
    {
        if (string.IsNullOrWhiteSpace(responseError))
            return BrokerFailureCode.ResponseErrorMissing;

        return TryClassifyKnownMessage(responseError, out BrokerFailureCode code)
            ? code
            : BrokerFailureCode.BrokerReportedError;
    }

    internal static BrokerResponseException CreateResponseException(string? responseError, string displayMessage) =>
        new(responseError, ClassifyResponseError(responseError), displayMessage);

    /// <summary>Returns a fixed localization key for the failure code.</summary>
    internal static string GetLocalizationKey(BrokerFailureCode code) => code switch
    {
        BrokerFailureCode.ServiceUnavailable => "Code.BrokerError.ServiceUnavailable",
        BrokerFailureCode.ProtocolMismatch => "Code.BrokerError.ProtocolMismatch",
        BrokerFailureCode.AccessDenied => "Code.BrokerError.AccessDenied",
        BrokerFailureCode.PipeUnavailable => "Code.BrokerError.PipeUnavailable",
        BrokerFailureCode.PipeProcessIdUnavailable => "Code.BrokerError.PipeProcessIdUnavailable",
        BrokerFailureCode.ServiceManagerQueryFailed => "Code.BrokerError.ServiceManagerQueryFailed",
        BrokerFailureCode.ServiceNotFound => "Code.BrokerError.ServiceNotFound",
        BrokerFailureCode.PipeServiceProcessMismatch => "Code.BrokerError.PipeServiceProcessMismatch",
        BrokerFailureCode.ServiceAccountUnexpected => "Code.BrokerError.ServiceAccountUnexpected",
        BrokerFailureCode.ServicePathUnexpected => "Code.BrokerError.ServicePathUnexpected",
        BrokerFailureCode.ProcessInspectionFailed => "Code.BrokerError.ProcessInspectionFailed",
        BrokerFailureCode.PipeProcessPathMismatch => "Code.BrokerError.PipeProcessPathMismatch",
        BrokerFailureCode.ProcessPathReadFailed => "Code.BrokerError.ProcessPathReadFailed",
        BrokerFailureCode.ServiceStatusReadFailed => "Code.BrokerError.ServiceStatusReadFailed",
        BrokerFailureCode.ServiceConfigurationReadFailed => "Code.BrokerError.ServiceConfigurationReadFailed",
        BrokerFailureCode.UserNotAuthorized => "Code.BrokerError.UserNotAuthorized",
        BrokerFailureCode.PauseActionUnconfirmed => "Code.BrokerError.PauseActionUnconfirmed",
        BrokerFailureCode.ProfileChangeUnconfirmed => "Code.BrokerError.ProfileChangeUnconfirmed",
        BrokerFailureCode.StatusQueryUnconfirmed => "Code.BrokerError.StatusQueryUnconfirmed",
        BrokerFailureCode.ActivationNotConfirmed => "Code.BrokerError.ActivationNotConfirmed",
        BrokerFailureCode.ProfileSelectionNotConfirmed => "Code.BrokerError.ProfileSelectionNotConfirmed",
        BrokerFailureCode.ResponseErrorMissing => "Code.BrokerError.ResponseErrorMissing",
        BrokerFailureCode.GenericResponse => "Code.BrokerError.GenericResponse",
        BrokerFailureCode.BrokerReportedError => "Code.BrokerError.BrokerReportedError",
        _ => "Code.BrokerError.BrokerReportedError"
    };

    /// <summary>
    /// Returns a stable allowlisted diagnostic identifier. Never append exception
    /// text, response content, IDs, paths, or other request data here.
    /// </summary>
    internal static string GetDiagnosticId(BrokerFailureCode code) => code switch
    {
        BrokerFailureCode.ServiceUnavailable => "BROKER_SERVICE_UNAVAILABLE",
        BrokerFailureCode.ProtocolMismatch => "BROKER_PROTOCOL_MISMATCH",
        BrokerFailureCode.AccessDenied => "BROKER_ACCESS_DENIED",
        BrokerFailureCode.PipeUnavailable => "BROKER_PIPE_UNAVAILABLE",
        BrokerFailureCode.PipeProcessIdUnavailable => "BROKER_PIPE_PROCESS_ID_UNAVAILABLE",
        BrokerFailureCode.ServiceManagerQueryFailed => "BROKER_SERVICE_MANAGER_QUERY_FAILED",
        BrokerFailureCode.ServiceNotFound => "BROKER_SERVICE_NOT_FOUND",
        BrokerFailureCode.PipeServiceProcessMismatch => "BROKER_PIPE_SERVICE_PROCESS_MISMATCH",
        BrokerFailureCode.ServiceAccountUnexpected => "BROKER_SERVICE_ACCOUNT_UNEXPECTED",
        BrokerFailureCode.ServicePathUnexpected => "BROKER_SERVICE_PATH_UNEXPECTED",
        BrokerFailureCode.ProcessInspectionFailed => "BROKER_PROCESS_INSPECTION_FAILED",
        BrokerFailureCode.PipeProcessPathMismatch => "BROKER_PIPE_PROCESS_PATH_MISMATCH",
        BrokerFailureCode.ProcessPathReadFailed => "BROKER_PROCESS_PATH_READ_FAILED",
        BrokerFailureCode.ServiceStatusReadFailed => "BROKER_SERVICE_STATUS_READ_FAILED",
        BrokerFailureCode.ServiceConfigurationReadFailed => "BROKER_SERVICE_CONFIGURATION_READ_FAILED",
        BrokerFailureCode.UserNotAuthorized => "BROKER_USER_NOT_AUTHORIZED",
        BrokerFailureCode.PauseActionUnconfirmed => "BROKER_PAUSE_ACTION_UNCONFIRMED",
        BrokerFailureCode.ProfileChangeUnconfirmed => "BROKER_PROFILE_CHANGE_UNCONFIRMED",
        BrokerFailureCode.StatusQueryUnconfirmed => "BROKER_STATUS_QUERY_UNCONFIRMED",
        BrokerFailureCode.ActivationNotConfirmed => "BROKER_ACTIVATION_NOT_CONFIRMED",
        BrokerFailureCode.ProfileSelectionNotConfirmed => "BROKER_PROFILE_SELECTION_NOT_CONFIRMED",
        BrokerFailureCode.ResponseErrorMissing => "BROKER_RESPONSE_ERROR_MISSING",
        BrokerFailureCode.GenericResponse => "BROKER_GENERIC_RESPONSE",
        BrokerFailureCode.BrokerReportedError => "BROKER_REPORTED_ERROR",
        _ => "BROKER_REPORTED_ERROR"
    };
}

/// <summary>Preserves broker response text in memory while exposing a safe UI message.</summary>
internal sealed class BrokerResponseException : Exception
{
    internal BrokerResponseException(string? responseError, BrokerFailureCode failureCode, string displayMessage)
        : base(displayMessage)
    {
        ResponseError = responseError;
        FailureCode = failureCode;
    }

    // Response text can contain private details. Callers may inspect it in memory,
    // but diagnostic logging accepts only FailureCode.
    internal string? ResponseError { get; }
    internal BrokerFailureCode FailureCode { get; }
}
