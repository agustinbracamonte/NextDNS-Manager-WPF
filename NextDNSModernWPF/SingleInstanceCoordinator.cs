using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace NextDNSModernWPF
{
    /// <summary>
    /// Coordinates one NextDNS Manager process per interactive Windows session.
    /// Keep MutexName in sync with the installer's [Setup] AppMutex setting.
    /// Names without a Global prefix use the current session's Local namespace.
    /// </summary>
    internal sealed class SingleInstanceCoordinator : IDisposable
    {
        internal const string MutexName = "NextDNSManagerWPF-Running";
        private const string RestoreEventName = "NextDNSManagerWPF-Restore";

        private readonly bool _isPrimary;
        private readonly Mutex? _mutex;
        private readonly EventWaitHandle? _restoreEvent;
        private readonly ManualResetEvent? _stopEvent;
        private Task? _listenerTask;
        private bool _disposed;

        private SingleInstanceCoordinator(
            bool isPrimary,
            Mutex? mutex = null,
            EventWaitHandle? restoreEvent = null,
            ManualResetEvent? stopEvent = null)
        {
            _isPrimary = isPrimary;
            _mutex = mutex;
            _restoreEvent = restoreEvent;
            _stopEvent = stopEvent;
        }

        internal bool IsPrimary => _isPrimary;

        internal static SingleInstanceCoordinator Start(Dispatcher dispatcher, Action restoreMainWindow, bool restoreExisting = true)
        {
            EventWaitHandle? restoreEvent = null;
            Mutex? mutex = null;
            bool ownsMutex = false;

            try
            {
                bool mutexCreatedNew;
                try
                {
                    mutex = new Mutex(true, MutexName, out mutexCreatedNew);
                    ownsMutex = mutexCreatedNew;
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (!restoreExisting) return new SingleInstanceCoordinator(false);
                    // An existing mutex can be inaccessible across different
                    // elevation/account tokens. Never launch another window in
                    // that case; wait briefly for and signal the primary's event.
                    if (TrySignalExistingInstance())
                    {
                        return new SingleInstanceCoordinator(false);
                    }

                    throw new InvalidOperationException(
                        "Ya existe una instancia de NextDNS Manager, pero no se pudo activar su ventana. " +
                        "Cierra esa instancia antes de volver a intentarlo.", ex);
                }

                if (!mutexCreatedNew)
                {
                    mutex.Dispose();
                    mutex = null;

                    if (!restoreExisting) return new SingleInstanceCoordinator(false);

                    if (TrySignalExistingInstance())
                    {
                        return new SingleInstanceCoordinator(false);
                    }

                    throw new InvalidOperationException(
                        "NextDNS Manager ya está en ejecución, pero no se pudo activar su ventana. " +
                        "Cierra esa instancia antes de volver a intentarlo.");
                }

                // Only the process that acquired the mutex creates the event.
                // A simultaneous second launch retries OpenExisting below until
                // this initialization step is complete.
                restoreEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    RestoreEventName);

                var stopEvent = new ManualResetEvent(false);
                var coordinator = new SingleInstanceCoordinator(true, mutex, restoreEvent, stopEvent);
                try
                {
                    coordinator.StartRestoreListener(dispatcher, restoreMainWindow);
                }
                catch
                {
                    coordinator.Dispose();
                    throw;
                }

                mutex = null;
                restoreEvent = null;
                ownsMutex = false;
                return coordinator;
            }
            catch
            {
                if (ownsMutex && mutex != null)
                {
                    try { mutex.ReleaseMutex(); }
                    catch (ApplicationException) { }
                }

                mutex?.Dispose();
                restoreEvent?.Dispose();
                throw;
            }
        }

        private static bool TrySignalExistingInstance()
        {
            // The mutex is created before the primary's event. Retry briefly so
            // a concurrent launch does not show an error while that event is
            // being initialized. OpenExisting avoids signaling a private event
            // that the primary cannot see.
            const int attempts = 20;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    using EventWaitHandle restoreEvent = EventWaitHandle.OpenExisting(RestoreEventName);
                    restoreEvent.Set();
                    return true;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    if (attempt + 1 < attempts)
                        Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }

            return false;
        }

        private void StartRestoreListener(Dispatcher dispatcher, Action restoreMainWindow)
        {
            EventWaitHandle restoreEvent = _restoreEvent!;
            ManualResetEvent stopEvent = _stopEvent!;
            _listenerTask = Task.Run(() =>
            {
                WaitHandle[] handles = { restoreEvent, stopEvent };
                try
                {
                    while (WaitHandle.WaitAny(handles) == 0)
                        dispatcher.BeginInvoke(restoreMainWindow);
                }
                catch (ObjectDisposedException)
                {
                    // Disposal during process exit wakes the listener.
                }
                catch (InvalidOperationException)
                {
                    // The WPF dispatcher is no longer accepting work.
                }
            });
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _stopEvent?.Set();

            try
            {
                _listenerTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // The listener has no state to preserve during application exit.
            }

            _stopEvent?.Dispose();
            _restoreEvent?.Dispose();

            if (_isPrimary && _mutex != null)
            {
                try { _mutex.ReleaseMutex(); }
                catch (ApplicationException) { }
            }

            _mutex?.Dispose();
        }
    }
}
