using System.Net;
using System.Net.NetworkInformation;

namespace NextDNSManager.Shared;

/// <summary>
/// Reads the current network adapter configuration without changing adapter,
/// DNS, service, or registry state and without sending network probes.
/// </summary>
internal static class NetworkDiagnostics
{
    internal static NetworkDiagnosticsSnapshot Capture()
    {
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException error)
        {
            return new NetworkDiagnosticsSnapshot(
                capturedAt, Array.Empty<NetworkAdapterSnapshot>(),
                NetworkConnectionDiagnosis.InspectionFailed, false, error.Message);
        }

        NetworkAdapterSnapshot[] adapters = interfaces.Select(CaptureAdapter).ToArray();
        NetworkAdapterSnapshot[] activeAdapters = adapters
            .Where(adapter => adapter.IsOperational && adapter.Type != NetworkInterfaceType.Loopback.ToString())
            .ToArray();

        bool hasGateway = activeAdapters.Any(adapter => adapter.Gateways.Length > 0);
        bool hasDns = activeAdapters.Any(adapter => adapter.DnsServers.Length > 0);
        bool hasUnreadableActiveAdapter = activeAdapters.Any(adapter => !adapter.PropertiesRead);

        NetworkConnectionDiagnosis diagnosis = activeAdapters.Length == 0
            ? NetworkConnectionDiagnosis.NoActiveAdapter
            : hasUnreadableActiveAdapter && (!hasGateway || !hasDns)
                ? NetworkConnectionDiagnosis.InspectionIncomplete
                : !hasGateway
                    ? NetworkConnectionDiagnosis.NoDefaultGateway
                    : !hasDns
                        ? NetworkConnectionDiagnosis.NoDnsServer
                        : NetworkConnectionDiagnosis.LocallyConfigured;

        return new NetworkDiagnosticsSnapshot(
            capturedAt, adapters, diagnosis, false,
            activeAdapters.FirstOrDefault(adapter => !adapter.PropertiesRead)?.PropertiesError);
    }

    private static NetworkAdapterSnapshot CaptureAdapter(NetworkInterface networkInterface)
    {
        string id = networkInterface.Id;
        string name = networkInterface.Name;
        string description = networkInterface.Description;
        string type = networkInterface.NetworkInterfaceType.ToString();
        string status = networkInterface.OperationalStatus.ToString();
        bool isOperational = networkInterface.OperationalStatus == OperationalStatus.Up;

        try
        {
            IPInterfaceProperties properties = networkInterface.GetIPProperties();
            string[] dnsServers = properties.DnsAddresses
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] gateways = properties.GatewayAddresses
                .Select(gateway => gateway.Address)
                .Where(address => address is not null && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
                .Select(address => address!.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] addresses = properties.UnicastAddresses
                .Select(unicast => unicast.Address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new NetworkAdapterSnapshot(
                id, name, description, type, status, isOperational,
                dnsServers, gateways, addresses, PropertiesRead: true, PropertiesError: null);
        }
        catch (NetworkInformationException error)
        {
            return FailedAdapter(id, name, description, type, status, isOperational, error.Message);
        }
        catch (InvalidOperationException error)
        {
            return FailedAdapter(id, name, description, type, status, isOperational, error.Message);
        }
    }

    private static NetworkAdapterSnapshot FailedAdapter(
        string id,
        string name,
        string description,
        string type,
        string status,
        bool isOperational,
        string error)
    {
        return new NetworkAdapterSnapshot(
            id, name, description, type, status, isOperational,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            PropertiesRead: false, PropertiesError: error);
    }
}

internal sealed record NetworkDiagnosticsSnapshot(
    DateTimeOffset CapturedAtUtc,
    NetworkAdapterSnapshot[] Adapters,
    NetworkConnectionDiagnosis Diagnosis,
    bool InternetReachabilityTested,
    string? Error);

internal sealed record NetworkAdapterSnapshot(
    string Id,
    string Name,
    string Description,
    string Type,
    string OperationalStatus,
    bool IsOperational,
    string[] DnsServers,
    string[] Gateways,
    string[] Addresses,
    bool PropertiesRead,
    string? PropertiesError);

internal enum NetworkConnectionDiagnosis
{
    InspectionFailed,
    InspectionIncomplete,
    NoActiveAdapter,
    NoDefaultGateway,
    NoDnsServer,
    LocallyConfigured
}
