using System.Text.Json;

namespace EmployeeAgent.Core;

/// <summary>
/// Writes activity events to a local JSON-lines log file. This is the
/// write-ahead buffer: SyncLoop (see SyncLoop.cs) reads from this same file
/// and ships unsent lines to the backend, but this class never talks to the
/// network itself and the file is never truncated - monitors keep logging
/// locally regardless of whether the backend is reachable.
/// </summary>
public sealed class ActivityLogger
{
    private readonly string _logFilePath;
    private readonly object _lock = new();
    private long _totalLinesWritten;

    public string LogFilePath => _logFilePath;

    /// Total lines ever written to the log file, including from previous
    /// process runs (counted once at startup, then tracked incrementally) -
    /// this is the same numbering space SyncLoop's "last synced line"
    /// pointer uses, so it has to reflect the file's real history, not just
    /// what this process instance has written since it started.
    public long TotalLinesWritten
    {
        get { lock (_lock) return _totalLinesWritten; }
    }

    public ActivityLogger()
    {
        // For the pilot on multiple laptops: set EMPLOYEEAGENT_LOG_DIR to a
        // shared OneDrive/Google Drive/network folder if you want logs to
        // land in one central place automatically instead of collecting
        // them manually. Leave it unset for the normal local default.
        //
        //   setx EMPLOYEEAGENT_LOG_DIR "C:\Users\you\OneDrive\EmployeeAgentPilot"
        //
        var overrideDir = Environment.GetEnvironmentVariable("EMPLOYEEAGENT_LOG_DIR");

        var folder = !string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EmployeeAgent");

        Directory.CreateDirectory(folder);

        // Machine name in the filename so 3 devices writing into the same
        // shared folder produce 3 separate files instead of one corrupted
        // shared file (multiple processes appending to one file isn't safe).
        var fileName = $"activity_{Environment.MachineName}.log";
        _logFilePath = Path.Combine(folder, fileName);

        _totalLinesWritten = File.Exists(_logFilePath) ? File.ReadLines(_logFilePath).Count() : 0;
    }

    public void Log(string eventType, string? details = null)
    {
        var evt = new ActivityEvent(eventType, DateTime.UtcNow, details);
        var line = JsonSerializer.Serialize(evt);

        lock (_lock)
        {
            File.AppendAllText(_logFilePath, line + Environment.NewLine);
            _totalLinesWritten++;
        }

        // Also print to console when run via `dotnet run`, useful while testing
        Console.WriteLine($"[{evt.TimestampUtc:O}] {eventType} {details}");
    }

    /// Reads every line from startLine (0-indexed) to the current end of
    /// file. Takes the same lock Log() writes under, so a caller never sees
    /// a torn read mid-append.
    public IReadOnlyList<string> ReadLinesFrom(long startLine)
    {
        lock (_lock)
        {
            if (!File.Exists(_logFilePath)) return Array.Empty<string>();
            return File.ReadLines(_logFilePath).Skip((int)startLine).ToList();
        }
    }
}
