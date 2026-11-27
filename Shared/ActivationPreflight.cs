using System.Net;
using System.Net.Sockets;

namespace NextDNSManager.Shared;

[Flags]
internal enum DnsPortTable
{
    None = 0,
    TcpIPv4 = 1,
    TcpIPv6 = 2,
    UdpIPv4 = 4,
    UdpIPv6 = 8,
    All = TcpIPv4 | TcpIPv6 | UdpIPv4 | UdpIPv6
}

internal enum DnsTransportProtocol
{
    Tcp,
    Udp
}

internal enum DnsListenerScope
{
    Loopback,
    Wildcard
}

internal enum PreflightServiceStatus
{
    Running,
    Stopped,
    NotInstalled,
    Unknown
}

internal enum PreflightFileStatus
{
    Present,
    Missing,
    Unknown
}

internal enum ActivationPreflightHint
{
    None,
    PossibleDnsPortUse,
    ManagerBrokerNotReady,
    NextDnsServiceNotReady,
    NextDnsCliMissing,
    OfficialServiceRegistered,
    NoOperationalNetworkAdapter,
    NoLoopbackOrWildcardListenerObserved
}

/// <summary>
/// A local endpoint from the OS TCP/UDP owner tables. Addresses and PIDs are
/// used only for filtering and are never copied into the user-facing report.
/// </summary>
internal readonly record struct DnsPortListener(
    DnsTransportProtocol Protocol,
    IPAddress LocalAddress,
    int Port,
    uint ProcessId);

internal readonly record struct DnsTcpRowLayout(
    int RowSize,
    int LocalAddressOffset,
    int LocalPortOffset,
    int ProcessIdOffset);

internal static class DnsPortTableLayout
{
    internal static DnsTcpRowLayout GetTcpRowLayout(AddressFamily family) => family switch
    {
        AddressFamily.InterNetwork => new DnsTcpRowLayout(
            RowSize: 24,
            LocalAddressOffset: 4,
            LocalPortOffset: 8,
            ProcessIdOffset: 20),
        AddressFamily.InterNetworkV6 => new DnsTcpRowLayout(
            RowSize: 56,
            LocalAddressOffset: 0,
            LocalPortOffset: 20,
            ProcessIdOffset: 52),
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };
}

internal sealed record ActivationPreflightSnapshot(
    DnsPortTable CompletedTables,
    IReadOnlyList<DnsPortListener> Listeners,
    uint? TrustedNextDnsProcessId,
    PreflightServiceStatus ManagerBrokerStatus,
    PreflightServiceStatus NextDnsServiceStatus,
    PreflightServiceStatus OfficialServiceStatus,
    PreflightFileStatus NextDnsCliFileStatus,
    bool NetworkAdapterInspectionCompleted,
    bool HasOperationalNetworkAdapter);

/// <summary>
/// Privacy-safe, local-only observations for an activation failure. The
/// returned report has no process IDs, addresses, paths, or process names.
/// </summary>
internal sealed record ActivationPreflightReport(
    bool ListenerInspectionComplete,
    IReadOnlyList<DnsPortFinding> PossibleDnsListeners,
    PreflightServiceStatus ManagerBrokerStatus,
    PreflightServiceStatus NextDnsServiceStatus,
    PreflightServiceStatus OfficialServiceStatus,
    PreflightFileStatus NextDnsCliFileStatus,
    bool NetworkAdapterInspectionCompleted,
    bool HasOperationalNetworkAdapter);

internal sealed record ActivationPreflightCaptureResult(
    ActivationPreflightReport? Report,
    bool TimedOut);

/// <summary>Awaits the local diagnostic so its result is not lost at the popup boundary.</summary>
internal static class ActivationPreflightCapturePolicy
{
    internal static async Task<ActivationPreflightCaptureResult> AwaitReportAsync(
        Task<ActivationPreflightReport?> captureTask,
        TimeSpan maximumWait)
    {
        ArgumentNullException.ThrowIfNull(captureTask);
        if (maximumWait <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        try
        {
            ActivationPreflightReport? report = await captureTask.WaitAsync(maximumWait).ConfigureAwait(false);
            return new ActivationPreflightCaptureResult(report, TimedOut: false);
        }
        catch (TimeoutException)
        {
            return new ActivationPreflightCaptureResult(Report: null, TimedOut: true);
        }
        catch
        {
            // Diagnostics must never replace the activation failure.
            return new ActivationPreflightCaptureResult(Report: null, TimedOut: false);
        }
    }
}

internal readonly record struct DnsPortFinding(
    DnsTransportProtocol Protocol,
    AddressFamily AddressFamily,
    DnsListenerScope Scope);

internal static class ActivationPreflightAnalyzer
{
    internal const int DnsPort = 53;

    internal static ActivationPreflightReport Analyze(ActivationPreflightSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        IEnumerable<DnsPortFinding> findings = snapshot.Listeners
            .Where(listener => listener.Port == DnsPort)
            .Where(listener => snapshot.TrustedNextDnsProcessId is not uint ownPid || listener.ProcessId != ownPid)
            .Select(TryCreateFinding)
            .Where(finding => finding.HasValue)
            .Select(finding => finding!.Value)
            .Distinct()
            .OrderBy(finding => finding.Protocol)
            .ThenBy(finding => finding.AddressFamily)
            .ThenBy(finding => finding.Scope);

        return new ActivationPreflightReport(
            snapshot.CompletedTables == DnsPortTable.All,
            findings.ToArray(),
            snapshot.ManagerBrokerStatus,
            snapshot.NextDnsServiceStatus,
            snapshot.OfficialServiceStatus,
            snapshot.NextDnsCliFileStatus,
            snapshot.NetworkAdapterInspectionCompleted,
            snapshot.HasOperationalNetworkAdapter);
    }

    internal static ActivationPreflightHint ChooseHint(ActivationPreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (report.PossibleDnsListeners.Count > 0)
            return ActivationPreflightHint.PossibleDnsPortUse;
        if (report.ManagerBrokerStatus is PreflightServiceStatus.NotInstalled or PreflightServiceStatus.Stopped)
            return ActivationPreflightHint.ManagerBrokerNotReady;
        if (report.NextDnsServiceStatus is PreflightServiceStatus.NotInstalled or PreflightServiceStatus.Stopped)
            return ActivationPreflightHint.NextDnsServiceNotReady;
        if (report.NextDnsCliFileStatus == PreflightFileStatus.Missing)
            return ActivationPreflightHint.NextDnsCliMissing;
        if (report.OfficialServiceStatus is PreflightServiceStatus.Running or PreflightServiceStatus.Stopped)
            return ActivationPreflightHint.OfficialServiceRegistered;
        if (report.NetworkAdapterInspectionCompleted && !report.HasOperationalNetworkAdapter)
            return ActivationPreflightHint.NoOperationalNetworkAdapter;
        if (report.ListenerInspectionComplete)
            return ActivationPreflightHint.NoLoopbackOrWildcardListenerObserved;
        return ActivationPreflightHint.None;
    }

    private static DnsPortFinding? TryCreateFinding(DnsPortListener listener)
    {
        IPAddress address = listener.LocalAddress;
        AddressFamily family = address.AddressFamily;
        bool isLoopback = IPAddress.IsLoopback(address);

        if (address.IsIPv4MappedToIPv6)
        {
            IPAddress ipv4Address = address.MapToIPv4();
            family = AddressFamily.InterNetwork;
            isLoopback = IPAddress.IsLoopback(ipv4Address);
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return new DnsPortFinding(listener.Protocol, family, DnsListenerScope.Wildcard);

        if (isLoopback)
            return new DnsPortFinding(listener.Protocol, family, DnsListenerScope.Loopback);

        // A listener bound only to a specific interface address does not, by
        // itself, establish a conflict with loopback or wildcard DNS binds.
        return null;
    }
}
