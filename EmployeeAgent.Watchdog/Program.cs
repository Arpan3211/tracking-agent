using System.Diagnostics;
using System.Text.Json;

// ---------------------------------------------------------------------
// BASIC anti-tamper watchdog.
//
// This is intentionally simple: a separate process that checks whether
// EmployeeAgent.exe is running, and relaunches it if not. It is NOT a
// complete anti-tamper solution - a determined user could still kill
// both processes, or task-kill this watchdog itself. True production-
// grade tamper resistance means converting the agent into a proper
// Windows Service with configured recovery actions (auto-restart on
// crash via `sc failure`), running under SYSTEM, and restricting who can
// stop the service via Group Policy. That's a bigger follow-up step -
// this watchdog covers the "basic" tier called out in the feature list.
// ---------------------------------------------------------------------

const string AgentProcessName = "EmployeeAgent";

// IMPORTANT: update this to wherever EmployeeAgent.exe actually ends up
// after you build/publish it (e.g. bin\Release\net8.0-windows\ or your
// chosen install folder).
const string AgentExePath = @"C:\Program Files\EmployeeAgent\EmployeeAgent.exe";

var checkInterval = TimeSpan.FromSeconds(30);

LogWatchdogEvent("watchdog_started", $"checking every {checkInterval.TotalSeconds}s, target={AgentExePath}");
Console.WriteLine("Watchdog started. Monitoring for EmployeeAgent.exe ...");

while (true)
{
    var isRunning = Process.GetProcessesByName(AgentProcessName).Length > 0;

    if (!isRunning)
    {
        Console.WriteLine($"[{DateTime.UtcNow:O}] Agent not running - attempting restart...");

        if (File.Exists(AgentExePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(AgentExePath) { UseShellExecute = true });
                LogWatchdogEvent("agent_restarted_by_watchdog", $"path={AgentExePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to restart agent: {ex.Message}");
                LogWatchdogEvent("agent_restart_failed", ex.Message);
            }
        }
        else
        {
            Console.WriteLine($"Agent exe not found at {AgentExePath} - update AgentExePath above to your actual build/install path.");
        }
    }

    Thread.Sleep(checkInterval);
}

// Writes to the SAME activity.log the main agent uses, in the same
// JSON-lines schema, so watchdog events show up in the generated report
// alongside everything else.
static void LogWatchdogEvent(string eventType, string? details)
{
    try
    {
        // Must match ActivityLogger's logic exactly so watchdog events land
        // in the SAME file the agent writes to, not a separate one.
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
        // Watchdog must never crash because logging failed - swallow and move on.
    }
}
