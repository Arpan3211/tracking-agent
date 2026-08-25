using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EmployeeAgent.Service;

/// <summary>
/// Duplicated queue-path/write logic matching ActivityLogger in the main
/// EmployeeAgent project (and the same pattern EmployeeAgent.NativeHost
/// uses) - kept independent on purpose so this service has no assembly
/// dependency on the per-user agent process it supervises. Writes into the
/// SAME local SQLite queue file EmployeeAgent's SyncLoop drains to the
/// backend; this process only ever inserts, never reads or deletes.
/// </summary>
internal static class ActivityLog
{
    public static void Write(string eventType, Dictionary<string, string>? details)
    {
        try
        {
            var overrideDir = Environment.GetEnvironmentVariable("EMPLOYEEAGENT_LOG_DIR");

            var folder = !string.IsNullOrWhiteSpace(overrideDir)
                ? overrideDir
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "EmployeeAgent");

            Directory.CreateDirectory(folder);
            var dbPath = Path.Combine(folder, $"events_{Environment.MachineName}.db");

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            connection.Open();

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }

            using (var createTable = connection.CreateCommand())
            {
                // EmployeeAgent.exe usually creates this table first, but the
                // service can start before any user session does, so it must
                // be able to create it too.
                createTable.CommandText = """
                    CREATE TABLE IF NOT EXISTS pending_events (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        event_type TEXT NOT NULL,
                        timestamp_utc TEXT NOT NULL,
                        details_json TEXT NULL
                    );
                    """;
                createTable.ExecuteNonQuery();
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO pending_events (event_type, timestamp_utc, details_json)
                VALUES ($eventType, $timestampUtc, $detailsJson);
                """;
            insert.Parameters.AddWithValue("$eventType", eventType);
            insert.Parameters.AddWithValue("$timestampUtc", DateTime.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$detailsJson", (object?)(details is null ? null : JsonSerializer.Serialize(details)) ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
        catch
        {
            // Must never crash the service because logging failed.
        }
    }
}
