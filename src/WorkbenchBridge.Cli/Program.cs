using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Serilog;
using WorkbenchBridge.Cli;
using WorkbenchBridge.Ipc;
using WorkbenchBridge.Rfc2217;
using WorkbenchBridge.Service;

// ESP32 Workbench Bridge CLI
//
// Service management commands (talk to the Windows service via named pipe):
//   workbenchbridge-cli list                         List configured bridges
//   workbenchbridge-cli status                       Service and bridge status
//   workbenchbridge-cli add <user-port> <host> <rfc-port> [options]
//   workbenchbridge-cli remove <user-port>           Remove a bridge mapping
//   workbenchbridge-cli start <user-port>            Start a bridge
//   workbenchbridge-cli stop <user-port>             Stop a bridge
//   workbenchbridge-cli diagnose <user-port>         Health check
//   workbenchbridge-cli set <user-port> [--verbose] [--hexdump]
//
// Direct/debug commands (no service required):
//   workbenchbridge-cli test <host> <port>           Test RFC 2217 connection
//   workbenchbridge-cli bridge <comport> <host> <port> [--verbose] [--hexdump]
//   workbenchbridge-cli discover <host> [portalPort] Discover Pi devices
//   workbenchbridge-cli pairs                        List com0com pairs

if (args.Length == 0)
{
    // No verb (e.g. double-clicked): show who we are + the live service status
    // (politely, in red, if it can't be reached), then the help. Don't vanish.
    ShowVersion();
    Console.WriteLine();
    await IpcStatusAsync();
    Console.WriteLine();
    PrintUsage();
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine();
        Console.Write("Press Enter to exit...");
        Console.ReadLine();
    }
    return 0;
}

// Help / version up front, accepting Windows and Unix flag styles equally.
string[] helpForms = { "--help", "-h", "-?", "/?", "/h", "/help", "help" };
string[] versionForms = { "--version", "-v", "/v", "/version", "version" };
if (args.Any(a => helpForms.Contains(a, StringComparer.OrdinalIgnoreCase)))
    return ShowHelp();
if (args.Any(a => versionForms.Contains(a, StringComparer.OrdinalIgnoreCase)))
    return ShowVersion();

// Parse global flags
bool verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));
bool hexDump = args.Any(a => a.Equals("--hexdump", StringComparison.OrdinalIgnoreCase));
string[] positionalArgs = args.Where(a =>
    !a.StartsWith("--", StringComparison.OrdinalIgnoreCase)).ToArray();
string[] flagArgs = args.Where(a =>
    a.StartsWith("--", StringComparison.OrdinalIgnoreCase)).ToArray();

if (hexDump) verbose = true;
var minLogLevel = verbose ? LogLevel.Debug : LogLevel.Information;

// Resolve the shared log location/cadence from appsettings (the same Bridge:Logging
// the service uses) so `logs --where/--clear` and the CLI's own cli-*.log all agree.
BridgeLogging.Configure(CliConfig.Load().Logging);
var cliFileLogger = BridgeLogging.BuildCliLogger();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.AddSerilog(cliFileLogger, dispose: true);   // mirror to cli-*.log
    builder.SetMinimumLevel(minLogLevel);
});

var command = positionalArgs.Length > 0 ? positionalArgs[0].ToLower() : "";
var bridgeOptions = new BridgeOptions { Verbose = verbose, HexDump = hexDump };

// `--version` and `--help` are long-form flags, so they were filtered out of
// positionalArgs above and the command switch never sees them (which is why
// `WorkbenchBridge.Cli.exe --version` reported "Unknown command:"). Resolve the
// flag forms here, before the switch, so both `version` and `--version` work.
// The bare-word `version`/`help` and the short `-h`/`-?` forms are handled by
// the switch below (those reach positionalArgs).
if (flagArgs.Any(f => f.Equals("--version", StringComparison.OrdinalIgnoreCase)))
    return ShowVersion();
if (flagArgs.Any(f => f.Equals("--help", StringComparison.OrdinalIgnoreCase)))
    return ShowHelp();

return command switch
{
    // Service management commands (via IPC)
    "--version" or "version" => ShowVersion(),
    "list"     => await IpcListAsync(),
    "status"   => await IpcStatusAsync(),
    "add"      => await IpcAddAsync(positionalArgs, flagArgs),
    "remove"   => await IpcRemoveAsync(positionalArgs),
    "start"    => await IpcStartStopAsync(positionalArgs, IpcCommand.Start),
    "stop"     => await IpcStartStopAsync(positionalArgs, IpcCommand.Stop),
    "diagnose" => await IpcDiagnoseAsync(positionalArgs),
    "set"      => await IpcSetLoggingAsync(positionalArgs, flagArgs),
    "reset-counters" => await IpcResetCountersAsync(positionalArgs),
    "debug" or "watch" => await RunDebugSessionAsync(positionalArgs, flagArgs),

    // Provisioning command (sends Local.json to the service via IPC; the
    // service does the registry/com0com work as SYSTEM - no elevation needed)
    "configure" => await ConfigureCommand.RunAsync(args, loggerFactory),

    // Flashing and chip identification
    "flash"    => await FlashCommand.RunAsync(args, loggerFactory),
    "device-info" or "identify" => await DeviceInfoCommand.RunAsync(args, loggerFactory),

    // Direct/debug commands (no service required)
    "test"     => await TestConnectionAsync(positionalArgs, loggerFactory),
    "bridge"   => await RunSingleBridgeAsync(positionalArgs, loggerFactory, bridgeOptions),
    "discover" => await DiscoverDevicesAsync(positionalArgs, loggerFactory),
    "pairs"    => await ListPairsAsync(positionalArgs, loggerFactory),
    "logs" or "monitor" => await LogsCommand.RunAsync(args),
    "help" or "--help" or "-h" or "-?" => ShowHelp(),

    _ => ShowUnknownCommand(command)
};

// ---------------------------------------------------------------
// Help and version
// ---------------------------------------------------------------

static int ShowVersion()
{
    // Version string is composed at build time by Directory.Build.targets:
    //   0.5.0.{commit-count}-dev+{git-hash}  for local dev builds
    //   0.5.0.{commit-count}                 for CI/release builds
    // See VERSIONING.md.
    var version = Assembly.GetEntryAssembly()
        ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "unknown";
    Console.WriteLine($"Workbench-Bridge CLI {version}");
    return 0;
}

static int ShowHelp()
{
    PrintUsage();
    return 0;
}

static int ShowUnknownCommand(string cmd)
{
    Console.Error.WriteLine($"Unknown command: {cmd}");
    Console.Error.WriteLine("Run 'workbenchbridge-cli help' for usage.");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("ESP32 Workbench Bridge CLI");
    Console.WriteLine();
    Console.WriteLine("Service management (requires the Windows service to be running):");
    Console.WriteLine("  list                                    List configured bridges and their status");
    Console.WriteLine("  status                                  Service health and per-bridge details");
    Console.WriteLine("  add <user-port> <host> <rfc-port>       Add a bridge mapping");
    Console.WriteLine("       [--internal <port>]                  Internal COM port (default: auto)");
    Console.WriteLine("       [--label <name>]                     Slot label (e.g. SLOT1)");
    Console.WriteLine("       [--description <text>]               Physical location description");
    Console.WriteLine("  remove <user-port>                      Remove a bridge mapping");
    Console.WriteLine("  start <user-port>                       Start a stopped bridge");
    Console.WriteLine("  stop <user-port>                        Stop a bridge (keeps config)");
    Console.WriteLine("  diagnose <user-port>                    Check com0com pair, Pi, RFC 2217");
    Console.WriteLine("  set <user-port> [--verbose] [--hexdump] [--raw]");
    Console.WriteLine("                                          Set logging for a bridge (applies live)");
    Console.WriteLine("  reset-counters <COMxx|all>              Zero a bridge's TX/RX byte counters");
    Console.WriteLine("                                          (diagnostics only; logs untouched)");
    Console.WriteLine("  debug <user-port> [--verbose] [--raw]   Live debug session: enable verbose/raw");
    Console.WriteLine("       (alias: watch)                       logging, stream it, restore on Ctrl+C");
    Console.WriteLine();
    Console.WriteLine("Provisioning (no elevation - the service does the privileged work):");
    Console.WriteLine("  configure [--source <path>] [--dry-run]");
    Console.WriteLine("                                          Send Local.json to the service, which seeds");
    Console.WriteLine("                                          com0com pairs, renames them, and restarts");
    Console.WriteLine("                                          its bridges. --dry-run prints the plan only.");
    Console.WriteLine();
    Console.WriteLine("Flashing and chip identification:");
    Console.WriteLine("  flash <port> <binary> [--address 0x10000] [--baud 460800] [--chip esp32s3]");
    Console.WriteLine("                                          Flash firmware via esptool. Reads the slot's");
    Console.WriteLine("                                          reset profile from the service and sequences the");
    Console.WriteLine("                                          reset (classic DTR/RTS, or native-USB download");
    Console.WriteLine("                                          mode via the Pi / manual BOOT) accordingly.");
    Console.WriteLine("  device-info <slot|comport>              Ask the Pi to identify the chip on a slot");
    Console.WriteLine("       [--host <ip>] [--port <portalPort>]  (alias: identify)");
    Console.WriteLine();
    Console.WriteLine("Direct commands (no service needed, for debugging):");
    Console.WriteLine("  test <host> <port>                      Test RFC 2217 connection");
    Console.WriteLine("  bridge <comport> <host> <port>          Run a single bridge");
    Console.WriteLine("  discover <host> [portalPort]            Discover ESP32 devices on Pi (with VID/PID,");
    Console.WriteLine("                                          device type, transport, chip, reset method)");
    Console.WriteLine("  pairs                                   List com0com virtual port pairs (from registry)");
    Console.WriteLine();
    Console.WriteLine("Logs (docker-logs style, reads the rolling Serilog file - no service needed):");
    Console.WriteLine("  logs [options]   (alias: monitor)");
    Console.WriteLine("       -f, --follow                        Stream new lines as they arrive");
    Console.WriteLine("       -n, --tail <N|all>                  Last N entries (default: all)");
    Console.WriteLine("       --since <ts|rel>                    From an ISO-8601 time or relative (42m, 2h, 1d)");
    Console.WriteLine("       --details                           Raw lines (full date, timezone, exceptions)");
    Console.WriteLine("       -c, --comport <COMxx>               Filter to one slot (user+internal+TCP port)");
    Console.WriteLine("       --path <file|dir>                   Read a specific log file or directory");
    Console.WriteLine("       --clear                             Delete log files (stop the service first to");
    Console.WriteLine("                                            clear the current day's in-use file)");
    Console.WriteLine("       --where                             Print the log directory + files and exit");
    Console.WriteLine("                                            (default C:\\ProgramData\\Workbench-Bridge\\logs:");
    Console.WriteLine("                                             service-*.log, cli-*.log, port-COMxx-*.log)");
    Console.WriteLine();
    Console.WriteLine("Other:");
    Console.WriteLine("  --version                               Show version");
    Console.WriteLine("  help                                    Show this help");
    Console.WriteLine();
    Console.WriteLine("Global flags:");
    Console.WriteLine("  --verbose   Enable debug logging");
    Console.WriteLine("  --hexdump   Enable hex dump of TX/RX data (implies --verbose)");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  workbenchbridge-cli list");
    Console.WriteLine("  workbenchbridge-cli add COM41 workbench.local 4001 --label SLOT1 --description \"RPi top-left USB\"");
    Console.WriteLine("  workbenchbridge-cli status");
    Console.WriteLine("  workbenchbridge-cli diagnose COM41");
    Console.WriteLine("  workbenchbridge-cli bridge COM241 workbench.local 4001 --verbose");
    Console.WriteLine("  workbenchbridge-cli logs -f -c COM42");
    Console.WriteLine("  workbenchbridge-cli logs --since 30m --tail 100");
    Console.WriteLine("  workbenchbridge-cli debug COM46 --raw      # live cable view, restores on Ctrl+C");
    Console.WriteLine("  workbenchbridge-cli flash COM41 firmware.bin --address 0x10000");
    Console.WriteLine("  workbenchbridge-cli device-info COM41      # identify the chip on the slot");
    Console.WriteLine("  workbenchbridge-cli logs --clear           # delete log files");
}

// ---------------------------------------------------------------
// IPC helper
// ---------------------------------------------------------------

static async Task<IpcResponse> SendIpcAsync(IpcCommand command, object? parameters = null)
{
    using var client = new IpcClient();
    var request = new IpcRequest
    {
        Command = command,
        Params = parameters is not null
            ? JsonSerializer.SerializeToElement(parameters)
            : null
    };

    try
    {
        return await client.SendAsync(request);
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
    {
        // Service not installed / not running / pipe gone. Never stack-dump;
        // hand back a polite, actionable message for the caller to print.
        return new IpcResponse
        {
            Success = false,
            Message = "WorkbenchBridge service is not reachable - is it installed and running?\n" +
                      "  install : Workbench-Bridge.Service.exe --install\n" +
                      "  or run  : Workbench-Bridge.Service.exe   (console mode)"
        };
    }
    catch (Exception ex)
    {
        return new IpcResponse { Success = false, Message = $"IPC error: {ex.Message}" };
    }
}

static void WriteErrorLine(string? message)
{
    var prev = Console.ForegroundColor;
    try
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(message ?? "Unknown error.");
    }
    finally { Console.ForegroundColor = prev; }
}

static string GetFlag(string[] flags, string name, string defaultValue = "")
{
    for (int i = 0; i < flags.Length - 1; i++)
    {
        if (flags[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return flags[i + 1];
    }
    return defaultValue;
}

static bool HasFlag(string[] flags, string name) =>
    flags.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase));

// ---------------------------------------------------------------
// Service management commands
// ---------------------------------------------------------------

static async Task<int> IpcListAsync()
{
    var response = await SendIpcAsync(IpcCommand.List);
    if (!response.Success)
    {
        WriteErrorLine(response.Message);
        return 1;
    }

    if (response.Data is null)
    {
        Console.WriteLine("No bridges configured.");
        return 0;
    }

    var bridges = response.Data.Value.Deserialize<List<BridgeInfo>>();
    if (bridges is null || bridges.Count == 0)
    {
        Console.WriteLine("No bridges configured.");
        return 0;
    }

    Console.WriteLine($"{"Port",-8} {"Host",-18} {"RFC2217",-8} {"Label",-8} {"State",-14} {"Baud",-8} {"Description"}");
    Console.WriteLine($"{"----",-8} {"----",-18} {"------",-8} {"-----",-8} {"-----",-14} {"----",-8} {"-----------"}");

    foreach (var b in bridges)
    {
        string state = FormatState(b.State);
        string baud = b.CurrentBaud?.ToString() ?? "-";
        Console.WriteLine(
            $"{b.UserPort,-8} {b.Host,-18} {b.Rfc2217Port,-8} {b.Label ?? "-",-8} {state,-14} {baud,-8} {b.Description ?? ""}");
    }

    return 0;
}

static async Task<int> IpcStatusAsync()
{
    var response = await SendIpcAsync(IpcCommand.Status);
    if (!response.Success)
    {
        WriteErrorLine(response.Message);
        return 1;
    }

    if (response.Data is null)
    {
        Console.WriteLine("Service is running but returned no status data.");
        return 0;
    }

    var status = response.Data.Value.Deserialize<ServiceStatus>();
    if (status is null)
    {
        Console.WriteLine("Could not parse service status.");
        return 1;
    }

    string piState = status.PiReachable ? "reachable" : "UNREACHABLE";
    Console.WriteLine($"Service version: {status.Version}");
    Console.WriteLine($"Uptime:          {status.Uptime}");
    Console.WriteLine($"Pi portal:       {status.PiHost ?? "-"} ({piState})");
    Console.WriteLine($"Bridges:         {status.Bridges.Count}");
    if (!status.PiReachable)
        Console.WriteLine("  (Pi portal not answering - device presence unknown; bridges run in degraded mode.)");
    Console.WriteLine();

    if (status.Bridges.Count > 0)
    {
        Console.WriteLine($"{"Port",-8} {"State",-12} {"Device",-14} {"Transport",-12} {"Baud",-8} {"TX bytes",-12} {"RX bytes",-12} {"Verbose",-8} {"Error"}");
        Console.WriteLine($"{"----",-8} {"-----",-12} {"------",-14} {"---------",-12} {"----",-8} {"--------",-12} {"--------",-12} {"-------",-8} {"-----"}");

        foreach (var b in status.Bridges)
        {
            string state = FormatState(b.State);
            // Prefer a compact device type (e.g. "CP210x", "ESP-JTAG") from the
            // portal classification; fall back to the devnode / portal state for
            // old portals that don't report a device type.
            string device = CompactDevice(b) ?? b.DevicePath ?? b.DeviceStatus ?? "-";
            string transport = CompactTransport(b.Transport);
            string baud = b.CurrentBaud?.ToString() ?? "-";
            string verboseFlag = (b.Verbose || b.HexDump || b.Raw)
                ? string.Join("+", new[]
                  {
                      b.Verbose ? "v" : null,
                      b.HexDump ? "hex" : null,
                      b.Raw ? "raw" : null
                  }.Where(x => x is not null))
                : "no";
            Console.WriteLine(
                $"{b.UserPort,-8} {state,-12} {device,-14} {transport,-12} {baud,-8} {b.BytesToDevice,-12} {b.BytesFromDevice,-12} {verboseFlag,-8} {b.LastError ?? ""}");
        }
    }

    return 0;
}

static string FormatState(BridgeState state) => state switch
{
    BridgeState.NoDevice => "no-device",
    _ => state.ToString().ToLowerInvariant()
};

/// <summary>
/// Condense the portal's verbose device_type into a short tag for the status
/// table (e.g. "CP210x", "ESP-JTAG", "CH340"). Returns null when the portal
/// gave us nothing to classify, so the caller can fall back to the devnode.
/// </summary>
static string? CompactDevice(BridgeInfo b)
{
    string? dt = b.DeviceType;
    if (string.IsNullOrEmpty(dt))
    {
        // No classification - chip family is the next best compact label.
        return string.IsNullOrEmpty(b.ChipFamily) ? null : b.ChipFamily;
    }

    bool Has(string s) => dt.Contains(s, StringComparison.OrdinalIgnoreCase);
    if (Has("JTAG")) return "ESP-JTAG";
    if (Has("CP210")) return "CP210x";
    if (Has("CH340") || Has("CH341")) return "CH340";
    if (Has("FTDI") || Has("FT232")) return "FTDI";
    if (Has("CDC")) return "USB-CDC";

    // Unknown phrasing: take the first word so the column stays narrow.
    string first = dt.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? dt;
    return first.Length > 13 ? first[..13] : first;
}

/// <summary>Short transport tag for the status table.</summary>
static string CompactTransport(string? transport) => transport?.ToLowerInvariant() switch
{
    "native-usb" => "native-usb",
    "uart-bridge" => "uart",
    null or "" => "-",
    var t => t!
};

static async Task<int> IpcAddAsync(string[] positional, string[] flags)
{
    if (positional.Length < 4)
    {
        Console.Error.WriteLine("Usage: add <user-port> <host> <rfc-port> [--internal <port>] [--label <name>] [--description <text>]");
        return 1;
    }

    string userPort = positional[1].ToUpper();
    string host = positional[2];
    int rfc2217Port = int.Parse(positional[3]);

    // Auto-generate internal port: COM41 -> COM241
    string internalPort = GetFlag(flags, "--internal");
    if (string.IsNullOrEmpty(internalPort))
    {
        // Extract number from user port and prepend "2"
        string num = new string(userPort.Where(char.IsDigit).ToArray());
        internalPort = $"COM2{num}";
    }

    var addParams = new AddBridgeParams
    {
        UserPort = userPort,
        InternalPort = internalPort.ToUpper(),
        Host = host,
        Rfc2217Port = rfc2217Port,
        Label = GetFlag(flags, "--label") is { Length: > 0 } label ? label : null,
        Description = GetFlag(flags, "--description") is { Length: > 0 } desc ? desc : null
    };

    var response = await SendIpcAsync(IpcCommand.Add, addParams);
    Console.WriteLine(response.Message ?? (response.Success ? "Bridge added." : "Failed."));
    return response.Success ? 0 : 1;
}

static async Task<int> IpcRemoveAsync(string[] positional)
{
    if (positional.Length < 2)
    {
        Console.Error.WriteLine("Usage: remove <user-port>");
        return 1;
    }

    var removeParams = new RemoveBridgeParams { UserPort = positional[1].ToUpper() };
    var response = await SendIpcAsync(IpcCommand.Remove, removeParams);
    Console.WriteLine(response.Message ?? (response.Success ? "Bridge removed." : "Failed."));
    return response.Success ? 0 : 1;
}

static async Task<int> IpcStartStopAsync(string[] positional, IpcCommand command)
{
    if (positional.Length < 2)
    {
        Console.Error.WriteLine($"Usage: {command.ToString().ToLower()} <user-port>");
        return 1;
    }

    var ssParams = new StartStopBridgeParams { UserPort = positional[1].ToUpper() };
    var response = await SendIpcAsync(command, ssParams);
    Console.WriteLine(response.Message ?? (response.Success ? "Done." : "Failed."));
    return response.Success ? 0 : 1;
}

static async Task<int> IpcDiagnoseAsync(string[] positional)
{
    if (positional.Length < 2)
    {
        Console.Error.WriteLine("Usage: diagnose <user-port>");
        return 1;
    }

    var diagParams = new DiagnoseParams { UserPort = positional[1].ToUpper() };
    var response = await SendIpcAsync(IpcCommand.Diagnose, diagParams);

    if (!response.Success)
    {
        WriteErrorLine(response.Message);
        return 1;
    }

    if (response.Data is null)
    {
        Console.WriteLine("No diagnostics data returned.");
        return 1;
    }

    var diag = response.Data.Value.Deserialize<DiagnoseResult>();
    if (diag is null) return 1;

    Console.WriteLine($"Diagnostics for {diag.UserPort}:");
    Console.WriteLine($"  com0com pair:     {(diag.Com0comPairExists ? "OK" : $"MISSING - {diag.Com0comError}")}");
    Console.WriteLine($"  Pi reachable:     {(diag.PiReachable ? "OK" : $"FAIL - {diag.PiError}")}");
    Console.WriteLine($"  RFC 2217 connect: {(diag.Rfc2217Connectable ? "OK" : $"FAIL - {diag.Rfc2217Error}")}");

    return (diag.Com0comPairExists && diag.PiReachable && diag.Rfc2217Connectable) ? 0 : 1;
}

static async Task<int> IpcResetCountersAsync(string[] positional)
{
    if (positional.Length < 2)
    {
        WriteErrorLine("Usage: reset-counters <COMxx|all>");
        return 1;
    }
    string arg = positional[1];
    string? port = arg.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : arg;
    var response = await SendIpcAsync(IpcCommand.ResetCounters, new { UserPort = port });
    if (!response.Success)
    {
        WriteErrorLine(response.Message);
        return 1;
    }
    Console.WriteLine(response.Message);
    return 0;
}

static async Task<int> IpcSetLoggingAsync(string[] positional, string[] flags)
{
    if (positional.Length < 2)
    {
        Console.Error.WriteLine("Usage: set <user-port> [--verbose] [--hexdump]");
        return 1;
    }

    var setParams = new SetLoggingParams
    {
        UserPort = positional[1].ToUpper(),
        Verbose = HasFlag(flags, "--verbose") ? true : null,
        HexDump = HasFlag(flags, "--hexdump") ? true : null,
        Raw = HasFlag(flags, "--raw") ? true : null
    };

    var response = await SendIpcAsync(IpcCommand.SetLogging, setParams);
    Console.WriteLine(response.Message ?? (response.Success ? "Logging updated." : "Failed."));
    return response.Success ? 0 : 1;
}

// ---------------------------------------------------------------
// debug/watch: a live debug session for one slot.
//   1. snapshot the slot's current logging levels
//   2. enable verbose + raw (or just what was asked) on the running bridge
//   3. stream the slot's log to stdout (raw ASCII of both directions appears)
//   4. on Ctrl+C, restore the original logging levels, then exit
// All of this happens against the live bridge - no restart needed.
// ---------------------------------------------------------------

static async Task<int> RunDebugSessionAsync(string[] positional, string[] flags)
{
    // Resolve the slot: `debug COM46` or `debug --comport COM46`.
    string? comport = GetFlag(flags, "--comport");
    if (string.IsNullOrEmpty(comport) && positional.Length > 1)
        comport = positional[1];
    if (string.IsNullOrEmpty(comport))
    {
        Console.Error.WriteLine("Usage: debug <comport> [--verbose] [--raw]   (alias: watch)");
        Console.Error.WriteLine("  Enables live verbose/raw logging for one slot, streams it, and");
        Console.Error.WriteLine("  restores the previous logging levels on Ctrl+C.");
        return 1;
    }
    comport = comport.ToUpper();

    bool wantVerbose = HasFlag(flags, "--verbose");
    bool wantRaw = HasFlag(flags, "--raw");
    // The whole point of `debug` is to see what is happening, so if the caller
    // gave no preference, turn on both verbose diagnostics and the raw cable view.
    if (!wantVerbose && !wantRaw) { wantVerbose = true; wantRaw = true; }

    // 1. Snapshot the slot's current logging so we can restore it on exit.
    var status = await SendIpcAsync(IpcCommand.Status);
    if (!status.Success || status.Data is null)
    {
        Console.Error.WriteLine($"Service not reachable: {status.Message}");
        Console.Error.WriteLine("The debug command needs the Windows service running.");
        return 1;
    }

    var svc = status.Data.Value.Deserialize<ServiceStatus>();
    var bridge = svc?.Bridges.FirstOrDefault(b =>
        b.UserPort.Equals(comport, StringComparison.OrdinalIgnoreCase) ||
        b.InternalPort.Equals(comport, StringComparison.OrdinalIgnoreCase));
    if (bridge is null)
    {
        Console.Error.WriteLine($"No bridge is configured for {comport}.");
        if (svc is { Bridges.Count: > 0 })
            Console.Error.WriteLine(
                "Configured ports: " +
                string.Join(", ", svc.Bridges.Select(b => b.UserPort)));
        return 1;
    }

    bool priorVerbose = bridge.Verbose, priorHex = bridge.HexDump, priorRaw = bridge.Raw;

    // 2. Enable the requested logging on the live bridge.
    var enable = new SetLoggingParams
    {
        UserPort = comport,
        Verbose = wantVerbose ? true : null,
        Raw = wantRaw ? true : null
    };
    var enableResp = await SendIpcAsync(IpcCommand.SetLogging, enable);
    if (!enableResp.Success)
    {
        Console.Error.WriteLine($"Could not enable debug logging: {enableResp.Message}");
        return 1;
    }

    Console.Error.WriteLine(
        $"-- live debug on {comport}: verbose={wantVerbose} raw={wantRaw}. " +
        "RAW RX/TX lines show the cable. Ctrl+C restores previous logging. --");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        // 3. Stream the slot's log (a short tail for context, then follow live).
        await LogsCommand.ExecuteAsync(LogsCommand.FollowOptions(comport, tail: 20), cts.Token);
    }
    finally
    {
        // 4. Restore the slot's original logging levels - best effort, even on Ctrl+C.
        var restore = new SetLoggingParams
        {
            UserPort = comport,
            Verbose = priorVerbose,
            HexDump = priorHex,
            Raw = priorRaw
        };
        var restoreResp = await SendIpcAsync(IpcCommand.SetLogging, restore);
        Console.Error.WriteLine(restoreResp.Success
            ? $"-- restored {comport} logging: verbose={priorVerbose} hexdump={priorHex} raw={priorRaw} --"
            : $"-- WARNING: could not restore logging on {comport}: {restoreResp.Message} --");
    }

    return 0;
}

// ---------------------------------------------------------------
// Direct/debug commands (unchanged, no service required)
// ---------------------------------------------------------------

static async Task<int> TestConnectionAsync(string[] args, ILoggerFactory loggerFactory)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: test <host> <port>");
        return 1;
    }

    string host = args[1];
    int port = int.Parse(args[2]);
    var logger = loggerFactory.CreateLogger<Rfc2217Client>();

    Console.WriteLine($"Testing RFC 2217 connection to {host}:{port}...");

    await using var client = new Rfc2217Client(host, port, logger);

    client.OnDataReceived(async (data, ct) =>
    {
        var text = System.Text.Encoding.ASCII.GetString(data.Span);
        Console.Write(text);
    });

    client.OnModemStateChanged(state =>
    {
        Console.WriteLine($"[Modem state: 0x{state:X2} CTS={((state & 0x10) != 0 ? "ON" : "OFF")} DSR={((state & 0x20) != 0 ? "ON" : "OFF")}]");
    });

    try
    {
        await client.ConnectAsync();
        Console.WriteLine("Connected. Negotiation complete.");
        Console.WriteLine($"Setting baud rate to 115200...");
        await client.SetBaudRateAsync(115200);
        Console.WriteLine($"Current baud rate: {client.CurrentBaudRate}");

        Console.WriteLine();
        Console.WriteLine("Listening for data. Press Ctrl+C to exit.");
        Console.WriteLine("Type text and press Enter to send to the device.");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        while (!cts.Token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                await client.SendDataAsync(new[] { (byte)key.KeyChar }, cts.Token);
            }
            await Task.Delay(10, cts.Token);
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Disconnected.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Connection failed: {ex.Message}");
        return 1;
    }

    return 0;
}

static async Task<int> RunSingleBridgeAsync(string[] args, ILoggerFactory loggerFactory, BridgeOptions options)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("Usage: bridge <comport> <host> <port> [--verbose] [--hexdump]");
        return 1;
    }

    string comPort = args[1];
    string host = args[2];
    int port = int.Parse(args[3]);

    Console.WriteLine($"Starting bridge: {comPort} <-> {host}:{port}");
    if (options.Verbose)
        Console.WriteLine($"Verbose logging enabled.{(options.HexDump ? " Hex dump enabled." : "")}");
    Console.WriteLine("Press Ctrl+C to stop.");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await using var bridge = new SerialBridge(comPort, host, port, loggerFactory, options);

    try
    {
        await bridge.StartAsync(ct: cts.Token);
        Console.WriteLine($"Bridge running. IDE can now use the paired COM port.");
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Stopping bridge...");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Bridge error: {ex.Message}");
        return 1;
    }

    return 0;
}

static async Task<int> DiscoverDevicesAsync(string[] args, ILoggerFactory loggerFactory)
{
    string host = args.Length > 1 ? args[1] : "workbench.local";
    int portalPort = args.Length > 2 ? int.Parse(args[2]) : 8080;

    Console.WriteLine($"Discovering devices on {host}:{portalPort}...");

    using var discovery = new PiDeviceDiscovery(
        host, portalPort, "/api/devices",
        loggerFactory.CreateLogger<PiDeviceDiscovery>());

    var result = await discovery.PollAsync();

    if (!result.Reachable)
    {
        Console.Error.WriteLine($"Pi portal at {host}:{portalPort} did not answer (/api/devices).");
        return 1;
    }

    var slots = result.Slots;
    int present = slots.Count(s => s.Present);
    Console.WriteLine($"Portal '{result.Hostname ?? host}': {slots.Count} slot(s), {present} with a device.");
    Console.WriteLine();
    Console.WriteLine($"  {"Slot",-8} {"TCP",-6} {"Present",-8} {"State",-8} {"Device",-15} {"Last error"}");
    Console.WriteLine($"  {"------",-8} {"----",-6} {"-------",-8} {"-----",-8} {"-------------",-15} {"----------"}");

    foreach (var s in slots.OrderBy(s => s.TcpPort))
    {
        string dev = s.Present && !string.IsNullOrEmpty(s.Devnode) ? s.Devnode : "-";
        Console.WriteLine(
            $"  {s.Label,-8} {s.TcpPort,-6} {(s.Present ? "yes" : "no"),-8} {s.State,-8} {dev,-15} {s.LastError ?? ""}");

        // Detail block from the portal's device-classification fields (portal
        // commit 9784ab3+). Only printed when the portal actually supplied them,
        // so against an older portal the table above is all you see.
        bool hasClassification =
            !string.IsNullOrEmpty(s.Vid) || !string.IsNullOrEmpty(s.DeviceType) ||
            !string.IsNullOrEmpty(s.Transport) || s.ResetProfile is not null;
        if (hasClassification)
        {
            string vidpid = !string.IsNullOrEmpty(s.Vid) || !string.IsNullOrEmpty(s.UsbPid)
                ? $"{s.Vid ?? "????"}:{s.UsbPid ?? "????"}"
                : "-";
            Console.WriteLine($"           VID:PID    {vidpid}" +
                (string.IsNullOrEmpty(s.UsbVendor) && string.IsNullOrEmpty(s.UsbModel)
                    ? ""
                    : $"  ({s.UsbVendor} {s.UsbModel})".TrimEnd()));
            Console.WriteLine($"           Type       {s.DeviceType ?? "-"}");
            Console.WriteLine($"           Transport  {s.Transport ?? "-"}");
            Console.WriteLine($"           Chip       {s.ChipFamily ?? "-"}");
            Console.WriteLine($"           Device ID  {s.DeviceId ?? "-"}");
            string reset = s.ResetProfile?.ResetMethod ?? "-";
            string dlCapable = s.ResetProfile?.DownloadModeCapable is bool dc
                ? (dc ? " (download-mode capable)" : " (manual download mode)")
                : "";
            Console.WriteLine($"           Reset      {reset}{dlCapable}");
        }
    }

    return 0;
}

static Task<int> ListPairsAsync(string[] args, ILoggerFactory loggerFactory)
{
    const string defaultParamsKey = @"SYSTEM\CurrentControlSet\Services\com0com\Parameters";
    try
    {
        var registry = new Com0comRegistry(defaultParamsKey,
            loggerFactory.CreateLogger<Com0comRegistry>());
        var sides = registry.ListSides();

        if (sides.Count == 0)
        {
            Console.WriteLine("No com0com pair subkeys found in registry.");
            return Task.FromResult(0);
        }

        var ordered = sides.Values
            .OrderBy(s => s.SubkeyName.Substring(4))
            .ThenBy(s => s.SubkeyName.Substring(0, 4))
            .ToList();

        Console.WriteLine($"Found {sides.Count} com0com side(s) in registry:");
        Console.WriteLine();
        Console.WriteLine($"  {"Subkey",-8} {"PortName",-10} {"RealPortName",-13} {"Exposed",-10} {"EmuBR",-6} {"EmuOvr"}");
        Console.WriteLine($"  {"------",-8} {"--------",-10} {"------------",-13} {"-------",-10} {"-----",-6} {"-----"}");

        foreach (var s in ordered)
        {
            string exposed = registry.ReadExposedPortNameFromEnum(s.SubkeyName) ?? "(none)";
            Console.WriteLine(
                $"  {s.SubkeyName,-8} {s.PortName,-10} {s.RealPortName,-13} {exposed,-10} " +
                $"{(s.EmuBR ? "yes" : "no"),-6} {(s.EmuOverrun ? "yes" : "no")}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to read com0com registry: {ex.Message}");
        return Task.FromResult(1);
    }

    return Task.FromResult(0);
}
