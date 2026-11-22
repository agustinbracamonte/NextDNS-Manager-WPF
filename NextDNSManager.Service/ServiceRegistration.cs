using Microsoft.Win32;
using NextDNSManager.Shared;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;

namespace NextDNSManager.Service;

internal static class ServiceRegistration
{
    internal static void Install()
    {
        BrokerPolicy.ConfigureController();
        ProtectInstallTree(new DirectoryInfo(BrokerPolicy.InstallDirectory), true);
        bool exists = NextDnsEngine.ServiceExists(BrokerProtocol.ServiceName);
        if (exists) ValidateRegistration();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Sc(new[] { exists ? "config" : "create", BrokerProtocol.ServiceName,
            "binPath=", '"' + BrokerPolicy.ServiceExecutable + '"',
            "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "NextDNS Manager Control" }, deadline.Token);
        Sc(new[] { "description", BrokerProtocol.ServiceName, "Control local de NextDNS para la interfaz de escritorio." }, deadline.Token);
        Sc(new[] { "failure", BrokerProtocol.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/30000" }, deadline.Token);
        if (!EventLog.SourceExists(BrokerProtocol.ServiceName)) EventLog.CreateEventSource(BrokerProtocol.ServiceName, "Application");
        using var service = new ServiceController(BrokerProtocol.ServiceName);
        if (service.Status != ServiceControllerStatus.Running) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        // SCM reaches Running only after OnStart has successfully opened all
        // secured pipe listeners. No DNS changes occur during installation.
    }

    internal static void Stop()
    {
        if (!NextDnsEngine.ServiceExists(BrokerProtocol.ServiceName)) return;
        ValidateRegistration();
        using (var service = new ServiceController(BrokerProtocol.ServiceName))
        {
            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(65));
            }
        }
        // A Stopped SCM state can precede the final process exit. Do not let
        // Setup replace its runtime DLLs until the old host actually exits.
        foreach (Process process in Process.GetProcessesByName("NextDNSManager.Service"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                if (!process.WaitForExit(10000)) throw new InvalidOperationException("El servicio auxiliar no terminó. Se conservaron sus archivos.");
            }
        }
    }

    internal static void Remove()
    {
        Stop();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        new NextDnsEngine().RemoveOwnedCliAsync(deadline.Token).GetAwaiter().GetResult();
        if (!NextDnsEngine.ServiceExists(BrokerProtocol.ServiceName)) return;
        Sc(new[] { "delete", BrokerProtocol.ServiceName }, deadline.Token);
        while (NextDnsEngine.ServiceExists(BrokerProtocol.ServiceName))
            Task.Delay(150, deadline.Token).GetAwaiter().GetResult();
        // Keep the read-only user policy and DNS recovery data if something
        // external removed an adapter: the engine refuses to discard a snapshot
        // before its restoration has been confirmed.
    }

    private static void ValidateRegistration()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + BrokerProtocol.ServiceName);
        string image = key?.GetValue("ImagePath") as string ?? "";
        if (!string.Equals(image.Trim().Trim('"'), BrokerPolicy.ServiceExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Existe un servicio auxiliar con una ruta distinta. Se rechazó modificarlo.");
    }

    private static void Sc(IEnumerable<string> arguments, CancellationToken token) =>
        ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments, token).GetAwaiter().GetResult();

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void ProtectInstallTree(DirectoryInfo directory, bool root)
    {
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("No se permiten enlaces en la carpeta del servicio.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(root, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        if (root)
        {
            foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        directory.SetAccessControl(security);
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("No se permiten enlaces en la carpeta del servicio.");
            if (entry is DirectoryInfo child) ProtectInstallTree(child, false);
            else
            {
                var fileSecurity = new FileSecurity();
                fileSecurity.SetAccessRuleProtection(false, false);
                fileSecurity.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                ((FileInfo)entry).SetAccessControl(fileSecurity);
            }
        }
    }
}
