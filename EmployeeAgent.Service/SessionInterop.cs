using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EmployeeAgent.Service;

/// <summary>
/// P/Invoke wrappers for the standard "SYSTEM service launches a process
/// into a user's interactive session" pattern: WTSQueryUserToken +
/// DuplicateTokenEx + CreateEnvironmentBlock + CreateProcessAsUser. This is
/// necessary because a plain CreateProcess call from a Session-0 service
/// would either fail or launch invisibly in Session 0 - it would never reach
/// the logged-on user's desktop, which is exactly why screenshot/window
/// capture can't live in the service itself.
/// </summary>
internal static class SessionInterop
{
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int CREATE_NEW_CONSOLE = 0x00000010;

    private enum WTS_CONNECTSTATE_CLASS
    {
        Active,
        Connected,
        ConnectQuery,
        Shadow,
        Disconnected,
        Idle,
        Listen,
        Reset,
        Down,
        Init
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionID;
        [MarshalAs(UnmanagedType.LPStr)] public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private enum SECURITY_IMPERSONATION_LEVEL { Anonymous, Identification, Impersonation, Delegation }
    private enum TOKEN_TYPE { TokenPrimary = 1, TokenImpersonation = 2 }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel, TOKEN_TYPE tokenType, out IntPtr phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken, string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, int dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static List<int> GetActiveInteractiveSessionIds()
    {
        var sessionIds = new List<int>();

        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessionInfoPtr, out var count))
            return sessionIds;

        try
        {
            var structSize = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var current = Marshal.PtrToStructure<WTS_SESSION_INFO>(sessionInfoPtr + i * structSize);
                if (current.State == WTS_CONNECTSTATE_CLASS.Active)
                    sessionIds.Add(current.SessionID);
            }
        }
        finally
        {
            WTSFreeMemory(sessionInfoPtr);
        }

        return sessionIds;
    }

    public static bool TryLaunchProcessInSession(int sessionId, string exePath, out string error)
    {
        error = "";
        var userToken = IntPtr.Zero;
        var duplicatedToken = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;

        try
        {
            if (!WTSQueryUserToken((uint)sessionId, out userToken))
            {
                error = $"WTSQueryUserToken failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return false;
            }

            if (!DuplicateTokenEx(userToken, TOKEN_ALL_ACCESS, IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.Impersonation, TOKEN_TYPE.TokenPrimary, out duplicatedToken))
            {
                error = $"DuplicateTokenEx failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return false;
            }

            CreateEnvironmentBlock(out environmentBlock, duplicatedToken, false);

            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = "winsta0\\default"
            };

            const int creationFlags = CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE;

            var created = CreateProcessAsUser(
                duplicatedToken, exePath, $"\"{exePath}\"", IntPtr.Zero, IntPtr.Zero, false,
                creationFlags, environmentBlock, Path.GetDirectoryName(exePath), ref startupInfo,
                out var processInfo);

            if (!created)
            {
                error = $"CreateProcessAsUser failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return false;
            }

            CloseHandle(processInfo.hProcess);
            CloseHandle(processInfo.hThread);
            return true;
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero) DestroyEnvironmentBlock(environmentBlock);
            if (duplicatedToken != IntPtr.Zero) CloseHandle(duplicatedToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }
}
