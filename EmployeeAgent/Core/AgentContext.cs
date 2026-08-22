using Microsoft.Win32;

namespace EmployeeAgent.Core;

/// <summary>
/// Runs with no visible window. Wires up every monitor and owns their
/// polling timers. This is the single place that knows about all features -
/// each monitor class is independent and only knows how to do its own job.
/// </summary>
public sealed class AgentContext : ApplicationContext
{
    // MVP defaults - move these to a config file once this graduates past testing
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WindowPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NetworkPollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScreenshotInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan LocationPollInterval = TimeSpan.FromMinutes(60);

    private readonly ActivityLogger _logger = new();
    private readonly Form _hiddenForm;
    private bool _isCurrentlyIdle;

    private readonly WindowActivityMonitor _windowMonitor;
    private readonly FileActivityMonitor _fileMonitor;
    private readonly ScreenshotCapture _screenshotCapture;
    private readonly NetworkUsageMonitor _networkMonitor;
    private readonly DeviceActivityMonitor _deviceMonitor;
    private readonly IpLocationMonitor _locationMonitor;

    private readonly System.Windows.Forms.Timer _idleTimer;
    private readonly System.Windows.Forms.Timer _windowTimer;
    private readonly System.Windows.Forms.Timer _networkTimer;
    private readonly System.Windows.Forms.Timer _screenshotTimer;
    private readonly System.Windows.Forms.Timer _locationTimer;

    public AgentContext()
    {
        // A real (but invisible) form is required to host the message loop
        // that SystemEvents relies on to deliver session-switch notifications.
        _hiddenForm = new Form
        {
            ShowInTaskbar = false,
            WindowState = FormWindowState.Minimized,
            Opacity = 0,
            FormBorderStyle = FormBorderStyle.FixedToolWindow
        };
        _hiddenForm.Load += (_, _) => _hiddenForm.Hide();
        _hiddenForm.Show();
        _hiddenForm.Hide();

        SystemEvents.SessionSwitch += OnSessionSwitch;

        _logger.Log("agent_started", $"user={Environment.UserName}, machine={Environment.MachineName}");

        _windowMonitor = new WindowActivityMonitor(_logger);
        _fileMonitor = new FileActivityMonitor(_logger);
        _screenshotCapture = new ScreenshotCapture(_logger);
        _networkMonitor = new NetworkUsageMonitor(_logger);
        _deviceMonitor = new DeviceActivityMonitor(_logger);
        _locationMonitor = new IpLocationMonitor(_logger);

        _deviceMonitor.Start();

        _idleTimer = StartTimer(IdlePollInterval, (_, _) => CheckIdleState());
        _windowTimer = StartTimer(WindowPollInterval, (_, _) => _windowMonitor.Poll());
        _networkTimer = StartTimer(NetworkPollInterval, (_, _) => _networkMonitor.Poll());
        _screenshotTimer = StartTimer(ScreenshotInterval, (_, _) => _screenshotCapture.CaptureNow());
        _locationTimer = StartTimer(LocationPollInterval, async (_, _) => await _locationMonitor.PollAsync());

        // Fire one location lookup immediately at startup too, don't wait a full hour
        _ = _locationMonitor.PollAsync();
    }

    private static System.Windows.Forms.Timer StartTimer(TimeSpan interval, EventHandler handler)
    {
        var timer = new System.Windows.Forms.Timer { Interval = (int)interval.TotalMilliseconds };
        timer.Tick += handler;
        timer.Start();
        return timer;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLogon:
                _logger.Log("login", Environment.UserName);
                break;
            case SessionSwitchReason.SessionLogoff:
                _logger.Log("logout", Environment.UserName);
                break;
            case SessionSwitchReason.SessionLock:
                _logger.Log("screen_locked");
                break;
            case SessionSwitchReason.SessionUnlock:
                _logger.Log("screen_unlocked");
                break;
            case SessionSwitchReason.RemoteConnect:
                _logger.Log("remote_connect");
                break;
            case SessionSwitchReason.RemoteDisconnect:
                _logger.Log("remote_disconnect");
                break;
        }
    }

    private void CheckIdleState()
    {
        var idleFor = IdleTimeMonitor.GetIdleTime();

        if (idleFor >= IdleThreshold && !_isCurrentlyIdle)
        {
            _isCurrentlyIdle = true;
            _logger.Log("idle_start", $"idle_for_seconds={idleFor.TotalSeconds:F0}");
        }
        else if (idleFor < IdleThreshold && _isCurrentlyIdle)
        {
            _isCurrentlyIdle = false;
            _logger.Log("idle_end");
        }
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;

        _idleTimer.Stop();
        _windowTimer.Stop();
        _networkTimer.Stop();
        _screenshotTimer.Stop();
        _locationTimer.Stop();

        _fileMonitor.Dispose();
        _deviceMonitor.Dispose();

        _logger.Log("agent_stopped");
        _hiddenForm.Dispose();
        base.ExitThreadCore();
    }
}
