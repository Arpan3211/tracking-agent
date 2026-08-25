using System.Runtime.InteropServices;

namespace EmployeeAgent.Core;

/// <summary>
/// Resolves the actual Windows logon time for the current session via the
/// WTS API. This exists because SystemEvents.SessionSwitch (see
/// AgentContext.OnSessionSwitch) only delivers FUTURE transitions after the
/// process subscribes to it - and this agent is launched by the anti-tamper
/// service up to ~30s AFTER the user's session already logged on, so it can
/// never see its own session's SessionLogon via that event. Querying the
/// session's LogonTime directly at startup gets the real, historical logon
/// timestamp regardless of when the agent itself started.
///
/// Uses WTS_INFO_CLASS.WTSSessionInfo (24, the full WTSINFO struct), not the
/// narrower WTSLogonTime (18) - the latter looks like the obvious choice but
/// returns ERROR_NOT_SUPPORTED (Win32 error 50) for local console sessions
/// on an ordinary desktop OS (confirmed by hand against this machine's real
/// session - it's a Terminal-Services-only info class in practice). The
/// full struct's LogonTime field works correctly for both local and RDP
/// sessions and was verified against Win32_LogonSession.StartTime for the
/// exact same session (matched to the second) before shipping this.
///
/// CharSet.Unicode on the DllImport is required, not cosmetic: without it,
/// the OS silently returns the narrower ANSI WTSINFOA structure instead
/// (216 bytes vs 144), and reading that back through a Unicode-shaped
/// struct definition misaligns every field after the fixed-size name
/// strings - LogonTime comes out as garbage (a bogus 17th-century date in
/// testing) with no error raised anywhere. Struct size mismatches like this
/// fail silently, not loudly - always mark the byte count.
/// </summary>
internal static class SessionInfo
{
    private const int WtsCurrentSession = -1;
    private const int WtsSessionInfo = 24;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTSINFO
    {
        public int State;
        public int SessionId;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
        public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
        public string UserName;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    public static DateTime? GetSessionLogonTimeUtc()
    {
        // hServer=IntPtr.Zero means the local server (WTS_CURRENT_SERVER_HANDLE).
        if (!WTSQuerySessionInformation(IntPtr.Zero, WtsCurrentSession, WtsSessionInfo, out var buffer, out var bytesReturned))
            return null;

        try
        {
            if (bytesReturned != Marshal.SizeOf<WTSINFO>()) return null; // wrong struct shape - don't trust it

            var info = Marshal.PtrToStructure<WTSINFO>(buffer);
            return info.LogonTime > 0 ? DateTime.FromFileTimeUtc(info.LogonTime) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }
}
