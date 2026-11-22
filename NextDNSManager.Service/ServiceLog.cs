namespace NextDNSManager.Service;

internal static class ServiceLog
{
    internal static void Write(string message)
    {
        try
        {
            System.Diagnostics.EventLog.WriteEntry(NextDNSManager.Shared.BrokerProtocol.ServiceName,
                message, System.Diagnostics.EventLogEntryType.Warning);
        }
        catch { }
    }
}
