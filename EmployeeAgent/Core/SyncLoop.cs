using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace EmployeeAgent.Core;

/// <summary>
/// Background loop that reads unsent lines from ActivityLogger's write-ahead
/// log file and POSTs them to the backend's ingestion API in batches. The
/// local file is never modified or truncated by this class - it stays the
/// offline-resilience mechanism regardless of whether the backend is
/// reachable. A separate "last synced line" pointer file tracks progress so
/// a restart doesn't resend already-synced lines or, more importantly,
/// silently drop unsent ones.
///
/// Sync is entirely opt-in: if EMPLOYEEAGENT_BACKEND_URL isn't set,
/// CreateIfConfigured returns null and the agent behaves exactly as it did
/// before the backend existed (log-file-only, per-device pilot reports).
///
/// Every failure path here is caught and logged via the same ActivityLogger
/// the rest of the agent uses, never rethrown - this must NEVER crash the
/// agent or block the monitors from continuing to log locally.
/// </summary>
public sealed class SyncLoop : IDisposable
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(30);
    private const int EventCountTrigger = 50;
    private const int MaxEventsPerRequest = 200;

    private readonly ActivityLogger _logger;
    private readonly string _backendBaseUrl;
    private readonly string _stateFilePath;
    private readonly string _credentialsFilePath;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private long _lastSyncedLine;
    private DateTime _lastSyncAttemptUtc = DateTime.MinValue;
    private string? _deviceApiKey;
    private bool _isSyncing;

    public static SyncLoop? CreateIfConfigured(ActivityLogger logger)
    {
        var backendUrl = Environment.GetEnvironmentVariable("EMPLOYEEAGENT_BACKEND_URL");
        return string.IsNullOrWhiteSpace(backendUrl) ? null : new SyncLoop(logger, backendUrl.TrimEnd('/'));
    }

    private SyncLoop(ActivityLogger logger, string backendBaseUrl)
    {
        _logger = logger;
        _backendBaseUrl = backendBaseUrl;

        var folder = Path.GetDirectoryName(logger.LogFilePath)!;
        _stateFilePath = Path.Combine(folder, $"sync_state_{Environment.MachineName}.json");
        _credentialsFilePath = Path.Combine(folder, $"device_credentials_{Environment.MachineName}.json");

        _lastSyncedLine = LoadSyncedLine();
    }

    /// Called on a short timer (e.g. every 5s) by AgentContext. Internally
    /// decides whether enough time OR enough unsent events have accumulated
    /// to actually attempt a sync - "every 30s or every 50 events,
    /// whichever comes first" - so most ticks are a cheap no-op.
    public async Task TickAsync()
    {
        if (_isSyncing) return; // a slow request from the previous tick is still in flight

        var pendingCount = _logger.TotalLinesWritten - _lastSyncedLine;
        if (pendingCount <= 0) return;

        var dueByTime = DateTime.UtcNow - _lastSyncAttemptUtc >= SyncInterval;
        var dueByCount = pendingCount >= EventCountTrigger;
        if (!dueByTime && !dueByCount) return;

        _isSyncing = true;
        _lastSyncAttemptUtc = DateTime.UtcNow;
        try
        {
            await SyncOnceAsync();
        }
        catch (Exception ex)
        {
            _logger.Log("sync_failed", ex.Message);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private async Task SyncOnceAsync()
    {
        _deviceApiKey ??= await LoadOrEnrollAsync();
        if (_deviceApiKey is null) return; // couldn't enroll yet - retry next due tick

        var pendingLines = _logger.ReadLinesFrom(_lastSyncedLine);
        if (pendingLines.Count == 0) return;

        var batchLines = pendingLines.Take(MaxEventsPerRequest).ToList();
        var events = new List<IngestEventDto>(batchLines.Count);
        foreach (var line in batchLines)
        {
            var parsed = TryParseLine(line);
            if (parsed is not null) events.Add(parsed);
            // Malformed lines are skipped, not retried forever - they still
            // count toward advancing the pointer below so sync doesn't get
            // stuck behind one bad line.
        }

        if (events.Count > 0 && !await PostBatchAsync(events))
        {
            return; // leave _lastSyncedLine as-is, retry this same batch next due tick
        }

        _lastSyncedLine += batchLines.Count;
        SaveSyncedLine(_lastSyncedLine);
    }

    private static IngestEventDto? TryParseLine(string line)
    {
        try
        {
            var evt = System.Text.Json.JsonSerializer.Deserialize<ActivityEvent>(line);
            return evt is null ? null : new IngestEventDto(evt.EventType, evt.TimestampUtc, evt.Details);
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> PostBatchAsync(List<IngestEventDto> events)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_backendBaseUrl}/api/v1/ingest/events");
            // Invariant: SyncOnceAsync only calls this after confirming _deviceApiKey is non-null.
            request.Headers.Add("X-API-Key", _deviceApiKey!);
            request.Content = JsonContent.Create(new IngestRequestDto(Environment.MachineName, events));

            using var response = await _http.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Cached key was rejected (e.g. the device was re-enrolled
                // from another install, invalidating this one) - drop it so
                // the next tick re-enrolls instead of failing forever.
                _deviceApiKey = null;
                TryDeleteCredentials();
                _logger.Log("sync_failed", "API key rejected by backend (401) - will re-enroll");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.Log("sync_failed", $"status={(int)response.StatusCode}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Log("sync_failed", ex.Message);
            return false;
        }
    }

    private async Task<string?> LoadOrEnrollAsync()
    {
        var cached = TryLoadCredentials();
        if (cached is not null) return cached;

        try
        {
            var request = new EnrollRequestDto(Environment.MachineName, Environment.OSVersion.VersionString);
            using var response = await _http.PostAsJsonAsync($"{_backendBaseUrl}/api/v1/devices/enroll", request);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Log("device_enrollment_failed", $"status={(int)response.StatusCode}");
                return null;
            }

            var enrolled = await response.Content.ReadFromJsonAsync<EnrollResponseDto>();
            if (enrolled is null) return null;

            SaveCredentials(enrolled.ApiKey);
            _logger.Log("device_enrolled", $"device_id={enrolled.DeviceId}");
            return enrolled.ApiKey;
        }
        catch (Exception ex)
        {
            _logger.Log("device_enrollment_failed", ex.Message);
            return null;
        }
    }

    private string? TryLoadCredentials()
    {
        try
        {
            if (!File.Exists(_credentialsFilePath)) return null;
            var json = File.ReadAllText(_credentialsFilePath);
            var stored = System.Text.Json.JsonSerializer.Deserialize<CredentialsFileDto>(json);
            return stored?.ApiKey;
        }
        catch
        {
            return null;
        }
    }

    private void SaveCredentials(string apiKey)
    {
        try
        {
            var dto = new CredentialsFileDto(apiKey);
            File.WriteAllText(_credentialsFilePath, System.Text.Json.JsonSerializer.Serialize(dto));
        }
        catch (Exception ex)
        {
            _logger.Log("credentials_save_failed", ex.Message);
        }
    }

    private void TryDeleteCredentials()
    {
        try
        {
            if (File.Exists(_credentialsFilePath)) File.Delete(_credentialsFilePath);
        }
        catch
        {
            // Best effort - a stale-but-rejected file just gets tried and
            // rejected again next time, no worse than not deleting it.
        }
    }

    private long LoadSyncedLine()
    {
        try
        {
            if (!File.Exists(_stateFilePath)) return 0;
            var json = File.ReadAllText(_stateFilePath);
            var state = System.Text.Json.JsonSerializer.Deserialize<SyncStateDto>(json);
            return state?.LastSyncedLine ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private void SaveSyncedLine(long lastSyncedLine)
    {
        try
        {
            var state = new SyncStateDto(lastSyncedLine);
            File.WriteAllText(_stateFilePath, System.Text.Json.JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            _logger.Log("sync_state_save_failed", ex.Message);
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record IngestEventDto(
        [property: JsonPropertyName("event_type")] string EventType,
        [property: JsonPropertyName("timestamp_utc")] DateTime TimestampUtc,
        [property: JsonPropertyName("details")] string? Details);

    private sealed record IngestRequestDto(
        [property: JsonPropertyName("machine_name")] string MachineName,
        [property: JsonPropertyName("events")] List<IngestEventDto> Events);

    private sealed record EnrollRequestDto(
        [property: JsonPropertyName("machine_name")] string MachineName,
        [property: JsonPropertyName("os_version")] string? OsVersion);

    private sealed record EnrollResponseDto(
        [property: JsonPropertyName("device_id")] Guid DeviceId,
        [property: JsonPropertyName("machine_name")] string MachineName,
        [property: JsonPropertyName("api_key")] string ApiKey);

    private sealed record CredentialsFileDto([property: JsonPropertyName("api_key")] string ApiKey);

    private sealed record SyncStateDto([property: JsonPropertyName("last_synced_line")] long LastSyncedLine);
}
