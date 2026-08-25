using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace EmployeeAgent.Core;

/// <summary>
/// Watches Desktop, Documents, and Downloads for file create/rename/change/
/// delete events via FileSystemWatcher, plus real file-OPEN events via ETW
/// (Microsoft-Windows-Kernel-File provider) - FileSystemWatcher structurally
/// cannot see opens, since it only reports filesystem-level changes.
///
/// Known limitation: Windows' Changed event can fire multiple times for a
/// single save (common with editors that write in chunks). No debouncing is
/// applied here to keep the MVP simple - the report generator just counts
/// raw events. The ETW open-tracking also fires on read-only opens (e.g. a
/// user just viewing a file), not just edits - "file_opened" means a handle
/// was opened, not that the file was necessarily modified.
///
/// Requires running elevated (Administrator): starting a kernel ETW trace
/// session is an admin-only operation. If elevation is missing, only the
/// FileSystemWatcher-based events (create/rename/change/delete) still work;
/// file-open tracking is skipped and a single failure event is logged.
/// </summary>
public sealed class FileActivityMonitor : IDisposable
{
    private readonly ActivityLogger _logger;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<string> _watchedFolders = new();
    private TraceEventSession? _etwSession;
    private Thread? _etwThread;

    public FileActivityMonitor(ActivityLogger logger)
    {
        _logger = logger;

        var foldersToWatch = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };

        foreach (var folder in foldersToWatch)
        {
            if (!Directory.Exists(folder)) continue;
            _watchedFolders.Add(folder);
            StartWatching(folder);
        }

        StartFileOpenTracing();
    }

    private void StartWatching(string folder)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        watcher.Created += (_, e) => _logger.Log("file_created", new() { ["path"] = e.FullPath });
        watcher.Renamed += (_, e) => _logger.Log("file_renamed", new() { ["old_path"] = e.OldFullPath, ["path"] = e.FullPath });
        watcher.Changed += (_, e) => _logger.Log("file_changed", new() { ["path"] = e.FullPath });
        watcher.Deleted += (_, e) => _logger.Log("file_deleted", new() { ["path"] = e.FullPath });

        _watchers.Add(watcher);
    }

    // NOTE: exact FileIOCreate field names come from the TraceEvent NuGet
    // package's KernelTraceEventParser - verify against the installed
    // package version on first Windows build (minor shape changes have
    // happened across TraceEvent releases).
    private void StartFileOpenTracing()
    {
        if (!(TraceEventSession.IsElevated() ?? false))
        {
            _logger.Log("file_open_tracking_failed", new() { ["message"] = "must run elevated (Administrator) to trace file-open events" });
            return;
        }

        try
        {
            _etwSession = new TraceEventSession("EmployeeAgentFileIOSession");
            _etwSession.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.FileIO);

            _etwSession.Source.Kernel.FileIOCreate += data =>
            {
                if (!IsWatchedPath(data.FileName)) return;

                string processName;
                try
                {
                    processName = Process.GetProcessById(data.ProcessID).ProcessName;
                }
                catch
                {
                    processName = $"pid_{data.ProcessID}";
                }

                _logger.Log("file_opened", new() { ["process"] = processName, ["path"] = data.FileName });
            };

            _etwThread = new Thread(() => _etwSession.Source.Process()) { IsBackground = true };
            _etwThread.Start();
        }
        catch (Exception ex)
        {
            _logger.Log("file_open_tracking_failed", new() { ["message"] = ex.Message });
        }
    }

    private bool IsWatchedPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return _watchedFolders.Any(folder => path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }

        _etwSession?.Stop();
        _etwSession?.Dispose();
    }
}
