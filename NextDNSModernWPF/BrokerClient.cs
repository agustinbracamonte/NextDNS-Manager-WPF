using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NextDNSManager.Shared;

namespace NextDNSModernWPF;

/// <summary>
/// Connects the interactive UI to the elevated broker. The connected pipe's
/// server is authenticated before any request bytes are sent.
/// </summary>
internal sealed class BrokerClient
{
    // The service allows 30 seconds for apply and a separate 30-second
    // rollback window. Leave time for pipe I/O and status serialization.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ServiceStatusProcessInfoLevel = 0;
    private const uint ServiceRunning = 4;

    internal async Task<BrokerResponse> SendAsync(BrokerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        CancellationToken token = linked.Token;

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                BrokerProtocol.PipeName,
                PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.Synchronize,
                PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification,
                HandleInheritability.None);

            try { await pipe.ConnectAsync(5000, token).ConfigureAwait(false); }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException("El servicio auxiliar no está disponible. Repara la instalación de NextDNS Manager.", ex);
            }

            // Do not disclose the request (including profile IDs or settings)
            // until the server process is verified as the installed LocalSystem service.
            VerifyServerIdentity(pipe);

            await BrokerProtocol.WriteAsync(pipe, request, token).ConfigureAwait(false);
            BrokerResponse response = await BrokerProtocol.ReadAsync<BrokerResponse>(pipe, token).ConfigureAwait(false);
            if (response.Version != BrokerProtocol.Version)
                throw new InvalidDataException("La versión del protocolo del broker no coincide con esta aplicación.");

            return response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("El broker de NextDNS no respondió en 60 segundos. Comprueba que el servicio esté iniciado o repáralo desde el instalador.", ex);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("No se pudo acceder al broker de NextDNS. Repara la instalación desde el instalador y vuelve a intentarlo.", ex);
        }
        catch (IOException ex) when (IsPipeUnavailable(ex))
        {
            throw new InvalidOperationException("No se pudo conectar con el broker de NextDNS. Repara la instalación desde el instalador y vuelve a intentarlo.", ex);
        }
    }

    private static bool IsPipeUnavailable(IOException exception)
    {
        const int ErrorFileNotFound = 2;
        const int ErrorPathNotFound = 3;
        const int ErrorPipeBusy = 231;
        const int ErrorPipeNotConnected = 233;
        int errorCode = exception.HResult & 0xFFFF;
        return errorCode is ErrorFileNotFound or ErrorPathNotFound or ErrorPipeBusy or ErrorPipeNotConnected;
    }

    private static void VerifyServerIdentity(NamedPipeClientStream pipe)
    {
        if (!NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId) || processId == 0)
            throw new InvalidOperationException("No se pudo verificar el proceso del broker. Repara la instalación desde el instalador.");

        // Query the service's configured executable/account and current PID via
        // SCM. A normal user can query these properties without SeDebugPrivilege,
        // unlike the token of a LocalSystem process.
        using SafeScmHandle scm = NativeMethods.OpenSCManager(null, null, ScManagerConnect);
        if (scm.IsInvalid)
            throw new InvalidOperationException("No se pudo verificar el servicio del broker. Repara la instalación desde el instalador.", NativeMethods.LastError());

        using SafeServiceHandle service = NativeMethods.OpenService(
            scm,
            BrokerProtocol.ServiceName,
            ServiceQueryConfig | ServiceQueryStatus);
        if (service.IsInvalid)
            throw new InvalidOperationException("No se encontró el servicio broker de NextDNS. Repara la instalación desde el instalador.", NativeMethods.LastError());

        ServiceStatusProcess serviceStatus = GetServiceStatus(service);
        if (serviceStatus.CurrentState != ServiceRunning || serviceStatus.ProcessId != processId)
            throw new InvalidOperationException("La conexión no pertenece al proceso en ejecución del servicio broker. Repara la instalación desde el instalador.");

        ServiceConfiguration configuration = GetServiceConfiguration(service);
        if (!string.Equals(configuration.StartName, "LocalSystem", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El broker no está configurado para ejecutarse como LocalSystem. Repara la instalación desde el instalador.");

        string expectedPath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NextDNS Manager",
            "service",
            "NextDNSManager.Service.exe"));
        Match configuredImage = Regex.Match(
            configuration.BinaryPath,
            @"^\s*""(?<path>[^""]+\.exe)""(?:\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!configuredImage.Success || !SamePath(configuredImage.Groups["path"].Value, expectedPath))
            throw new InvalidOperationException("La ruta configurada para el broker es inesperada. Repara la instalación desde el instalador.");

        using SafeNativeHandle process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            // Standard desktop tokens can query SCM but cannot necessarily open
            // a SYSTEM process. The kernel-reported pipe PID already matches
            // SCM's live PID, exact executable and LocalSystem account above.
            // SCM retains the process handle while that PID is registered, so
            // an unrelated user process cannot reuse it while status is Running.
            if (Marshal.GetLastWin32Error() == 5) return;
            throw new InvalidOperationException("No se pudo inspeccionar el proceso del broker. Repara la instalación desde el instalador.", NativeMethods.LastError());
        }

        string actualPath = GetProcessPath(process);
        if (!SamePath(actualPath, expectedPath))
            throw new InvalidOperationException("El servidor de la conexión no es el broker instalado. Repara la instalación desde el instalador.");
    }

    private static bool SamePath(string path, string expectedPath)
    {
        try
        {
            return string.Equals(Path.GetFullPath(path), expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetProcessPath(SafeNativeHandle process)
    {
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        if (!NativeMethods.QueryFullProcessImageName(process, 0, path, ref length))
            throw new InvalidOperationException("No se pudo leer la ruta del proceso del broker.", NativeMethods.LastError());
        return path.ToString();
    }

    private static ServiceStatusProcess GetServiceStatus(SafeServiceHandle service)
    {
        int statusSize = Marshal.SizeOf<ServiceStatusProcess>();
        IntPtr buffer = Marshal.AllocHGlobal(statusSize);
        try
        {
            if (!NativeMethods.QueryServiceStatusEx(service, ServiceStatusProcessInfoLevel, buffer, (uint)statusSize, out _))
                throw new InvalidOperationException("No se pudo leer el estado del servicio broker.", NativeMethods.LastError());

            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ServiceConfiguration GetServiceConfiguration(SafeServiceHandle service)
    {
        _ = NativeMethods.QueryServiceConfig(service, IntPtr.Zero, 0, out uint requiredLength);
        if (requiredLength == 0)
            throw new InvalidOperationException("No se pudo leer la configuración del servicio broker.", NativeMethods.LastError());

        IntPtr buffer = Marshal.AllocHGlobal(checked((int)requiredLength));
        try
        {
            if (!NativeMethods.QueryServiceConfig(service, buffer, requiredLength, out _))
                throw new InvalidOperationException("No se pudo leer la configuración del servicio broker.", NativeMethods.LastError());

            QueryServiceConfig config = Marshal.PtrToStructure<QueryServiceConfig>(buffer);
            return new ServiceConfiguration(
                Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty,
                Marshal.PtrToStringUni(config.ServiceStartName) ?? string.Empty);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private readonly record struct ServiceConfiguration(string BinaryPath, string StartName);

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

    private sealed class SafeNativeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeNativeHandle() : base(true) { }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private sealed class SafeScmHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeScmHandle() : base(true) { }

        protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeServiceHandle() : base(true) { }

        protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeNativeHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(SafeNativeHandle process, uint flags, StringBuilder imageName, ref uint size);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeScmHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeServiceHandle OpenService(SafeScmHandle manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceStatusEx(SafeServiceHandle service, int infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryServiceConfig(SafeServiceHandle service, IntPtr config, uint bufferSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        internal static Exception LastError() => new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}
