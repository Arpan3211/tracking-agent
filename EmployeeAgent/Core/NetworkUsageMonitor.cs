using System.Net.NetworkInformation;

namespace EmployeeAgent.Core;

/// <summary>
/// Reports bytes sent/received since the last poll, summed across all
/// active (non-loopback) network interfaces. This is MACHINE-WIDE network
/// usage, not per-app - Windows doesn't expose a simple, reliable per-app
/// bandwidth API without much deeper (and fragile) packet-capture work.
/// </summary>
public sealed class NetworkUsageMonitor
{
    private readonly ActivityLogger _logger;
    private long _lastBytesSent;
    private long _lastBytesReceived;
    private bool _initialized;

    public NetworkUsageMonitor(ActivityLogger logger)
    {
        _logger = logger;
    }

    public void Poll()
    {
        long totalSent = 0;
        long totalReceived = 0;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var stats = nic.GetIPv4Statistics();
            totalSent += stats.BytesSent;
            totalReceived += stats.BytesReceived;
        }

        if (_initialized)
        {
            var deltaSent = Math.Max(0, totalSent - _lastBytesSent);
            var deltaReceived = Math.Max(0, totalReceived - _lastBytesReceived);

            if (deltaSent > 0 || deltaReceived > 0)
            {
                _logger.Log("network_usage", $"bytes_sent={deltaSent}; bytes_received={deltaReceived}");
            }
        }

        _lastBytesSent = totalSent;
        _lastBytesReceived = totalReceived;
        _initialized = true;
    }
}
