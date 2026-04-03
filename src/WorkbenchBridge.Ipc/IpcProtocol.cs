using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkbenchBridge.Ipc;

/// <summary>
/// Named pipe IPC protocol between the WorkbenchBridge Windows service and CLI.
///
/// The service listens on a named pipe. The CLI connects, sends a JSON request
/// (one line, newline terminated), and reads a JSON response (one line, newline
/// terminated). Then the connection closes.
///
/// Pipe name: "WorkbenchBridge" (full path: \\.\pipe\WorkbenchBridge)
/// </summary>
public static class IpcProtocol
{
    public const string PipeName = "WorkbenchBridge";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize<T>(T obj) =>
        JsonSerializer.Serialize(obj, JsonOptions);

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions);
}

// ---------------------------------------------------------------
// Request types
// ---------------------------------------------------------------

/// <summary>
/// Base request sent from CLI to service.
/// </summary>
public class IpcRequest
{
    /// <summary>
    /// The command to execute. Maps to CLI subcommands.
    /// </summary>
    public required IpcCommand Command { get; init; }

    /// <summary>
    /// Optional parameters for the command, serialised as a JSON object.
    /// The service deserialises this based on the Command value.
    /// </summary>
    public JsonElement? Params { get; init; }
}

public enum IpcCommand
{
    /// <summary>List all configured bridge mappings with their current status.</summary>
    List,

    /// <summary>Get detailed status of the service and all bridges.</summary>
    Status,

    /// <summary>Add a new bridge mapping.</summary>
    Add,

    /// <summary>Remove a bridge mapping.</summary>
    Remove,

    /// <summary>Start a specific bridge.</summary>
    Start,

    /// <summary>Stop a specific bridge (keeps config).</summary>
    Stop,

    /// <summary>Run diagnostics on a specific bridge.</summary>
    Diagnose,

    /// <summary>Update logging settings for a bridge or globally.</summary>
    SetLogging,

    /// <summary>Get the service version.</summary>
    Version
}

// ---------------------------------------------------------------
// Parameter types for specific commands
// ---------------------------------------------------------------

public class AddBridgeParams
{
    /// <summary>The user-facing COM port (e.g. "COM41"). IDE connects here.</summary>
    public required string UserPort { get; init; }

    /// <summary>The internal COM port (e.g. "COM241"). Bridge connects here.</summary>
    public required string InternalPort { get; init; }

    /// <summary>Pi hostname or IP address.</summary>
    public required string Host { get; init; }

    /// <summary>RFC 2217 port on the Pi.</summary>
    public required int Rfc2217Port { get; init; }

    /// <summary>Optional slot label (e.g. "SLOT1").</summary>
    public string? Label { get; init; }

    /// <summary>Optional human description (e.g. "RPi top-left USB, blue cable").</summary>
    public string? Description { get; init; }
}

public class RemoveBridgeParams
{
    /// <summary>The user-facing COM port to remove.</summary>
    public required string UserPort { get; init; }
}

public class StartStopBridgeParams
{
    /// <summary>The user-facing COM port to start or stop.</summary>
    public required string UserPort { get; init; }
}

public class DiagnoseParams
{
    /// <summary>The user-facing COM port to diagnose.</summary>
    public required string UserPort { get; init; }
}

public class SetLoggingParams
{
    /// <summary>
    /// The COM port to set logging for, or null for global settings.
    /// </summary>
    public string? UserPort { get; init; }

    /// <summary>Enable verbose logging.</summary>
    public bool? Verbose { get; init; }

    /// <summary>Enable hex dump logging.</summary>
    public bool? HexDump { get; init; }
}

// ---------------------------------------------------------------
// Response types
// ---------------------------------------------------------------

/// <summary>
/// Base response from service to CLI.
/// </summary>
public class IpcResponse
{
    /// <summary>Whether the command succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Human readable message (success or error description).</summary>
    public string? Message { get; init; }

    /// <summary>Optional data payload, type depends on the command.</summary>
    public JsonElement? Data { get; init; }
}

// ---------------------------------------------------------------
// Data types returned in IpcResponse.Data
// ---------------------------------------------------------------

/// <summary>
/// Status of a single bridge mapping.
/// </summary>
public class BridgeInfo
{
    /// <summary>User-facing COM port (e.g. "COM41").</summary>
    public required string UserPort { get; init; }

    /// <summary>Internal COM port (e.g. "COM241").</summary>
    public required string InternalPort { get; init; }

    /// <summary>Pi host (IP or FQDN).</summary>
    public required string Host { get; init; }

    /// <summary>RFC 2217 port on Pi.</summary>
    public required int Rfc2217Port { get; init; }

    /// <summary>Optional slot label.</summary>
    public string? Label { get; init; }

    /// <summary>Optional human description of the physical USB connection.</summary>
    public string? Description { get; init; }

    /// <summary>Current bridge state.</summary>
    public required BridgeState State { get; init; }

    /// <summary>Current baud rate (as known by the sniffer).</summary>
    public int? CurrentBaud { get; init; }

    /// <summary>Bytes sent from IDE to device since bridge start.</summary>
    public long BytesToDevice { get; init; }

    /// <summary>Bytes received from device to IDE since bridge start.</summary>
    public long BytesFromDevice { get; init; }

    /// <summary>Last error message, if any.</summary>
    public string? LastError { get; init; }

    /// <summary>Whether verbose logging is enabled for this bridge.</summary>
    public bool Verbose { get; init; }

    /// <summary>Whether hex dump logging is enabled for this bridge.</summary>
    public bool HexDump { get; init; }
}

public enum BridgeState
{
    /// <summary>Configured but not started.</summary>
    Stopped,

    /// <summary>Actively bridging data.</summary>
    Running,

    /// <summary>Attempting to reconnect after a connection drop.</summary>
    Reconnecting,

    /// <summary>Failed and not retrying.</summary>
    Error
}

/// <summary>
/// Overall service status returned by the Status command.
/// </summary>
public class ServiceStatus
{
    /// <summary>Assembly version of the service.</summary>
    public required string Version { get; init; }

    /// <summary>How long the service has been running.</summary>
    public required string Uptime { get; init; }

    /// <summary>All configured bridges and their status.</summary>
    public required List<BridgeInfo> Bridges { get; init; }
}

/// <summary>
/// Diagnostics result for a single bridge.
/// </summary>
public class DiagnoseResult
{
    public required string UserPort { get; init; }
    public required bool Com0comPairExists { get; init; }
    public required bool PiReachable { get; init; }
    public required bool Rfc2217Connectable { get; init; }
    public string? Com0comError { get; init; }
    public string? PiError { get; init; }
    public string? Rfc2217Error { get; init; }
}
