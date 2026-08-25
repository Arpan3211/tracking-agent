using System.Runtime.InteropServices;

namespace EmployeeAgent.Core;

/// <summary>
/// Reports how long the machine has had no keyboard/mouse input, using the
/// native Win32 GetLastInputInfo API. This is the standard, reliable way to
/// measure idle time on Windows - it works regardless of which app has
/// focus.
/// </summary>
public static class IdleTimeMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static TimeSpan GetIdleTime()
    {
        var lastInput = new LASTINPUTINFO();
        lastInput.cbSize = (uint)Marshal.SizeOf(lastInput);

        if (!GetLastInputInfo(ref lastInput))
            return TimeSpan.Zero;

        uint idleTicks = unchecked((uint)Environment.TickCount - lastInput.dwTime);
        return TimeSpan.FromMilliseconds(idleTicks);
    }
}
