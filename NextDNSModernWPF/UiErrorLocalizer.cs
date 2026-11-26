using System;
using System.Collections.Generic;
using System.Linq;

namespace NextDNSModernWPF;

/// <summary>Localizes stable, application-owned errors shown by the UI.</summary>
internal static class UiErrorLocalizer
{
    private static readonly IReadOnlyDictionary<string, string> KnownMessageKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["El servicio auxiliar no está disponible. Repara la instalación de NextDNS Manager."] = "Code.Error.Broker.Generic",
            ["La versión del protocolo del broker no coincide con esta aplicación."] = "Code.Error.Broker.Generic",
            ["No se pudo acceder al broker de NextDNS. Repara la instalación desde el instalador y vuelve a intentarlo."] = "Code.Error.Broker.Generic",
            ["No se pudo conectar con el broker de NextDNS. Repara la instalación desde el instalador y vuelve a intentarlo."] = "Code.Error.Broker.Generic",
            ["No se pudo verificar el proceso del broker. Repara la instalación desde el instalador."] = "Code.Error.Broker.Generic",
            ["No se pudo verificar el servicio del broker. Repara la instalación desde el instalador."] = "Code.Error.Broker.Generic",
            ["No se encontró el servicio broker de NextDNS. Repara la instalación desde el instalador."] = "Code.Error.Broker.Generic",
            ["La conexión no pertenece al proceso en ejecución del servicio broker. Repara la instalación desde el instalador."] = "Code.Error.Broker.Generic",
            ["El broker no está configurado para ejecutarse como LocalSystem. Repara la instalación desde el instalador."] = "Code.Error.Broker.Generic",
            ["La ruta configurada para el broker es inesperada. Repara la instalación desde el instalador."] = "Code.Error.Broker.Generic",
            ["No se pudo inspeccionar el proceso del broker. Repara la instalación desde el instalador."] = "Code.Error.Broker.Generic",
            ["El servidor de la conexión no es el broker instalado. Repara la instalación desde el instalador."] = "Code.Error.Broker.Generic",
            ["No se pudo leer la ruta del proceso del broker."] = "Code.Error.Broker.Generic",
            ["No se pudo leer el estado del servicio broker."] = "Code.Error.Broker.Generic",
            ["No se pudo leer la configuración del servicio broker."] = "Code.Error.Broker.Generic",
            ["No se pudo cambiar la pausa."] = "Code.Error.Broker.Generic",
            ["El broker de NextDNS no respondió en 60 segundos. Comprueba que el servicio esté iniciado o repáralo desde el instalador."] = "Code.Error.Broker.Timeout",
            ["La operación tardó demasiado. Consulta el estado antes de reintentar."] = "Code.Error.Broker.OperationTimeout",
            ["El broker no pudo completar la operación."] = "Code.Error.Broker.Generic",
            ["El broker no pudo cambiar de perfil."] = "Code.Error.Broker.Generic",
            ["La operación tardó demasiado. Consulta el estado antes de volver a intentarlo."] = "Code.Error.Broker.OperationTimeout",
            ["No se pudo consultar el estado del broker."] = "Code.Error.Broker.Generic",
            ["El broker no confirmó el perfil solicitado."] = "Code.Error.Broker.Generic",
            ["El broker no confirmó el perfil seleccionado."] = "Code.Error.Broker.Generic",
            ["La pausa debe durar 5, 15 o 30 minutos."] = "Code.Error.Pause.InvalidDuration",
            ["Ya hay una pausa temporal pendiente."] = "Code.Error.Pause.AlreadyPending",
            ["La protección debe estar activa para pausarla."] = "Code.Error.Pause.NotActive",
            ["No hay un perfil válido guardado para reanudar la protección."] = "Code.Error.Pause.ProfileUnavailable",
            ["No hay una pausa temporal pendiente."] = "Code.Error.Pause.NotPending",
            ["No se pudo reanudar porque el perfil guardado no es válido; la protección quedó desactivada."] = "Code.Error.Pause.ResumeFailed",
            ["No se pudo aplicar el perfil solicitado. Se restauró el perfil anterior."] = "Code.Error.Profile.SwitchFailedRestored",
            ["No se pudo aplicar el perfil solicitado ni restaurar el perfil anterior. La protección requiere recuperación; conserva el perfil anterior y reinténtalo cuando vuelva la conexión."] = "Code.Error.Profile.SwitchFailedRecovery",
            ["No se pudo guardar el estado de recuperación del cambio de perfil."] = "Code.Error.Profile.RecoveryStateSaveFailed",
            ["Windows no confirmó la restauración DNS del perfil anterior."] = "Code.Error.Profile.RestoreNotConfirmed",
            ["El estado de pausa guardado no es válido; se desactivó la protección para recuperar DNS."] = "Code.Error.Recovery.InvalidPauseState",
            ["La configuración NextDNS guardada no es válida; DNS se restauró y la protección quedó desactivada."] = "Code.Error.Recovery.InvalidConfiguration",
            ["El journal del cambio de perfil no contiene una configuración válida; se conservó para revisión."] = "Code.Error.Recovery.InvalidProfileJournal",
            ["El journal del cambio de perfil está dañado; se conservó para revisión."] = "Code.Error.Recovery.DamagedProfileJournal",
            ["Falta la clave protegida para completar el cambio de perfil."] = "Code.Error.Profile.MissingProtectedKey",
            ["El archivo protegido de la API key supera el tamaño permitido."] = "Code.Error.ApiKey.Storage",
            ["No se pudo descifrar la API key guardada para este usuario de Windows."] = "Code.Error.ApiKey.Storage",
            ["La API key guardada no tiene un formato válido."] = "Code.Error.ApiKey.Storage",
            ["No se pudo determinar la carpeta segura para la API key."] = "Code.Error.ApiKey.Storage",
            ["El almacenamiento protegido de API keys requiere Windows."] = "Code.Error.ApiKey.Storage",
            ["La API key protegida tiene un tamaño inesperado."] = "Code.Error.ApiKey.Storage",
            ["La entrada está vacía."] = "Code.Error.Domain.EmptyEntry",
            ["La entrada es demasiado larga o contiene caracteres de control."] = "Code.Error.Domain.EntryTooLongOrControl",
            ["La URL no es válida."] = "Code.Error.Domain.InvalidUrl",
            ["Usa un dominio o una URL HTTP/HTTPS."] = "Code.Error.Domain.UseDomainOrUrl",
            ["El dominio tiene un punto final inválido."] = "Code.Error.Domain.InvalidTrailingDot",
            ["El dominio está vacío."] = "Code.Error.Domain.EmptyDomain",
            ["El dominio no tiene un formato válido."] = "Code.Error.Domain.InvalidFormat",
            ["Se requiere un nombre de dominio, no una dirección IP."] = "Code.Error.Domain.NotIpAddress",
            ["El dominio contiene una etiqueta inválida."] = "Code.Error.Domain.InvalidLabel",
            ["La búsqueda no puede superar 512 caracteres."] = "Code.Error.Api.SearchTooLong",
            ["La API key de NextDNS no puede estar vacía."] = "Code.Error.ApiKey.Empty",
            ["La API key tiene caracteres no permitidos para un encabezado HTTP."] = "Code.Error.ApiKey.InvalidHeaderCharacters",
            ["El ID de perfil de NextDNS no es válido."] = "Code.Error.ApiProfile.InvalidId",
            ["Los filtros de logs no pueden contener caracteres de control."] = "Code.Error.Activity.LogFilterControlCharacters",
            ["Los filtros de analytics no pueden contener caracteres de control."] = "Code.Error.Activity.AnalyticsFilterControlCharacters",
            ["Un filtro de logs supera el tamaño permitido."] = "Code.Error.Activity.LogFilterTooLong",
            ["Un filtro de analytics supera el tamaño permitido."] = "Code.Error.Activity.AnalyticsFilterTooLong",
            ["Un filtro del stream de logs supera el tamaño permitido."] = "Code.Error.Activity.StreamFilterTooLong",
            ["Los filtros del stream de logs no pueden contener caracteres de control."] = "Code.Error.Activity.StreamFilterControlCharacters",
            ["No se pudo identificar la sesión de Windows."] = "Code.Error.WindowsSession.Unknown",
            ["No se pudo determinar la carpeta local del usuario."] = "Code.Error.UserFolder.Unknown",
            ["La carpeta de descarga debe estar en una unidad local."] = "Code.Error.Update.Generic",
            ["No se pudo determinar la versión instalada de NextDNS Manager."] = "Code.Error.Update.Generic"
        };

    public static string Localize(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is NextDnsApiException apiException)
            return Localize(apiException);

        return Localize(exception.Message);
    }

    public static string Localize(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string firstLine = message.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
        if (KnownMessageKeys.TryGetValue(firstLine, out string? key))
            return LocalizationManager.Get(key);

        if (TryLocalizeDynamicBrokerMessage(message, out string? localized) && localized is not null)
            return localized;

        string translated = LocalizationManager.TranslateExistingText(message);
        if (!string.Equals(translated, message, StringComparison.Ordinal))
            return translated;

        return message;
    }

    private static bool TryLocalizeDynamicBrokerMessage(string message, out string? localized)
    {
        (string Prefix, string Key)[] dynamicMessages =
        [
            ("No se pudo completar la activación DNS. Se intentó restaurar la configuración original; queda guardada para reintentar.", "Code.Error.Dns.ActivationFailedWithDetails"),
            ("No se pudo restaurar la configuración DNS de todos los adaptadores. La copia de seguridad se conservó para reintentar.", "Code.Error.Dns.RestoreFailedWithDetails"),
            ("El núcleo DNS terminó con código ", "Code.Error.Dns.CoreFailedWithDetails")
        ];

        foreach ((string prefix, string key) in dynamicMessages)
        {
            if (!message.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            string details = message[prefix.Length..].Trim();
            localized = LocalizationManager.T(key, details);
            return true;
        }

        localized = null;
        return false;
    }

    private static string Localize(NextDnsApiException exception)
    {
        string key = exception.Kind switch
        {
            NextDnsApiErrorKind.Rejected => "Code.Error.Api.Rejected",
            NextDnsApiErrorKind.Unauthorized => "Code.Error.Api.Unauthorized",
            NextDnsApiErrorKind.Forbidden => "Code.Error.Api.Forbidden",
            NextDnsApiErrorKind.NotFound => "Code.Error.Api.NotFound",
            NextDnsApiErrorKind.RateLimited => "Code.Error.Api.RateLimited",
            NextDnsApiErrorKind.ServiceUnavailable => "Code.Error.Api.ServiceUnavailable",
            NextDnsApiErrorKind.Network => "Code.Error.Api.Network",
            NextDnsApiErrorKind.Timeout => "Code.Error.Api.Timeout",
            NextDnsApiErrorKind.UnexpectedRedirect => "Code.Error.Api.UnexpectedRedirect",
            NextDnsApiErrorKind.UnexpectedResponse => "Code.Error.Api.UnexpectedResponse",
            _ => "Code.Error.Api.Generic"
        };

        string summary = LocalizationManager.Get(key);
        string details = string.Join("; ", exception.Errors.Select(error =>
        {
            string detail = error.Code;
            if (!string.IsNullOrWhiteSpace(error.Source))
                detail += " [" + error.Source + "]";
            if (!string.IsNullOrWhiteSpace(error.Detail))
                detail += ": " + error.Detail;
            return detail;
        }));

        return string.IsNullOrWhiteSpace(details)
            ? summary
            : LocalizationManager.T("Code.Error.Api.WithDetails", summary, details);
    }
}
