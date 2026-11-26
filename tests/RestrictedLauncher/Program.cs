using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

internal static class Program
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint DisableMaxPrivilege = 0x00000001;
    private const uint GroupEnabled = 0x00000004;
    private const uint GroupUseForDenyOnly = 0x00000010;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const ushort SwHide = 0;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x00000102;
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string InteractiveSid = "S-1-5-4";
    private const string NetworkSid = "S-1-5-2";

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("RestrictedLauncher runs on Windows only.");
            return 2;
        }

        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: RestrictedLauncher <BrokerProbe.exe>");
            return 2;
        }

        try
        {
            return RunStatus(args[0]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.Message);
            return 1;
        }
    }

    private static int RunStatus(string probePath)
    {
        string fullProbePath = Path.GetFullPath(probePath);
        if (!File.Exists(fullProbePath))
            throw new FileNotFoundException("BrokerProbe.exe was not found.", fullProbePath);

        using SafeTokenHandle currentToken = OpenCurrentProcessToken(TokenQuery | TokenDuplicate | TokenAssignPrimary);
        string expectedUserSid = GetTokenUserSid(currentToken);
        uint expectedSessionId = GetTokenSessionId(currentToken);
        if (expectedSessionId != checked((uint)Process.GetCurrentProcess().SessionId))
            throw new InvalidOperationException("The runner token session does not match the current process session.");

        using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
        {
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("This launcher is intended for the elevated Windows runner token.");
        }

        using SafeLocalMemoryHandle administratorsSid = ConvertStringSid(AdministratorsSid);
        using SafeTokenHandle restrictedToken = CreateNonElevatedToken(currentToken, administratorsSid);
        ValidateRestrictedToken(restrictedToken, expectedUserSid, expectedSessionId, administratorsSid);

        string outputPath = Path.Combine(Path.GetTempPath(), $"NextDNS-BrokerProbe-{Guid.NewGuid():N}.log");
        string output;
        uint exitCode;
        try
        {
            using SafeFileHandle logHandle = CreateInheritableFile(outputPath);
            using SafeFileHandle inputHandle = OpenInheritableNullInput();
            ProcessInformation processInfo = StartProbe(fullProbePath, Directory.GetCurrentDirectory(), restrictedToken, inputHandle, logHandle);
            bool childExited = false;
            try
            {
                using SafeTokenHandle childToken = OpenProcessToken(processInfo.ProcessHandle, TokenQuery);
                ValidateRestrictedToken(childToken, expectedUserSid, expectedSessionId, administratorsSid);

                uint waitResult = NativeMethods.WaitForSingleObject(processInfo.ProcessHandle, 120_000);
                if (waitResult == WaitTimeout)
                    throw new TimeoutException("BrokerProbe --status exceeded the 120-second launcher timeout.");
                if (waitResult != WaitObject0)
                    throw LastWin32Error("WaitForSingleObject");
                childExited = true;
                if (!NativeMethods.GetExitCodeProcess(processInfo.ProcessHandle, out exitCode))
                    throw LastWin32Error("GetExitCodeProcess");
            }
            finally
            {
                if (!childExited)
                {
                    _ = NativeMethods.TerminateProcess(processInfo.ProcessHandle, 1);
                    _ = NativeMethods.WaitForSingleObject(processInfo.ProcessHandle, 5_000);
                }
                if (processInfo.ThreadHandle != IntPtr.Zero)
                    NativeMethods.CloseHandle(processInfo.ThreadHandle);
                if (processInfo.ProcessHandle != IntPtr.Zero)
                    NativeMethods.CloseHandle(processInfo.ProcessHandle);
            }

            inputHandle.Dispose();
            logHandle.Dispose();
            output = File.ReadAllText(outputPath);
        }
        finally
        {
            try { File.Delete(outputPath); } catch (IOException) { }
        }

        Console.WriteLine("Identity check passed: same user SID and session; Administrators is disabled.");
        Console.Write(output);

        if (exitCode != 0)
            throw new InvalidOperationException($"BrokerProbe --status exited with code {exitCode}.");
        if (!HasOutputLine(output, "Admin: false"))
            throw new InvalidOperationException("BrokerProbe did not confirm that its token is non-elevated.");
        if (!HasOutputLine(output, "Success: true"))
            throw new InvalidOperationException("BrokerProbe did not confirm a successful broker status response.");

        Console.WriteLine("PASS: Broker status was read by the same user in a restricted token.");
        return 0;
    }

    private static SafeTokenHandle OpenCurrentProcessToken(uint desiredAccess)
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), desiredAccess, out SafeTokenHandle token))
            throw LastWin32Error("OpenProcessToken");
        return token;
    }

    private static SafeTokenHandle OpenProcessToken(IntPtr processHandle, uint desiredAccess)
    {
        if (!NativeMethods.OpenProcessToken(processHandle, desiredAccess, out SafeTokenHandle token))
            throw LastWin32Error("OpenProcessToken");
        return token;
    }

    private static SafeTokenHandle CreateNonElevatedToken(SafeTokenHandle currentToken, SafeLocalMemoryHandle administratorsSid)
    {
        var sidToDisable = new SidAndAttributes { Sid = administratorsSid.DangerousGetHandle(), Attributes = 0 };
        // These explicit restrictions remove runner privileges and make the
        // Administrators group deny-only. The legacy LUA_TOKEN filter is unnecessary
        // here and can affect native process startup on hosted runners.
        if (!NativeMethods.CreateRestrictedToken(currentToken, DisableMaxPrivilege,
                1, ref sidToDisable, 0, IntPtr.Zero, 0, IntPtr.Zero, out SafeTokenHandle restrictedToken))
            throw LastWin32Error("CreateRestrictedToken");
        return restrictedToken;
    }

    private static void ValidateRestrictedToken(
        SafeTokenHandle token,
        string expectedUserSid,
        uint expectedSessionId,
        SafeLocalMemoryHandle administratorsSid)
    {
        string userSid = GetTokenUserSid(token);
        if (!string.Equals(userSid, expectedUserSid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The restricted token does not belong to the runner user.");

        uint sessionId = GetTokenSessionId(token);
        if (sessionId != expectedSessionId)
            throw new InvalidOperationException("The restricted token is not in the runner's session.");

        uint? adminAttributes = GetGroupAttributes(token, administratorsSid.DangerousGetHandle());
        if (adminAttributes is { } attributes && (attributes & GroupEnabled) != 0)
            throw new InvalidOperationException("The Administrators SID is still enabled in the restricted token.");
        if (adminAttributes is { } denyOnlyAttributes &&
            (denyOnlyAttributes & GroupUseForDenyOnly) == 0)
            throw new InvalidOperationException("The Administrators SID was neither disabled nor marked deny-only.");

        using SafeLocalMemoryHandle interactiveSid = ConvertStringSid(InteractiveSid);
        uint? interactiveAttributes = GetGroupAttributes(token, interactiveSid.DangerousGetHandle());
        if (interactiveAttributes is not { } interactive || (interactive & GroupEnabled) == 0)
            throw new InvalidOperationException("The restricted token is not an interactive-session token.");

        using SafeLocalMemoryHandle networkSid = ConvertStringSid(NetworkSid);
        uint? networkAttributes = GetGroupAttributes(token, networkSid.DangerousGetHandle());
        if (networkAttributes is { } network && (network & GroupEnabled) != 0)
            throw new InvalidOperationException("The restricted token is marked as a network logon.");
    }

    private static string GetTokenUserSid(SafeTokenHandle token)
    {
        using SafeTokenInfoBuffer buffer = GetTokenInformation(token, TokenInformationClass.User);
        IntPtr sid = Marshal.ReadIntPtr(buffer.DangerousGetHandle());
        if (sid == IntPtr.Zero) throw new InvalidOperationException("The token has no user SID.");

        if (!NativeMethods.ConvertSidToStringSid(sid, out IntPtr sidString))
            throw LastWin32Error("ConvertSidToStringSid");
        try
        {
            return Marshal.PtrToStringUni(sidString) ?? throw new InvalidOperationException("The token user SID could not be read.");
        }
        finally
        {
            NativeMethods.LocalFree(sidString);
        }
    }

    private static uint GetTokenSessionId(SafeTokenHandle token)
    {
        using SafeTokenInfoBuffer buffer = GetTokenInformation(token, TokenInformationClass.SessionId);
        return unchecked((uint)Marshal.ReadInt32(buffer.DangerousGetHandle()));
    }

    private static uint? GetGroupAttributes(SafeTokenHandle token, IntPtr sidToFind)
    {
        using SafeTokenInfoBuffer buffer = GetTokenInformation(token, TokenInformationClass.Groups);
        IntPtr baseAddress = buffer.DangerousGetHandle();
        uint groupCount = unchecked((uint)Marshal.ReadInt32(baseAddress));
        int groupOffset = Marshal.OffsetOf<TokenGroupsHeader>(nameof(TokenGroupsHeader.FirstGroup)).ToInt32();
        int groupSize = Marshal.SizeOf<SidAndAttributes>();

        for (uint index = 0; index < groupCount; index++)
        {
            IntPtr groupAddress = IntPtr.Add(baseAddress, checked(groupOffset + (int)index * groupSize));
            IntPtr groupSid = Marshal.ReadIntPtr(groupAddress);
            if (!NativeMethods.EqualSid(groupSid, sidToFind)) continue;
            return unchecked((uint)Marshal.ReadInt32(groupAddress, IntPtr.Size));
        }

        return null;
    }

    private static SafeTokenInfoBuffer GetTokenInformation(SafeTokenHandle token, TokenInformationClass informationClass)
    {
        _ = NativeMethods.GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out uint requiredLength);
        int error = Marshal.GetLastWin32Error();
        if (requiredLength == 0 || error != NativeMethods.ErrorInsufficientBuffer)
            throw new Win32Exception(error, "GetTokenInformation could not determine the buffer size.");

        var buffer = new SafeTokenInfoBuffer(Marshal.AllocHGlobal(checked((int)requiredLength)));
        if (!NativeMethods.GetTokenInformation(token, informationClass, buffer.DangerousGetHandle(), requiredLength, out _))
        {
            buffer.Dispose();
            throw LastWin32Error("GetTokenInformation");
        }
        return buffer;
    }

    private static ProcessInformation StartProbe(
        string probePath,
        string workingDirectory,
        SafeTokenHandle restrictedToken,
        SafeFileHandle inputHandle,
        SafeFileHandle outputHandle)
    {
        var startupInfo = new StartupInfoEx
        {
            StartupInfo = new StartupInfo
            {
                Size = checked((uint)Marshal.SizeOf<StartupInfoEx>()),
                Flags = StartfUseShowWindow | StartfUseStdHandles,
                ShowWindow = SwHide,
                StandardInput = inputHandle.DangerousGetHandle(),
                StandardOutput = outputHandle.DangerousGetHandle(),
                StandardError = outputHandle.DangerousGetHandle()
            }
        };
        var commandLine = new StringBuilder($"\"{probePath}\" --status");

        IntPtr attributeListSize = IntPtr.Zero;
        _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        if (Marshal.GetLastWin32Error() != NativeMethods.ErrorInsufficientBuffer || attributeListSize == IntPtr.Zero)
            throw LastWin32Error("InitializeProcThreadAttributeList(size)");

        IntPtr attributeList = Marshal.AllocHGlobal(attributeListSize);
        IntPtr inheritedHandles = Marshal.AllocHGlobal(2 * IntPtr.Size);
        bool attributeListInitialized = false;
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                throw LastWin32Error("InitializeProcThreadAttributeList");
            attributeListInitialized = true;

            Marshal.WriteIntPtr(inheritedHandles, 0, inputHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, outputHandle.DangerousGetHandle());
            if (!NativeMethods.UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList,
                    inheritedHandles, new IntPtr(2 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw LastWin32Error("UpdateProcThreadAttribute(handle list)");
            startupInfo.AttributeList = attributeList;

            if (!NativeMethods.CreateProcessAsUser(restrictedToken, probePath, commandLine,
                    IntPtr.Zero, IntPtr.Zero, true, CreateNoWindow | ExtendedStartupInfoPresent,
                    IntPtr.Zero, workingDirectory, ref startupInfo, out ProcessInformation processInfo))
                throw LastWin32Error("CreateProcessAsUser");
            return processInfo;
        }
        finally
        {
            if (attributeListInitialized)
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(inheritedHandles);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private static SafeFileHandle CreateInheritableFile(string path)
    {
        var attributes = new SecurityAttributes
        {
            Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
            InheritHandle = true
        };
        SafeFileHandle handle = NativeMethods.CreateFile(path, 0x40000000, 0x00000007,
            ref attributes, 1, 0x00000100, IntPtr.Zero);
        if (handle.IsInvalid) throw LastWin32Error("CreateFile(output log)");
        return handle;
    }

    private static SafeFileHandle OpenInheritableNullInput()
    {
        var attributes = new SecurityAttributes
        {
            Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
            InheritHandle = true
        };
        SafeFileHandle handle = NativeMethods.CreateFile("NUL", 0x80000000, 0x00000003,
            ref attributes, 3, 0x00000080, IntPtr.Zero);
        if (handle.IsInvalid) throw LastWin32Error("CreateFile(NUL)");
        return handle;
    }

    private static SafeLocalMemoryHandle ConvertStringSid(string sid)
    {
        if (!NativeMethods.ConvertStringSidToSid(sid, out IntPtr sidPointer))
            throw LastWin32Error("ConvertStringSidToSid");
        return new SafeLocalMemoryHandle(sidPointer);
    }

    private static bool HasOutputLine(string output, string expectedLine) =>
        output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), expectedLine, StringComparison.Ordinal));

    private static Exception LastWin32Error(string operation) =>
        new Win32Exception(Marshal.GetLastWin32Error(), operation + " failed.");

    private enum TokenInformationClass
    {
        User = 1,
        Groups = 2,
        SessionId = 12
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        internal IntPtr Sid;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroupsHeader
    {
        internal uint GroupCount;
        internal SidAndAttributes FirstGroup;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal uint Size;
        internal IntPtr Reserved;
        internal IntPtr Desktop;
        internal IntPtr Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort Reserved2Size;
        internal IntPtr Reserved2;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr ProcessHandle;
        internal IntPtr ThreadHandle;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal uint Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    private sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeTokenHandle() : base(true) { }
        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private sealed class SafeTokenInfoBuffer : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeTokenInfoBuffer(IntPtr pointer) : base(true) => SetHandle(pointer);
        protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); return true; }
    }

    private sealed class SafeLocalMemoryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeLocalMemoryHandle(IntPtr pointer) : base(true) => SetHandle(pointer);
        protected override bool ReleaseHandle() => NativeMethods.LocalFree(handle) == IntPtr.Zero;
    }

    private static class NativeMethods
    {
        internal const int ErrorInsufficientBuffer = 122;

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeTokenHandle tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateRestrictedToken(
            SafeTokenHandle existingToken,
            uint flags,
            uint disableSidCount,
            ref SidAndAttributes sidsToDisable,
            uint deletePrivilegeCount,
            IntPtr privilegesToDelete,
            uint restrictedSidCount,
            IntPtr sidsToRestrict,
            out SafeTokenHandle newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(
            SafeTokenHandle tokenHandle,
            TokenInformationClass tokenInformationClass,
            IntPtr tokenInformation,
            uint tokenInformationLength,
            out uint returnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EqualSid(IntPtr sid1, IntPtr sid2);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            ref SecurityAttributes securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, uint flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            uint attribute,
            IntPtr value,
            IntPtr size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessAsUserW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessAsUser(
            SafeTokenHandle token,
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
