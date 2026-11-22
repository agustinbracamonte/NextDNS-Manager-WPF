using System.Diagnostics;

namespace NextDNSManager.Service;

internal static class ProcessRunner
{
    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using Process process = Process.Start(info) ?? throw new IOException("No se pudo iniciar el núcleo DNS.");
        Task<string> output = ReadBoundedAsync(process.StandardOutput, token);
        Task<string> error = ReadBoundedAsync(process.StandardError, token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            string stdout = await output.ConfigureAwait(false);
            string stderr = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"El núcleo DNS terminó con código {process.ExitCode}: {stderr} {stdout}".Trim());
            return stdout.Trim();
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new System.Text.StringBuilder();
        char[] buffer = new char[512];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            if (text.Length < 2048) text.Append(buffer, 0, Math.Min(count, 2048 - text.Length));
        return text.ToString();
    }
}
