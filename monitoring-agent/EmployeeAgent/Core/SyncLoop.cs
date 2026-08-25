using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace EmployeeAgent.Core;

/// <summary>
/// Background sender that drains ActivityLogger's local SQLite queue to the
/// backend's ingestion API in small, frequent batches. AgentContext ticks
/// this every few seconds; each tick that finds pending events sends
/// whatever's queued (up to MaxEventsPerRequest) immediately - there is no
/// local flat-file log anymore, so the queue IS the offline-resilience
/// buffer: rows only leave it once the backend has confirmed it accepted
/// them, so a laptop going offline mid-batch just means those rows stay
/// queued and get retried on the next tick, indefinitely, until connectivity
/// returns.
///
/// Sync is entirely opt-in: if EMPLOYEEAGENT_BACKEND_URL isn't set,
/// CreateIfConfigured returns null and events simply accumulate in the local
/// queue unsent (same standalone-agent behavior as before, just backed by
/// SQLite instead of a flat file).
///
/// Every failure path here is caught and logged via the same ActivityLogger
/// the rest of the agent uses, never rethrown - this must NEVER crash the
/// agent or block the monitors from continuing to queue events locally.
/// </summary>
public sealed class SyncLoop : IDisposable
{
    private const int MaxEventsPerRequest = 100;

    private readonly ActivityLogger _logger;
    private readonly string _backendBaseUrl;
    private readonly string _credentialsFilePath;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

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

        var folder = Path.GetDirectoryName(logger.DbPath)!;
        _credentialsFilePath = Path.Combine(folder, $"device_credentials_{Environment.MachineName}.json");
    }

    /// Called on a short timer (every few seconds) by AgentContext. Cheap
    /// no-op when the queue is empty or a previous tick's request is still
    /// in flight; otherwise sends immediately - the tick interval itself is
    /// what makes this "small, frequent batches" rather than needing a
    /// separate time/count threshold.
    public async Task TickAsync()
    {
        if (_isSyncing) return; // a slow request from the previous tick is still in flight

        if (await _logger.CountPendingAsync() == 0) return;

        _isSyncing = true;
        try
        {
            await SyncOnceAsync();
        }
        catch (Exception ex)
        {
            _logger.Log("sync_failed", new() { ["message"] = ex.Message });
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private async Task SyncOnceAsync()
    {
        _deviceApiKey ??= await LoadOrEnrollAsync();
        if (_deviceApiKey is null) return; // couldn't enroll yet - retry next tick

        var pending = await _logger.ReadPendingAsync(MaxEventsPerRequest);
        if (pending.Count == 0) return;

        var events = pending
            .Select(p => new IngestEventDto(p.EventType, p.TimestampUtc, p.Details))
            .ToList();

        if (!await PostBatchAsync(events)) return; // leave the queue as-is, retry this same batch next tick

        var maxId = pending[^1].Id;
        await _logger.DeleteSyncedUpToAsync(maxId);
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
                _logger.Log("sync_failed", new() { ["message"] = "API key rejected by backend (401) - will re-enroll" });
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.Log("sync_failed", new() { ["status"] = ((int)response.StatusCode).ToString() });
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Log("sync_failed", new() { ["message"] = ex.Message });
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
                _logger.Log("device_enrollment_failed", new() { ["status"] = ((int)response.StatusCode).ToString() });
                return null;
            }

            var enrolled = await response.Content.ReadFromJsonAsync<EnrollResponseDto>();
            if (enrolled is null) return null;

            SaveCredentials(enrolled.ApiKey);
            _logger.Log("device_enrolled", new() { ["device_id"] = enrolled.DeviceId.ToString() });
            return enrolled.ApiKey;
        }
        catch (Exception ex)
        {
            _logger.Log("device_enrollment_failed", new() { ["message"] = ex.Message });
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
            _logger.Log("credentials_save_failed", new() { ["message"] = ex.Message });
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

    public void Dispose() => _http.Dispose();

    private sealed record IngestEventDto(
        [property: JsonPropertyName("event_type")] string EventType,
        [property: JsonPropertyName("timestamp_utc")] DateTime TimestampUtc,
        [property: JsonPropertyName("details")] Dictionary<string, string>? Details);

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
}
