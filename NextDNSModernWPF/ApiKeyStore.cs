using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NextDNSModernWPF;

/// <summary>
/// Stores the user's NextDNS API key as a DPAPI CurrentUser protected blob.
/// This prevents another Windows account from reading a copied key file; it does
/// not protect a key from code already running as the same user.
/// </summary>
public sealed class ApiKeyStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private const string ProductFolder = "NextDNS Manager";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NextDNSManager.ApiKey.v1");
    private readonly string _path;

    public ApiKeyStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductFolder,
            "api-key.dpapi"));
    }

    public bool HasApiKey => File.Exists(_path);

    /// <summary>Returns null when no key is stored.</summary>
    public string? Load()
    {
        if (!File.Exists(_path)) return null;
        var fileInfo = new FileInfo(_path);
        if (fileInfo.Length > 64 * 1024)
            throw new InvalidOperationException("El archivo protegido de la API key supera el tamaño permitido.");

        byte[] protectedBytes = File.ReadAllBytes(_path);
        byte[]? plaintext = null;
        try
        {
            plaintext = Dpapi.Unprotect(protectedBytes, Entropy);
            string key = new UTF8Encoding(false, true).GetString(plaintext);
            return NextDnsApiClient.ValidateApiKey(key);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("No se pudo descifrar la API key guardada para este usuario de Windows.");
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException("La API key guardada no tiene un formato válido.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Save(string apiKey)
    {
        string validatedKey = NextDnsApiClient.ValidateApiKey(apiKey);
        byte[] plaintext = Encoding.UTF8.GetBytes(validatedKey);
        byte[]? protectedBytes = null;
        string? temporaryPath = null;
        try
        {
            protectedBytes = Dpapi.Protect(plaintext, Entropy);
            string? directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("No se pudo determinar la carpeta segura para la API key.");

            Directory.CreateDirectory(directory);
            temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes, 0, protectedBytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null) CryptographicOperations.ZeroMemory(protectedBytes);
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch { /* Keep the original save error, if any. */ }
            }
        }
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static class Dpapi
    {
        internal static byte[] Protect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: true);
        internal static byte[] Unprotect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: false);

        private static byte[] Transform(byte[] data, byte[] entropy, bool protect)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("El almacenamiento protegido de API keys requiere Windows.");

            IntPtr input = IntPtr.Zero;
            IntPtr optionalEntropy = IntPtr.Zero;
            IntPtr output = IntPtr.Zero;
            IntPtr description = IntPtr.Zero;
            int outputLength = 0;
            try
            {
                input = AllocateBlob(data);
                optionalEntropy = AllocateBlob(entropy);
                var inputBlob = new DataBlob { Size = data.Length, Data = input };
                var entropyBlob = new DataBlob { Size = entropy.Length, Data = optionalEntropy };
                DataBlob outputBlob;
                bool succeeded = protect
                    ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out outputBlob)
                    : CryptUnprotectData(ref inputBlob, out description, ref entropyBlob, IntPtr.Zero,
                        IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);

                if (description != IntPtr.Zero)
                {
                    LocalFree(description);
                    description = IntPtr.Zero;
                }
                if (!succeeded) throw new CryptographicException(Marshal.GetLastWin32Error());

                output = outputBlob.Data;
                outputLength = outputBlob.Size;
                if (outputBlob.Size < 0 || outputBlob.Size > 1024 * 1024)
                    throw new CryptographicException("La API key protegida tiene un tamaño inesperado.");

                byte[] result = new byte[outputBlob.Size];
                if (result.Length > 0) Marshal.Copy(output, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (input != IntPtr.Zero) FreeAndZero(input, data.Length);
                if (optionalEntropy != IntPtr.Zero) FreeAndZero(optionalEntropy, entropy.Length);
                if (output != IntPtr.Zero)
                {
                    if (outputLength > 0) ZeroUnmanaged(output, Math.Min(outputLength, 1024 * 1024));
                    LocalFree(output);
                }
                if (description != IntPtr.Zero) LocalFree(description);
            }
        }

        private static IntPtr AllocateBlob(byte[] bytes)
        {
            if (bytes.Length == 0) return IntPtr.Zero;
            IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            return memory;
        }

        private static void FreeAndZero(IntPtr memory, int length)
        {
            if (length > 0) ZeroUnmanaged(memory, length);
            Marshal.FreeHGlobal(memory);
        }

        private static void ZeroUnmanaged(IntPtr memory, int length)
        {
            for (int index = 0; index < length; index++) Marshal.WriteByte(memory, index, 0);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string? description,
            ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, out IntPtr description,
            ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);

    }
}
