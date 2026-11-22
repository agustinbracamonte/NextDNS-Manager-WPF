using Microsoft.Win32;
using NextDNSManager.Shared;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace NextDNSManager.Service;

internal sealed class NextDnsEngine
{
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly DnsConfiguration _dns = new();
    private static string InstalledCli => @"C:\Program Files\nextdns\nextdns.exe";

    internal async Task<BrokerResponse> ExecuteAsync(BrokerRequest request)
    {
        ValidateRequest(request);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _operations.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (request.Operation == "activate") await EnableAsync(request, deadline.Token).ConfigureAwait(false);
            else if (request.Operation == "deactivate")
            {
                SetWanted(false);
                // Restore network configuration even if the resolver crashed.
                // Disabling DNS never runs the CLI's reset-to-DHCP command.
                await _dns.DisableAsync(deadline.Token).ConfigureAwait(false);
            }
            return Status();
        }
        finally { _operations.Release(); }
    }

    internal static void ValidateRequest(BrokerRequest request)
    {
        if (request.Version != BrokerProtocol.Version) throw new ArgumentException("La versión de la interfaz no coincide. Repara la instalación.");
        if (request.Operation is not ("status" or "activate" or "deactivate")) throw new ArgumentException("Operación no permitida.");
        if (request.Operation == "activate")
        {
            if (request.ProfileId == null || !Regex.IsMatch(request.ProfileId, "\\A[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant))
                throw new ArgumentException("El ID de perfil debe contener exactamente seis caracteres hexadecimales.");
        }
        else if (request.ProfileId != null || request.ReportClientInfo || request.SetupRouter)
            throw new ArgumentException("La operación no acepta parámetros de configuración.");
    }

    private async Task EnableAsync(BrokerRequest request, CancellationToken token)
    {
        if (ServiceExists("NextDNSService"))
            throw new InvalidOperationException("El cliente oficial de NextDNS sigue instalado. Retíralo desde Windows antes de activar este administrador.");
        bool exists = ServiceExists("nextdns");
        if (exists) ValidateOwnedCli();

        string profile = request.ProfileId!.ToLowerInvariant();
        string signature = $"{profile}:{request.ReportClientInfo}:{request.SetupRouter}";
        bool changed = ReadSetting("Configuration") != signature;
        if (exists && !changed && IsCliRunning() && _dns.IsActive)
        {
            SetWanted(true);
            return;
        }
        if (_dns.HasSnapshot && (changed || !IsCliRunning()))
            await _dns.DisableAsync(token).ConfigureAwait(false);

        string[] options = {
            "-profile=" + profile,
            "-auto-activate=false",
            "-report-client-info=" + request.ReportClientInfo.ToString().ToLowerInvariant(),
            // Windows is a desktop host. The checkbox controls the listener;
            // router-specific platform integrations must not alter DNS here.
            "-setup-router=false",
            "-listen=" + (request.SetupRouter ? "0.0.0.0:53" : "127.0.0.1:53"),
            "-listen=" + (request.SetupRouter ? "[::]:53" : "[::1]:53")
        };
        if (!exists)
        {
            // Claim the exact fixed location before provisioning. A failed
            // install can be repaired, but an existing foreign service is never
            // adopted or stopped just because its name happens to match.
            BrokerPolicy.OwnsCli = true;
            await RunCliAsync(new[] { "install" }.Concat(options), token).ConfigureAwait(false);
            ValidateOwnedCli();
        }
        else if (changed)
        {
            await RunCliAsync(new[] { "config", "set" }.Concat(options), token).ConfigureAwait(false);
            if (IsCliRunning()) await RunCliAsync(new[] { "restart" }, token).ConfigureAwait(false);
            else await RunCliAsync(new[] { "start" }, token).ConfigureAwait(false);
        }
        else if (!IsCliRunning()) await RunCliAsync(new[] { "start" }, token).ConfigureAwait(false);

        while (!IsCliRunning()) { await Task.Delay(150, token).ConfigureAwait(false); }
        ValidateOwnedCli();
        await VerifyResolverAsync(token).ConfigureAwait(false);
        BrokerPolicy.SavedProfile = profile;
        WriteSetting("Configuration", signature);
        SetWanted(true);
        try
        {
            await _dns.EnableAsync(token).ConfigureAwait(false);
            SetWanted(true);
        }
        catch
        {
            using var restoreDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _dns.DisableAsync(restoreDeadline.Token).ConfigureAwait(false);
            SetWanted(false);
            throw;
        }
    }

    private BrokerResponse Status()
    {
        bool active = false;
        if (ServiceExists("nextdns") && BrokerPolicy.OwnsCli)
        {
            ValidateOwnedCli();
            active = IsCliRunning() && _dns.IsActive;
        }
        string[] options = (ReadSetting("Configuration") ?? "").Split(':');
        return new BrokerResponse
        {
            Success = true, Active = active,
            State = active ? "active" : (_dns.HasSnapshot ? "attention" : "inactive"),
            ProfileId = active ? BrokerPolicy.SavedProfile : null,
            ReportClientInfo = options.Length == 3 && bool.TryParse(options[1], out bool report) && report,
            SetupRouter = options.Length == 3 && bool.TryParse(options[2], out bool router) && router
        };
    }

    internal async Task ReconcileAsync()
    {
        if (ReadSetting("Enabled") != "true")
        {
            if (_dns.HasSnapshot)
            {
                try { await ExecuteAsync(new BrokerRequest { Operation = "deactivate" }).ConfigureAwait(false); }
                catch (Exception ex) { ServiceLog.Write("Restauración DNS pendiente: " + ex.Message); }
            }
            return;
        }
        string? config = ReadSetting("Configuration");
        string[] parts = config?.Split(':') ?? Array.Empty<string>();
        if (parts.Length != 3 || !bool.TryParse(parts[1], out bool report) || !bool.TryParse(parts[2], out bool router)) return;
        try
        {
            await ExecuteAsync(new BrokerRequest { Operation = "activate", ProfileId = parts[0], ReportClientInfo = report, SetupRouter = router }).ConfigureAwait(false);
        }
        catch (Exception ex) { ServiceLog.Write("Reconciliación DNS: " + ex.Message); }
    }

    internal async Task RemoveOwnedCliAsync(CancellationToken token)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _dns.DisableAsync(token).ConfigureAwait(false);
            SetWanted(false);
            if (!ServiceExists("nextdns") || !BrokerPolicy.OwnsCli) return;
            ValidateOwnedCli();
            // CLI uninstall calls ResetDNS (DHCP) unconditionally on Windows.
            // Remove only our validated SCM registration, after restoring DNS.
            using (var service = new ServiceController("nextdns"))
            {
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }
            }
            await ProcessRunner.RunAsync(Path.Combine(Environment.SystemDirectory, "sc.exe"), new[] { "delete", "nextdns" }, token).ConfigureAwait(false);
            while (ServiceExists("nextdns")) await Task.Delay(150, token).ConfigureAwait(false);
            BrokerPolicy.OwnsCli = false;
        }
        finally { _operations.Release(); }
    }

    private static async Task<string> RunCliAsync(IEnumerable<string> args, CancellationToken token)
    {
        using FileStream file = File.Open(BrokerPolicy.CliExecutable, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(BrokerPolicy.CliHash, StringComparison.Ordinal))
            throw new InvalidOperationException("El núcleo DNS no coincide con la versión verificada. Repara la instalación.");
        return await ProcessRunner.RunAsync(BrokerPolicy.CliExecutable, args, token).ConfigureAwait(false);
    }

    internal static bool ServiceExists(string name)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key != null;
    }

    private static bool IsCliRunning()
    {
        if (!ServiceExists("nextdns")) return false;
        using var service = new ServiceController("nextdns");
        return service.Status == ServiceControllerStatus.Running;
    }

    private static void ValidateOwnedCli()
    {
        if (!BrokerPolicy.OwnsCli) throw new InvalidOperationException("Hay otro servicio CLI de NextDNS instalado. No se modificará desde este administrador.");
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\nextdns");
        string image = key?.GetValue("ImagePath") as string ?? "";
        if (!Regex.IsMatch(image, "\\A\\s*\"?" + Regex.Escape(InstalledCli) + "\"?\\s+run\\s*\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidOperationException("El servicio nextdns apunta a un ejecutable o configuración ajenos. Se rechazó la operación.");
        using FileStream file = File.Open(InstalledCli, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(file)) != BrokerPolicy.CliHash)
            throw new InvalidOperationException("El núcleo del servicio instalado no coincide con la versión verificada.");
    }

    private static async Task VerifyResolverAsync(CancellationToken token)
    {
        // A running SCM state does not prove that DNS is responding. Send a
        // normal query directly to loopback before changing adapter settings.
        byte[] query = Convert.FromHexString("123401000001000000000000076E657874646E7302696F0000010001");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                using var udp = new System.Net.Sockets.UdpClient(System.Net.Sockets.AddressFamily.InterNetwork);
                udp.Connect(System.Net.IPAddress.Loopback, 53);
                await udp.SendAsync(query, deadline.Token).ConfigureAwait(false);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                var response = await udp.ReceiveAsync(attempt.Token).ConfigureAwait(false);
                if (response.Buffer.Length >= 12 && response.Buffer[0] == 0x12 && response.Buffer[1] == 0x34 &&
                    (response.Buffer[2] & 0x80) != 0 && (response.Buffer[3] & 0x0F) == 0) return;
            }
            catch (System.Net.Sockets.SocketException) { }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
            await Task.Delay(150, deadline.Token).ConfigureAwait(false);
        }
    }

    private static void SetWanted(bool enabled) => WriteSetting("Enabled", enabled ? "true" : "false");
    private static string? ReadSetting(string name) { using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath); return key?.GetValue(name) as string; }
    private static void WriteSetting(string name, string value) { using RegistryKey key = Registry.LocalMachine.CreateSubKey(BrokerPolicy.RegistryPath, true); key.SetValue(name, value); }
}
