using System.Text.Json;

namespace EmployeeAgent.Core;

/// <summary>
/// Periodically resolves a rough, CITY-LEVEL location from the machine's
/// public IP via a free geolocation API (ip-api.com). This is NOT GPS -
/// it's an approximation, and it will be wrong entirely if the machine is
/// behind a VPN/proxy. Runs infrequently since location rarely changes
/// mid-session, and depends on outbound internet access + a third-party
/// service's availability/rate limits.
///
/// For production use, swap this for a paid/self-hosted GeoIP database
/// instead of relying on a free public API.
/// </summary>
public sealed class IpLocationMonitor
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ActivityLogger _logger;

    public IpLocationMonitor(ActivityLogger logger)
    {
        _logger = logger;
    }

    public async Task PollAsync()
    {
        try
        {
            var response = await Http.GetStringAsync(
                "http://ip-api.com/json/?fields=status,country,regionName,city,query");

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            if (root.GetProperty("status").GetString() == "success")
            {
                var city = root.GetProperty("city").GetString() ?? "";
                var region = root.GetProperty("regionName").GetString() ?? "";
                var country = root.GetProperty("country").GetString() ?? "";
                _logger.Log("location_estimate", new() { ["city"] = city, ["region"] = region, ["country"] = country });
            }
        }
        catch (Exception ex)
        {
            _logger.Log("location_lookup_failed", new() { ["message"] = ex.Message });
        }
    }
}
