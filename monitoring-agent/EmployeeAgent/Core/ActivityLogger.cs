using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace EmployeeAgent.Core;

/// <summary>
/// A single row read back off the local queue: enough to rebuild the
/// original ActivityEvent plus the row id SyncLoop needs to delete it once
/// it's been successfully synced.
/// </summary>
public sealed record PendingEvent(long Id, string EventType, DateTime TimestampUtc, Dictionary<string, string>? Details);

/// <summary>
/// Every monitored event goes straight into a local SQLite queue - this is
/// the durability buffer: if the backend is unreachable (offline laptop,
/// backend down), events pile up here instead of being lost, and SyncLoop
/// (see SyncLoop.cs) drains them the moment connectivity comes back. There
/// is no local flat-file log and no local "report" of any kind anymore -
/// this queue exists purely as retry-on-reconnect plumbing, not as
/// something a user is meant to look at directly. Rows are deleted the
/// moment they're confirmed synced, so the queue only ever holds what's
/// actually still pending.
///
/// This class is called from every monitor, including from WinForms Timer
/// tick handlers on the agent's one UI/message-loop thread - Log() itself
/// therefore must NEVER touch disk on the calling thread, since this agent
/// runs unattended 24/7 and a slow disk (antivirus scanning the file,
/// momentary contention, etc.) must never stall a timer or a monitor's
/// event callback. Log() only ever does an in-memory channel write; a
/// single dedicated background task drains that channel to SQLite. Reads
/// (used by SyncLoop) go through the same serialized, persistent
/// connection but are dispatched via Task.Run so they run off whichever
/// thread called them too.
///
/// WAL mode + a busy-timeout are set on the connection because
/// EmployeeAgent.Service and EmployeeAgent.NativeHost write into this same
/// database file from separate processes - SQLite handles that fine as
/// long as writers retry instead of failing immediately on a lock.
/// </summary>
public sealed class ActivityLogger : IDisposable
{
    private readonly string _dbPath;
    private readonly object _lock = new();
    private readonly SqliteConnection _connection;
    private readonly Channel<ActivityEvent> _writeChannel = Channel.CreateUnbounded<ActivityEvent>();
    private readonly Task _writerTask;

    public string DbPath => _dbPath;

    public ActivityLogger()
    {
        _dbPath = ResolveDbPath();
        _connection = OpenConnection();
        EnsureDatabase();

        // One background thread owns all the actual disk I/O for writes, so
        // every monitor's Log() call is just a fast, non-blocking enqueue -
        // see the class doc comment above for why that matters here.
        _writerTask = Task.Run(ProcessWritesAsync);
    }

    /// <summary>
    /// Resolves the same shared queue file every writer process (agent,
    /// service, native host) uses. For the pilot on multiple laptops: set
    /// EMPLOYEEAGENT_LOG_DIR to a shared OneDrive/Google Drive/network
    /// folder if you want each machine's queue file to land in one central
    /// place. Leave it unset for the normal local default.
    /// </summary>
    public static string ResolveDbPath()
    {
        var overrideDir = Environment.GetEnvironmentVariable("EMPLOYEEAGENT_LOG_DIR");

        var folder = !string.IsNullOrWhiteSpace(overrideDir)
            ? overrideDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EmployeeAgent");

        Directory.CreateDirectory(folder);

        // Machine name in the filename so multiple devices writing into the
        // same shared folder produce separate queue files instead of one
        // file contended by unrelated machines.
        return Path.Combine(folder, $"events_{Environment.MachineName}.db");
    }

    public static string BuildConnectionString(string dbPath) => new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
    }.ToString();

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(BuildConnectionString(ResolveDbPath()));
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            // WAL lets EmployeeAgent read/delete while Service/NativeHost
            // insert concurrently; busy_timeout makes a writer retry for up
            // to 5s instead of throwing SQLITE_BUSY the instant two
            // processes touch the file at the same moment.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }
        return connection;
    }

    private void EnsureDatabase()
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS pending_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_type TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    details_json TEXT NULL
                );
                """;
            command.ExecuteNonQuery();
        }
    }

    /// Enqueues the event in memory and returns immediately - no disk I/O
    /// happens on the calling thread. Safe to call from any monitor's
    /// timer/event callback, as often as needed.
    public void Log(string eventType, Dictionary<string, string>? details = null) =>
        Log(eventType, DateTime.UtcNow, details);

    /// Same as Log(), but for backfilling an event that actually happened
    /// earlier than "now" - e.g. the real Windows logon time, queried after
    /// the fact (see SessionInfo.GetSessionLogonTimeUtc, used by
    /// AgentContext at startup).
    public void Log(string eventType, DateTime timestampUtc, Dictionary<string, string>? details = null)
    {
        var evt = new ActivityEvent(eventType, timestampUtc, details);
        _writeChannel.Writer.TryWrite(evt);
    }

    private async Task ProcessWritesAsync()
    {
        await foreach (var evt in _writeChannel.Reader.ReadAllAsync())
        {
            try
            {
                lock (_lock)
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText = """
                        INSERT INTO pending_events (event_type, timestamp_utc, details_json)
                        VALUES ($eventType, $timestampUtc, $detailsJson);
                        """;
                    command.Parameters.AddWithValue("$eventType", evt.EventType);
                    command.Parameters.AddWithValue("$timestampUtc", evt.TimestampUtc.ToString("O"));
                    command.Parameters.AddWithValue(
                        "$detailsJson",
                        (object?)(evt.Details is null ? null : JsonSerializer.Serialize(evt.Details)) ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }

                // Also print to console when run via `dotnet run`, useful while testing
                Console.WriteLine($"[{evt.TimestampUtc:O}] {evt.EventType} {(evt.Details is null ? "" : JsonSerializer.Serialize(evt.Details))}");
            }
            catch
            {
                // A single bad write must never take down the background
                // writer thread - the event is dropped, but every other
                // queued write keeps flowing.
            }
        }
    }

    /// Number of events still waiting to be synced - a cheap check SyncLoop
    /// uses to decide whether there's anything to do on a given tick.
    public Task<long> CountPendingAsync() => Task.Run(() =>
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pending_events;";
            return (long)command.ExecuteScalar()!;
        }
    });

    /// Reads up to maxCount of the oldest pending events, in order - never
    /// deletes anything itself. SyncLoop only calls DeleteSyncedUpTo() after
    /// a batch is confirmed accepted by the backend.
    public Task<IReadOnlyList<PendingEvent>> ReadPendingAsync(int maxCount) => Task.Run(() =>
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, event_type, timestamp_utc, details_json
                FROM pending_events
                ORDER BY id
                LIMIT $maxCount;
                """;
            command.Parameters.AddWithValue("$maxCount", maxCount);

            using var reader = command.ExecuteReader();
            var results = new List<PendingEvent>();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var eventType = reader.GetString(1);
                var timestampUtc = DateTime.Parse(reader.GetString(2)).ToUniversalTime();
                var detailsJson = reader.IsDBNull(3) ? null : reader.GetString(3);
                var details = detailsJson is null
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(detailsJson);

                results.Add(new PendingEvent(id, eventType, timestampUtc, details));
            }
            return (IReadOnlyList<PendingEvent>)results;
        }
    });

    /// Deletes every row up to and including maxId - called only after
    /// those exact rows have been confirmed accepted by the backend.
    public Task DeleteSyncedUpToAsync(long maxId) => Task.Run(() =>
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM pending_events WHERE id <= $maxId;";
            command.Parameters.AddWithValue("$maxId", maxId);
            command.ExecuteNonQuery();
        }
    });

    /// Stops accepting new writes, waits briefly for the background writer
    /// to drain whatever's still in memory, then closes the connection.
    public void Dispose()
    {
        _writeChannel.Writer.TryComplete();
        _writerTask.Wait(TimeSpan.FromSeconds(2));
        _connection.Dispose();
    }
}
