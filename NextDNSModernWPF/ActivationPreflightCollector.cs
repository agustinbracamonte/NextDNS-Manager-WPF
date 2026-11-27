using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NextDNSManager.Shared;

namespace NextDNSModernWPF;

/// <summary>
/// Performs bounded, read-only local inspection away from the UI thread. It
/// never starts/stops services, binds sockets, sends DNS queries, or logs data.
/// </summary>
internal static class ActivationPreflightCollector
{
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorServiceDoesNotExist = 1060;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ServiceStatusProcessInfoLevel = 0;
    private const int TcpTableOwnerPidListener = 3;
    private const int UdpTableOwnerPid = 1;
    private const string OwnedCliSha256 = "5A603C759BC4A854A6AAE46CADB91AE00A95BA5BB0591B985F49A95597856B8E";

    internal static Task<ActivationPreflightReport?> CaptureAsync() => Task.Run(CaptureSafely);

    private static ActivationPreflightReport? CaptureSafely()
    {
        try { return Capture(); }
        catch
        {
            // A diagnostic must never replace or delay the activation error.
            return null;
        }
    }

    private static ActivationPreflightReport Capture()
    {
        (DnsPortTable completed, DnsPortListener[] listeners) = CapturePort53Listeners();
        ServiceQueryResult broker = QueryService(BrokerProtocol.ServiceName, readConfiguration: false);
        ServiceQueryResult engine = QueryService("nextdns", readConfiguration: true);
        ServiceQueryResult official = QueryService("NextDNSService", readConfiguration: false);
        uint? trustedEnginePid = TryGetTrustedNextDnsProcessId(engine);
        PreflightFileStatus cliStatus = ReadCliFileStatus();
        (bool adapterInspectionCompleted, bool hasOperationalAdapter) = ReadNetworkAdapterState();

        return ActivationPreflightAnalyzer.Analyze(new ActivationPreflightSnapshot(
            completed,
            listeners,
            trustedEnginePid,
            broker.Status,
            engine.Status,
            official.Status,
            cliStatus,
            adapterInspectionCompleted,
            hasOperationalAdapter));
    }

    private static (DnsPortTable Completed, DnsPortListener[] Listeners) CapturePort53Listeners()
    {
        var completed = DnsPortTable.None;
        var listeners = new List<DnsPortListener>();
        ReadTcpTable(AddressFamily.InterNetwork, DnsPortTable.TcpIPv4, listeners, ref completed);
        ReadTcpTable(AddressFamily.InterNetworkV6, DnsPortTable.TcpIPv6, listeners, ref completed);
        ReadUdpTable(AddressFamily.InterNetwork, DnsPortTable.UdpIPv4, listeners, ref completed);
        ReadUdpTable(AddressFamily.InterNetworkV6, DnsPortTable.UdpIPv6, listeners, ref completed);
        return (completed, listeners.ToArray());
    }

    private static void ReadTcpTable(
        AddressFamily family,
        DnsPortTable tableFlag,
        List<DnsPortListener> listeners,
        ref DnsPortTable completed)
    {
        IntPtr buffer = IntPtr.Zero;
        DnsTcpRowLayout layout = DnsPortTableLayout.GetTcpRowLayout(family);
        try
        {
            int size = 0;
            uint result = NativeMethods.GetExtendedTcpTable(
                IntPtr.Zero, ref size, true, (int)family, TcpTableOwnerPidListener, 0);
            if (result == 0 && size == 0)
            {
                completed |= tableFlag;
                return;
            }
            if (result != ErrorInsufficientBuffer || size <= sizeof(uint)) return;

            buffer = Marshal.AllocHGlobal(size);
            result = NativeMethods.GetExtendedTcpTable(
                buffer, ref size, true, (int)family, TcpTableOwnerPidListener, 0);
            if (result != 0) return;

            int count = Marshal.ReadInt32(buffer);
            int rowSize = layout.RowSize;
            if (!TryReadRowCount(count, size, rowSize)) return;

            for (int index = 0; index < count; index++)
            {
                IntPtr row = IntPtr.Add(buffer, sizeof(uint) + index * rowSize);
                IPAddress address = ReadAddress(row, layout.LocalAddressOffset, family);
                int port = ReadNetworkPort(Marshal.ReadInt32(row, layout.LocalPortOffset));
                if (port == ActivationPreflightAnalyzer.DnsPort)
                {
                    uint processId = unchecked((uint)Marshal.ReadInt32(row, layout.ProcessIdOffset));
                    listeners.Add(new DnsPortListener(DnsTransportProtocol.Tcp, address, port, processId));
                }
            }

            completed |= tableFlag;
        }
        catch
        {
            // Partial/denied inspection is represented as indeterminate.
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ReadUdpTable(
        AddressFamily family,
        DnsPortTable tableFlag,
        List<DnsPortListener> listeners,
        ref DnsPortTable completed)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int size = 0;
            uint result = NativeMethods.GetExtendedUdpTable(
                IntPtr.Zero, ref size, true, (int)family, UdpTableOwnerPid, 0);
            if (result == 0 && size == 0)
            {
                completed |= tableFlag;
                return;
            }
            if (result != ErrorInsufficientBuffer || size <= sizeof(uint)) return;

            buffer = Marshal.AllocHGlobal(size);
            result = NativeMethods.GetExtendedUdpTable(
                buffer, ref size, true, (int)family, UdpTableOwnerPid, 0);
            if (result != 0) return;

            int count = Marshal.ReadInt32(buffer);
            int rowSize = family == AddressFamily.InterNetwork ? 12 : 28;
            if (!TryReadRowCount(count, size, rowSize)) return;

            for (int index = 0; index < count; index++)
            {
                IntPtr row = IntPtr.Add(buffer, sizeof(uint) + index * rowSize);
                int localAddressOffset = 0;
                int localPortOffset = family == AddressFamily.InterNetwork ? 4 : 20;
                int processIdOffset = family == AddressFamily.InterNetwork ? 8 : 24;
                IPAddress address = ReadAddress(row, localAddressOffset, family);
                int port = ReadNetworkPort(Marshal.ReadInt32(row, localPortOffset));
                if (port == ActivationPreflightAnalyzer.DnsPort)
                {
                    uint processId = unchecked((uint)Marshal.ReadInt32(row, processIdOffset));
                    listeners.Add(new DnsPortListener(DnsTransportProtocol.Udp, address, port, processId));
                }
            }

            completed |= tableFlag;
        }
        catch
        {
            // Partial/denied inspection is represented as indeterminate.
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryReadRowCount(int count, int bufferSize, int rowSize) =>
        count >= 0 && count <= 65536 && (long)sizeof(uint) + (long)count * rowSize <= bufferSize;

    private static IPAddress ReadAddress(IntPtr row, int addressOffset, AddressFamily family)
    {
        if (family == AddressFamily.InterNetwork)
        {
            uint address = unchecked((uint)Marshal.ReadInt32(row, addressOffset));
            return new IPAddress(BitConverter.GetBytes(address));
        }

        byte[] bytes = new byte[16];
        Marshal.Copy(IntPtr.Add(row, addressOffset), bytes, 0, bytes.Length);
        return new IPAddress(bytes);
    }

    private static int ReadNetworkPort(int value) =>
        unchecked((ushort)IPAddress.NetworkToHostOrder((short)(value & 0xffff)));

    private static ServiceQueryResult QueryService(string serviceName, bool readConfiguration)
    {
        try
        {
            using SafeScmHandle scm = NativeMethods.OpenSCManager(null, null, ScManagerConnect);
            if (scm.IsInvalid) return ServiceQueryResult.Unknown;

            uint desiredAccess = ServiceQueryStatus | (readConfiguration ? ServiceQueryConfig : 0);
            using SafeServiceHandle service = NativeMethods.OpenService(scm, serviceName, desiredAccess);
            if (service.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                return error == ErrorServiceDoesNotExist
                    ? new ServiceQueryResult(PreflightServiceStatus.NotInstalled, 0, null)
                    : ServiceQueryResult.Unknown;
            }

            int statusSize = Marshal.SizeOf<ServiceStatusProcess>();
            IntPtr statusBuffer = Marshal.AllocHGlobal(statusSize);
            try
            {
                if (!NativeMethods.QueryServiceStatusEx(
                    service, ServiceStatusProcessInfoLevel, statusBuffer, (uint)statusSize, out _))
                    return ServiceQueryResult.Unknown;

                ServiceStatusProcess status = Marshal.PtrToStructure<ServiceStatusProcess>(statusBuffer);
                string? binaryPath = readConfiguration ? QueryServiceBinaryPath(service) : null;
                PreflightServiceStatus mappedStatus = status.CurrentState switch
                {
                    4 => PreflightServiceStatus.Running,
                    1 => PreflightServiceStatus.Stopped,
                    2 or 3 or 5 or 6 or 7 => PreflightServiceStatus.Unknown,
                    _ => PreflightServiceStatus.Unknown
                };
                return new ServiceQueryResult(mappedStatus, status.ProcessId, binaryPath);
            }
            finally { Marshal.FreeHGlobal(statusBuffer); }
        }
        catch
        {
            return ServiceQueryResult.Unknown;
        }
    }

    private static string? QueryServiceBinaryPath(SafeServiceHandle service)
    {
        _ = NativeMethods.QueryServiceConfig(service, IntPtr.Zero, 0, out uint requiredLength);
        if (requiredLength == 0 || requiredLength > 1024 * 1024) return null;

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
        try
        {
            if (!NativeMethods.QueryServiceConfig(service, buffer, requiredLength, out _)) return null;
            QueryServiceConfig config = Marshal.PtrToStructure<QueryServiceConfig>(buffer);
            return Marshal.PtrToStringUni(config.BinaryPathName);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static uint? TryGetTrustedNextDnsProcessId(ServiceQueryResult service)
    {
        if (service.Status != PreflightServiceStatus.Running || service.ProcessId == 0 ||
            service.BinaryPath is null)
            return null;

        try
        {
            string expectedPath = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "nextdns",
                "nextdns.exe"));
            if (!IsExpectedServiceImage(service.BinaryPath, expectedPath)) return null;
            if (!string.Equals(GetProcessPath(service.ProcessId), expectedPath, StringComparison.OrdinalIgnoreCase))
                return null;

            FileAttributes attributes = File.GetAttributes(expectedPath);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return null;
            using FileStream file = File.Open(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string hash = Convert.ToHexString(SHA256.HashData(file));
            return string.Equals(hash, OwnedCliSha256, StringComparison.OrdinalIgnoreCase)
                ? service.ProcessId
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsExpectedServiceImage(string configuredImage, string expectedPath)
    {
        string quoted = $"\"{expectedPath}\" run";
        string unquoted = expectedPath + " run";
        return string.Equals(configuredImage.Trim(), quoted, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(configuredImage.Trim(), unquoted, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProcessPath(uint processId)
    {
        using SafeProcessHandle process = NativeMethods.OpenProcess(
            ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        if (!NativeMethods.QueryFullProcessImageName(process, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return Path.GetFullPath(path.ToString());
    }

    private static PreflightFileStatus ReadCliFileStatus()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NextDNS Manager",
            "nextdns.exe");
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                ? PreflightFileStatus.Present
                : PreflightFileStatus.Unknown;
        }
        catch (FileNotFoundException) { return PreflightFileStatus.Missing; }
        catch (DirectoryNotFoundException) { return PreflightFileStatus.Missing; }
        catch { return PreflightFileStatus.Unknown; }
    }

    private static (bool Completed, bool HasOperationalAdapter) ReadNetworkAdapterState()
    {
        try
        {
            bool hasActive = NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
                adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            return (true, hasActive);
        }
        catch
        {
            return (false, false);
        }
    }

    private sealed record ServiceQueryResult(
        PreflightServiceStatus Status,
        uint ProcessId,
        string? BinaryPath)
    {
        internal static ServiceQueryResult Unknown { get; } = new(PreflightServiceStatus.Unknown, 0, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
        internal uint ProcessId;
        internal uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct QueryServiceConfig
    {
        internal uint ServiceType;
        internal uint StartType;
        internal uint ErrorControl;
        internal IntPtr BinaryPathName;
        internal IntPtr LoadOrderGroup;
        internal uint TagId;
        internal IntPtr Dependencies;
        internal IntPtr ServiceStartName;
        internal IntPtr DisplayName;
    }

    private sealed class SafeScmHandle : SafeHandle
    {
        internal SafeScmHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
    }

    private sealed class SafeServiceHandle : SafeHandle
    {
        internal SafeServiceHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
    }

    private sealed class SafeProcessHandle : SafeHandle
    {
        internal SafeProcessHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedTcpTable(
            IntPtr tcpTable, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
            int addressFamily, int tableClass, uint reserved);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        internal static extern uint GetExtendedUdpTable(
            IntPtr udpTable, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
            int addressFamily, int tableClass, uint reserved);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeScmHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeServiceHandle OpenService(SafeScmHandle manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(
            SafeServiceHandle service, int infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig(
            SafeServiceHandle service, IntPtr config, uint bufferSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder imageName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}

internal static class ActivationPreflightFormatter
{
    internal static string FormatCaptureTimeout()
    {
        string lang = LocalizationManager.CurrentLanguage;
        return lang switch
        {
            "en" => "The local checks did not finish within five seconds; no preflight result is available.",
            "fr" => "Les vérifications locales n’ont pas terminé en cinq secondes ; aucun résultat de diagnostic n’est disponible.",
            "pt" => "As verificações locais não terminaram em cinco segundos; nenhum resultado de diagnóstico está disponível.",
            "de" => "Die lokalen Prüfungen waren nach fünf Sekunden nicht abgeschlossen; es liegt kein Vorabprüfungsergebnis vor.",
            _ => "Las comprobaciones locales no terminaron en cinco segundos; no hay un resultado de diagnóstico disponible."
        };
    }

    internal static string? Format(ActivationPreflightReport report)
    {
        string lang = LocalizationManager.CurrentLanguage;
        return ActivationPreflightAnalyzer.ChooseHint(report) switch
        {
            ActivationPreflightHint.PossibleDnsPortUse => L(lang,
                "Posible uso del puerto DNS local (TCP/UDP 53); no confirma causa ni propietario.",
                "Possible local DNS port use (TCP/UDP 53); this does not confirm a cause or owner.",
                "Utilisation possible du port DNS local (TCP/UDP 53) ; la cause et le propriétaire ne sont pas confirmés.",
                "Possível uso da porta DNS local (TCP/UDP 53); isso não confirma causa nem proprietário.",
                "Mögliche Nutzung des lokalen DNS-Ports (TCP/UDP 53); Ursache und Eigentümer sind unbestätigt."),
            ActivationPreflightHint.ManagerBrokerNotReady => L(lang,
                "El servicio auxiliar de NextDNS Manager no figura en ejecución; esto puede influir en la activación.",
                "The NextDNS Manager broker service is not running; this may affect activation.",
                "Le service auxiliaire NextDNS Manager n’est pas en cours d’exécution ; cela peut affecter l’activation.",
                "O serviço auxiliar do NextDNS Manager não está em execução; isso pode afetar a ativação.",
                "Der NextDNS-Manager-Hilfsdienst läuft nicht; dies kann die Aktivierung beeinflussen."),
            ActivationPreflightHint.NextDnsServiceNotReady => L(lang,
                "El servicio local de NextDNS CLI no figura en ejecución; esto puede influir en la activación.",
                "The local NextDNS CLI service is not running; this may affect activation.",
                "Le service CLI NextDNS local n’est pas en cours d’exécution ; cela peut affecter l’activation.",
                "O serviço CLI local do NextDNS não está em execução; isso pode afetar a ativação.",
                "Der lokale NextDNS-CLI-Dienst läuft nicht; dies kann die Aktivierung beeinflussen."),
            ActivationPreflightHint.NextDnsCliMissing => L(lang,
                "No se encontró el archivo local de NextDNS CLI; podría faltar un prerrequisito.",
                "The local NextDNS CLI file was not found; a prerequisite may be missing.",
                "Le fichier CLI NextDNS local est introuvable ; un prérequis peut manquer.",
                "O arquivo CLI local do NextDNS não foi encontrado; pode faltar um pré-requisito.",
                "Die lokale NextDNS-CLI-Datei wurde nicht gefunden; eine Voraussetzung könnte fehlen."),
            ActivationPreflightHint.OfficialServiceRegistered => L(lang,
                "Hay un registro local de servicio NextDNSService; podría requerir revisión, sin confirmar conflicto.",
                "A local NextDNSService registration is present; it may need review, but no conflict is confirmed.",
                "Un enregistrement local NextDNSService est présent ; il peut nécessiter une vérification, sans confirmer un conflit.",
                "Há um registro local do serviço NextDNSService; pode exigir revisão, sem confirmar conflito.",
                "Eine lokale NextDNSService-Registrierung ist vorhanden; sie kann geprüft werden, ein Konflikt ist nicht bestätigt."),
            ActivationPreflightHint.NoOperationalNetworkAdapter => L(lang,
                "No se detectó un adaptador de red activo; esto puede influir en la activación.",
                "No active network adapter was detected; this may affect activation.",
                "Aucun adaptateur réseau actif détecté ; cela peut affecter l’activation.",
                "Nenhum adaptador de rede ativo detectado; isso pode afetar a ativação.",
                "Kein aktiver Netzwerkadapter erkannt; dies kann die Aktivierung beeinflussen."),
            ActivationPreflightHint.NoLoopbackOrWildcardListenerObserved => L(lang,
                "No se observó listener local de bucle/comodín en TCP/UDP 53; drivers o filtros DNS no quedan descartados.",
                "No local loopback/wildcard TCP/UDP 53 listener was observed; DNS drivers or filters are not ruled out.",
                "Aucune écoute locale TCP/UDP 53 sur boucle locale/adresse générique ; les pilotes ou filtres DNS ne sont pas exclus.",
                "Nenhum listener local TCP/UDP 53 em loopback/curinga observado; drivers ou filtros DNS não são descartados.",
                "Kein lokaler Loopback-/Wildcard-Listener auf TCP/UDP 53 beobachtet; DNS-Treiber oder -Filter sind nicht ausgeschlossen."),
            _ => null
        };
    }

    private static string L(string lang, string es, string en, string fr, string pt, string de) => lang switch
    {
        "en" => en,
        "fr" => fr,
        "pt" => pt,
        "de" => de,
        _ => es
    };
}
