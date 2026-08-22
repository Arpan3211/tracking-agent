using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace EmployeeAgent.Core;

/// <summary>
/// Attributes network bytes sent/received to individual processes using
/// Event Tracing for Windows (Microsoft-Windows-Kernel-Network provider, via
/// the Microsoft.Diagnostics.Tracing.TraceEvent package) - the only practical
/// way to get real per-process bandwidth on Windows without a packet-capture
/// driver. TCP and UDP send/receive events carry the owning process ID, which
/// public networking APIs don't expose per byte.
///
/// Requires running elevated (Administrator): starting a kernel ETW trace
/// session is an admin-only operation. If elevation is missing, this monitor
/// logs a single failure event and reports nothing further.
///
/// NOTE: exact TcpIpSend/TcpIpRecv/UdpIpSend/UdpIpRecv event shapes come from
/// the TraceEvent NuGet package's KernelTraceEventParser - verify field names
/// against the installed package version on first Windows build (minor shape
/// changes have happened across TraceEvent releases). Loopback/UDP edge cases
/// may still be undercounted; this is a best-effort attribution, not a
/// packet-perfect accounting.
/// </summary>
public sealed class NetworkUsageMonitor : IDisposable
{
    private readonly ActivityLogger _logger;
    private readonly Dictionary<int, (long sent, long received)> _bytesByProcessId = new();
    private readonly object _lock = new();
    private TraceEventSession? _session;
    private Thread? _processingThread;

    public NetworkUsageMonitor(ActivityLogger logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        if (!(TraceEventSession.IsElevated() ?? false))
        {
            _logger.Log("network_monitor_failed", "must run elevated (Administrator) to trace per-process network usage");
            return;
        }

        try
        {
            _session = new TraceEventSession("EmployeeAgentNetworkSession");
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            _session.Source.Kernel.TcpIpSend += data => RecordBytes(data.ProcessID, sent: data.size, received: 0);
            _session.Source.Kernel.TcpIpRecv += data => RecordBytes(data.ProcessID, sent: 0, received: data.size);
            _session.Source.Kernel.UdpIpSend += data => RecordBytes(data.ProcessID, sent: data.size, received: 0);
            _session.Source.Kernel.UdpIpRecv += data => RecordBytes(data.ProcessID, sent: 0, received: data.size);

            _processingThread = new Thread(() => _session.Source.Process()) { IsBackground = true };
            _processingThread.Start();
        }
        catch (Exception ex)
        {
            _logger.Log("network_monitor_failed", ex.Message);
        }
    }

    private void RecordBytes(int processId, long sent, long received)
    {
        lock (_lock)
        {
            var (existingSent, existingReceived) = _bytesByProcessId.GetValueOrDefault(processId, (0L, 0L));
            _bytesByProcessId[processId] = (existingSent + sent, existingReceived + received);
        }
    }

    /// <summary>
    /// Flushes accumulated per-process byte counts to the log. Called on a
    /// timer from AgentContext (same 30s cadence the old whole-machine poll
    /// used), rather than logging on every single ETW event.
    /// </summary>
    public void Flush()
    {
        Dictionary<int, (long sent, long received)> snapshot;
        lock (_lock)
        {
            if (_bytesByProcessId.Count == 0) return;
            snapshot = new Dictionary<int, (long sent, long received)>(_bytesByProcessId);
            _bytesByProcessId.Clear();
        }

        foreach (var (processId, counts) in snapshot)
        {
            if (counts.sent == 0 && counts.received == 0) continue;

            string processName;
            try
            {
                processName = Process.GetProcessById(processId).ProcessName;
            }
            catch
            {
                processName = $"pid_{processId}";
            }

            _logger.Log("network_usage", $"process={processName}; bytes_sent={counts.sent}; bytes_received={counts.received}");
        }
    }

    public void Dispose()
    {
        _session?.Stop();
        _session?.Dispose();
    }
}
