using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NextDNSManager.Service;

internal static class BrokerPolicy
{
    internal const string RegistryPath = @"SOFTWARE\NextDNSManager";
    internal const string CliHash = "5A603C759BC4A854A6AAE46CADB91AE00A95BA5BB0591B985F49A95597856B8E";
    internal static string InstallDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NextDNS Manager");
    internal static string ServiceExecutable => Path.Combine(InstallDirectory, "service", "NextDNSManager.Service.exe");
    internal static string CliExecutable => Path.Combine(InstallDirectory, "nextdns.exe");

    internal static void RequireTrustedLocation()
    {
        if (!string.Equals(Path.GetFullPath(Environment.ProcessPath!), ServiceExecutable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El servicio auxiliar debe estar instalado en Program Files. Ejecuta el instalador de NextDNS Manager.");
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(ServiceExecutable)!); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("La carpeta del servicio no puede ser un enlace o junction.");
    }

    internal static void RequireAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("La instalación del servicio auxiliar requiere permisos de administrador una sola vez.");
    }

    internal static string ControllerSid
    {
        get
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            string value = key?.GetValue("ControllerSid") as string ?? throw new InvalidOperationException("Falta el usuario autorizado. Repara la instalación.");
            _ = new SecurityIdentifier(value);
            return value;
        }
    }

    internal static void ConfigureController()
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath, true);
        if (key.GetValue("ControllerSid") is not string)
        {
            // The session belongs to the desktop user even if Setup was elevated
            // using credentials from another administrator account.
            string user = SessionString(5);
            string domain = SessionString(7);
            if (string.IsNullOrWhiteSpace(user)) throw new InvalidOperationException("No se identificó al usuario de esta sesión de Windows.");
            var account = new NTAccount(domain, user);
            string sid = ((SecurityIdentifier)account.Translate(typeof(SecurityIdentifier))).Value;
            key.SetValue("ControllerSid", sid, RegistryValueKind.String);
        }
        var security = new RegistrySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid, null), RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(ControllerSid), RegistryRights.ReadKey, AccessControlType.Allow));
        key.SetAccessControl(security);
    }

    internal static bool OwnsCli
    {
        get { using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath); return (int?)key?.GetValue("OwnsCli") == 1; }
        set { using RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath, true); key.SetValue("OwnsCli", value ? 1 : 0); }
    }

    internal static string? SavedProfile
    {
        get { using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryPath); return key?.GetValue("ProfileId") as string; }
        set { using RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryPath, true); key.SetValue("ProfileId", value ?? ""); }
    }

    private static string SessionString(int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, Process.GetCurrentProcess().SessionId, infoClass, out IntPtr data, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try { return Marshal.PtrToStringUni(data) ?? ""; }
        finally { WTSFreeMemory(data); }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
