using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WorkbenchBridge.Service;

/// <summary>
/// Talks to the Universal-ESP32-Workbench portal API on the Pi to learn which
/// slots currently have an ESP32 attached. The portal exposes
/// <c>GET /api/devices</c>, which returns an object of the shape:
/// <code>
/// { "slots": [ { "label": "SLOT1", "tcp_port": 4001, "present": true,
///               "running": true, "devnode": "/dev/ttyUSB0", "state": "idle",
///               "last_error": null, ... }, ... ],
///   "host_ip": "192.168.1.50", "hostname": "workbench" }
/// </code>
/// The <c>present</c> flag is authoritative for "is a device plugged into this
/// slot" - <c>devnode</c> can linger after a removal, so we never infer presence
/// from it.
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
    /// Single round-trip that reports both whether the Pi portal answered and
    /// the slots it returned. Distinguishes a portal outage (Reachable=false)
    /// from a reachable portal with no devices present.
    /// </summary>
    public async Task<PiPollResult> PollAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogDebug("Querying Pi at {Url}{Endpoint}", _baseUrl, _endpoint);
            var response = await _http.GetFromJsonAsync<PiPortalResponse>(_endpoint, ct);
            var slots = response?.Slots ?? new List<PiSlot>();
            _logger.LogDebug(
                "Pi portal '{Host}' reported {Count} slot(s)",
                response?.Hostname ?? "?", slots.Count);
            return new PiPollResult
            {
                Reachable = true,
                Hostname = response?.Hostname,
                Slots = slots
            };
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _logger.LogDebug("Pi poll failed: {Message}", ex.Message);
            return new PiPollResult { Reachable = false, Slots = new List<PiSlot>() };
        }
    }

    /// <summary>
    /// Query the Pi portal for its slots. Returns an empty list if the portal is
    /// unreachable or returns malformed data.
    /// </summary>
    public async Task<List<PiSlot>> DiscoverAsync(CancellationToken ct = default)
        => (await PollAsync(ct)).Slots;

    /// <summary>
    /// Check if the Pi portal is reachable (any 2xx from the API endpoint).
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

    /// <summary>
    /// Ask the portal to actively identify the chip on a slot via
    /// <c>POST /api/devices/{slot}/device_id</c> (portal commit 9784ab3+). The
    /// portal runs an esptool chip-id probe on the Pi and returns whatever it
    /// learned (chip family, MAC/device id, flash size, features, ...). The
    /// response shape is portal-defined, so it is returned as a loosely-typed
    /// map the caller can print verbatim.
    /// </summary>
    /// <param name="slot">Slot identifier the portal expects in the path (e.g. "SLOT1").</param>
    public async Task<PiDeviceIdResult> RequestDeviceIdAsync(string slot, CancellationToken ct = default)
    {
        string path = $"/api/devices/{Uri.EscapeDataString(slot)}/device_id";
        try
        {
            _logger.LogDebug("POST {Url}{Path}", _baseUrl, path);
            using var response = await _http.PostAsync(path, content: null, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return new PiDeviceIdResult
                {
                    Success = false,
                    Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}"
                };
            }

            Dictionary<string, JsonElement>? fields = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try { fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body); }
                catch (JsonException) { /* non-object body; surface as raw */ }
            }

            return new PiDeviceIdResult
            {
                Success = true,
                Fields = fields ?? new Dictionary<string, JsonElement>(),
                Raw = body
            };
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new PiDeviceIdResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Ask the portal to force a GPIO-wired slot into the download bootloader via
    /// <c>POST /api/serial/recover {"slot": label}</c>. For boards with
    /// gpio_boot/gpio_en wired this drives BOOT low, pulses EN, rebinds USB, and
    /// leaves the slot in the <c>download_mode</c> state until
    /// <see cref="ReleaseSlotGpioAsync"/>. The portal runs this asynchronously
    /// (after a flap cooldown), so callers should poll
    /// <see cref="GetSlotByLabelAsync"/> for <c>download_mode</c> before flashing.
    /// Returns false (with the error) on an old / non-GPIO portal so the caller
    /// can fall back to a manual-BOOT prompt.
    /// </summary>
    public Task<(bool Success, string? Error)> RecoverSlotAsync(
        string label, CancellationToken ct = default)
        => PostSlotActionAsync("recover", label, ct);

    /// <summary>
    /// Release a slot's BOOT/EN GPIO after flashing and reboot it cleanly via
    /// <c>POST /api/serial/release {"slot": label}</c>. Best effort - pair it with
    /// a successful <see cref="RecoverSlotAsync"/>.
    /// </summary>
    public Task<(bool Success, string? Error)> ReleaseSlotGpioAsync(
        string label, CancellationToken ct = default)
        => PostSlotActionAsync("release", label, ct);

    /// <summary>
    /// POST <c>/api/serial/{action} {"slot": label}</c>. The portal answers 200
    /// with <c>{"ok": true|false, "error": ...}</c> even for logical failures, so
    /// we inspect <c>ok</c> and surface its error.
    /// </summary>
    private async Task<(bool, string?)> PostSlotActionAsync(
        string action, string label, CancellationToken ct)
    {
        string path = $"/api/serial/{action}";
        try
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, string> { ["slot"] = label });
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            _logger.LogDebug("POST {Url}{Path} slot={Label}", _baseUrl, path, label);
            using var response = await _http.PostAsync(path, content, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return (false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");

            try
            {
                var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
                if (obj is not null && obj.TryGetValue("ok", out var ok) &&
                    ok.ValueKind == JsonValueKind.False)
                    return (false, obj.TryGetValue("error", out var e) ? e.GetString() : "portal reported failure");
            }
            catch (JsonException) { /* non-JSON 200 body - treat as success */ }

            return (true, null);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Find a slot by its label in a fresh <c>/api/devices</c> poll. Returns null
    /// if the portal is unreachable or the label is unknown. Used to watch for a
    /// slot entering <c>download_mode</c> after <see cref="RecoverSlotAsync"/>.
    /// </summary>
    public async Task<PiSlot?> GetSlotByLabelAsync(string label, CancellationToken ct = default)
    {
        var result = await PollAsync(ct);
        return result.Slots.FirstOrDefault(s =>
            string.Equals(s.Label, label, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Result of <see cref="PiDeviceDiscovery.RequestDeviceIdAsync"/>. The portal's
/// device-id response shape is not fixed, so successful results carry both a
/// parsed field map and the raw JSON body for verbatim display.
/// </summary>
public sealed class PiDeviceIdResult
{
    public bool Success { get; init; }

    /// <summary>Parsed top-level JSON fields when the body was a JSON object.</summary>
    public Dictionary<string, JsonElement> Fields { get; init; } = new();

    /// <summary>The raw response body (for display / non-object payloads).</summary>
    public string? Raw { get; init; }

    /// <summary>Error description when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Result of a single <see cref="PiDeviceDiscovery.PollAsync"/> round-trip.
/// </summary>
public sealed class PiPollResult
{
    /// <summary>True if the portal API answered (even with zero present slots).</summary>
    public bool Reachable { get; init; }

    /// <summary>The portal hostname (e.g. "esp32-workbench"), if reported.</summary>
    public string? Hostname { get; init; }

    /// <summary>Slots the portal reported (may be empty).</summary>
    public List<PiSlot> Slots { get; init; } = new();
}

/// <summary>
/// One slot from the Pi portal's <c>/api/devices</c> response. Only the fields
/// the bridge cares about are mapped; the portal sends more (gpio_*, gdb_port,
/// seq, recovering, ...) which we ignore.
/// </summary>
public sealed class PiSlot
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>RFC2217 TCP port for this slot - matches ComPortMapping.PiTcpPort.</summary>
    [JsonPropertyName("tcp_port")]
    public int TcpPort { get; set; }

    /// <summary>Authoritative "is an ESP32 plugged into this slot" flag.</summary>
    [JsonPropertyName("present")]
    public bool Present { get; set; }

    /// <summary>Whether ser2net is serving this slot.</summary>
    [JsonPropertyName("running")]
    public bool Running { get; set; }

    /// <summary>Pi-side device node (e.g. "/dev/ttyUSB0"). Can linger after removal.</summary>
    [JsonPropertyName("devnode")]
    public string? Devnode { get; set; }

    /// <summary>Slot state string: "idle", "busy", "absent", ...</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    /// <summary>rfc2217:// URL for the slot, when present.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Last error the portal recorded for this slot, if any.</summary>
    [JsonPropertyName("last_error")]
    public string? LastError { get; set; }

    // ---------------------------------------------------------------
    // USB / device classification (portal commit 9784ab3 and later).
    // All of these are absent on older portals, so every field is nullable
    // and the bridge must treat null/empty as "unknown" rather than an error.
    // ---------------------------------------------------------------

    /// <summary>USB vendor id, lowercase hex without prefix (e.g. "303a"). Null on old portals.</summary>
    [JsonPropertyName("vid")]
    public string? Vid { get; set; }

    /// <summary>USB product id, lowercase hex without prefix (e.g. "1001"). Null on old portals.
    /// The portal exposes the USB product id as "usb_pid"; the bare "pid" field is the proxy
    /// PROCESS id (an integer) - see <see cref="ProcessPid"/>. Mapping "pid" to this string
    /// throws "JSON value could not be converted to System.String" on every poll.</summary>
    [JsonPropertyName("usb_pid")]
    public string? UsbPid { get; set; }

    /// <summary>Proxy process id on the Pi (the portal's integer "pid" field). NOT the USB
    /// product id (that is <see cref="UsbPid"/>). Mapped only so the integer deserialises
    /// cleanly; the bridge does not otherwise use it.</summary>
    [JsonPropertyName("pid")]
    public long? ProcessPid { get; set; }

    /// <summary>Human USB vendor string (e.g. "Espressif").</summary>
    [JsonPropertyName("usb_vendor")]
    public string? UsbVendor { get; set; }

    /// <summary>Human USB model/product string (e.g. "USB JTAG serial debug unit").</summary>
    [JsonPropertyName("usb_model")]
    public string? UsbModel { get; set; }

    /// <summary>USB serial-number string, if the device exposes one.</summary>
    [JsonPropertyName("serial")]
    public string? Serial { get; set; }

    /// <summary>Linux kernel driver bound to the port (e.g. "cdc_acm", "cp210x", "ch341").</summary>
    [JsonPropertyName("usb_driver")]
    public string? UsbDriver { get; set; }

    /// <summary>
    /// Portal's classification of the device
    /// (e.g. "Espressif USB-JTAG (ESP32-S3/C3/C6)", "Silicon Labs CP210x UART").
    /// </summary>
    [JsonPropertyName("device_type")]
    public string? DeviceType { get; set; }

    /// <summary>Physical transport: "native-usb" or "uart-bridge". Null on old portals.</summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    /// <summary>Detected chip family in esptool form (e.g. "esp32s3", "esp32c3").</summary>
    [JsonPropertyName("chip_family")]
    public string? ChipFamily { get; set; }

    /// <summary>Stable device identity (e.g. the chip MAC "34:b7:da:59:cf:00"), if known.</summary>
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }

    /// <summary>How the bridge should reset/flash this slot. Null on old portals.</summary>
    [JsonPropertyName("reset_profile")]
    public PiResetProfile? ResetProfile { get; set; }
}

/// <summary>
/// Per-slot reset/flash hints from the portal's <c>reset_profile</c> object.
/// Tells the bridge how to put a given board into the download bootloader.
/// Absent on portals older than commit 9784ab3, so all fields are nullable.
/// </summary>
public sealed class PiResetProfile
{
    /// <summary>Transport this profile assumes: "uart-bridge" or "native-usb".</summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; set; }

    /// <summary>Chip family in esptool form (e.g. "esp32s3").</summary>
    [JsonPropertyName("chip_family")]
    public string? ChipFamily { get; set; }

    /// <summary>
    /// Reset method:
    ///   "gpio"     - BOOT/EN wired to Pi GPIO (most reliable; Pi can sequence it),
    ///   "usb-jtag" - native USB-Serial-JTAG (re-enumerates on bootloader reset),
    ///   "classic"  - UART-bridge board reset via DTR/RTS.
    /// </summary>
    [JsonPropertyName("reset_method")]
    public string? ResetMethod { get; set; }

    /// <summary>Pi GPIO (BCM) wired to the board's BOOT/IO0 pin, if any.</summary>
    [JsonPropertyName("gpio_boot")]
    public int? GpioBoot { get; set; }

    /// <summary>Pi GPIO (BCM) wired to the board's EN/RESET pin, if any.</summary>
    [JsonPropertyName("gpio_en")]
    public int? GpioEn { get; set; }

    /// <summary>True if the bridge can automatically enter download mode for this slot.</summary>
    [JsonPropertyName("download_mode_capable")]
    public bool? DownloadModeCapable { get; set; }
}

/// <summary>Top-level shape of the portal's <c>/api/devices</c> response.</summary>
public sealed class PiPortalResponse
{
    [JsonPropertyName("slots")]
    public List<PiSlot> Slots { get; set; } = new();

    [JsonPropertyName("host_ip")]
    public string? HostIp { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }
}
