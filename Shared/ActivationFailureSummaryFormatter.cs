namespace NextDNSManager.Shared;

/// <summary>Formats only fixed activation enums; broker exception text is never shown here.</summary>
internal static class ActivationFailureSummaryFormatter
{
    internal static string? Format(
        ActivationFailureStage stage,
        ActivationFailureCategory category,
        string language)
    {
        string? stageText = GetStageText(stage, language);
        string? categoryText = GetCategoryText(category, language);
        if (stageText is null || categoryText is null)
            return null;

        string lang = language switch
        {
            "en" => "en",
            "fr" => "fr",
            "pt" => "pt",
            "de" => "de",
            _ => "es"
        };

        return lang switch
        {
            "en" => $"Detected activation stage: {stageText}; error category: {categoryText}.",
            "fr" => $"Étape d’activation détectée : {stageText} ; catégorie d’erreur : {categoryText}.",
            "pt" => $"Etapa de ativação detectada: {stageText}; categoria do erro: {categoryText}.",
            "de" => $"Erkannte Aktivierungsphase: {stageText}; Fehlerkategorie: {categoryText}.",
            _ => $"Etapa de activación detectada: {stageText}; categoría del error: {categoryText}."
        };
    }

    private static string? GetStageText(ActivationFailureStage stage, string language) => language switch
    {
        "en" => stage switch
        {
            ActivationFailureStage.RequestValidation => "request validation",
            ActivationFailureStage.EngineServiceValidation => "local service validation",
            ActivationFailureStage.CliInstall => "NextDNS CLI installation",
            ActivationFailureStage.CliConfigure => "NextDNS CLI configuration",
            ActivationFailureStage.CliStart => "NextDNS CLI startup",
            ActivationFailureStage.LocalDnsQuery => "local DNS query",
            ActivationFailureStage.DnsApply => "DNS settings application",
            ActivationFailureStage.DnsRestore => "DNS settings restoration",
            _ => null
        },
        "fr" => stage switch
        {
            ActivationFailureStage.RequestValidation => "la validation de la requête",
            ActivationFailureStage.EngineServiceValidation => "la validation du service local",
            ActivationFailureStage.CliInstall => "l’installation de NextDNS CLI",
            ActivationFailureStage.CliConfigure => "la configuration de NextDNS CLI",
            ActivationFailureStage.CliStart => "le démarrage de NextDNS CLI",
            ActivationFailureStage.LocalDnsQuery => "la requête DNS locale",
            ActivationFailureStage.DnsApply => "l’application des paramètres DNS",
            ActivationFailureStage.DnsRestore => "la restauration des paramètres DNS",
            _ => null
        },
        "pt" => stage switch
        {
            ActivationFailureStage.RequestValidation => "a validação da solicitação",
            ActivationFailureStage.EngineServiceValidation => "a validação do serviço local",
            ActivationFailureStage.CliInstall => "a instalação da NextDNS CLI",
            ActivationFailureStage.CliConfigure => "a configuração da NextDNS CLI",
            ActivationFailureStage.CliStart => "a inicialização da NextDNS CLI",
            ActivationFailureStage.LocalDnsQuery => "a consulta DNS local",
            ActivationFailureStage.DnsApply => "a aplicação das configurações DNS",
            ActivationFailureStage.DnsRestore => "a restauração das configurações DNS",
            _ => null
        },
        "de" => stage switch
        {
            ActivationFailureStage.RequestValidation => "die Anforderungsprüfung",
            ActivationFailureStage.EngineServiceValidation => "die Prüfung des lokalen Dienstes",
            ActivationFailureStage.CliInstall => "die Installation der NextDNS CLI",
            ActivationFailureStage.CliConfigure => "die Konfiguration der NextDNS CLI",
            ActivationFailureStage.CliStart => "den Start der NextDNS CLI",
            ActivationFailureStage.LocalDnsQuery => "die lokale DNS-Abfrage",
            ActivationFailureStage.DnsApply => "das Anwenden der DNS-Einstellungen",
            ActivationFailureStage.DnsRestore => "das Wiederherstellen der DNS-Einstellungen",
            _ => null
        },
        _ => stage switch
        {
            ActivationFailureStage.RequestValidation => "la validación de la solicitud",
            ActivationFailureStage.EngineServiceValidation => "la validación del servicio local",
            ActivationFailureStage.CliInstall => "la instalación de NextDNS CLI",
            ActivationFailureStage.CliConfigure => "la configuración de NextDNS CLI",
            ActivationFailureStage.CliStart => "el inicio de NextDNS CLI",
            ActivationFailureStage.LocalDnsQuery => "la consulta DNS local",
            ActivationFailureStage.DnsApply => "la aplicación de la configuración DNS",
            ActivationFailureStage.DnsRestore => "la restauración de la configuración DNS",
            _ => null
        }
    };

    private static string? GetCategoryText(ActivationFailureCategory category, string language) => language switch
    {
        "en" => category switch
        {
            ActivationFailureCategory.Access => "access",
            ActivationFailureCategory.Io => "input/output",
            ActivationFailureCategory.Network => "network",
            ActivationFailureCategory.TimeoutOrCancelled => "timeout or cancellation",
            ActivationFailureCategory.InvalidData => "invalid data",
            ActivationFailureCategory.InvalidArgument => "invalid argument",
            ActivationFailureCategory.InvalidOperation => "invalid operation",
            ActivationFailureCategory.WindowsError => "Windows error",
            ActivationFailureCategory.Other => "other",
            _ => null
        },
        "fr" => category switch
        {
            ActivationFailureCategory.Access => "accès",
            ActivationFailureCategory.Io => "entrée/sortie",
            ActivationFailureCategory.Network => "réseau",
            ActivationFailureCategory.TimeoutOrCancelled => "délai dépassé ou annulation",
            ActivationFailureCategory.InvalidData => "données invalides",
            ActivationFailureCategory.InvalidArgument => "argument invalide",
            ActivationFailureCategory.InvalidOperation => "opération invalide",
            ActivationFailureCategory.WindowsError => "erreur Windows",
            ActivationFailureCategory.Other => "autre",
            _ => null
        },
        "pt" => category switch
        {
            ActivationFailureCategory.Access => "acesso",
            ActivationFailureCategory.Io => "entrada/saída",
            ActivationFailureCategory.Network => "rede",
            ActivationFailureCategory.TimeoutOrCancelled => "tempo limite ou cancelamento",
            ActivationFailureCategory.InvalidData => "dados inválidos",
            ActivationFailureCategory.InvalidArgument => "argumento inválido",
            ActivationFailureCategory.InvalidOperation => "operação inválida",
            ActivationFailureCategory.WindowsError => "erro do Windows",
            ActivationFailureCategory.Other => "outro",
            _ => null
        },
        "de" => category switch
        {
            ActivationFailureCategory.Access => "Zugriff",
            ActivationFailureCategory.Io => "Ein-/Ausgabe",
            ActivationFailureCategory.Network => "Netzwerk",
            ActivationFailureCategory.TimeoutOrCancelled => "Zeitüberschreitung oder Abbruch",
            ActivationFailureCategory.InvalidData => "ungültige Daten",
            ActivationFailureCategory.InvalidArgument => "ungültiges Argument",
            ActivationFailureCategory.InvalidOperation => "ungültige Operation",
            ActivationFailureCategory.WindowsError => "Windows-Fehler",
            ActivationFailureCategory.Other => "sonstiger Fehler",
            _ => null
        },
        _ => category switch
        {
            ActivationFailureCategory.Access => "acceso",
            ActivationFailureCategory.Io => "entrada/salida",
            ActivationFailureCategory.Network => "red",
            ActivationFailureCategory.TimeoutOrCancelled => "tiempo de espera o cancelación",
            ActivationFailureCategory.InvalidData => "datos inválidos",
            ActivationFailureCategory.InvalidArgument => "argumento inválido",
            ActivationFailureCategory.InvalidOperation => "operación no válida",
            ActivationFailureCategory.WindowsError => "error de Windows",
            ActivationFailureCategory.Other => "otro error",
            _ => null
        }
    };
}
