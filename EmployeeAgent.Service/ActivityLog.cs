using System.Text.Json;

namespace EmployeeAgent.Service;

/// <summary>
/// Duplicated log-path/write logic matching ActivityLogger in the main
/// EmployeeAgent project (and the same pattern EmployeeAgent.NativeHost
/// uses) - kept independent on purpose so this service has no assembly
/// dependency on the per-user agent process it supervises.
/// </summary>
internal static class ActivityLog
{
    public static void Write(string eventType, string? details)
    {
        try
        {
            var overrideDir = Environment.GetEnvironmentVariable("EMPLOYEEAGENT_LOG_DIR");

            var folder = !string.IsNullOrWhiteSpace(overrideDir)
                ? overrideDir
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "EmployeeAgent");

            Directory.CreateDirectory(folder);
            var logPath = Path.Combine(folder, $"activity_{Environment.MachineName}.log");

            var evt = new { EventType = eventType, TimestampUtc = DateTime.UtcNow, Details = details };
            File.AppendAllText(logPath, JsonSerializer.Serialize(evt) + Environment.NewLine);
        }
        catch
        {
            // Must never crash the service because logging failed.
        }
    }
}
