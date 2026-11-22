using NextDNSManager.Shared;
using System.Security.Principal;
using System.ServiceProcess;

namespace NextDNSManager.Service;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            BrokerPolicy.RequireTrustedLocation();
            if (args.Length == 0)
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                if (!identity.IsSystem) throw new UnauthorizedAccessException("Este componente lo inicia Windows como servicio; abre NextDNS Manager desde su acceso directo.");
                ServiceBase.Run(new BrokerWindowsService());
                return 0;
            }
            BrokerPolicy.RequireAdministrator();
            if (args.Length != 1) throw new ArgumentException("Operación de instalación inválida.");
            switch (args[0])
            {
                case "--install": ServiceRegistration.Install(); break;
                case "--stop": ServiceRegistration.Stop(); break;
                case "--remove": ServiceRegistration.Remove(); break;
                default: throw new ArgumentException("Operación de instalación inválida.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

internal sealed class BrokerWindowsService : ServiceBase
{
    private PipeServer? _server;
    private System.Threading.Timer? _networkDebounce;
    private readonly object _reconcileLock = new();
    private Task _reconcileTask = Task.CompletedTask;
    private bool _stopping;
    private bool _reconcilePending;
    private bool _reconcileRunning;
    internal BrokerWindowsService()
    {
        ServiceName = BrokerProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandlePowerEvent = true;
        AutoLog = true;
    }
    protected override void OnStart(string[] args)
    {
        _server = new PipeServer();
        _server.Start();
        _networkDebounce = new System.Threading.Timer(state => QueueReconcile());
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += NetworkChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        _networkDebounce.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }
    protected override void OnStop()
    {
        RequestAdditionalTime(70000);
        StopServer();
    }
    private void StopServer()
    {
        Task pending;
        lock (_reconcileLock) { _stopping = true; pending = _reconcileTask; }
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        _networkDebounce?.Dispose();
        _server?.Dispose();
        try { pending.GetAwaiter().GetResult(); } catch (Exception ex) { ServiceLog.Write(ex.Message); }
        _server = null;
    }
    protected override void OnShutdown() => StopServer();
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend)
        {
            try { _networkDebounce?.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
        return true;
    }
    private void QueueReconcile()
    {
        lock (_reconcileLock)
        {
            if (_stopping || _server == null) return;
            _reconcilePending = true;
            if (!_reconcileRunning)
            {
                _reconcileRunning = true;
                _reconcileTask = Task.Run(ReconcilePendingAsync);
            }
        }
    }
    private async Task ReconcilePendingAsync()
    {
        while (true)
        {
            NextDnsEngine? engine;
            lock (_reconcileLock)
            {
                if (_stopping || !_reconcilePending || _server == null)
                {
                    _reconcileRunning = false;
                    return;
                }
                _reconcilePending = false;
                engine = _server.Engine;
            }
            try { await engine.ReconcileAsync().ConfigureAwait(false); }
            catch (Exception ex) { ServiceLog.Write("Reconciliación: " + ex.Message); }
        }
    }
    private void NetworkChanged(object? sender, EventArgs e)
    {
        try { _networkDebounce?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }
    private void NetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) => NetworkChanged(sender, e);
}
