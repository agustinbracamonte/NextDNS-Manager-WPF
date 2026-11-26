using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace NextDNSModernWPF;

internal sealed record UpdateDownloadProgress(string Stage, long BytesReceived, long TotalBytes);

/// <summary>
/// Downloads only the exact release assets described by UpdateChecker, verifies
/// SHA256SUMS, and stages the installer in a private directory. The checksum is
/// a transport-integrity check, not an independently trusted publisher signature.
/// </summary>
internal sealed class UpdateInstaller : IDisposable
{
    private const int MaximumRedirects = 5;
    private static readonly HashSet<string> RedirectHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com"
    };

    private static readonly HttpClient SharedHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    internal UpdateInstaller()
    {
        _httpClient = SharedHttpClient;
    }

    internal UpdateInstaller(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _ownsHttpClient = true;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    internal async Task<VerifiedInstaller> DownloadAndVerifyAsync(
        UpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ValidateUpdateInfo(update);

        progress?.Report(new UpdateDownloadProgress("Descargando SHA256SUMS…", 0, update.Checksums.Size));
        byte[] checksumFile = await DownloadBoundedBytesAsync(
            update.Checksums,
            UpdateChecker.MaximumChecksumsBytes,
            progress,
            "Descargando SHA256SUMS…",
            cancellationToken).ConfigureAwait(false);
        byte[] expectedHash = ParseSha256Sums(checksumFile, update.Installer.Name);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("No se pudo determinar la carpeta local del usuario.");

        string normalizedLocalAppData = Path.GetFullPath(localAppData);
        ValidateExistingDirectoryChain(normalizedLocalAppData);
        if (normalizedLocalAppData.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("La carpeta de descarga debe estar en una unidad local.");

        string stagingDirectory = Path.Combine(normalizedLocalAppData, $"NextDNSManager-Update-{Guid.NewGuid():N}");
        string installerPath = Path.Combine(stagingDirectory, update.Installer.Name);
        DirectorySecurity directorySecurity = CreatePrivateDirectorySecurity();
        bool directoryCreated = false;

        try
        {
            new DirectoryInfo(stagingDirectory).Create(directorySecurity);
            directoryCreated = true;

            progress?.Report(new UpdateDownloadProgress("Descargando instalador…", 0, update.Installer.Size));
            await DownloadToNewFileAsync(
                update.Installer,
                installerPath,
                progress,
                cancellationToken).ConfigureAwait(false);

            VerifiedInstaller verified = OpenAndVerifyStagedInstaller(
                installerPath,
                update.Version,
                expectedHash,
                update.Installer.Size);
            directoryCreated = false;
            progress?.Report(new UpdateDownloadProgress("Descarga verificada.", update.Installer.Size, update.Installer.Size));
            return verified;
        }
        catch
        {
            if (directoryCreated)
                TryDeleteStagingDirectory(stagingDirectory, update.Installer.Name);
            throw;
        }
    }

    /// <summary>
    /// Re-fetches the current release metadata and checksum in the unelevated
    /// helper before it accepts a staged payload. Caller-supplied hashes are
    /// never treated as an authority.
    /// </summary>
    internal static async Task<VerifiedInstaller> OpenVerifiedReleaseFromHandoffAsync(
        string installerPath,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (!IsCanonicalVersion(version))
            throw new InvalidDataException("La versión de la actualización no es válida.");

        using var checker = new UpdateChecker();
        UpdateInfo? update = await checker.CheckAsync(cancellationToken).ConfigureAwait(false);
        if (update is null || !string.Equals(update.Version, version, StringComparison.Ordinal))
            throw new InvalidDataException("La publicación disponible cambió y ya no coincide con la descarga.");

        var downloader = new UpdateInstaller();
        byte[] checksumFile = await downloader.DownloadBoundedBytesAsync(
            update.Checksums,
            UpdateChecker.MaximumChecksumsBytes,
            progress: null,
            stage: "Verificando SHA256SUMS…",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        byte[] expectedHash = ParseSha256Sums(checksumFile, update.Installer.Name);

        string expectedName = $"NextDNS-Manager-{version}-Setup-x64.exe";
        string fullPath = Path.GetFullPath(installerPath);
        if (!string.Equals(Path.GetFileName(fullPath), expectedName, StringComparison.Ordinal))
            throw new InvalidDataException("El nombre del instalador no coincide con la versión esperada.");

        if (update.Installer.Name != expectedName)
            throw new InvalidDataException("El instalador publicado no coincide con la versión esperada.");

        string stagingDirectory = Path.GetDirectoryName(fullPath)!;
        string localAppData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string stagingParent = Path.GetDirectoryName(stagingDirectory)!;
        if (!string.Equals(stagingParent, localAppData, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(stagingDirectory), "\\ANextDNSManager-Update-[0-9a-f]{32}\\z", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("La ubicación del instalador no es válida.");
        }

        ValidateExistingDirectoryChain(stagingDirectory);
        ValidateUpdateInfo(update);
        return OpenAndVerifyStagedInstaller(fullPath, version, expectedHash, update.Installer.Size);
    }

    internal static byte[] ParseSha256Sums(byte[] content, string expectedAssetName)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0 || content.Length > UpdateChecker.MaximumChecksumsBytes)
            throw new InvalidDataException("El archivo SHA256SUMS supera el tamaño permitido o está vacío.");

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("El archivo SHA256SUMS no es UTF-8 válido.", exception);
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
            throw new InvalidDataException("El archivo SHA256SUMS no puede incluir BOM.");

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains('\r'))
            throw new InvalidDataException("El archivo SHA256SUMS contiene saltos de línea no válidos.");

        if (normalized.EndsWith('\n'))
            normalized = normalized[..^1];
        if (normalized.Contains('\n'))
            throw new InvalidDataException("El archivo SHA256SUMS debe contener una sola entrada.");

        const int hashLength = 64;
        if (normalized.Length != hashLength + 2 + expectedAssetName.Length ||
            normalized[hashLength] != ' ' ||
            normalized[hashLength + 1] != ' ' ||
            !string.Equals(normalized[(hashLength + 2)..], expectedAssetName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("SHA256SUMS no identifica exactamente el instalador esperado.");
        }

        try
        {
            return Convert.FromHexString(normalized[..hashLength]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("El hash del instalador en SHA256SUMS no es válido.", exception);
        }
    }

    internal static bool IsAllowedDownloadRedirect(Uri originalAssetUrl, Uri redirectedUrl)
    {
        if (!IsHttpsUri(redirectedUrl) || !RedirectHosts.Contains(redirectedUrl.Host))
            return false;

        if (string.Equals(redirectedUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(redirectedUrl.Scheme, originalAssetUrl.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(redirectedUrl.Host, originalAssetUrl.Host, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(redirectedUrl.AbsolutePath, originalAssetUrl.AbsolutePath, StringComparison.Ordinal) &&
                redirectedUrl.Fragment.Length == 0;
        }

        return redirectedUrl.Fragment.Length == 0;
    }

    internal async Task<byte[]> DownloadBoundedBytesAsync(
        ReleaseAsset asset,
        int maximumBytes,
        IProgress<UpdateDownloadProgress>? progress,
        string stage,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream(checked((int)asset.Size));
        await DownloadResponseAsync(
            asset,
            (stream, contentLength, token) => CopyBoundedAsync(stream, buffer, contentLength, asset.Size, maximumBytes, progress, stage, token),
            cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private async Task DownloadToNewFileAsync(
        ReleaseAsset asset,
        string path,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        await DownloadResponseAsync(
            asset,
            async (stream, contentLength, token) =>
            {
                await CopyBoundedAsync(
                    stream,
                    output,
                    contentLength,
                    asset.Size,
                    UpdateChecker.MaximumInstallerBytes,
                    progress,
                    "Descargando instalador…",
                    token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadResponseAsync(
        ReleaseAsset asset,
        Func<Stream, long?, CancellationToken, Task> consume,
        CancellationToken cancellationToken)
    {
        Uri current = asset.DownloadUrl;
        for (int redirectCount = 0; ; redirectCount++)
        {
            ValidateInitialOrRedirectUrl(asset.DownloadUrl, current);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("NextDNS-Manager-WPF", "1.0"));

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            int status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                if (redirectCount >= MaximumRedirects || response.Headers.Location is null)
                    throw new HttpRequestException("La descarga de GitHub devolvió demasiadas redirecciones o una ubicación vacía.", null, response.StatusCode);

                Uri next = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                if (!IsAllowedDownloadRedirect(asset.DownloadUrl, next))
                    throw new InvalidDataException("GitHub redirigió la descarga a un destino no permitido.");

                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("No se pudo descargar un archivo de la publicación de GitHub.", null, response.StatusCode);

            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is long length && length != asset.Size)
                throw new InvalidDataException("El tamaño descargado no coincide con el tamaño declarado por la publicación.");

            await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await consume(input, contentLength, cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private static async Task CopyBoundedAsync(
        Stream input,
        Stream output,
        long? contentLength,
        long expectedBytes,
        long maximumBytes,
        IProgress<UpdateDownloadProgress>? progress,
        string stage,
        CancellationToken cancellationToken)
    {
        if (expectedBytes <= 0 || expectedBytes > maximumBytes || (contentLength is long length && length > maximumBytes))
            throw new InvalidDataException("El archivo supera el tamaño máximo permitido.");

        byte[] buffer = new byte[64 * 1024];
        long received = 0;
        long lastReported = 0;
        var timer = Stopwatch.StartNew();
        while (true)
        {
            int bytesRead = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                break;

            received = checked(received + bytesRead);
            if (received > expectedBytes || received > maximumBytes)
                throw new InvalidDataException("El archivo descargado supera el tamaño permitido.");

            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            if (received - lastReported >= 256 * 1024 || timer.ElapsedMilliseconds >= 100)
            {
                progress?.Report(new UpdateDownloadProgress(stage, received, expectedBytes));
                lastReported = received;
                timer.Restart();
            }
        }

        if (received != expectedBytes)
            throw new InvalidDataException("El archivo descargado está incompleto.");

        progress?.Report(new UpdateDownloadProgress(stage, received, expectedBytes));
    }

    private static VerifiedInstaller OpenAndVerifyStagedInstaller(
        string installerPath,
        string version,
        byte[] expectedHash,
        long expectedSize)
    {
        if (!IsCanonicalVersion(version) || expectedHash.Length != 32)
            throw new InvalidDataException("La versión o el hash del instalador no son válidos.");

        string expectedName = $"NextDNS-Manager-{version}-Setup-x64.exe";
        string fullPath = Path.GetFullPath(installerPath);
        string stagingDirectory = Path.GetDirectoryName(fullPath)!;
        if (!string.Equals(Path.GetFileName(fullPath), expectedName, StringComparison.Ordinal))
            throw new InvalidDataException("El nombre del instalador no coincide con la versión esperada.");

        string localAppData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        if (!string.Equals(Path.GetDirectoryName(stagingDirectory), localAppData, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(stagingDirectory), "\\ANextDNSManager-Update-[0-9a-f]{32}\\z", RegexOptions.CultureInvariant))
        {
            throw new InvalidDataException("La carpeta de descarga no es válida.");
        }

        ValidateExistingDirectoryChain(stagingDirectory);
        SafeFileHandle? directoryHandle = null;
        FileStream? file = null;
        try
        {
            directoryHandle = OpenDirectoryWithoutFollowingLinks(stagingDirectory);
            EnsureExpectedFinalPath(directoryHandle, stagingDirectory, requireDirectory: true);
            file = OpenFileWithoutFollowingLinks(fullPath);
            EnsureExpectedFinalPath(file.SafeFileHandle, fullPath, requireDirectory: false);

            if (!GetFileInformationByHandle(file.SafeFileHandle, out ByHandleFileInformation info) ||
                (info.FileAttributes & FileAttributeReparsePoint) != 0 ||
                (info.FileAttributes & FileAttributeDirectory) != 0)
            {
                throw new InvalidDataException("El instalador no es un archivo normal.");
            }

            long size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            if (size != expectedSize || size > UpdateChecker.MaximumInstallerBytes)
                throw new InvalidDataException("El tamaño del instalador no coincide con la publicación o supera el máximo permitido.");

            byte[] actualHash = SHA256.HashData(file);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                throw new InvalidDataException("El SHA-256 del instalador no coincide con SHA256SUMS.");

            file.Position = 0;
            var result = VerifiedInstaller.Create(fullPath, version, Convert.ToHexString(actualHash).ToLowerInvariant(), file, directoryHandle);
            file = null;
            directoryHandle = null;
            return result;
        }
        finally
        {
            file?.Dispose();
            directoryHandle?.Dispose();
        }
    }

    private static FileStream OpenFileWithoutFollowingLinks(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            GenericRead,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("No se pudo fijar el archivo descargado para verificarlo.", new Win32Exception(error));
        }

        return new FileStream(handle, FileAccess.Read, bufferSize: 64 * 1024, isAsync: false);
    }

    private static SafeFileHandle OpenDirectoryWithoutFollowingLinks(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("No se pudo fijar la carpeta de descarga.", new Win32Exception(error));
        }

        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info) ||
            (info.FileAttributes & FileAttributeDirectory) == 0 ||
            (info.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            handle.Dispose();
            throw new InvalidDataException("No se permiten enlaces o carpetas no válidas en la carpeta de descarga.");
        }

        return handle;
    }

    private static void EnsureExpectedFinalPath(SafeFileHandle handle, string expectedPath, bool requireDirectory)
    {
        string? finalPath = GetFinalPath(handle);
        if (finalPath is null || !string.Equals(finalPath, Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La ruta del instalador cambió durante la verificación.");

        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info) ||
            (info.FileAttributes & FileAttributeReparsePoint) != 0 ||
            (((info.FileAttributes & FileAttributeDirectory) != 0) != requireDirectory))
        {
            throw new InvalidDataException("La ruta del instalador contiene un enlace o un tipo de archivo no válido.");
        }
    }

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
            return null;
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                return null;
        }

        string path = buffer.ToString();
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            path = "\\\\" + path[8..];
        else if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            path = path[4..];
        return Path.GetFullPath(path);
    }

    internal static void ValidateExistingDirectoryChain(string directory)
    {
        string full = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(full) ?? throw new InvalidDataException("La ruta local no es válida.");
        string current = root;
        string relative = full[root.Length..];
        foreach (string part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("No se permiten enlaces o carpetas no válidas en la ruta de descarga.");
        }
    }

    internal static DirectorySecurity CreatePrivateDirectorySecurity()
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No se pudo identificar la sesión actual de Windows.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        AddFullControl(security, user);
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        return security;
    }

    private static void AddFullControl(DirectorySecurity security, SecurityIdentifier sid)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static bool IsCanonicalVersion(string version) =>
        Regex.IsMatch(version, "\\A(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\z", RegexOptions.CultureInvariant);

    private static bool IsHttpsUri(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static void ValidateUpdateInfo(UpdateInfo update)
    {
        if (!IsCanonicalVersion(update.Version) ||
            update.Installer.Name != $"NextDNS-Manager-{update.Version}-Setup-x64.exe" ||
            update.Checksums.Name != "SHA256SUMS.txt" ||
            update.Installer.Size <= 0 || update.Installer.Size > UpdateChecker.MaximumInstallerBytes ||
            update.Checksums.Size <= 0 || update.Checksums.Size > UpdateChecker.MaximumChecksumsBytes ||
            !IsTrustedReleaseDownloadUrl(update.Installer.DownloadUrl, update.Version, update.Installer.Name) ||
            !IsTrustedReleaseDownloadUrl(update.Checksums.DownloadUrl, update.Version, update.Checksums.Name))
        {
            throw new InvalidDataException("Los datos de la actualización no coinciden con la release esperada.");
        }
    }

    private static bool IsTrustedReleaseDownloadUrl(Uri uri, string version, string assetName)
    {
        string expectedPath = $"/{UpdateChecker.RepositoryOwner}/{UpdateChecker.RepositoryName}/releases/download/v{version}/{assetName}";
        return IsHttpsUri(uri) &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.Query.Length == 0 &&
            string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal);
    }

    private static void ValidateInitialOrRedirectUrl(Uri original, Uri current)
    {
        if (!IsHttpsUri(current) || !RedirectHosts.Contains(current.Host) || current.Query.Length > 4096)
            throw new InvalidDataException("La descarga contiene una URL HTTPS no permitida.");
        if (string.Equals(current.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            (!string.Equals(current.AbsolutePath, original.AbsolutePath, StringComparison.Ordinal) || current.Fragment.Length != 0))
        {
            throw new InvalidDataException("La descarga salió de la ruta del archivo de la release.");
        }
    }

    private static void TryDeleteStagingDirectory(string directory, string assetName)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            FileAttributes attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return;

            string expectedName = $"NextDNS-Manager-.*-Setup-x64\\.exe";
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (Path.GetFileName(file) == assetName || Regex.IsMatch(Path.GetFileName(file), expectedName, RegexOptions.CultureInvariant))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                        File.Delete(file);
                }
            }

            Directory.Delete(directory, recursive: false);
        }
        catch
        {
            // Best-effort cleanup; failure never changes verification or launch decisions.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder filePath, uint filePathLength, uint flags);

    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    internal static ProcessStartInfo CreateAutomaticInstallerStartInfo(string path, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = workingDirectory
        };
        startInfo.ArgumentList.Add("/VERYSILENT");
        startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
        startInfo.ArgumentList.Add("/NORESTART");
        return startInfo;
    }

    internal sealed class VerifiedInstaller : IDisposable
    {
        private FileStream? _file;
        private SafeFileHandle? _directory;

        internal string Path { get; }
        internal string Version { get; }
        internal string Sha256 { get; }
        internal string StagingDirectory => System.IO.Path.GetDirectoryName(Path)!;

        private VerifiedInstaller(string path, string version, string sha256, FileStream file, SafeFileHandle directory)
        {
            Path = path;
            Version = version;
            Sha256 = sha256;
            _file = file;
            _directory = directory;
        }

        internal static VerifiedInstaller Create(string path, string version, string sha256, FileStream file, SafeFileHandle directory) =>
            new(path, version, sha256, file, directory);

        internal ProcessStartInfo CreateHelperStartInfo(int parentProcessId, string pipeName, string helperExecutable)
        {
            if (string.IsNullOrWhiteSpace(helperExecutable) ||
                !string.Equals(System.IO.Path.GetFileName(helperExecutable), "NextDNSModernWPF.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("La copia temporal del helper no es válida.");
            }
            var startInfo = new ProcessStartInfo(helperExecutable)
            {
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(helperExecutable) ?? Environment.CurrentDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--launch-verified-update");
            startInfo.ArgumentList.Add(Path);
            startInfo.ArgumentList.Add(Version);
            startInfo.ArgumentList.Add(parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(pipeName);
            return startInfo;
        }

        internal ProcessStartInfo CreateElevatedInstallerStartInfo()
        {
            return CreateAutomaticInstallerStartInfo(Path, StagingDirectory);
        }

        internal void EnsureStillVerified()
        {
            FileStream file = _file ?? throw new ObjectDisposedException(nameof(VerifiedInstaller));
            file.Position = 0;
            byte[] currentHash = SHA256.HashData(file);
            if (!CryptographicOperations.FixedTimeEquals(currentHash, Convert.FromHexString(Sha256)))
                throw new InvalidDataException("El instalador cambió después de la descarga verificada.");
            file.Position = 0;
        }

        public void Dispose()
        {
            string path = Path;
            string directory = StagingDirectory;
            FileStream? file = Interlocked.Exchange(ref _file, null);
            SafeFileHandle? folder = Interlocked.Exchange(ref _directory, null);
            try { file?.Dispose(); }
            finally { folder?.Dispose(); }
            TryDeleteStagingDirectory(directory, System.IO.Path.GetFileName(path));
        }
    }

}
