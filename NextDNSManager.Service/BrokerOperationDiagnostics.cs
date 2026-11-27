using System.ComponentModel;
using System.Net.Sockets;
using System.Security;
using System.Text.Json;
using NextDNSManager.Shared;

namespace NextDNSManager.Service;

/// <summary>
/// Carries a fixed, non-sensitive activation stage on the original exception.
/// The marker is serialized only into the existing broker Error string; it is
/// never written to diagnostic logs.
/// </summary>
internal static class BrokerOperationDiagnostics
{
    private const string StageDataKey = "NextDNSManager.ActivationDiagnosticStage.v1";
    private const int MaximumErrorCharacters = 1600;
    private const string TimeoutMessage = "La operación tardó demasiado. Consulta el estado antes de reintentar.";
    private static readonly AsyncLocal<ActivationScopeState?> CurrentScope = new();

    private sealed class ActivationScopeState { }

    private sealed class ActivationScope : IDisposable
    {
        private readonly ActivationScopeState? _previous;
        private bool _disposed;

        internal ActivationScope()
        {
            _previous = CurrentScope.Value;
            CurrentScope.Value = new ActivationScopeState();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentScope.Value = _previous;
        }
    }

    internal static IDisposable BeginActivation(bool enabled) =>
        enabled ? new ActivationScope() : EmptyScope.Instance;

    internal static T RunSync<T>(ActivationFailureStage stage, Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try { return operation(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            MarkFailure(error, stage);
            throw;
        }
    }

    internal static void RunSync(ActivationFailureStage stage, Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try { operation(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            MarkFailure(error, stage);
            throw;
        }
    }

    internal static async Task RunAsync(ActivationFailureStage stage, Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try { await operation().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            MarkFailure(error, stage);
            throw;
        }
    }

    internal static async Task<T> RunAsync<T>(ActivationFailureStage stage, Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try { return await operation().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            MarkFailure(error, stage);
            throw;
        }
    }

    internal static void MarkFailureIfMissing(Exception error, ActivationFailureStage stage)
    {
        ArgumentNullException.ThrowIfNull(error);
        MarkFailure(error, stage);
    }

    internal static void MarkFailureAsPrimary(Exception error, ActivationFailureStage stage)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (CurrentScope.Value is null || stage == ActivationFailureStage.Unknown)
            return;

        try { error.Data[StageDataKey] = stage; }
        catch
        {
            // Diagnostic metadata must not replace the original exception or
            // change the activation/rollback result.
        }
    }

    internal static string FormatBrokerError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        string message = error is OperationCanceledException ? TimeoutMessage : error.Message;
        (ActivationFailureStage Stage, Exception Error)? tagged = FindTaggedError(error);
        string? prefix = tagged is { } metadata
            ? ActivationFailureEnvelope.Encode(metadata.Stage, GetCategory(metadata.Error), string.Empty)
            : null;
        if (prefix is not null)
        {
            int maximumMessageCharacters = MaximumErrorCharacters - prefix.Length;
            if (message.Length > maximumMessageCharacters)
                message = message[..maximumMessageCharacters];
            return prefix + message;
        }

        return message.Length <= MaximumErrorCharacters
            ? message
            : message[..MaximumErrorCharacters];
    }

    private static void MarkFailure(Exception error, ActivationFailureStage stage)
    {
        try
        {
            if (CurrentScope.Value is null || FindTaggedError(error) is not null)
                return;

            error.Data[StageDataKey] = stage;
        }
        catch
        {
            // Diagnostic metadata must not replace the original exception or
            // change the activation/rollback result.
        }
    }

    private static (ActivationFailureStage Stage, Exception Error)? FindTaggedError(Exception error)
    {
        var pending = new Queue<Exception>();
        var visited = new HashSet<Exception>(ReferenceExceptionComparer.Instance);
        pending.Enqueue(error);
        (ActivationFailureStage Stage, Exception Error)? firstTagged = null;

        while (pending.Count > 0)
        {
            Exception current = pending.Dequeue();
            if (!visited.Add(current)) continue;

            if (TryReadStage(current, out ActivationFailureStage stage))
            {
                if (stage == ActivationFailureStage.DnsRestore)
                    return (stage, current);
                firstTagged ??= (stage, current);
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                    pending.Enqueue(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Enqueue(inner);
            }
        }

        return firstTagged;
    }

    private static bool TryReadStage(Exception error, out ActivationFailureStage stage)
    {
        try
        {
            if (error.Data[StageDataKey] is ActivationFailureStage stored &&
                stored != ActivationFailureStage.Unknown && Enum.IsDefined(stored))
            {
                stage = stored;
                return true;
            }
        }
        catch
        {
            // Ignore unavailable exception metadata; never log or expose it.
        }

        stage = default;
        return false;
    }

    private static ActivationFailureCategory GetCategory(Exception error) => error switch
    {
        UnauthorizedAccessException or SecurityException => ActivationFailureCategory.Access,
        IOException => ActivationFailureCategory.Io,
        SocketException => ActivationFailureCategory.Network,
        TimeoutException or OperationCanceledException => ActivationFailureCategory.TimeoutOrCancelled,
        JsonException => ActivationFailureCategory.InvalidData,
        ArgumentException => ActivationFailureCategory.InvalidArgument,
        InvalidOperationException => ActivationFailureCategory.InvalidOperation,
        Win32Exception => ActivationFailureCategory.WindowsError,
        _ => ActivationFailureCategory.Other
    };

    private sealed class ReferenceExceptionComparer : IEqualityComparer<Exception>
    {
        internal static readonly ReferenceExceptionComparer Instance = new();

        public bool Equals(Exception? left, Exception? right) => ReferenceEquals(left, right);
        public int GetHashCode(Exception error) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(error);
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
