using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    PrintUsage();
    return 1;
}

// Parse global flags
bool verbose = args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));
bool hexDump = args.Any(a => a.Equals("--hexdump", StringComparison.OrdinalIgnoreCase));
string[] positionalArgs = args.Where(a =>
    !a.StartsWith("--", StringComparison.OrdinalIgnoreCase)).ToArray();
string[] flagArgs = args.Where(a =>
    a.StartsWith("--", StringComparison.OrdinalIgnoreCase)).ToArray();

if (hexDump) verbose = true;
var minLogLevel = verbose ? LogLevel.Debug : LogLevel.Information;

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(minLogLevel);
});

var command = positionalArgs.Length > 0 ? positionalArgs[0].ToLower() : "";
var bridgeOptions = new BridgeOptions { Verbose = verbose, HexDump = hexDump };

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

    // Direct/debug commands (no service required)
    "test"     => await TestConnectionAsync(positionalArgs, loggerFactory),
    "bridge"   => await RunSingleBridgeAsync(positionalArgs, loggerFactory, bridgeOptions),
    "discover" => await DiscoverDevicesAsync(positionalArgs, loggerFactory),
    "pairs"    => await ListPairsAsync(positionalArgs, loggerFactory),
    "help" or "--help" or "-h" or "-?" => ShowHelp(),

    _ => ShowUnknownCommand(command)
};

// ---------------------------------------------------------------
// Help and version
// ---------------------------------------------------------------

static int ShowVersion()
{
    var version = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "unknown";
    Console.WriteLine($"workbenchbridge-cli {version}");
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
    Console.WriteLine("  set <user-port> [--verbose] [--hexdump] Set logging for a bridge");
    Console.WriteLine();
    Console.WriteLine("Direct commands (no service needed, for debugging):");
    Console.WriteLine("  test <host> <port>                      Test RFC 2217 connection");
    Console.WriteLine("  bridge <comport> <host> <port>          Run a single bridge");
    Console.WriteLine("  discover <host> [portalPort]            Discover ESP32 devices on Pi");
    Console.WriteLine("  pairs                                   List com0com virtual port pairs");
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
    Console.WriteLine("  workbenchbridge-cli add COM41 192.168.8.32 4001 --label SLOT1 --description \"RPi top-left USB\"");
    Console.WriteLine("  workbenchbridge-cli status");
    Console.WriteLine("  workbenchbridge-cli diagnose COM41");
    Console.WriteLine("  workbenchbridge-cli bridge COM241 192.168.8.32 4001 --verbose");
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
    catch (InvalidOperationException ex)
    {
        return new IpcResponse { Success = false, Message = ex.Message };
    }
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
        Console.Error.WriteLine(response.Message);
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
        string state = b.State.ToString().ToLower();
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
        Console.Error.WriteLine(response.Message);
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

    Console.WriteLine($"Service version: {status.Version}");
    Console.WriteLine($"Uptime:          {status.Uptime}");
    Console.WriteLine($"Bridges:         {status.Bridges.Count}");
    Console.WriteLine();

    if (status.Bridges.Count > 0)
    {
        Console.WriteLine($"{"Port",-8} {"State",-14} {"Baud",-8} {"TX bytes",-12} {"RX bytes",-12} {"Verbose",-8} {"Error"}");
        Console.WriteLine($"{"----",-8} {"-----",-14} {"----",-8} {"--------",-12} {"--------",-12} {"-------",-8} {"-----"}");

        foreach (var b in status.Bridges)
        {
            string state = b.State.ToString().ToLower();
            string baud = b.CurrentBaud?.ToString() ?? "-";
            string verboseFlag = b.Verbose ? (b.HexDump ? "hexdump" : "yes") : "no";
            Console.WriteLine(
                $"{b.UserPort,-8} {state,-14} {baud,-8} {b.BytesToDevice,-12} {b.BytesFromDevice,-12} {verboseFlag,-8} {b.LastError ?? ""}");
        }
    }

    return 0;
}

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
        Console.Error.WriteLine(response.Message);
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
        HexDump = HasFlag(flags, "--hexdump") ? true : null
    };

    var response = await SendIpcAsync(IpcCommand.SetLogging, setParams);
    Console.WriteLine(response.Message ?? (response.Success ? "Logging updated." : "Failed."));
    return response.Success ? 0 : 1;
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
    string host = args.Length > 1 ? args[1] : "192.168.8.32";
    int portalPort = args.Length > 2 ? int.Parse(args[2]) : 8080;

    Console.WriteLine($"Discovering devices on {host}:{portalPort}...");

    using var discovery = new PiDeviceDiscovery(
        host, portalPort, "/api/devices",
        loggerFactory.CreateLogger<PiDeviceDiscovery>());

    var devices = await discovery.DiscoverAsync();

    if (devices.Count == 0)
    {
        Console.WriteLine("No devices found.");
        return 0;
    }

    Console.WriteLine($"Found {devices.Count} device(s):");
    Console.WriteLine();
    Console.WriteLine($"  {"Slot",-8} {"Port",-6} {"Device",-15} {"Chip",-15} {"Status"}");
    Console.WriteLine($"  {"------",-8} {"----",-6} {"-------------",-15} {"-------------",-15} {"------"}");

    foreach (var device in devices)
    {
        Console.WriteLine($"  {device.Slot,-8} {device.Port,-6} {device.Device,-15} {device.Chip,-15} {device.Status}");
    }

    return 0;
}

static async Task<int> ListPairsAsync(string[] args, ILoggerFactory loggerFactory)
{
    string com0comPath = @"C:\Program Files (x86)\com0com";

    try
    {
        var manager = new Com0comManager(com0comPath, loggerFactory.CreateLogger<Com0comManager>());
        var pairs = await manager.ListPairsAsync();

        if (pairs.Count == 0)
        {
            Console.WriteLine("No com0com pairs found.");
            return 0;
        }

        Console.WriteLine($"Found {pairs.Count} com0com pair(s):");
        Console.WriteLine();
        Console.WriteLine($"  {"Index",-7} {"Port A",-10} {"Port B",-11} {"EmuBR"}");
        Console.WriteLine($"  {"-----",-7} {"--------",-10} {"---------",-11} {"-----"}");

        foreach (var pair in pairs)
        {
            Console.WriteLine($"  {pair.Index,-7} {pair.PortA ?? "??",-10} {pair.PortB ?? "??",-11} {(pair.HasEmuBR ? "yes" : "no")}");
        }
    }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    return 0;
}
