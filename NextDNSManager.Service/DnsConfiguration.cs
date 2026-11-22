using Microsoft.Win32;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace NextDNSManager.Service;

/// <summary>
/// Redirects active network adapters to the local NextDNS engine and retains
/// the pre-existing per-adapter DNS configuration until every restore verifies.
/// </summary>
internal sealed class DnsConfiguration
{
    private const string SnapshotValueName = "DnsSnapshot";
    private const string Tcpip4Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    private const string Tcpip6Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\";
    private const int SnapshotVersion = 2;
    private static readonly string NetshPath = Path.Combine(Environment.SystemDirectory, "netsh.exe");
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    internal bool HasSnapshot
    {
        get
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath);
            return key?.GetValueNames().Contains(SnapshotValueName, StringComparer.OrdinalIgnoreCase) == true;
        }
    }

    internal bool IsActive
    {
        get
        {
            try
            {
                SnapshotDocument? snapshot = ReadSnapshot();
                if (snapshot is null || snapshot.Adapters.Count == 0) return false;

                List<AdapterInfo> active = EnumerateAdapters(upOnly: true);
                if (active.Count == 0) return false;

                foreach (AdapterInfo adapter in active)
                {
                    AdapterDnsSnapshot? saved = snapshot.Adapters.FirstOrDefault(item =>
                        string.Equals(item.InterfaceId, adapter.InterfaceId, StringComparison.OrdinalIgnoreCase));
                    if (saved is null) return false;

                    if (adapter.IPv4Supported &&
                        (!saved.IPv4Supported || !IsOnlyAddress(ReadNameServers(adapter.InterfaceId, ipv6: false), IPAddress.Loopback)))
                        return false;
                    if (adapter.IPv6Supported &&
                        (!saved.IPv6Supported || !IsOnlyAddress(ReadNameServers(adapter.InterfaceId, ipv6: true), IPAddress.IPv6Loopback)))
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    internal async Task EnableAsync(CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await EnableCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _mutationGate.Release(); }
    }

    private async Task EnableCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<AdapterInfo> active = EnumerateAdapters(upOnly: true);
        if (active.Count == 0)
            throw new InvalidOperationException("No hay adaptadores de red activos para configurar.");

        SnapshotDocument? existingSnapshot = ReadSnapshot();
        SnapshotDocument snapshot = existingSnapshot ?? new SnapshotDocument();
        if (snapshot.Version != SnapshotVersion || snapshot.Adapters.Count > 500)
            throw new InvalidOperationException("La copia de seguridad DNS no tiene un formato compatible.");

        bool snapshotChanged = existingSnapshot is null;
        foreach (AdapterInfo adapter in active)
        {
            AdapterDnsSnapshot? saved = snapshot.Adapters.FirstOrDefault(item =>
                string.Equals(item.InterfaceId, adapter.InterfaceId, StringComparison.OrdinalIgnoreCase));
            if (saved is null)
            {
                snapshot.Adapters.Add(CaptureAdapter(adapter));
                snapshotChanged = true;
            }
            else if (MergeNewlySupportedFamilies(saved, adapter)) snapshotChanged = true;
        }

        if (snapshot.Adapters.Count == 0)
            throw new InvalidOperationException("No se pudo guardar la configuración DNS original.");

        // Persist the original values (including newly seen adapters) before
        // allowing netsh to mutate any adapter.
        if (snapshotChanged)
        {
            WriteSnapshot(snapshot);
            snapshotChanged = false;
        }

        var touched = new List<AdapterDnsSnapshot>();
        try
        {
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Network interfaces can appear while this operation runs.
                // Capture and persist every newly-up adapter before touching it.
                active = EnumerateAdapters(upOnly: true);
                foreach (AdapterInfo adapter in active)
                {
                    AdapterDnsSnapshot? saved = snapshot.Adapters.FirstOrDefault(item =>
                        string.Equals(item.InterfaceId, adapter.InterfaceId, StringComparison.OrdinalIgnoreCase));
                    if (saved is null)
                    {
                        snapshot.Adapters.Add(CaptureAdapter(adapter));
                        snapshotChanged = true;
                    }
                    else if (MergeNewlySupportedFamilies(saved, adapter)) snapshotChanged = true;
                }
                if (snapshotChanged)
                {
                    WriteSnapshot(snapshot);
                    snapshotChanged = false;
                }

                AdapterInfo? next = active.FirstOrDefault(adapter => !processed.Contains(adapter.InterfaceId));
                if (next is null)
                {
                    if (IsActive) return;
                    throw new InvalidOperationException("Windows no confirmó la configuración DNS local en todos los adaptadores activos.");
                }
                if (processed.Count >= 500 || snapshot.Adapters.Count > 500)
                    throw new InvalidOperationException("La lista de adaptadores cambió demasiadas veces durante la activación DNS.");

                AdapterDnsSnapshot original = snapshot.Adapters.Single(item =>
                    string.Equals(item.InterfaceId, next.InterfaceId, StringComparison.OrdinalIgnoreCase));
                touched.Add(original);
                if (next.IPv4Supported)
                    await SetAddressAsync(next, ipv6: false, [IPAddress.Loopback], cancellationToken).ConfigureAwait(false);
                if (next.IPv6Supported)
                    await SetAddressAsync(next, ipv6: true, [IPAddress.IPv6Loopback], cancellationToken).ConfigureAwait(false);
                processed.Add(next.InterfaceId);
            }
        }
        catch (Exception enableError)
        {
            // A caller cancellation must not prevent a best-effort rollback.
            using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            List<string> rollbackErrors;
            try
            {
                rollbackErrors = await RestoreAdaptersAsync(touched, rollbackTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                rollbackErrors = [$"la restauración de emergencia también falló: {rollbackError.Message}"];
            }
            if (rollbackErrors.Count > 0)
                throw new InvalidOperationException(
                    $"No se pudo completar la activación DNS. Se intentó restaurar la configuración original; queda guardada para reintentar. {string.Join("; ", rollbackErrors)}",
                    enableError);
            throw;
        }
    }

    internal async Task DisableAsync(CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await DisableCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _mutationGate.Release(); }
    }

    private async Task DisableCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotDocument? snapshot = ReadSnapshot();
        if (snapshot is null) return;
        if (snapshot.Version != SnapshotVersion || snapshot.Adapters.Count == 0)
            throw new InvalidOperationException("La copia de seguridad DNS está vacía o tiene un formato incompatible; se conservó para revisión.");

        List<string> failures = await RestoreAdaptersAsync(snapshot.Adapters, cancellationToken).ConfigureAwait(false);
        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"No se pudo restaurar la configuración DNS de todos los adaptadores. La copia de seguridad se conservó para reintentar. {string.Join("; ", failures)}");

        using RegistryKey key = OpenPolicyKey(writable: true);
        key.DeleteValue(SnapshotValueName, throwOnMissingValue: false);
    }

    private static async Task<List<string>> RestoreAdaptersAsync(
        IEnumerable<AdapterDnsSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (AdapterDnsSnapshot snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdapterInfo? adapter;
            try { adapter = FindAdapter(snapshot.InterfaceId); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                failures.Add($"{snapshot.InterfaceId}: no se pudo consultar el adaptador: {error.Message}");
                continue;
            }
            if (adapter is null)
            {
                failures.Add($"No se encontró el adaptador {snapshot.InterfaceId}.");
                continue;
            }

            await RestoreFamilyAsync(adapter, snapshot, ipv6: false, cancellationToken, failures).ConfigureAwait(false);
            await RestoreFamilyAsync(adapter, snapshot, ipv6: true, cancellationToken, failures).ConfigureAwait(false);
        }
        return failures;
    }

    private static async Task RestoreFamilyAsync(
        AdapterInfo adapter,
        AdapterDnsSnapshot snapshot,
        bool ipv6,
        CancellationToken cancellationToken,
        List<string> failures)
    {
        string family = ipv6 ? "ipv6" : "ipv4";
        int? index = ipv6 ? adapter.IPv6Index : adapter.IPv4Index;
        bool supported = ipv6 ? snapshot.IPv6Supported : snapshot.IPv4Supported;
        string[] original = ipv6 ? snapshot.IPv6NameServers : snapshot.IPv4NameServers;
        if (!supported) return;
        if (index is null)
        {
            failures.Add($"No se obtuvo el índice {family} del adaptador {snapshot.InterfaceId}.");
            return;
        }

        try
        {
            await RestoreAddressListAsync(family, index.Value, original, cancellationToken).ConfigureAwait(false);
            string[] actual = ReadNameServers(snapshot.InterfaceId, ipv6);
            if (!AddressListsEqual(actual, original))
                failures.Add($"Windows no confirmó la lista DNS {family} original del adaptador {snapshot.InterfaceId}.");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            failures.Add($"{family} {snapshot.InterfaceId}: {error.Message}");
        }
    }

    private static async Task SetAddressAsync(
        AdapterInfo adapter,
        bool ipv6,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken)
    {
        string family = ipv6 ? "ipv6" : "ipv4";
        int? index = ipv6 ? adapter.IPv6Index : adapter.IPv4Index;
        if (index is null)
            throw new InvalidOperationException($"No se obtuvo el índice {family} del adaptador {adapter.InterfaceId}.");

        IPAddress[] checkedAddresses = addresses.Select(address => ValidateAddress(address.ToString(), ipv6)).ToArray();
        if (checkedAddresses.Length == 0)
            throw new ArgumentException("Se requiere al menos una dirección DNS estática.", nameof(addresses));

        await RunNetshAsync(
            ["interface", family, "set", "dnsservers", $"name={FormatIndex(index.Value)}", "source=static",
             $"address={checkedAddresses[0]}", "validate=no"], cancellationToken).ConfigureAwait(false);
        VerifyAddressList(adapter.InterfaceId, ipv6, checkedAddresses);
    }

    private static async Task RestoreAddressListAsync(
        string family,
        int index,
        string[] addresses,
        CancellationToken cancellationToken)
    {
        if (addresses.Length == 0)
        {
            await RunNetshAsync(
                ["interface", family, "set", "dnsservers", $"name={FormatIndex(index)}", "source=dhcp"],
                cancellationToken).ConfigureAwait(false);
            return;
        }

        bool ipv6 = family == "ipv6";
        IPAddress[] checkedAddresses = addresses.Select(address => ValidateAddress(address, ipv6)).ToArray();
        await RunNetshAsync(
            ["interface", family, "set", "dnsservers", $"name={FormatIndex(index)}", "source=static",
             $"address={checkedAddresses[0]}", "validate=no"], cancellationToken).ConfigureAwait(false);

        for (int addressIndex = 1; addressIndex < checkedAddresses.Length; addressIndex++)
        {
            await RunNetshAsync(
                ["interface", family, "add", "dnsservers", $"name={FormatIndex(index)}",
                 $"address={checkedAddresses[addressIndex]}", $"index={FormatIndex(addressIndex + 1)}", "validate=no"],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RunNetshAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        await ProcessRunner.RunAsync(NetshPath, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static void VerifyAddressList(string interfaceId, bool ipv6, IReadOnlyList<IPAddress> expected)
    {
        string[] actual = ReadNameServers(interfaceId, ipv6);
        string[] expectedText = expected.Select(address => address.ToString()).ToArray();
        if (!AddressListsEqual(actual, expectedText))
            throw new InvalidOperationException($"Windows no confirmó la configuración DNS {(ipv6 ? "ipv6" : "ipv4")} del adaptador {interfaceId}.");
    }

    private static bool IsOnlyAddress(string[] actual, IPAddress expected) =>
        actual.Length == 1 && IPAddress.TryParse(actual[0], out IPAddress? parsed) && parsed.Equals(expected);

    private static AdapterDnsSnapshot CaptureAdapter(AdapterInfo adapter) => new()
    {
        InterfaceId = adapter.InterfaceId,
        IPv4Supported = adapter.IPv4Supported,
        IPv6Supported = adapter.IPv6Supported,
        IPv4NameServers = adapter.IPv4Supported ? ReadNameServers(adapter.InterfaceId, ipv6: false) : Array.Empty<string>(),
        IPv6NameServers = adapter.IPv6Supported ? ReadNameServers(adapter.InterfaceId, ipv6: true) : Array.Empty<string>()
    };

    private static bool MergeNewlySupportedFamilies(AdapterDnsSnapshot snapshot, AdapterInfo adapter)
    {
        bool changed = false;
        if (adapter.IPv4Supported && !snapshot.IPv4Supported)
        {
            snapshot.IPv4NameServers = ReadNameServers(adapter.InterfaceId, ipv6: false);
            snapshot.IPv4Supported = true;
            changed = true;
        }
        if (adapter.IPv6Supported && !snapshot.IPv6Supported)
        {
            snapshot.IPv6NameServers = ReadNameServers(adapter.InterfaceId, ipv6: true);
            snapshot.IPv6Supported = true;
            changed = true;
        }
        return changed;
    }

    private static SnapshotDocument? ReadSnapshot()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath);
        if (key is null || !key.GetValueNames().Contains(SnapshotValueName, StringComparer.OrdinalIgnoreCase))
            return null;

        object? value = key.GetValue(SnapshotValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is not string json || string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("La copia de seguridad DNS guardada en el registro no es válida.");

        SnapshotDocument document = JsonSerializer.Deserialize<SnapshotDocument>(json, JsonOptions)
            ?? throw new InvalidOperationException("La copia de seguridad DNS está vacía.");
        if (document.Version != SnapshotVersion || document.Adapters is null || document.Adapters.Count == 0 || document.Adapters.Count > 500)
            throw new InvalidOperationException("La copia de seguridad DNS tiene un formato incompatible.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AdapterDnsSnapshot adapter in document.Adapters)
        {
            if (!Guid.TryParse(adapter.InterfaceId, out Guid id))
                throw new InvalidOperationException("La copia de seguridad DNS contiene un ID de adaptador no válido.");
            adapter.InterfaceId = id.ToString("B");
            if (!ids.Add(adapter.InterfaceId))
                throw new InvalidOperationException("La copia de seguridad DNS contiene adaptadores duplicados.");
            adapter.IPv4NameServers = ValidateSavedAddresses(adapter.IPv4NameServers, ipv6: false);
            adapter.IPv6NameServers = ValidateSavedAddresses(adapter.IPv6NameServers, ipv6: true);
            if ((!adapter.IPv4Supported && adapter.IPv4NameServers.Length > 0) ||
                (!adapter.IPv6Supported && adapter.IPv6NameServers.Length > 0) ||
                (!adapter.IPv4Supported && !adapter.IPv6Supported))
                throw new InvalidOperationException("La copia de seguridad DNS no coincide con las familias del adaptador.");
        }
        return document;
    }

    private static void WriteSnapshot(SnapshotDocument snapshot)
    {
        string json = JsonSerializer.Serialize(snapshot, JsonOptions);
        using RegistryKey key = OpenPolicyKey(writable: true);
        key.SetValue(SnapshotValueName, json, RegistryValueKind.String);
    }

    private static RegistryKey OpenPolicyKey(bool writable) =>
        Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath, writable)
        ?? throw new InvalidOperationException("Falta la clave protegida de configuración del broker.");

    private static string[] ReadNameServers(string interfaceId, bool ipv6)
    {
        if (!Guid.TryParse(interfaceId, out Guid id))
            throw new InvalidOperationException("El ID del adaptador no es válido.");
        string path = (ipv6 ? Tcpip6Interfaces : Tcpip4Interfaces) + id.ToString("B");
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(path);
        if (key is null) return Array.Empty<string>();
        object? value = key.GetValue("NameServer", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return Array.Empty<string>();
        if (value is not string raw)
            throw new InvalidOperationException($"El valor NameServer del adaptador {id:B} no es texto.");
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        string[] tokens = raw.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return ValidateSavedAddresses(tokens, ipv6);
    }

    private static string[] ValidateSavedAddresses(string[]? addresses, bool ipv6)
    {
        if (addresses is null) throw new InvalidOperationException("La copia de seguridad DNS no contiene una lista válida.");
        return addresses.Select(address => ValidateAddress(address, ipv6).ToString()).ToArray();
    }

    private static IPAddress ValidateAddress(string address, bool ipv6)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address, out IPAddress? parsed) ||
            (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) != ipv6)
            throw new InvalidOperationException("La lista DNS contiene una dirección IP no válida.");
        return parsed;
    }

    private static bool AddressListsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count) return false;
        for (int index = 0; index < left.Count; index++)
        {
            if (!IPAddress.TryParse(left[index], out IPAddress? leftAddress) ||
                !IPAddress.TryParse(right[index], out IPAddress? rightAddress) || !leftAddress.Equals(rightAddress))
                return false;
        }
        return true;
    }

    private static List<AdapterInfo> EnumerateAdapters(bool upOnly)
    {
        var result = new List<AdapterInfo>();
        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                (upOnly && networkInterface.OperationalStatus != OperationalStatus.Up))
                continue;
            if (!Guid.TryParse(networkInterface.Id, out Guid id)) continue;
            AdapterInfo? adapter = TryCreateActiveAdapterInfo(networkInterface, id.ToString("B"));
            if (adapter is not null) result.Add(adapter);
        }
        return result;
    }

    private static AdapterInfo? FindAdapter(string interfaceId)
    {
        if (!Guid.TryParse(interfaceId, out Guid expected)) return null;
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(networkInterface => Guid.TryParse(networkInterface.Id, out Guid actual) && actual == expected)
            .Select(networkInterface => CreateAdapterInfoForRestore(networkInterface, expected.ToString("B")))
            .FirstOrDefault();
    }

    private static AdapterInfo? TryCreateActiveAdapterInfo(NetworkInterface networkInterface, string interfaceId)
    {
        IPInterfaceProperties properties;
        try { properties = networkInterface.GetIPProperties(); }
        catch (NetworkInformationException) { return null; }
        if (!properties.UnicastAddresses.Any(item =>
                (item.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
                 item.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) &&
                !IPAddress.IsLoopback(item.Address)))
            return null;

        bool ipv4KeyExists = InterfaceRegistryKeyExists(interfaceId, ipv6: false);
        bool ipv6KeyExists = InterfaceRegistryKeyExists(interfaceId, ipv6: true);
        if (!ipv4KeyExists && !ipv6KeyExists) return null;

        int? ipv4Index = null;
        int? ipv6Index = null;
        try { ipv4Index = properties.GetIPv4Properties()?.Index; } catch (NetworkInformationException) { }
        try { ipv6Index = properties.GetIPv6Properties()?.Index; } catch (NetworkInformationException) { }
        bool ipv4Supported = ipv4KeyExists && ipv4Index is not null;
        bool ipv6Supported = ipv6KeyExists && ipv6Index is not null;
        if (!ipv4Supported && !ipv6Supported) return null;
        return new AdapterInfo(interfaceId, ipv4Index, ipv6Index, ipv4Supported, ipv6Supported);
    }

    private static AdapterInfo CreateAdapterInfoForRestore(NetworkInterface networkInterface, string interfaceId)
    {
        int? ipv4Index = null;
        int? ipv6Index = null;
        try
        {
            IPInterfaceProperties properties = networkInterface.GetIPProperties();
            try { ipv4Index = properties.GetIPv4Properties()?.Index; } catch (NetworkInformationException) { }
            try { ipv6Index = properties.GetIPv6Properties()?.Index; } catch (NetworkInformationException) { }
        }
        catch (NetworkInformationException) { }
        return new AdapterInfo(interfaceId, ipv4Index, ipv6Index,
            InterfaceRegistryKeyExists(interfaceId, ipv6: false) && ipv4Index is not null,
            InterfaceRegistryKeyExists(interfaceId, ipv6: true) && ipv6Index is not null);
    }

    private static bool InterfaceRegistryKeyExists(string interfaceId, bool ipv6)
    {
        if (!Guid.TryParse(interfaceId, out Guid id)) return false;
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey((ipv6 ? Tcpip6Interfaces : Tcpip4Interfaces) + id.ToString("B"));
        return key is not null;
    }

    private static string FormatIndex(int index)
    {
        if (index <= 0) throw new InvalidOperationException("Windows devolvió un índice de interfaz no válido.");
        return index.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record AdapterInfo(string InterfaceId, int? IPv4Index, int? IPv6Index, bool IPv4Supported, bool IPv6Supported);

    private sealed class SnapshotDocument
    {
        public SnapshotDocument() { }
        public int Version { get; set; } = SnapshotVersion;
        public List<AdapterDnsSnapshot> Adapters { get; set; } = new();
    }

    private sealed class AdapterDnsSnapshot
    {
        public AdapterDnsSnapshot() { }
        public string InterfaceId { get; set; } = "";
        public bool IPv4Supported { get; set; }
        public bool IPv6Supported { get; set; }
        public string[] IPv4NameServers { get; set; } = Array.Empty<string>();
        public string[] IPv6NameServers { get; set; } = Array.Empty<string>();
    }
}
