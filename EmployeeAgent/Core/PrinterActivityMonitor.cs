using System.Management;

namespace EmployeeAgent.Core;

/// <summary>
/// Polls Win32_PrintJob via WMI to track print jobs. Polling (rather than a
/// WQL event query, as DeviceActivityMonitor uses for USB) is deliberate here -
/// print-job event queries are known to be flaky across print spooler/driver
/// combinations, while polling the job collection is simple and reliable.
/// A job is "submitted" the first time its JobId is seen and "completed" the
/// first poll after its JobId disappears from the collection.
/// </summary>
public sealed class PrinterActivityMonitor
{
    private readonly ActivityLogger _logger;
    private readonly HashSet<uint> _seenJobIds = new();
    private readonly Dictionary<uint, string> _jobDetailsById = new();

    public PrinterActivityMonitor(ActivityLogger logger)
    {
        _logger = logger;
    }

    public void Poll()
    {
        var currentJobIds = new HashSet<uint>();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PrintJob");
            using var results = searcher.Get();

            foreach (ManagementObject job in results)
            {
                var jobId = Convert.ToUInt32(job["JobId"]);
                currentJobIds.Add(jobId);

                if (_seenJobIds.Add(jobId))
                {
                    var document = job["Document"]?.ToString() ?? "unknown";
                    var owner = job["Owner"]?.ToString() ?? "unknown";
                    var printerName = job["Name"]?.ToString() ?? "unknown";
                    var totalPages = job["TotalPages"]?.ToString() ?? "unknown";

                    var details = $"document={document}; owner={owner}; printer={printerName}; total_pages={totalPages}";
                    _jobDetailsById[jobId] = details;
                    _logger.Log("print_job_submitted", details);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log("printer_monitor_failed", ex.Message);
            return;
        }

        // Any job we'd previously seen that's no longer in the collection has finished.
        var finishedJobIds = _seenJobIds.Where(id => !currentJobIds.Contains(id)).ToList();
        foreach (var jobId in finishedJobIds)
        {
            var details = _jobDetailsById.GetValueOrDefault(jobId, $"job_id={jobId}");
            _logger.Log("print_job_completed", details);
            _seenJobIds.Remove(jobId);
            _jobDetailsById.Remove(jobId);
        }
    }
}
