using System.Text;
using System.Text.Json;

// ---------------------------------------------------------------------
// Native Messaging host for the EmployeeAgent browser extension.
//
// Implements Chrome/Edge's native messaging stdio protocol: a message is a
// 4-byte length prefix (native/little-endian byte order) followed by that
// many bytes of UTF-8 JSON. The extension calls chrome.runtime.sendNativeMessage,
// which spawns this process fresh per call and expects exactly one request,
// one response, then process exit - so this host does not loop.
//
// Writes straight into the SAME activity log the main agent uses, in the
// same JSON-lines schema, using log-path logic duplicated by hand (same
// approach EmployeeAgent.Service uses) so events land in one place without
// a shared assembly dependency between processes.
// ---------------------------------------------------------------------

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

try
{
    var message = ReadMessage(stdin);
    if (message is { } m)
    {
        LogWebsiteVisited(m);
    }

    WriteMessage(stdout, new { status = "ok" });
}
catch
{
    // Native host must never leave the browser hanging - always try to
    // respond, even if something above failed.
    try { WriteMessage(stdout, new { status = "error" }); } catch { /* give up quietly */ }
}

static (string Url, string Title, string TimestampUtc)? ReadMessage(Stream stdin)
{
    var lengthBytes = new byte[4];
    if (!ReadExact(stdin, lengthBytes, 4)) return null;

    var length = BitConverter.ToInt32(lengthBytes, 0);
    if (length <= 0 || length > 10 * 1024 * 1024) return null;

    var payloadBytes = new byte[length];
    if (!ReadExact(stdin, payloadBytes, length)) return null;

    using var doc = JsonDocument.Parse(payloadBytes);
    var root = doc.RootElement;

    var url = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() ?? "" : "";
    var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
    var timestamp = root.TryGetProperty("timestampUtc", out var tsProp) ? tsProp.GetString() ?? "" : "";

    if (string.IsNullOrEmpty(url)) return null;
    return (url, title, timestamp);
}

static bool ReadExact(Stream stream, byte[] buffer, int count)
{
    var offset = 0;
    while (offset < count)
    {
        var read = stream.Read(buffer, offset, count - offset);
        if (read == 0) return false; // stdin closed early
        offset += read;
    }
    return true;
}

static void WriteMessage(Stream stdout, object payload)
{
    var json = JsonSerializer.Serialize(payload);
    var jsonBytes = Encoding.UTF8.GetBytes(json);
    var lengthBytes = BitConverter.GetBytes(jsonBytes.Length);

    stdout.Write(lengthBytes, 0, 4);
    stdout.Write(jsonBytes, 0, jsonBytes.Length);
    stdout.Flush();
}

static void LogWebsiteVisited((string Url, string Title, string TimestampUtc) message)
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
        var logPath = Path.Combine(folder, $"activity_{Environment.MachineName}.log");

        var details = $"url={message.Url}; title={message.Title}";
        var evt = new { EventType = "website_visited", TimestampUtc = DateTime.UtcNow, Details = details };
        File.AppendAllText(logPath, JsonSerializer.Serialize(evt) + Environment.NewLine);
    }
    catch
    {
        // Never crash the native host over a logging failure - the browser
        // is waiting on a response either way.
    }
}
