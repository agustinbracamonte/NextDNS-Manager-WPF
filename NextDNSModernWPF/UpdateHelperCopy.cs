using System.IO;
using System.Security.AccessControl;

namespace NextDNSModernWPF;

/// <summary>
/// Creates a private copy of the self-contained application bundle so the
/// update helper never loads assemblies from the directory the installer will
/// replace. A later application start removes the copy after the helper releases
/// its executable image.
/// </summary>
internal sealed class UpdateHelperCopy : IDisposable
{
    private const string DirectoryPrefix = "NextDNSManager-UpdateHelper-";
    private static readonly string[] RequiredFiles =
    [
        "NextDNSModernWPF.exe",
        "NextDNSModernWPF.dll",
        "NextDNSModernWPF.runtimeconfig.json",
        "NextDNSModernWPF.deps.json"
    ];

    private FileStream? _copyLock;
    private bool _cleanupDeferred;

    internal string DirectoryPath { get; }
    internal string ExecutablePath => Path.Combine(DirectoryPath, "NextDNSModernWPF.exe");

    private UpdateHelperCopy(string directoryPath) => DirectoryPath = directoryPath;

    internal static UpdateHelperCopy CreateFromDirectory(string sourceDirectory)
    {
        string source = Path.GetFullPath(sourceDirectory);
        UpdateInstaller.ValidateExistingDirectoryChain(source);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("No se pudo determinar la carpeta local del usuario.");

        string normalizedLocalAppData = Path.GetFullPath(localAppData);
        UpdateInstaller.ValidateExistingDirectoryChain(normalizedLocalAppData);
        if (normalizedLocalAppData.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("La copia del helper debe estar en una unidad local.");

        string destination = Path.Combine(normalizedLocalAppData, DirectoryPrefix + Guid.NewGuid().ToString("N"));
        var copy = new UpdateHelperCopy(destination);
        bool created = false;
        try
        {
            new DirectoryInfo(destination).Create(UpdateInstaller.CreatePrivateDirectorySecurity());
            created = true;
            string lockPath = Path.Combine(destination, "helper.lock");
            copy._copyLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
            CopyTree(source, destination);

            foreach (string requiredFile in RequiredFiles)
            {
                if (!File.Exists(Path.Combine(destination, requiredFile)))
                    throw new InvalidDataException($"Falta {requiredFile} en la copia temporal del helper.");
            }

            return copy;
        }
        catch
        {
            if (created)
            {
                copy.Dispose();
            }
            throw;
        }
    }

    /// <summary>Leaves the copy for a later application start, after this helper releases its executable.</summary>
    internal void DeferCleanupUntilNextLaunch()
    {
        _cleanupDeferred = true;
        Interlocked.Exchange(ref _copyLock, null)?.Dispose();
    }

    /// <summary>Removes the copy immediately after a cancelled or failed handoff.</summary>
    internal void CleanupAfterHelperExit()
    {
        Interlocked.Exchange(ref _copyLock, null)?.Dispose();
        _cleanupDeferred = false;
        TryDeletePrivateDirectory(DirectoryPath, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>Locks this private helper directory for the lifetime of its process.</summary>
    internal static FileStream AcquireRunningLock()
    {
        string localAppData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string helperDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string expectedParent = localAppData.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(helperDirectory), expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !IsHelperDirectoryName(Path.GetFileName(helperDirectory)))
        {
            throw new InvalidDataException("El helper no se está ejecutando desde su copia temporal privada.");
        }

        UpdateInstaller.ValidateExistingDirectoryChain(helperDirectory);
        string lockPath = Path.Combine(helperDirectory, "helper.lock");
        if ((File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("El bloqueo del helper contiene un enlace no permitido.");
        return new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
    }

    /// <summary>
    /// Removes only abandoned helper copies from the direct, private LocalAppData
    /// child directories. An active helper's directory lock prevents deletion; that
    /// directory is skipped and retried at the next application start.
    /// </summary>
    internal static void CleanupStaleCopies()
    {
        try
        {
            string localAppData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            UpdateInstaller.ValidateExistingDirectoryChain(localAppData);
            string? runningDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            foreach (string directory in Directory.EnumerateDirectories(localAppData, DirectoryPrefix + "*", SearchOption.TopDirectoryOnly))
            {
                if (runningDirectory is not null && string.Equals(
                    Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(runningDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                TryDeletePrivateDirectory(directory, localAppData);
            }
        }
        catch
        {
            // Cleanup is best effort and must not change startup or update behavior.
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _copyLock, null)?.Dispose();
        if (!_cleanupDeferred)
            TryDeletePrivateDirectory(DirectoryPath, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    private static void CopyTree(string source, string destination)
    {
        FileAttributes sourceAttributes = File.GetAttributes(source);
        if ((sourceAttributes & FileAttributes.Directory) == 0 ||
            (sourceAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("La carpeta de la aplicación no es válida para copiar el helper.");
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("No se permiten enlaces o junctions en la copia temporal del helper.");

            string target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopyTree(entry, target);
            }
            else
            {
                File.Copy(entry, target, overwrite: false);
            }
        }
    }

    private static void TryDeletePrivateDirectory(string directory, string expectedParent)
    {
        try
        {
            string fullDirectory = Path.GetFullPath(directory);
            string fullParent = Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string expectedPath = fullParent + Path.DirectorySeparatorChar + Path.GetFileName(fullDirectory);
            if (!string.Equals(fullDirectory, expectedPath, StringComparison.OrdinalIgnoreCase) ||
                !IsHelperDirectoryName(Path.GetFileName(fullDirectory)) ||
                !Directory.Exists(fullDirectory) ||
                (File.GetAttributes(fullDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            if (!TreeHasNoReparsePoints(fullDirectory))
                return;

            string lockPath = Path.Combine(fullDirectory, "helper.lock");
            using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                DeleteTreeContentsExceptLock(fullDirectory, lockPath);
            }

            File.Delete(lockPath);
            Directory.Delete(fullDirectory, recursive: false);
        }
        catch
        {
            // Cleanup is best effort and must not change update handoff results.
        }
    }

    private static bool TreeHasNoReparsePoints(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return false;
            if ((attributes & FileAttributes.Directory) != 0 && !TreeHasNoReparsePoints(entry))
                return false;
        }
        return true;
    }

    private static void DeleteTreeContentsExceptLock(string directory, string lockPath)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (string.Equals(Path.GetFullPath(entry), lockPath, StringComparison.OrdinalIgnoreCase))
                continue;

            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
    }

    private static bool IsHelperDirectoryName(string name) =>
        name.StartsWith(DirectoryPrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(name[DirectoryPrefix.Length..], "N", out _);
}
