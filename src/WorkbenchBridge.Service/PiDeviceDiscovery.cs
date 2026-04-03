using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WorkbenchBridge.Service;

/// <summary>
/// Discovers ESP32 devices connected to the Pi workbench via the portal API.
/// </summary>
public sealed class PiDeviceDiscovery : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _endpoint;
    private readonly ILogger<PiDeviceDiscovery> _logger;

    public PiDeviceDiscovery(string host, int portalPort, string endpoint, ILogger<PiDeviceDiscovery> logger)
    {
        _baseUrl = $"http://{host}:{portalPort}";
        _endpoint = endpoint;
        _logger = logger;
        _http = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Query the Pi portal for connected ESP32 devices.
    /// </summary>
    public async Task<List<PiDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("Querying Pi at {Url}{Endpoint}", _baseUrl, _endpoint);
            var response = await _http.GetFromJsonAsync<List<PiDevice>>(_endpoint, ct);
            var devices = response ?? new List<PiDevice>();
            _logger.LogDebug("Discovered {Count} device(s)", devices.Count);
            return devices;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Pi discovery failed: {Message}", ex.Message);
            return new List<PiDevice>();
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Pi discovery timed out");
            return new List<PiDevice>();
        }
    }

    /// <summary>
    /// Check if the Pi portal is reachable.
    /// </summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync(_endpoint, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Represents an ESP32 device discovered via the Pi portal API.
/// Structure matches the JSON from /api/devices.
/// </summary>
public class PiDevice
{
    [JsonPropertyName("slot")]
    public string Slot { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("device")]
    public string Device { get; set; } = "";

    [JsonPropertyName("chip")]
    public string Chip { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}
