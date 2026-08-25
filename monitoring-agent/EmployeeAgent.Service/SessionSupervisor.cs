using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmployeeAgent.Service;

/// <summary>
/// The Windows Service's only real job: make sure exactly one EmployeeAgent.exe
/// is running in every active interactive session, relaunching it into that
/// session if it's missing (killed, crashed, or the session just logged on).
/// This is the OS-enforced replacement for the old EmployeeAgent.Watchdog
/// process - Windows Service Control Manager restarts THIS service if it's
/// killed (configured via install-service.ps1's `sc failure` policy), and
/// this class in turn restarts the per-user agent if that's killed. A SYSTEM
/// service alone can't do the agent's actual job (it's confined to Session 0
/// and can't capture the interactive user's desktop) - hence the split.
/// </summary>
public sealed class SessionSupervisor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const string AgentProcessName = "EmployeeAgent";

    // IMPORTANT: update this to wherever EmployeeAgent.exe actually ends up
    // after you build/publish it - must match install-service.ps1's target.
    private const string AgentExePath = @"C:\Program Files\EmployeeAgent\EmployeeAgent.exe";

    private readonly ILogger<SessionSupervisor> _logger;

    public SessionSupervisor(ILogger<SessionSupervisor> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ActivityLog.Write("service_started", new() { ["poll_interval_seconds"] = PollInterval.TotalSeconds.ToString(), ["target"] = AgentExePath });

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                SuperviseSessions();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session supervision pass failed");
                ActivityLog.Write("session_agent_relaunch_failed", new() { ["message"] = ex.Message });
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        ActivityLog.Write("service_stopped", null);
    }

    private void SuperviseSessions()
    {
        if (!File.Exists(AgentExePath))
        {
            _logger.LogWarning("Agent exe not found at {Path}", AgentExePath);
            return;
        }

        var activeSessionIds = SessionInterop.GetActiveInteractiveSessionIds();

        var runningAgentSessionIds = Process.GetProcessesByName(AgentProcessName)
            .Select(p =>
            {
                try { return p.SessionId; }
                catch { return -1; }
            })
            .ToHashSet();

        foreach (var sessionId in activeSessionIds)
        {
            if (runningAgentSessionIds.Contains(sessionId)) continue;

            _logger.LogInformation("No agent running in session {SessionId} - launching", sessionId);

            if (SessionInterop.TryLaunchProcessInSession(sessionId, AgentExePath, out var error))
            {
                ActivityLog.Write("session_agent_relaunched", new() { ["session_id"] = sessionId.ToString(), ["path"] = AgentExePath });
            }
            else
            {
                ActivityLog.Write("session_agent_relaunch_failed", new() { ["session_id"] = sessionId.ToString(), ["error"] = error });
            }
        }
    }
}
