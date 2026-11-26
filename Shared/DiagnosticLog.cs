using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace NextDNSManager.Shared;

internal enum DiagnosticEvent
{
    ManagerStarted,
    ManagerShutdown,
    ManagerShutdownFailure,
    WindowsSessionEnding,
    StartupFailure,
    DispatcherUnhandledException,
    ProcessUnhandledException,
    UnobservedTaskException,
    AutostartConfigurationFailure,
    UpdateCheckFailure,
    UpdateDownloadFailure,
    ReleasePageOpenFailure,
    BrokerActionFailure,
    ProfileSwitchFailure,
    BrokerStatusCheckFailure,
    ProfilesLoadFailure,
    ProfilesSaveFailure,
    TrayCleanupFailure,
    DiagnosticsViewFailure,
    ServiceStarted,
    ServiceStartFailure,
    ServiceStopRequested,
    ServiceStopped,
    ServiceShutdown,
    ServiceStopFailure,
    ServiceStopDrainFailure,
    ServiceReconcileFailure,
    ServiceReconcileWaitFailure,
    ServiceProfileSwitchCleanupPending
}

/// <summary>
/// Writes allowlisted diagnostic events only. Exception messages, stacks, paths,
/// request data, profile identifiers, domains, and IP addresses are never serialized.
/// </summary>
internal static class DiagnosticLog
{
    internal const int RetentionDays = 30;
    internal const long MaximumDailyFileBytes = 512 * 1024;
    internal const int MaximumDisplayedCharactersPerArea = 24000;
    internal const string ServiceRecordPrefix = "NDM-DIAG/1";

    private const int MaximumPruneCandidates = 128;
    private const int MaximumEventEntriesToScan = 1000;
    private const int MaximumServiceEntriesToDisplay = 250;
    private static readonly object ManagerLock = new();
    private static readonly Dictionary<(DiagnosticEvent Event, string Code), RateState> ManagerRate = new();
    private const string ProductFolderName = "NextDNS Manager";
    private const string LogsFolderName = "Logs";

    private sealed class RateState
    {
        internal DateTimeOffset LastWritten;
        internal int Suppressed;
    }

    internal static string ManagerDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName, LogsFolderName);

    internal static void WriteManager(DiagnosticEvent diagnosticEvent, Exception? exception = null)
        => WriteManagerCore(diagnosticEvent, ExceptionCategory(exception));

    /// <summary>
    /// Writes a broker failure using only a fixed code from the allowlist. The
    /// response text and exception details are deliberately not accepted here.
    /// </summary>
    internal static void WriteManagerBrokerFailure(DiagnosticEvent diagnosticEvent, BrokerFailureCode failureCode)
        => WriteManagerCore(diagnosticEvent, BrokerFailureCatalog.GetDiagnosticId(failureCode));

    internal static string CreateManagerBrokerFailureRecord(
        DateTimeOffset now,
        DiagnosticEvent diagnosticEvent,
        BrokerFailureCode failureCode,
        int suppressed = 0) =>
        FormatManagerRecord(now, diagnosticEvent, BrokerFailureCatalog.GetDiagnosticId(failureCode), suppressed);

    private static void WriteManagerCore(DiagnosticEvent diagnosticEvent, string diagnosticCode)
    {
        try
        {
            string directory = ManagerDirectory;
            lock (ManagerLock)
            {
                DateTimeOffset now = DateTimeOffset.Now;
                if (!TryTakeManagerRateSlot(diagnosticEvent, diagnosticCode, now, out int suppressed))
                    return;

                Directory.CreateDirectory(directory);
                if (!IsRegularDirectory(directory))
                    return;

                PruneManagerLogs(directory, now.Date);
                string path = GetManagerFilePath(directory, now.Date);
                if (File.Exists(path) && IsReparsePoint(path))
                    return;

                byte[] record = Encoding.UTF8.GetBytes(FormatManagerRecord(now, diagnosticEvent, diagnosticCode, suppressed));
                // Share read access for the diagnostics viewer, while the absence of
                // FileShare.Write serializes append-and-size checks across log writers.
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (stream.Length + record.Length > MaximumDailyFileBytes)
                    return;

                stream.Seek(0, SeekOrigin.End);
                stream.Write(record, 0, record.Length);
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Diagnostics must never prevent startup, shutdown, or DNS operations.
        }
    }

    internal static string ReadManager()
    {
        try
        {
            string directory = ManagerDirectory;
            if (!IsRegularDirectory(directory) || !IsRegularDirectory(Path.GetDirectoryName(directory) ?? string.Empty))
                return string.Empty;

            DateTime today = DateTime.Now.Date;
            var output = new StringBuilder(MaximumDisplayedCharactersPerArea);
            // Derive at most 30 exact paths instead of enumerating an unbounded directory.
            for (int offset = RetentionDays - 1; offset >= 0; offset--)
            {
                string path = GetManagerFilePath(directory, today.AddDays(-offset));
                if (!File.Exists(path) || IsReparsePoint(path))
                    continue;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length == 0 || stream.Length > MaximumDailyFileBytes)
                    continue;

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                output.Append(reader.ReadToEnd());
                TrimOutput(output, MaximumDisplayedCharactersPerArea);
            }
            return output.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string ReadServiceEvents()
    {
        try
        {
            const long retentionMilliseconds = (long)RetentionDays * 24 * 60 * 60 * 1000;
            string queryText = $"*[System[Provider[@Name='{BrokerProtocol.ServiceName}'] and TimeCreated[timediff(@SystemTime) <= {retentionMilliseconds}]]]";
            var query = new EventLogQuery("Application", PathType.LogName, queryText)
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
            DateTime upperBound = DateTime.Now.AddMinutes(5);
            var output = new List<string>(MaximumServiceEntriesToDisplay);

            for (int scanned = 0; scanned < MaximumEventEntriesToScan && output.Count < MaximumServiceEntriesToDisplay; scanned++)
            {
                using EventRecord? entry = reader.ReadEvent();
                if (entry == null)
                    break;
                if (!string.Equals(entry.ProviderName, BrokerProtocol.ServiceName, StringComparison.OrdinalIgnoreCase))
                    continue;

                DateTime? timeCreated = entry.TimeCreated;
                if (!timeCreated.HasValue)
                    continue;
                DateTime eventTime = timeCreated.Value.ToLocalTime();
                if (eventTime < cutoff || eventTime > upperBound)
                    continue;

                // Do not display or retain the raw Event Log message. Only parse a
                // short, strict service record and rebuild its text from local constants.
                string message;
                try { message = entry.FormatDescription() ?? string.Empty; }
                catch { continue; }
                if (message.Length > 256 || !TryParseServiceRecord(message, out DiagnosticEvent code, out string category, out int suppressed))
                    continue;

                output.Add(FormatServiceViewLine(eventTime, code, category, suppressed));
            }

            output.Reverse();
            return string.Concat(output);
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string CreateServiceRecord(DiagnosticEvent diagnosticEvent, Exception? exception, int suppressed) =>
        $"{ServiceRecordPrefix}\t{diagnosticEvent}\t{Level(diagnosticEvent)}\t{ExceptionCategory(exception)}\t{Math.Clamp(suppressed, 0, 1000000)}";

    internal static bool TryParseServiceRecord(string message, out DiagnosticEvent diagnosticEvent, out string category, out int suppressed)
    {
        diagnosticEvent = default;
        category = string.Empty;
        suppressed = 0;
        string[] fields = message.TrimEnd('\r', '\n').Split('\t');
        if (fields.Length != 5 || fields[0] != ServiceRecordPrefix ||
            !Enum.TryParse(fields[1], ignoreCase: false, out diagnosticEvent) || !IsServiceEvent(diagnosticEvent) ||
            fields[2] != Level(diagnosticEvent) || !IsExceptionCategory(fields[3]) ||
            !int.TryParse(fields[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out suppressed) ||
            suppressed is < 0 or > 1000000)
            return false;

        category = fields[3];
        return true;
    }

    internal static string EventDescription(DiagnosticEvent diagnosticEvent) => diagnosticEvent switch
    {
        DiagnosticEvent.ManagerStarted => "La aplicación inició.",
        DiagnosticEvent.ManagerShutdown => "La aplicación terminó.",
        DiagnosticEvent.ManagerShutdownFailure => "Ocurrió un error al cerrar la aplicación.",
        DiagnosticEvent.WindowsSessionEnding => "Windows está cerrando la sesión.",
        DiagnosticEvent.StartupFailure => "No se pudo iniciar la aplicación.",
        DiagnosticEvent.DispatcherUnhandledException => "Ocurrió un error no controlado en la interfaz.",
        DiagnosticEvent.ProcessUnhandledException => "Ocurrió un error no controlado en el proceso.",
        DiagnosticEvent.UnobservedTaskException => "Una tarea terminó con un error no observado.",
        DiagnosticEvent.AutostartConfigurationFailure => "No se pudo configurar el inicio automático.",
        DiagnosticEvent.UpdateCheckFailure => "No se pudo comprobar si hay actualizaciones.",
        DiagnosticEvent.UpdateDownloadFailure => "No se pudo descargar o verificar una actualización.",
        DiagnosticEvent.ReleasePageOpenFailure => "No se pudo abrir la página de versiones.",
        DiagnosticEvent.BrokerActionFailure => "No se confirmó la operación solicitada.",
        DiagnosticEvent.ProfileSwitchFailure => "No se confirmó el cambio de perfil.",
        DiagnosticEvent.BrokerStatusCheckFailure => "No se pudo consultar el estado del servicio.",
        DiagnosticEvent.ProfilesLoadFailure => "No se pudieron leer los perfiles guardados.",
        DiagnosticEvent.ProfilesSaveFailure => "No se pudieron guardar los perfiles.",
        DiagnosticEvent.TrayCleanupFailure => "No se pudo cerrar correctamente el icono de notificación.",
        DiagnosticEvent.DiagnosticsViewFailure => "No se pudo mostrar el panel de diagnóstico.",
        DiagnosticEvent.ServiceStarted => "El servicio de control inició.",
        DiagnosticEvent.ServiceStartFailure => "No se pudo iniciar el servicio de control.",
        DiagnosticEvent.ServiceStopRequested => "Windows solicitó detener el servicio de control.",
        DiagnosticEvent.ServiceStopped => "El servicio de control terminó.",
        DiagnosticEvent.ServiceShutdown => "Windows está apagando el sistema.",
        DiagnosticEvent.ServiceStopFailure => "Ocurrió un error al detener el servicio de control.",
        DiagnosticEvent.ServiceStopDrainFailure => "El servicio no pudo completar una tarea pendiente al detenerse.",
        DiagnosticEvent.ServiceReconcileFailure => "El servicio no pudo reconciliar el estado de DNS.",
        DiagnosticEvent.ServiceReconcileWaitFailure => "El servicio no pudo esperar para reconciliar el estado de DNS.",
        DiagnosticEvent.ServiceProfileSwitchCleanupPending => "Quedó pendiente limpiar el estado de un cambio de perfil.",
        _ => "Ocurrió un evento de diagnóstico."
    };

    internal static string Level(DiagnosticEvent diagnosticEvent) => diagnosticEvent switch
    {
        DiagnosticEvent.ManagerStarted or DiagnosticEvent.ManagerShutdown or DiagnosticEvent.WindowsSessionEnding or
        DiagnosticEvent.ServiceStarted or DiagnosticEvent.ServiceStopRequested or DiagnosticEvent.ServiceStopped or
        DiagnosticEvent.ServiceShutdown => "INFO",
        DiagnosticEvent.AutostartConfigurationFailure or DiagnosticEvent.UpdateCheckFailure or DiagnosticEvent.UpdateDownloadFailure or
        DiagnosticEvent.ReleasePageOpenFailure or DiagnosticEvent.ProfilesLoadFailure or
        DiagnosticEvent.TrayCleanupFailure or DiagnosticEvent.ServiceProfileSwitchCleanupPending => "WARN",
        _ => "ERROR"
    };

    private static string FormatManagerRecord(DateTimeOffset now, DiagnosticEvent diagnosticEvent, string diagnosticCode, int suppressed)
    {
        string codeText = diagnosticCode.Length == 0 ? string.Empty : $" ({diagnosticCode})";
        string suppressionText = suppressed > 0 ? $" Se omitieron {suppressed} eventos repetidos." : string.Empty;
        return $"{now:yyyy-MM-dd HH:mm:ss.fff zzz}\t{Level(diagnosticEvent)}\t{diagnosticEvent}{codeText}\t{EventDescription(diagnosticEvent)}{suppressionText}{Environment.NewLine}";
    }

    private static string FormatServiceViewLine(DateTime time, DiagnosticEvent diagnosticEvent, string category, int suppressed)
    {
        string exceptionText = category.Length == 0 ? string.Empty : $" ({category})";
        string suppressionText = suppressed > 0 ? $" Se omitieron {suppressed} eventos repetidos." : string.Empty;
        return $"{time:yyyy-MM-dd HH:mm:ss.fff}\t{Level(diagnosticEvent)}\t{diagnosticEvent}{exceptionText}\t{EventDescription(diagnosticEvent)}{suppressionText}{Environment.NewLine}";
    }

    private static bool TryTakeManagerRateSlot(DiagnosticEvent diagnosticEvent, string diagnosticCode, DateTimeOffset now, out int suppressed)
    {
        suppressed = 0;
        if (diagnosticEvent is DiagnosticEvent.ManagerStarted or DiagnosticEvent.ManagerShutdown or DiagnosticEvent.WindowsSessionEnding)
            return true;

        var key = (diagnosticEvent, diagnosticCode);

        if (!ManagerRate.TryGetValue(key, out RateState? state))
        {
            ManagerRate.Add(key, new RateState { LastWritten = now });
            return true;
        }

        if (now - state.LastWritten < TimeSpan.FromMinutes(1))
        {
            if (state.Suppressed < 1000000)
                state.Suppressed++;
            return false;
        }

        suppressed = state.Suppressed;
        state.Suppressed = 0;
        state.LastWritten = now;
        return true;
    }

    private static void PruneManagerLogs(string directory, DateTime today)
    {
        DateTime oldestRetainedDay = today.AddDays(-(RetentionDays - 1));
        foreach (string path in Directory.EnumerateFiles(directory, "Manager-*.log")
                     .Where(path => TryGetManagerFileDay(path, out DateTime day) && (day < oldestRetainedDay || day > today))
                     .Take(MaximumPruneCandidates))
        {
            if (IsReparsePoint(path) || !TryGetManagerFileDay(path, out DateTime day))
                continue;
            if (day >= oldestRetainedDay && day <= today)
                continue;
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string GetManagerFilePath(string directory, DateTime day) =>
        Path.Combine(directory, "Manager-" + day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".log");

    private static bool TryGetManagerFileDay(string path, out DateTime day)
    {
        day = default;
        string name = Path.GetFileName(path);
        const string prefix = "Manager-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".log", StringComparison.Ordinal))
            return false;
        string value = name.Substring(prefix.Length, name.Length - prefix.Length - 4);
        return DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out day);
    }

    private static bool IsRegularDirectory(string path)
    {
        try { return Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
        catch { return false; }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private static void TrimOutput(StringBuilder output, int limit)
    {
        if (output.Length <= limit)
            return;
        int start = output.Length - limit;
        int newline = output.ToString(start, limit).IndexOf('\n');
        output.Remove(0, newline >= 0 ? start + newline + 1 : start);
    }

    private static string ExceptionCategory(Exception? exception) => exception switch
    {
        null => string.Empty,
        UnauthorizedAccessException or SecurityException => "ACCESS",
        IOException => "IO",
        SocketException => "NETWORK",
        TimeoutException or OperationCanceledException => "TIMEOUT_OR_CANCELLED",
        JsonException => "INVALID_DATA",
        ArgumentException => "INVALID_ARGUMENT",
        InvalidOperationException => "INVALID_OPERATION",
        Win32Exception => "WINDOWS_ERROR",
        _ => "OTHER"
    };

    private static bool IsExceptionCategory(string value) => value is "" or "ACCESS" or "IO" or "NETWORK" or
        "TIMEOUT_OR_CANCELLED" or "INVALID_DATA" or "INVALID_ARGUMENT" or "INVALID_OPERATION" or "WINDOWS_ERROR" or "OTHER";

    private static bool IsServiceEvent(DiagnosticEvent diagnosticEvent) => diagnosticEvent is
        DiagnosticEvent.ServiceStarted or DiagnosticEvent.ServiceStartFailure or DiagnosticEvent.ServiceStopRequested or
        DiagnosticEvent.ServiceStopped or DiagnosticEvent.ServiceShutdown or DiagnosticEvent.ServiceStopFailure or
        DiagnosticEvent.ServiceStopDrainFailure or DiagnosticEvent.ServiceReconcileFailure or
        DiagnosticEvent.ServiceReconcileWaitFailure or DiagnosticEvent.ServiceProfileSwitchCleanupPending;
}
