using Microsoft.Win32;
using NextDNSManager.Shared;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace NextDNSManager.Service;

internal sealed class NextDnsEngine
{
    private const string ProfileSwitchJournalValueName = "ProfileSwitchJournal";
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
            if (request.Operation != "status" && HasProfileSwitchJournal())
                await RecoverPendingProfileSwitchAsync(deadline.Token).ConfigureAwait(false);

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
        string? previousConfiguration = ReadSetting("Configuration");
        bool changed = previousConfiguration != signature;
        bool cliWasRunning = exists && IsCliRunning();
        bool wasActive = cliWasRunning && _dns.IsActive;
        if (exists && !changed && wasActive)
        {
            SetWanted(true);
            return;
        }

        if (wasActive && changed)
        {
            if (!TryParseConfiguration(previousConfiguration, out string previousProfile, out bool previousReport, out bool previousRouter))
                throw new InvalidOperationException("No se puede cambiar el perfil con seguridad porque la configuración activa anterior no es válida.");

            var actions = new ActiveProfileSwitchActions(
                this,
                request,
                signature,
                previousConfiguration!,
                previousProfile,
                previousReport,
                previousRouter,
                ProfileSwitchJournal.Create(previousConfiguration!, signature));
            await ProfileSwitchTransaction.ExecuteAsync(actions, token).ConfigureAwait(false);
            return;
        }

        await ApplyConfigurationAsync(request, exists, changed, signature, token).ConfigureAwait(false);
    }

    private async Task ApplyConfigurationAsync(
        BrokerRequest request,
        bool exists,
        bool changed,
        string signature,
        CancellationToken token)
    {
        string profile = request.ProfileId!.ToLowerInvariant();
        bool wantedBefore = ReadSetting("Enabled") == "true";
        if (_dns.HasSnapshot && (changed || !IsCliRunning()))
            await _dns.DisableAsync(token).ConfigureAwait(false);

        string[] options = BuildCliOptions(profile, request.ReportClientInfo, request.SetupRouter);
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
            else await EnsureCliRunningAsync(token).ConfigureAwait(false);
        }
        else if (!IsCliRunning()) await EnsureCliRunningAsync(token).ConfigureAwait(false);

        await WaitForCliRunningAsync(token).ConfigureAwait(false);
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
            try { await _dns.DisableAsync(restoreDeadline.Token).ConfigureAwait(false); }
            finally { SetWanted(wantedBefore); }
            throw;
        }
    }

    private static async Task EnsureCliRunningAsync(CancellationToken token)
    {
        using var service = new ServiceController("nextdns");
        await ServiceStartupCoordinator.EnsureRunningAsync(
            () => { service.Refresh(); return service.Status; },
            cancellation => RunCliAsync(new[] { "start" }, cancellation),
            cancellation => Task.Delay(200, cancellation), token).ConfigureAwait(false);
    }

    private static void MarkProfileSwitchRecoveryRequired(ProfileSwitchJournal journal)
    {
        // Preserve the durable journal and prior profile. ReconcileAsync uses
        // that record after a network change or service restart.
        var errors = new List<Exception>();
        try { WriteProfileSwitchJournal(journal); }
        catch (Exception error) { errors.Add(error); }
        try { SetWanted(false); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0)
            throw new AggregateException("No se pudo guardar el estado de recuperación del cambio de perfil.", errors);
    }

    private static async Task WaitForCliRunningAsync(CancellationToken token)
    {
        while (!IsCliRunning())
            await Task.Delay(150, token).ConfigureAwait(false);
    }

    private static string[] BuildCliOptions(string profile, bool reportClientInfo, bool setupRouter) =>
    [
        "-profile=" + profile,
        "-auto-activate=false",
        "-report-client-info=" + reportClientInfo.ToString().ToLowerInvariant(),
        // Windows is a desktop host. The checkbox controls the listener;
        // router-specific platform integrations must not alter DNS here.
        "-setup-router=false",
        "-listen=" + (setupRouter ? "0.0.0.0:53" : "127.0.0.1:53"),
        "-listen=" + (setupRouter ? "[::]:53" : "[::1]:53")
    ];

    private static bool TryParseConfiguration(string? signature, out string profile, out bool reportClientInfo, out bool setupRouter)
    {
        profile = string.Empty;
        reportClientInfo = false;
        setupRouter = false;
        if (string.IsNullOrWhiteSpace(signature)) return false;

        string[] parts = signature.Split(':');
        if (parts.Length != 3 ||
            !Regex.IsMatch(parts[0], "\\A[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant) ||
            !bool.TryParse(parts[1], out reportClientInfo) ||
            !bool.TryParse(parts[2], out setupRouter))
        {
            return false;
        }

        profile = parts[0].ToLowerInvariant();
        return true;
    }

    private sealed class ActiveProfileSwitchActions : IProfileSwitchActions
    {
        private readonly NextDnsEngine _engine;
        private readonly BrokerRequest _targetRequest;
        private readonly string _targetSignature;
        private readonly string _previousSignature;
        private readonly string _previousProfile;
        private readonly bool _previousReportClientInfo;
        private readonly bool _previousSetupRouter;
        private readonly ProfileSwitchJournal _journal;

        internal ActiveProfileSwitchActions(
            NextDnsEngine engine,
            BrokerRequest targetRequest,
            string targetSignature,
            string previousSignature,
            string previousProfile,
            bool previousReportClientInfo,
            bool previousSetupRouter,
            ProfileSwitchJournal journal)
        {
            _engine = engine;
            _targetRequest = targetRequest;
            _targetSignature = targetSignature;
            _previousSignature = previousSignature;
            _previousProfile = previousProfile;
            _previousReportClientInfo = previousReportClientInfo;
            _previousSetupRouter = previousSetupRouter;
            _journal = journal;
        }

        public Task BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteProfileSwitchJournal(_journal);
            return Task.CompletedTask;
        }

        public Task DisableDnsAsync(CancellationToken cancellationToken) =>
            _engine._dns.DisableAsync(cancellationToken);

        public Task SetTargetConfigurationAsync(CancellationToken cancellationToken) =>
            RunCliAsync(new[] { "config", "set" }.Concat(BuildCliOptions(
                _targetRequest.ProfileId!.ToLowerInvariant(),
                _targetRequest.ReportClientInfo,
                _targetRequest.SetupRouter)), cancellationToken);

        public async Task RestartTargetServiceAsync(CancellationToken cancellationToken)
        {
            if (IsCliRunning()) await RunCliAsync(new[] { "restart" }, cancellationToken).ConfigureAwait(false);
            else await EnsureCliRunningAsync(cancellationToken).ConfigureAwait(false);
            await WaitForCliRunningAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task VerifyTargetResolverAsync(CancellationToken cancellationToken)
        {
            ValidateOwnedCli();
            await VerifyResolverAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task EnableTargetDnsAsync(CancellationToken cancellationToken)
        {
            BrokerPolicy.SavedProfile = _targetRequest.ProfileId!.ToLowerInvariant();
            WriteSetting("Configuration", _targetSignature);
            SetWanted(true);
            await _engine._dns.EnableAsync(cancellationToken).ConfigureAwait(false);
            SetWanted(true);
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteProfileSwitchJournal(_journal with { Phase = ProfileSwitchJournal.CommittedPhase });
            try { ClearProfileSwitchJournal(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                ServiceLog.Write("Limpieza pendiente del cambio de perfil: " + error.Message);
            }
            return Task.CompletedTask;
        }

        public Task BeginRollbackAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteProfileSwitchJournal(_journal with { Phase = ProfileSwitchJournal.RollingBackPhase });
            return Task.CompletedTask;
        }

        public async Task DisableDnsForRollbackAsync(CancellationToken cancellationToken)
        {
            SetWanted(false);
            await _engine._dns.DisableAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task SetPreviousConfigurationAsync(CancellationToken cancellationToken)
        {
            ValidateOwnedCli();
            return RunCliAsync(new[] { "config", "set" }.Concat(BuildCliOptions(
                _previousProfile, _previousReportClientInfo, _previousSetupRouter)), cancellationToken);
        }

        public async Task RestartPreviousServiceAsync(CancellationToken cancellationToken)
        {
            if (IsCliRunning()) await RunCliAsync(new[] { "restart" }, cancellationToken).ConfigureAwait(false);
            else await EnsureCliRunningAsync(cancellationToken).ConfigureAwait(false);
            await WaitForCliRunningAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task VerifyPreviousResolverAsync(CancellationToken cancellationToken)
        {
            ValidateOwnedCli();
            await VerifyResolverAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task EnablePreviousDnsAsync(CancellationToken cancellationToken)
        {
            BrokerPolicy.SavedProfile = _previousProfile;
            WriteSetting("Configuration", _previousSignature);
            SetWanted(true);
            await _engine._dns.EnableAsync(cancellationToken).ConfigureAwait(false);
            SetWanted(true);
            if (!IsCliRunning() || !_engine._dns.IsActive)
                throw new InvalidOperationException("Windows no confirmó la restauración DNS del perfil anterior.");
        }

        public Task CompleteRollbackAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearProfileSwitchJournal();
            return Task.CompletedTask;
        }

        public void MarkRecoveryRequired() => MarkProfileSwitchRecoveryRequired(_journal);
    }

    private BrokerResponse Status()
    {
        if (HasProfileSwitchJournal())
        {
            ProfileSwitchJournal? journal = null;
            try { journal = ReadProfileSwitchJournal(); }
            catch (InvalidOperationException) { }
            string? priorConfiguration = journal?.PreviousConfiguration;
            string[] priorOptions = (priorConfiguration ?? "").Split(':');
            string? priorProfile = priorOptions.Length == 3 && Regex.IsMatch(priorOptions[0], "\\A[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant)
                ? priorOptions[0].ToLowerInvariant()
                : (Regex.IsMatch(BrokerPolicy.SavedProfile ?? "", "\\A[0-9a-fA-F]{6}\\z", RegexOptions.CultureInvariant)
                    ? BrokerPolicy.SavedProfile!.ToLowerInvariant()
                    : null);
            string[] displayedOptions = (priorConfiguration ?? ReadSetting("Configuration") ?? "").Split(':');
            return new BrokerResponse
            {
                Success = true,
                Active = false,
                State = "attention",
                ProfileId = priorProfile,
                ReportClientInfo = displayedOptions.Length == 3 && bool.TryParse(displayedOptions[1], out bool priorReport) && priorReport,
                SetupRouter = displayedOptions.Length == 3 && bool.TryParse(displayedOptions[2], out bool priorRouter) && priorRouter
            };
        }

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

    internal async Task<bool> ReconcileAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await _operations.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { ServiceLog.Write("Reconciliación en espera: " + ex.Message); return false; }

        try
        {
            // Read the recovery marker and desired configuration under the
            // same gate as activation. Otherwise a queued network event could
            // capture the old profile, wait for a switch, and reactivate stale
            // settings after the new profile has committed.
            if (HasProfileSwitchJournal())
            {
                await RecoverPendingProfileSwitchAsync(deadline.Token).ConfigureAwait(false);
                return true;
            }

            if (ReadSetting("Enabled") != "true")
            {
                SetWanted(false);
                if (_dns.HasSnapshot)
                    await _dns.DisableAsync(deadline.Token).ConfigureAwait(false);
                return true;
            }

            string? config = ReadSetting("Configuration");
            if (!TryParseConfiguration(config, out string profile, out bool report, out bool router)) return true;
            await EnableAsync(new BrokerRequest
            {
                Version = BrokerProtocol.Version,
                Operation = "activate",
                ProfileId = profile,
                ReportClientInfo = report,
                SetupRouter = router
            }, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) { ServiceLog.Write("Reconciliación DNS: " + ex.Message); return false; }
        finally { _operations.Release(); }
    }

    internal async Task RemoveOwnedCliAsync(CancellationToken token)
    {
        await _operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (HasProfileSwitchJournal())
                await RecoverPendingProfileSwitchAsync(token).ConfigureAwait(false);
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

    private async Task RecoverPendingProfileSwitchAsync(CancellationToken token)
    {
        ProfileSwitchJournal? journal;
        try { journal = ReadProfileSwitchJournal(); }
        catch (InvalidOperationException journalError)
        {
            SetWanted(false);
            try { await _dns.DisableAsync(token).ConfigureAwait(false); }
            catch (Exception dnsError) when (dnsError is not OutOfMemoryException)
            {
                throw new AggregateException(journalError, dnsError);
            }
            throw;
        }
        if (journal is null) return;

        if (!journal.RequiresRollback)
        {
            ClearProfileSwitchJournal();
            return;
        }

        if (!TryParseConfiguration(journal.PreviousConfiguration, out string previousProfile, out bool previousReport, out bool previousRouter) ||
            !TryParseConfiguration(journal.TargetConfiguration, out string targetProfile, out bool targetReport, out bool targetRouter))
            throw new InvalidOperationException("El journal del cambio de perfil no contiene una configuración válida; se conservó para revisión.");

        var targetRequest = new BrokerRequest
        {
            Version = BrokerProtocol.Version,
            Operation = "activate",
            ProfileId = targetProfile,
            ReportClientInfo = targetReport,
            SetupRouter = targetRouter
        };
        var actions = new ActiveProfileSwitchActions(
            this,
            targetRequest,
            journal.TargetConfiguration,
            journal.PreviousConfiguration,
            previousProfile,
            previousReport,
            previousRouter,
            journal);
        try
        {
            await ProfileSwitchTransaction.RestorePreviousAsync(actions, token).ConfigureAwait(false);
        }
        catch (Exception recoveryError) when (recoveryError is not OutOfMemoryException)
        {
            try { MarkProfileSwitchRecoveryRequired(journal); }
            catch (Exception stateError) when (stateError is not OutOfMemoryException)
            {
                throw new AggregateException(recoveryError, stateError);
            }
            throw;
        }
    }

    private static bool HasProfileSwitchJournal()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath);
        return key?.GetValueNames().Contains(ProfileSwitchJournalValueName, StringComparer.OrdinalIgnoreCase) == true;
    }

    private static ProfileSwitchJournal? ReadProfileSwitchJournal()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath);
        if (key is null || !key.GetValueNames().Contains(ProfileSwitchJournalValueName, StringComparer.OrdinalIgnoreCase)) return null;
        object? value = key.GetValue(ProfileSwitchJournalValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is string json && ProfileSwitchJournal.TryDeserialize(json, out ProfileSwitchJournal? journal)) return journal;
        throw new InvalidOperationException("El journal del cambio de perfil está dañado; se conservó para revisión.");
    }

    private static void WriteProfileSwitchJournal(ProfileSwitchJournal journal)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(BrokerPolicy.RegistryPath, true);
        key.SetValue(ProfileSwitchJournalValueName, journal.Serialize(), RegistryValueKind.String);
        key.Flush();
    }

    private static void ClearProfileSwitchJournal()
    {
        using RegistryKey key = Registry.LocalMachine.OpenSubKey(BrokerPolicy.RegistryPath, true)
            ?? throw new InvalidOperationException("Falta la clave protegida para completar el cambio de perfil.");
        key.DeleteValue(ProfileSwitchJournalValueName, throwOnMissingValue: false);
        key.Flush();
    }
}
