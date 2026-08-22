using System.Text.Json;

namespace EmployeeAgent.Core;

/// <summary>
/// Writes activity events to a local JSON-lines log file.
/// This stands in for the "local buffer + upload to backend" step from the
/// full architecture - for this MVP we just prove the events are captured
/// correctly. Swap Log()'s body for an HTTP POST (or SQLite insert) later
/// without touching any of the callers.
/// </summary>
public sealed class ActivityLogger
{
    private readonly string _logFilePath;
    private readonly object _lock = new();

    public string LogFilePath => _logFilePath;

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
    }

    public void Log(string eventType, string? details = null)
    {
        var evt = new ActivityEvent(eventType, DateTime.UtcNow, details);
        var line = JsonSerializer.Serialize(evt);

        lock (_lock)
        {
            File.AppendAllText(_logFilePath, line + Environment.NewLine);
        }

        // Also print to console when run via `dotnet run`, useful while testing
        Console.WriteLine($"[{evt.TimestampUtc:O}] {eventType} {details}");
    }
}
