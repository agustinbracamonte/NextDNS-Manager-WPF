using NextDNSManager.Shared;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NextDNSManager.Service;

internal sealed class PipeServer : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<NamedPipeServerStream> _pipes = new();
    private readonly NextDnsEngine _engine = new();
    private readonly string _controllerSid = BrokerPolicy.ControllerSid;
    private Task? _work;
    private bool _disposed;
    internal NextDnsEngine Engine => _engine;

    internal void Start()
    {
        try
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (WellKnownSidType sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(_controllerSid),
                PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes |
                PipeAccessRights.Synchronize, AccessControlType.Allow));
            for (int index = 0; index < 4; index++)
            {
                // Keep the first instance alive for the service lifetime. Other
                // users never receive FILE_CREATE_PIPE_INSTANCE permission.
                PipeOptions options = PipeOptions.Asynchronous | (index == 0 ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                _pipes.Add(NamedPipeServerStreamAcl.Create(BrokerProtocol.PipeName, PipeDirection.InOut, 4,
                    PipeTransmissionMode.Byte, options, 4096, 4096, security, HandleInheritability.None));
            }
            _work = Task.WhenAll(_pipes.Select(ServeAsync));
        }
        catch { Dispose(); throw; }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                BrokerResponse response;
                try
                {
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                    BrokerRequest request = await BrokerProtocol.ReadAsync<BrokerRequest>(pipe, readDeadline.Token).ConfigureAwait(false);
                    bool authorized = false;
                    // Read a bounded message before impersonation: Windows needs
                    // a received message to identify the connected pipe client.
                    pipe.RunAsClient(() =>
                    {
                        using WindowsIdentity? identity = WindowsIdentity.GetCurrent(true);
                        if (identity == null) return;
                        var principal = new WindowsPrincipal(identity);
                        authorized = identity.IsSystem ||
                            (identity.User?.Value == _controllerSid &&
                             principal.IsInRole(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null)) &&
                             !principal.IsInRole(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)));
                    });
                    if (!authorized) throw new UnauthorizedAccessException("Este usuario no está autorizado para controlar NextDNS.");
                    // Impersonation has ended here. Only this fixed engine runs
                    // as SYSTEM; the client supplies no paths or command lines.
                    response = await _engine.ExecuteAsync(request).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    response = new BrokerResponse { Success = false, State = "error", Error = SafeMessage(ex) };
                }
                using var writeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await BrokerProtocol.WriteAsync(pipe, response, writeDeadline.Token).ConfigureAwait(false);
                // Disconnect discards unread pipe buffers on Windows. Keep the
                // connection alive until the client has read the complete reply
                // and closed it; otherwise fast toggles can lose the response.
                using var closeDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                closeDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                await pipe.ReadAsync(new byte[1], closeDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { }
            finally
            {
                if (pipe.IsConnected) { try { pipe.Disconnect(); } catch (IOException) { } }
            }
        }
    }

    private static string SafeMessage(Exception error)
    {
        if (error is OperationCanceledException) return "La operación tardó demasiado. Consulta el estado antes de reintentar.";
        string text = error.Message;
        return text.Length <= 1600 ? text : text[..1600];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        try { _work?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        foreach (NamedPipeServerStream pipe in _pipes) pipe.Dispose();
        _pipes.Clear();
        _stop.Dispose();
    }
}
