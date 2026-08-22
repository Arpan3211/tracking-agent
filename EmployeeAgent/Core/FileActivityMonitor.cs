namespace EmployeeAgent.Core;

/// <summary>
/// Watches Desktop, Documents, and Downloads for file create/rename/change/
/// delete events. This is a lightweight audit trail (filenames + paths
/// only) - it does not inspect file contents, so it's not full DLP.
///
/// Known limitation: Windows' Changed event can fire multiple times for a
/// single save (common with editors that write in chunks). No debouncing
/// is applied here to keep the MVP simple - the report generator just
/// counts raw events.
/// </summary>
public sealed class FileActivityMonitor : IDisposable
{
    private readonly ActivityLogger _logger;
    private readonly List<FileSystemWatcher> _watchers = new();

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
            StartWatching(folder);
        }
    }

    private void StartWatching(string folder)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        watcher.Created += (_, e) => _logger.Log("file_created", e.FullPath);
        watcher.Renamed += (_, e) => _logger.Log("file_renamed", $"{e.OldFullPath} -> {e.FullPath}");
        watcher.Changed += (_, e) => _logger.Log("file_changed", e.FullPath);
        watcher.Deleted += (_, e) => _logger.Log("file_deleted", e.FullPath);

        _watchers.Add(watcher);
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
    }
}
