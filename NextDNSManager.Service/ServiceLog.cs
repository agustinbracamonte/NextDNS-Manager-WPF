using NextDNSManager.Shared;

namespace NextDNSManager.Service;

internal static class ServiceLog
{
    private static readonly object Gate = new();
    private static readonly Dictionary<DiagnosticEvent, RateState> Rates = new();
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    private sealed class RateState
    {
        internal DateTimeOffset LastWritten;
        internal int Suppressed;
    }

    internal static void Write(DiagnosticEvent diagnosticEvent, Exception? exception = null)
    {
        try
        {
            int suppressed;
            lock (Gate)
            {
                DateTimeOffset now = DateTimeOffset.Now;
                if (!Rates.TryGetValue(diagnosticEvent, out RateState? rate))
                {
                    Rates.Add(diagnosticEvent, new RateState { LastWritten = now });
                    suppressed = 0;
                }
                else if (DiagnosticLog.Level(diagnosticEvent) != "INFO" && now - rate.LastWritten < MinimumInterval)
                {
                    if (rate.Suppressed < 1000000)
                        rate.Suppressed++;
                    return;
                }
                else
                {
                    suppressed = rate.Suppressed;
                    rate.Suppressed = 0;
                    rate.LastWritten = now;
                }
            }

            System.Diagnostics.EventLogEntryType type = DiagnosticLog.Level(diagnosticEvent) switch
            {
                "INFO" => System.Diagnostics.EventLogEntryType.Information,
                "WARN" => System.Diagnostics.EventLogEntryType.Warning,
                _ => System.Diagnostics.EventLogEntryType.Error
            };
            System.Diagnostics.EventLog.WriteEntry(NextDNSManager.Shared.BrokerProtocol.ServiceName,
                DiagnosticLog.CreateServiceRecord(diagnosticEvent, exception, suppressed), type);
        }
        catch { }
    }
}
