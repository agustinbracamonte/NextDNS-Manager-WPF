using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32.SafeHandles;

namespace NextDNSModernWPF;

internal static class UpdateLaunchCoordinator
{
    private const string HelperArgument = "--launch-verified-update";
    private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromMinutes(3);

    internal enum HandoffResult
    {
        Ready,
        Cancelled,
        Failed
    }

    internal static async Task<HandoffResult> HandOffToUnelevatedHelperAsync(
        UpdateInstaller.VerifiedInstaller installer,
        CancellationToken cancellationToken = default)
    {
        string pipeName = "NextDNSUpdate-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using UpdateHelperCopy helperCopy = UpdateHelperCopy.CreateFromDirectory(AppContext.BaseDirectory);
        using Process helper = Process.Start(installer.CreateHelperStartInfo(Environment.ProcessId, pipeName, helperCopy.ExecutablePath))
            ?? throw new InvalidOperationException("No se pudo iniciar el proceso de actualización.");
        bool helperAcceptedHandoff = false;
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(PipeConnectTimeout);
            await server.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint clientPid) || clientPid != (uint)helper.Id)
                return HandoffResult.Failed;

            // RunHelperAsync acquires its own directory lock before connecting,
            // so the parent can release its copy lock after authenticating it.
            helperCopy.DeferCleanupUntilNextLaunch();
            byte[] result = new byte[1];
            using var verificationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            verificationTimeout.CancelAfter(VerificationTimeout);
            int read = await server.ReadAsync(result.AsMemory(), verificationTimeout.Token).ConfigureAwait(false);
            if (read != 1)
                return HandoffResult.Failed;

            if (result[0] != 3)
                return TranslateStatus(result[0]);

            // The second status is intentionally not timed out: the helper is
            // displaying an explicit user confirmation dialog at this point.
            read = await server.ReadAsync(result.AsMemory(), cancellationToken).ConfigureAwait(false);
            HandoffResult handoff = read == 1 ? TranslateStatus(result[0]) : HandoffResult.Failed;
            helperAcceptedHandoff = handoff == HandoffResult.Ready;
            return handoff;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("El proceso de actualización no pudo volver a verificar la publicación a tiempo.");
        }
        finally
        {
            if (!helperAcceptedHandoff)
            {
                if (!helper.HasExited)
                {
                    try
                    {
                        helper.Kill(entireProcessTree: true);
                        await helper.WaitForExitAsync().ConfigureAwait(false);
                    }
                    catch { }
                }
                helperCopy.CleanupAfterHelperExit();
            }
        }
    }

    private static HandoffResult TranslateStatus(byte status) => status switch
    {
        1 => HandoffResult.Ready,
        2 => HandoffResult.Cancelled,
        _ => HandoffResult.Failed
    };

    private static async Task SendStatusAsync(NamedPipeClientStream pipe, byte status)
    {
        await pipe.WriteAsync(new byte[] { status }).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    internal static bool IsHelperInvocation(string[] arguments) =>
        arguments.Length == 5 && string.Equals(arguments[0], HelperArgument, StringComparison.Ordinal);

    internal static async Task<int> RunHelperAsync(string[] arguments)
    {
        if (!IsHelperInvocation(arguments) ||
            !int.TryParse(arguments[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parentProcessId) ||
            parentProcessId <= 0 ||
            !Regex.IsMatch(arguments[4], "\\ANextDNSUpdate-[0-9a-f]{32}\\z", RegexOptions.CultureInvariant))
        {
            return 1;
        }

        using FileStream helperDirectoryLock = UpdateHelperCopy.AcquireRunningLock();
        await using var pipe = new NamedPipeClientStream(
            ".",
            arguments[4],
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch
        {
            return 1;
        }

        UpdateInstaller.VerifiedInstaller? verifiedInstaller = null;
        try
        {
            using var verificationTimeout = new CancellationTokenSource(VerificationTimeout);
            verifiedInstaller = await UpdateInstaller.OpenVerifiedReleaseFromHandoffAsync(
                arguments[1],
                arguments[2],
                verificationTimeout.Token);
            await SendStatusAsync(pipe, 3).ConfigureAwait(false);

            string confirmation =
                $"La versión {verifiedInstaller.Version} está lista. El instalador no tiene firma digital; SHA-256 solo comprueba que coincide con SHA256SUMS y no autentica al publicador.\n\n" +
                "Si continúas, NextDNS Manager se cerrará, Windows pedirá permisos y la actualización se instalará automáticamente. ¿Continuar?";
            MessageBoxResult confirmationResult = System.Windows.MessageBox.Show(
                confirmation,
                "Confirmar instalación de NextDNS Manager",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmationResult != MessageBoxResult.Yes)
            {
                await SendStatusAsync(pipe, 2).ConfigureAwait(false);
                verifiedInstaller.Dispose();
                return 0;
            }

            await SendStatusAsync(pipe, 1).ConfigureAwait(false);
        }
        catch
        {
            try { await SendStatusAsync(pipe, 0).ConfigureAwait(false); } catch { }
            verifiedInstaller?.Dispose();
            return 1;
        }

        bool installerStarted = false;
        try
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            using Process parent = Process.GetProcessById(parentProcessId);
            using var parentExitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await parent.WaitForExitAsync(parentExitTimeout.Token).ConfigureAwait(false);

            verifiedInstaller.EnsureStillVerified();
            using Process installerProcess = Process.Start(verifiedInstaller.CreateElevatedInstallerStartInfo())
                ?? throw new InvalidOperationException("Windows no inició el instalador.");
            installerStarted = true;

            await installerProcess.WaitForExitAsync().ConfigureAwait(false);
            if (installerProcess.ExitCode != 0)
            {
                System.Windows.MessageBox.Show(
                    $"El instalador terminó con el código {installerProcess.ExitCode}. Puedes volver a abrir NextDNS Manager e intentarlo de nuevo.",
                    "Actualización de NextDNS Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                TryRestartInstalledManager();
            }
            else
            {
                TryRestartInstalledManager();
            }
            return installerProcess.ExitCode;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            System.Windows.MessageBox.Show(
                "Se canceló la solicitud de permisos de Windows. No se instaló la actualización; puedes volver a abrir NextDNS Manager.",
                "Actualización cancelada",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            TryRestartInstalledManager();
            return 0;
        }
        catch
        {
            System.Windows.MessageBox.Show(
                "No se pudo iniciar o completar el instalador. No se modificó el DNS. Puedes volver a abrir NextDNS Manager e intentarlo de nuevo.",
                "Actualización de NextDNS Manager",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            if (!installerStarted)
                TryRestartInstalledManager();
            return 1;
        }
        finally
        {
            verifiedInstaller.Dispose();
        }
    }

    private static void TryRestartInstalledManager()
    {
        try
        {
            string managerDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NextDNS Manager");
            string managerExecutable = Path.Combine(managerDirectory, "NextDNSModernWPF.exe");
            if (!File.Exists(managerExecutable))
                return;

            Process.Start(new ProcessStartInfo(managerExecutable)
            {
                UseShellExecute = false,
                WorkingDirectory = managerDirectory
            });
        }
        catch
        {
            // The failure dialog already tells the user how to reopen the Manager.
        }
    }
}
