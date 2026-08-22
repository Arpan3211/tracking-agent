using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace EmployeeAgent.Core;

/// <summary>
/// Polls the foreground (active) window every few seconds and logs an
/// "app_focus_change" event whenever the user switches to a different
/// app/window. App usage DURATION isn't computed here - it's derived later
/// by the report generator from the time between consecutive events.
///
/// Website domain extraction is best-effort only: it looks for a
/// domain-like pattern inside the window title. Modern browsers frequently
/// show the page title instead of the URL, so this will often come back
/// empty - reliable full-URL tracking needs a browser extension (a
/// separate component, not part of this agent).
/// </summary>
public sealed class WindowActivityMonitor
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static readonly Regex DomainPattern = new(
        @"([a-z0-9-]+\.)+(com|org|net|io|co|in|dev|app|gov|edu)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> KnownBrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera"
    };

    private readonly ActivityLogger _logger;
    private string? _lastProcessName;
    private string? _lastWindowTitle;

    public WindowActivityMonitor(ActivityLogger logger)
    {
        _logger = logger;
    }

    public void Poll()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return;

        var titleBuilder = new StringBuilder(256);
        GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
        var windowTitle = titleBuilder.ToString();

        GetWindowThreadProcessId(hWnd, out uint pid);

        string processName;
        try
        {
            processName = Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            processName = "unknown";
        }

        // Only log when the focused app or window title actually changes -
        // avoids writing a duplicate line every single poll interval.
        if (processName == _lastProcessName && windowTitle == _lastWindowTitle)
            return;

        _lastProcessName = processName;
        _lastWindowTitle = windowTitle;

        string? domain = null;
        if (KnownBrowserProcesses.Contains(processName))
        {
            var match = DomainPattern.Match(windowTitle);
            if (match.Success)
                domain = match.Value.ToLowerInvariant();
        }

        var details = domain is not null
            ? $"process={processName}; title={windowTitle}; domain={domain}"
            : $"process={processName}; title={windowTitle}";

        _logger.Log("app_focus_change", details);
    }
}
