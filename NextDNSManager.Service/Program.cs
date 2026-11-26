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
    private System.Threading.Timer? _pauseExpiryTimer;
    private readonly object _reconcileLock = new();
    private Task _reconcileTask = Task.CompletedTask;
    private bool _stopping;
    private bool _reconcilePending;
    private bool _reconcileRunning;
    private int _retrySeconds = 5;
    private DateTimeOffset? _retryNotBeforeUtc;
    internal BrokerWindowsService()
    {
        ServiceName = BrokerProtocol.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandlePowerEvent = true;
        AutoLog = false;
    }
    protected override void OnStart(string[] args)
    {
        try
        {
            _server = new PipeServer();
            _server.Start();
            _server.Engine.PauseScheduleChanged += PauseScheduleChanged;
            _server.Engine.ReconciliationRequested += ReconciliationRequested;
            _networkDebounce = new System.Threading.Timer(NetworkDebounceElapsed);
            _pauseExpiryTimer = new System.Threading.Timer(PauseDeadlineReached, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += NetworkChanged;
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
            ScheduleNetworkDebounce(TimeSpan.FromSeconds(2));
            SchedulePauseExpiry();
            ServiceLog.Write(DiagnosticEvent.ServiceStarted);
        }
        catch (Exception ex)
        {
            ServiceLog.Write(DiagnosticEvent.ServiceStartFailure, ex);
            throw;
        }
    }
    protected override void OnStop()
    {
        ServiceLog.Write(DiagnosticEvent.ServiceStopRequested);
        try
        {
            RequestAdditionalTime(70000);
            StopServer();
            ServiceLog.Write(DiagnosticEvent.ServiceStopped);
        }
        catch (Exception ex)
        {
            ServiceLog.Write(DiagnosticEvent.ServiceStopFailure, ex);
            throw;
        }
    }
    private void StopServer()
    {
        Task pending;
        lock (_reconcileLock) { _stopping = true; pending = _reconcileTask; }
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= NetworkChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        _networkDebounce?.Dispose();
        _pauseExpiryTimer?.Dispose();
        if (_server != null)
        {
            _server.Engine.PauseScheduleChanged -= PauseScheduleChanged;
            _server.Engine.ReconciliationRequested -= ReconciliationRequested;
        }
        _server?.Dispose();
        try { pending.GetAwaiter().GetResult(); }
        catch (Exception ex) { ServiceLog.Write(DiagnosticEvent.ServiceStopDrainFailure, ex); }
        _server = null;
    }
    protected override void OnShutdown()
    {
        ServiceLog.Write(DiagnosticEvent.ServiceShutdown);
        try { StopServer(); }
        catch (Exception ex)
        {
            ServiceLog.Write(DiagnosticEvent.ServiceStopFailure, ex);
            throw;
        }
    }
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus is PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeCritical)
        {
            ScheduleNetworkDebounce(TimeSpan.FromSeconds(3));
            SchedulePauseExpiry();
        }
        return true;
    }

    private void PauseScheduleChanged() => SchedulePauseExpiry();

    private void ReconciliationRequested() => QueueReconcile();

    private void PauseDeadlineReached(object? state) => QueueReconcile();

    private void SchedulePauseExpiry()
    {
        NextDnsEngine engine;
        System.Threading.Timer timer;
        lock (_reconcileLock)
        {
            if (_stopping || _server == null || _pauseExpiryTimer == null) return;
            engine = _server.Engine;
            timer = _pauseExpiryTimer;
        }

        TimeSpan dueIn;
        try
        {
            DateTimeOffset? pauseUntil = engine.PauseUntilUtcForScheduler;
            dueIn = TemporaryPausePolicy.GetTimerDelay(pauseUntil, DateTimeOffset.UtcNow);
        }
        catch
        {
            // Startup, network resume, and failed-request reconciliation all
            // inspect malformed state and fail closed. Do not rearm a zero
            // timer here; a persistent registry error would create a hot loop.
            dueIn = Timeout.InfiniteTimeSpan;
        }

        lock (_reconcileLock)
        {
            if (_stopping || !ReferenceEquals(timer, _pauseExpiryTimer)) return;
            try { timer.Change(dueIn, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }
    private void QueueReconcile()
    {
        lock (_reconcileLock)
        {
            if (_stopping || _server == null) return;
            _reconcilePending = true;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!ServiceReconcilePolicy.RetryGateElapsed(_retryNotBeforeUtc, now))
            {
                try
                {
                    _networkDebounce?.Change(
                        ServiceReconcilePolicy.DebounceDelay(TimeSpan.Zero, _retryNotBeforeUtc, now),
                        Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException) { }
                return;
            }
            if (!_reconcileRunning)
            {
                _reconcileRunning = true;
                _reconcileTask = Task.Run(ReconcilePendingAsync);
            }
        }
    }

    private void ScheduleNetworkDebounce(TimeSpan requestedDelay)
    {
        lock (_reconcileLock)
        {
            if (_stopping || _networkDebounce == null) return;
            TimeSpan delay = ServiceReconcilePolicy.DebounceDelay(
                requestedDelay, _retryNotBeforeUtc, DateTimeOffset.UtcNow);
            try { _networkDebounce.Change(delay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    private void NetworkDebounceElapsed(object? state)
    {
        lock (_reconcileLock)
        {
            if (_stopping || _server == null) return;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!ServiceReconcilePolicy.RetryGateElapsed(_retryNotBeforeUtc, now))
            {
                try
                {
                    _networkDebounce?.Change(
                        ServiceReconcilePolicy.DebounceDelay(TimeSpan.Zero, _retryNotBeforeUtc, now),
                        Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException) { }
                return;
            }
            _retryNotBeforeUtc = null;
        }
        QueueReconcile();
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
            bool succeeded = false;
            try { succeeded = await engine.ReconcileAsync().ConfigureAwait(false); }
            catch (Exception ex) { ServiceLog.Write(DiagnosticEvent.ServiceReconcileFailure, ex); }
            lock (_reconcileLock)
            {
                if (succeeded)
                {
                    _retrySeconds = 5;
                    _retryNotBeforeUtc = null;
                    if (!ServiceReconcilePolicy.ShouldDrainPendingImmediately(succeeded, _reconcilePending))
                    {
                        _reconcileRunning = false;
                        return;
                    }
                }
                else
                {
                    // Discard triggers accumulated during this failed attempt.
                    // The retry timer below will re-read the latest network state
                    // after the backoff rather than hammering a failing restore.
                    _reconcilePending = false;
                    if (!_stopping && _networkDebounce != null)
                    {
                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        TimeSpan delay = TimeSpan.FromSeconds(_retrySeconds);
                        _retryNotBeforeUtc = now.Add(delay);
                        delay = ServiceReconcilePolicy.DebounceDelay(delay, _retryNotBeforeUtc, now);
                        try { _networkDebounce.Change(delay, Timeout.InfiniteTimeSpan); }
                        catch (ObjectDisposedException) { }
                        _retrySeconds = ServiceStartupBackoff.NextDelaySeconds(_retrySeconds);
                    }
                    _reconcileRunning = false;
                    return;
                }
            }
        }
    }
    private void NetworkChanged(object? sender, EventArgs e)
    {
        ScheduleNetworkDebounce(TimeSpan.FromSeconds(2));
    }
    private void NetworkAvailabilityChanged(object? sender, System.Net.NetworkInformation.NetworkAvailabilityEventArgs e) => NetworkChanged(sender, e);
}
