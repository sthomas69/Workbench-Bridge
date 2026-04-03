using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkbenchBridge.Ipc;
using WorkbenchBridge.Rfc2217;

namespace WorkbenchBridge.Service;

/// <summary>
/// Background worker that manages the full bridge lifecycle:
///
/// 1. Loads bridge configuration from appsettings.json
/// 2. Ensures com0com virtual pairs exist for each mapping
/// 3. Creates SerialBridge instances for each configured slot
/// 4. Listens on a named pipe for CLI commands (add/remove/start/stop/status)
/// 5. Handles dynamic reconfiguration without restarting the service
///
/// Each bridge runs independently in its own async tasks. Adding or removing
/// one bridge does not affect others.
/// </summary>
public sealed class BridgeWorker : BackgroundService
{
    private readonly BridgeConfig _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BridgeWorker> _logger;
    private readonly DateTime _startTime = DateTime.UtcNow;

    // Active bridges keyed by user-facing COM port (e.g. "COM41").
    private readonly ConcurrentDictionary<string, ManagedBridge> _bridges = new();

    private Com0comManager? _com0com;
    private PiDeviceDiscovery? _discovery;
    private IpcServer? _ipcServer;

    public BridgeWorker(
        IOptions<BridgeConfig> config,
        ILoggerFactory loggerFactory)
    {
        _config = config.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<BridgeWorker>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ESP32 Workbench Bridge service starting");

        // Initialise com0com manager
        try
        {
            _com0com = new Com0comManager(
                _config.Tools.Com0comPath,
                _loggerFactory.CreateLogger<Com0comManager>());
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogCritical(ex, "com0com not found. Install com0com v3.0.0 first.");
            return;
        }

        // Initialise Pi discovery
        _discovery = new PiDeviceDiscovery(
            _config.Pi.Host,
            _config.Pi.PortalPort,
            _config.Pi.DiscoveryEndpoint,
            _loggerFactory.CreateLogger<PiDeviceDiscovery>());

        // Start IPC server for CLI commands
        _ipcServer = new IpcServer(
            _loggerFactory.CreateLogger<IpcServer>(),
            HandleIpcRequestAsync);
        _ipcServer.Start(stoppingToken);

        // Load configured bridges and start them
        foreach (var mapping in _config.ComPortMapping)
        {
            var info = new BridgeMapping
            {
                UserPort = mapping.UserPort,
                InternalPort = mapping.InternalPort,
                Host = _config.Pi.Host,
                Rfc2217Port = mapping.PiTcpPort,
                Label = mapping.SlotLabel,
                Description = null
            };

            _bridges[mapping.UserPort.ToUpper()] = new ManagedBridge { Mapping = info };
        }

        // Start all configured bridges
        foreach (var entry in _bridges)
        {
            await TryStartBridgeAsync(entry.Key, stoppingToken);
        }

        // Health monitoring loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorBridgesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error in bridge monitoring loop");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_config.Service.DiscoveryPollingIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Shutdown
        _logger.LogInformation("Shutting down, stopping all bridges");
        if (_ipcServer is not null)
            await _ipcServer.DisposeAsync();

        foreach (var entry in _bridges)
        {
            if (entry.Value.Bridge is not null)
                await entry.Value.Bridge.DisposeAsync();
        }
        _bridges.Clear();
        _discovery?.Dispose();
    }

    // ---------------------------------------------------------------
    // Bridge lifecycle management
    // ---------------------------------------------------------------

    private async Task TryStartBridgeAsync(string userPort, CancellationToken ct)
    {
        if (!_bridges.TryGetValue(userPort, out var managed))
            return;

        if (managed.Bridge?.IsRunning == true)
            return;

        var mapping = managed.Mapping;

        // Ensure com0com pair exists
        try
        {
            await _com0com!.EnsurePairAsync(mapping.UserPort, mapping.InternalPort, ct);
        }
        catch (Exception ex)
        {
            managed.LastError = $"com0com pair creation failed: {ex.Message}";
            managed.State = BridgeState.Error;
            _logger.LogError(ex, "Failed to create com0com pair for {Port}", userPort);
            return;
        }

        // Create and start bridge
        try
        {
            var options = new BridgeOptions
            {
                Verbose = managed.Verbose,
                HexDump = managed.HexDump
            };

            var bridge = new SerialBridge(
                mapping.InternalPort,
                mapping.Host,
                mapping.Rfc2217Port,
                _loggerFactory,
                options);

            await bridge.StartAsync(ct: ct);
            managed.Bridge = bridge;
            managed.State = BridgeState.Running;
            managed.LastError = null;

            _logger.LogInformation(
                "Bridge active: {UserPort} ({InternalPort}) <-> {Host}:{TcpPort} [{Label}]",
                mapping.UserPort, mapping.InternalPort, mapping.Host,
                mapping.Rfc2217Port, mapping.Label ?? "no label");
        }
        catch (Exception ex)
        {
            managed.LastError = $"Start failed: {ex.Message}";
            managed.State = BridgeState.Error;
            _logger.LogError(ex, "Failed to start bridge for {Port}", userPort);
        }
    }

    private async Task StopBridgeAsync(string userPort)
    {
        if (!_bridges.TryGetValue(userPort, out var managed))
            return;

        if (managed.Bridge is not null)
        {
            await managed.Bridge.DisposeAsync();
            managed.Bridge = null;
        }
        managed.State = BridgeState.Stopped;
        managed.LastError = null;
    }

    private async Task MonitorBridgesAsync(CancellationToken ct)
    {
        foreach (var entry in _bridges)
        {
            var managed = entry.Value;
            if (managed.State == BridgeState.Running &&
                managed.Bridge is not null &&
                !managed.Bridge.IsRunning)
            {
                _logger.LogWarning("Bridge for {Port} died, attempting restart", entry.Key);
                managed.Bridge = null;
                managed.State = BridgeState.Error;
                await TryStartBridgeAsync(entry.Key, ct);
            }
        }
    }

    // ---------------------------------------------------------------
    // IPC command handler
    // ---------------------------------------------------------------

    private async Task<IpcResponse> HandleIpcRequestAsync(
        IpcRequest request, CancellationToken ct)
    {
        return request.Command switch
        {
            IpcCommand.Version => HandleVersion(),
            IpcCommand.List    => HandleList(),
            IpcCommand.Status  => HandleStatus(),
            IpcCommand.Add     => await HandleAddAsync(request.Params, ct),
            IpcCommand.Remove  => await HandleRemoveAsync(request.Params),
            IpcCommand.Start   => await HandleStartAsync(request.Params, ct),
            IpcCommand.Stop    => await HandleStopAsync(request.Params),
            IpcCommand.Diagnose => await HandleDiagnoseAsync(request.Params, ct),
            IpcCommand.SetLogging => HandleSetLogging(request.Params),
            _ => new IpcResponse { Success = false, Message = $"Unknown command: {request.Command}" }
        };
    }

    private IpcResponse HandleVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        return new IpcResponse
        {
            Success = true,
            Message = version
        };
    }

    private IpcResponse HandleList()
    {
        var bridges = _bridges.Values.Select(ToBridgeInfo).ToList();
        return new IpcResponse
        {
            Success = true,
            Data = JsonSerializer.SerializeToElement(bridges)
        };
    }

    private IpcResponse HandleStatus()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        var uptime = DateTime.UtcNow - _startTime;

        var status = new ServiceStatus
        {
            Version = version,
            Uptime = uptime.ToString(@"d\.hh\:mm\:ss"),
            Bridges = _bridges.Values.Select(ToBridgeInfo).ToList()
        };

        return new IpcResponse
        {
            Success = true,
            Data = JsonSerializer.SerializeToElement(status)
        };
    }

    private async Task<IpcResponse> HandleAddAsync(JsonElement? paramsJson, CancellationToken ct)
    {
        if (paramsJson is null)
            return new IpcResponse { Success = false, Message = "Missing parameters" };

        var addParams = paramsJson.Value.Deserialize<AddBridgeParams>();
        if (addParams is null)
            return new IpcResponse { Success = false, Message = "Invalid parameters" };

        string key = addParams.UserPort.ToUpper();
        if (_bridges.ContainsKey(key))
            return new IpcResponse { Success = false, Message = $"{key} is already configured" };

        var mapping = new BridgeMapping
        {
            UserPort = addParams.UserPort,
            InternalPort = addParams.InternalPort,
            Host = addParams.Host,
            Rfc2217Port = addParams.Rfc2217Port,
            Label = addParams.Label,
            Description = addParams.Description
        };

        var managed = new ManagedBridge { Mapping = mapping };
        _bridges[key] = managed;

        await TryStartBridgeAsync(key, ct);

        return new IpcResponse
        {
            Success = true,
            Message = $"Bridge added: {key} <-> {addParams.Host}:{addParams.Rfc2217Port}"
        };
    }

    private async Task<IpcResponse> HandleRemoveAsync(JsonElement? paramsJson)
    {
        if (paramsJson is null)
            return new IpcResponse { Success = false, Message = "Missing parameters" };

        var removeParams = paramsJson.Value.Deserialize<RemoveBridgeParams>();
        if (removeParams is null)
            return new IpcResponse { Success = false, Message = "Invalid parameters" };

        string key = removeParams.UserPort.ToUpper();
        if (!_bridges.TryRemove(key, out var managed))
            return new IpcResponse { Success = false, Message = $"{key} is not configured" };

        if (managed.Bridge is not null)
            await managed.Bridge.DisposeAsync();

        return new IpcResponse
        {
            Success = true,
            Message = $"Bridge removed: {key}"
        };
    }

    private async Task<IpcResponse> HandleStartAsync(JsonElement? paramsJson, CancellationToken ct)
    {
        if (paramsJson is null)
            return new IpcResponse { Success = false, Message = "Missing parameters" };

        var ssParams = paramsJson.Value.Deserialize<StartStopBridgeParams>();
        if (ssParams is null)
            return new IpcResponse { Success = false, Message = "Invalid parameters" };

        string key = ssParams.UserPort.ToUpper();
        if (!_bridges.ContainsKey(key))
            return new IpcResponse { Success = false, Message = $"{key} is not configured" };

        await TryStartBridgeAsync(key, ct);

        var managed = _bridges[key];
        return new IpcResponse
        {
            Success = managed.State == BridgeState.Running,
            Message = managed.State == BridgeState.Running
                ? $"{key} started"
                : $"Failed to start {key}: {managed.LastError}"
        };
    }

    private async Task<IpcResponse> HandleStopAsync(JsonElement? paramsJson)
    {
        if (paramsJson is null)
            return new IpcResponse { Success = false, Message = "Missing parameters" };

        var ssParams = paramsJson.Value.Deserialize<StartStopBridgeParams>();
        if (ssParams is null)
            return new IpcResponse { Success = false, Message = "Invalid parameters" };

        string key = ssParams.UserPort.ToUpper();
        if (!_bridges.ContainsKey(key))
            return new IpcResponse { Success = false, Message = $"{key} is not configured" };

        await StopBridgeAsync(key);

        return new IpcResponse { Success = true, Message = $"{key} stopped" };
    }

    private async Task<IpcResponse> HandleDiagnoseAsync(JsonElement? paramsJson, CancellationToken ct)
    {
        if (paramsJson is null)
            return new IpcResponse { Success = false, Message = "Missing parameters" };

        var diagParams = paramsJson.Value.Deserialize<DiagnoseParams>();
        if (diagParams is null)
            return new IpcResponse { Success = false, Message = "Invalid parameters" };

        string key = diagParams.UserPort.ToUpper();
        if (!_bridges.TryGetValue(key, out var managed))
            return new IpcResponse { Success = false, Message = $"{key} is not configured" };

        // Check com0com pair
        bool com0comPairExists = false;
        string? com0comError = null;
        try
        {
            var pairs = await _com0com!.ListPairsAsync(ct);
            com0comPairExists = pairs.Any(p =>
                (p.PortA?.Equals(managed.Mapping.UserPort, StringComparison.OrdinalIgnoreCase) == true ||
                 p.PortB?.Equals(managed.Mapping.UserPort, StringComparison.OrdinalIgnoreCase) == true) &&
                (p.PortA?.Equals(managed.Mapping.InternalPort, StringComparison.OrdinalIgnoreCase) == true ||
                 p.PortB?.Equals(managed.Mapping.InternalPort, StringComparison.OrdinalIgnoreCase) == true));
        }
        catch (Exception ex)
        {
            com0comError = ex.Message;
        }

        // Check Pi reachability
        bool piReachable = false;
        string? piError = null;
        try
        {
            piReachable = await _discovery!.IsReachableAsync(ct);
        }
        catch (Exception ex)
        {
            piError = ex.Message;
        }

        // Check RFC 2217 connectivity
        bool rfc2217Connectable = false;
        string? rfc2217Error = null;
        try
        {
            await using var testClient = new Rfc2217Client(
                managed.Mapping.Host,
                managed.Mapping.Rfc2217Port,
                _loggerFactory.CreateLogger<Rfc2217Client>());

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await testClient.ConnectAsync(linked.Token);
            rfc2217Connectable = true;
        }
        catch (Exception ex)
        {
            rfc2217Error = ex.Message;
        }

        var result = new DiagnoseResult
        {
            UserPort = key,
            Com0comPairExists = com0comPairExists,
            PiReachable = piReachable,
            Rfc2217Connectable = rfc2217Connectable,
            Com0comError = com0comError,
            PiError = piError,
            Rfc2217Error = rfc2217Error
        };

        return new IpcResponse
        {
            Success = true,
            Data = JsonSerializer.SerializeToElement(result)
        };
    }

    private IpcResponse HandleSetLogging(JsonElement? paramsJson)
    {
        if (paramsJson is null)
            return new IpcResponse { Success = false, Message = "Missing parameters" };

        var setParams = paramsJson.Value.Deserialize<SetLoggingParams>();
        if (setParams is null)
            return new IpcResponse { Success = false, Message = "Invalid parameters" };

        if (setParams.UserPort is not null)
        {
            string key = setParams.UserPort.ToUpper();
            if (!_bridges.TryGetValue(key, out var managed))
                return new IpcResponse { Success = false, Message = $"{key} is not configured" };

            if (setParams.Verbose.HasValue) managed.Verbose = setParams.Verbose.Value;
            if (setParams.HexDump.HasValue) managed.HexDump = setParams.HexDump.Value;

            // Note: logging changes take effect on next bridge restart.
            // Dynamic change would require bridge to expose mutable options.
            return new IpcResponse
            {
                Success = true,
                Message = $"Logging updated for {key}. Restart the bridge for changes to take effect."
            };
        }

        return new IpcResponse { Success = false, Message = "UserPort is required" };
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static BridgeInfo ToBridgeInfo(ManagedBridge managed) => new()
    {
        UserPort = managed.Mapping.UserPort,
        InternalPort = managed.Mapping.InternalPort,
        Host = managed.Mapping.Host,
        Rfc2217Port = managed.Mapping.Rfc2217Port,
        Label = managed.Mapping.Label,
        Description = managed.Mapping.Description,
        State = managed.State,
        CurrentBaud = null, // TODO: expose from SerialBridge
        BytesToDevice = 0,  // TODO: expose from SerialBridge
        BytesFromDevice = 0,
        LastError = managed.LastError,
        Verbose = managed.Verbose,
        HexDump = managed.HexDump
    };

    // ---------------------------------------------------------------
    // Internal state classes
    // ---------------------------------------------------------------

    private sealed class ManagedBridge
    {
        public required BridgeMapping Mapping { get; init; }
        public SerialBridge? Bridge { get; set; }
        public BridgeState State { get; set; } = BridgeState.Stopped;
        public string? LastError { get; set; }
        public bool Verbose { get; set; }
        public bool HexDump { get; set; }
    }

    private sealed class BridgeMapping
    {
        public required string UserPort { get; init; }
        public required string InternalPort { get; init; }
        public required string Host { get; init; }
        public required int Rfc2217Port { get; init; }
        public string? Label { get; init; }
        public string? Description { get; init; }
    }
}
