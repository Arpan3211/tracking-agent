using System.Management;

namespace EmployeeAgent.Core;

/// <summary>
/// Listens for USB/device arrival and removal using WMI
/// (Win32_DeviceChangeEvent). Requires the System.Management NuGet
/// package, already added to the .csproj.
/// </summary>
public sealed class DeviceActivityMonitor : IDisposable
{
    private readonly ActivityLogger _logger;
    private ManagementEventWatcher? _watcher;

    public DeviceActivityMonitor(ActivityLogger logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        try
        {
            var query = new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent");
            _watcher = new ManagementEventWatcher(query);
            _watcher.EventArrived += OnDeviceChange;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            _logger.Log("device_monitor_failed", ex.Message);
        }
    }

    private void OnDeviceChange(object sender, EventArrivedEventArgs e)
    {
        // EventType per Win32_DeviceChangeEvent: 2 = device arrival, 3 = device removal
        var eventTypeCode = e.NewEvent["EventType"]?.ToString() ?? "unknown";
        var eventType = eventTypeCode switch
        {
            "2" => "device_connected",
            "3" => "device_disconnected",
            _ => "device_change_other"
        };

        _logger.Log(eventType, $"event_type_code={eventTypeCode}");
    }

    public void Dispose()
    {
        _watcher?.Stop();
        _watcher?.Dispose();
    }
}
