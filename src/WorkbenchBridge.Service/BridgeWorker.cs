using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkbenchBridge.Ipc;
using WorkbenchBridge.Rfc2217;

namespace WorkbenchBridge.Service;

/// <summary>
/// Background worker that manages the bridge lifecycle.
///
/// 1. Loads configuration (appsettings.json layered with appsettings.Local.json)
/// 2. Validates that each configured ComPortMapping matches the live com0com
///    registry (read-only). Logs a warning if anything is out of sync but
///    still starts what it can.
/// 3. Creates a SerialBridge for each configured slot and supervises them.
/// 4. Listens on a named pipe for CLI commands (list/status/diagnose/etc).
///
/// On a Configure command the service applies the machine configuration itself
/// (com0com pairs, registry, pnputil) via <see cref="Provisioner"/> - it runs as
/// LocalSystem, so the CLI never needs elevation. The bridges are torn down
/// before and rebuilt after provisioning; the service never stops itself.
/// </summary>
public sealed class BridgeWorker : BackgroundService
{
    // The active config. Replaced in place when the CLI sends a Configure
    // request (see HandleConfigureAsync), so it is not readonly.
    private BridgeConfig _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BridgeWorker> _logger;
    private readonly DateTime _startTime = DateTime.UtcNow;

    // Upper bound on a single bridge start (TCP connect + RFC 2217 negotiation).
    // Kept below the discovery polling interval so a stalled slot cannot stack up
    // across the sequential reconcile pass. See TryStartBridgeAsync.
    private const int BridgeStartTimeoutSeconds = 8;

    // Active bridges keyed by user-facing COM port (e.g. "COM41").
    private readonly ConcurrentDictionary<string, ManagedBridge> _bridges = new();

    // Serialises Configure (which tears down and rebuilds all bridges + mutates
    // the com0com registry) against the background monitor loop so the two never
    // race on _bridges or fight over the com0com pairs mid-reinstall.
    private readonly SemaphoreSlim _configureLock = new(1, 1);

    // Self-heal: per-pair-index count of attempts this service run has made to
    // finish a stuck com0com Ports-class migration, capped so a pair that
    // genuinely can't migrate doesn't re-run setupc on every poll.
    private readonly Dictionary<int, int> _selfHealAttempts = new();
    // Pair indices we've already logged a "gave up" warning for, so the message
    // fires once instead of on every poll cycle after the cap is reached.
    private readonly HashSet<int> _selfHealGaveUp = new();
    // Install (may defer rename) + settle/rename pass + one spare. Each pass is
    // bounded and, with serenum stripped + sermouse disabled, cannot disturb the
    // desktop, so this can be a little generous without risk.
    private const int SelfHealMaxAttempts = 3;

    private Com0comRegistry? _com0com;
    private PiDeviceDiscovery? _discovery;
    private IpcServer? _ipcServer;

    // Latest snapshot from the Pi portal API, refreshed each monitor pass. The
    // dictionary is keyed by RFC2217 TCP port (matches ComPortMapping.PiTcpPort).
    // Reads from the IPC thread and writes from the monitor loop only ever swap
    // the whole reference, so no lock is needed.
    private volatile bool _piReachable;
    private Dictionary<int, PiSlot> _slotsByPort = new();

    // Per-port log factories (port-COMxx-*.log), created lazily and reused across
    // bridge restarts; disposed once at shutdown. Keyed by the user COM port.
    private readonly ConcurrentDictionary<string, ILoggerFactory> _portLoggers =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastLogPurgeUtc = DateTime.UtcNow;

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
        // Version string is composed at build time by Directory.Build.targets:
        //   0.5.0.{commit-count}-dev+{git-hash}  for local dev builds
        //   0.5.0.{commit-count}                 for CI/release builds. See VERSIONING.md.
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        _logger.LogInformation("ESP32 Workbench Bridge service starting (v{Version})", version);
        _logger.LogInformation(
            "Config: {Count} ComPortMapping(s), com0com params at HKLM\\{Key}",
            _config.ComPortMapping.Count, _config.Registry.Com0comParametersKey);

        _com0com = new Com0comRegistry(
            _config.Registry.Com0comParametersKey,
            _loggerFactory.CreateLogger<Com0comRegistry>());

        _discovery = new PiDeviceDiscovery(
            _config.Pi.Host,
            _config.Pi.PortalPort,
            _config.Pi.DiscoveryEndpoint,
            _loggerFactory.CreateLogger<PiDeviceDiscovery>());

        _ipcServer = new IpcServer(
            _loggerFactory.CreateLogger<IpcServer>(),
            HandleIpcRequestAsync);
        _ipcServer.Start(stoppingToken);

        if (_config.ComPortMapping.Count == 0)
        {
            _logger.LogWarning(
                "No ComPortMapping entries configured. Drop appsettings.Local.json next to " +
                "the service exe or run 'workbenchbridge-cli configure' to apply a config.");
        }

        ValidateRegistry();

        foreach (var mapping in _config.ComPortMapping)
        {
            var info = new BridgeMapping
            {
                UserPort = mapping.User.PortName,
                InternalPort = mapping.Internal.PortName,
                Host = _config.Pi.Host,
                Rfc2217Port = mapping.PiTcpPort,
                Label = mapping.SlotLabel,
                Description = null
            };

            _bridges[mapping.User.PortName.ToUpper()] = new ManagedBridge { Mapping = info };
        }

        // Ask the Pi which slots actually have an ESP32 before we try to bridge
        // anything, so empty slots come up as NoDevice instead of hammering a
        // refused TCP port and reporting a socket error.
        await PollPiAsync(stoppingToken);
        foreach (var entry in _bridges)
        {
            await ReconcileBridgeAsync(entry.Key, stoppingToken);
        }

        // Finish any com0com pair whose Ports-class migration didn't complete
        // (e.g. an interrupted install left it stuck in the raw CNCPorts class).
        await SelfHealStuckComPortsAsync(stoppingToken);

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

            MaybePurgeLogs();

            // Periodically re-check for com0com pairs stuck outside the Ports
            // class and finish their migration (no-op when all are healthy).
            await SelfHealStuckComPortsAsync(stoppingToken);

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_config.Runtime.DiscoveryPollingIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Shutting down, stopping all bridges");
        if (_ipcServer is not null)
            await _ipcServer.DisposeAsync();

        foreach (var entry in _bridges)
        {
            if (entry.Value.Bridge is not null)
                await entry.Value.Bridge.DisposeAsync();
        }
        _bridges.Clear();
        foreach (var f in _portLoggers.Values) { try { f.Dispose(); } catch { /* flushing */ } }
        _portLoggers.Clear();
        _discovery?.Dispose();
        _configureLock.Dispose();
    }

    /// <summary>
    /// Purge stale logs roughly hourly (the startup sweep runs in Program.cs). Cheap,
    /// best-effort — files the running service still holds are skipped until they roll.
    /// </summary>
    private void MaybePurgeLogs()
    {
        if (DateTime.UtcNow - _lastLogPurgeUtc < TimeSpan.FromHours(1)) return;
        _lastLogPurgeUtc = DateTime.UtcNow;
        try
        {
            int n = BridgeLogging.PurgeOldLogs(DateTime.UtcNow);
            if (n > 0) _logger.LogInformation("Purged {Count} stale log file(s)", n);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Log purge failed this pass"); }
    }

    /// <summary>
    /// Read-only sanity check that the com0com registry matches what we plan to
    /// open. We never mutate the registry here - the CLI does that.
    /// </summary>
    private void ValidateRegistry()
    {
        Dictionary<string, Com0comSideStatus> sides;
        try
        {
            sides = _com0com!.ListSides();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read com0com registry. Bridges may still start " +
                                 "but registry validation is skipped.");
            return;
        }

        int matched = 0;
        foreach (var mapping in _config.ComPortMapping)
        {
            bool ok = ValidateSide(mapping.SlotLabel, mapping.User, sides) &
                      ValidateSide(mapping.SlotLabel, mapping.Internal, sides);
            if (ok) matched++;
        }
        _logger.LogInformation(
            "Registry validation: {Matched}/{Total} slot(s) match config.",
            matched, _config.ComPortMapping.Count);
    }

    private bool ValidateSide(
        string slot, Com0comSideConfig side, Dictionary<string, Com0comSideStatus> registry)
    {
        if (!registry.TryGetValue(side.RegistryKey, out var status))
        {
            _logger.LogWarning(
                "Slot {Slot}: registry subkey {Key} is missing. " +
                "Run 'workbenchbridge-cli configure' (elevated) to seed and apply.",
                slot, side.RegistryKey);
            return false;
        }

        // The authoritative effective COM port name is in the PnP Enum
        // branch (HKLM\...\Enum\COM0COM\PORT\{subkey}\Device Parameters\PortName)
        // - that's what Windows actually exposes, and it can differ from
        // Parameters\PortName/RealPortName depending on how the device was
        // last registered. Fall back to the Parameters values for diagnostic
        // text if the Enum side isn't populated.
        string? exposed = _com0com!.ReadExposedPortNameFromEnum(side.RegistryKey);
        string effective = !string.IsNullOrEmpty(exposed) ? exposed : status.EffectivePortName;

        if (!string.Equals(effective, side.PortName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Slot {Slot}: {Key} is exposed as '{Actual}' (Parameters PortName='{PortName}', " +
                "RealPortName='{RealPortName}') but config wants '{Expected}'. " +
                "Run 'workbenchbridge-cli configure' to fix.",
                slot, side.RegistryKey, effective,
                status.PortName, status.RealPortName, side.PortName);
            return false;
        }

        // EmuBR must be ON (com0com paces bytes to the configured baud rate) and
        // EmuOverrun must be OFF (with it on, com0com emulates a UART receive
        // overrun and silently DISCARDS bytes when the reader lags, corrupting
        // ESP32 traffic). The provisioner normalises to exactly this; warn only
        // when the live registry disagrees. See Com0comRegistry.EnsureEmuFlags.
        if (!status.EmuBR)
        {
            _logger.LogWarning(
                "Slot {Slot}: {Key} ({Port}) has EmuBR off. Baud rate pacing will be wrong. " +
                "Run 'workbenchbridge-cli configure' to fix.",
                slot, side.RegistryKey, side.PortName);
        }
        if (status.EmuOverrun)
        {
            _logger.LogWarning(
                "Slot {Slot}: {Key} ({Port}) has EmuOverrun ON; it must be off or ESP32 " +
                "traffic (esptool sync/flash) will be corrupted. " +
                "Run 'workbenchbridge-cli configure' to fix.",
                slot, side.RegistryKey, side.PortName);
        }

        return true;
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

        var options = new BridgeOptions
        {
            Verbose = managed.Verbose,
            HexDump = managed.HexDump,
            Raw = managed.Raw
        };
        // Keep a reference so a live `set`/`debug` toggle can mutate the running
        // bridge's options without a restart (the bridge reads them per-loop).
        managed.LiveOptions = options;

        // Route this bridge's logs (lifecycle, DTR/RTS, reset translation, --raw
        // serial) to its own port-COMxx-*.log so each slot has a dedicated file.
        var portFactory = _portLoggers.GetOrAdd(
            mapping.UserPort, p => BridgeLogging.CreatePortLoggerFactory(p));
        var bridge = new SerialBridge(
            mapping.InternalPort,
            mapping.Host,
            mapping.Rfc2217Port,
            portFactory,
            options);

        try
        {
            // Bound the start with a timeout. The portal can report a slot as
            // present a moment before ser2net actually accepts on its TCP port
            // (common with USB-CDC /dev/ttyACM* devices), and a refused-but-not-
            // reset port makes TcpClient.ConnectAsync hang for the OS default
            // (~21s) before failing. Because the monitor loop reconciles bridges
            // sequentially, one such slot would stall every other slot for that
            // whole time. Fail fast instead and let the next poll retry.
            using var startTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(BridgeStartTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, startTimeout.Token);

            await bridge.StartAsync(ct: linked.Token);
            managed.Bridge = bridge;
            managed.State = BridgeState.Running;
            managed.LastError = null;

            // Cache the slot's device classification + reset profile (when the
            // portal supplies them) so flash commands can read the profile via
            // IPC without re-querying the Pi, and so it survives a transient
            // "no-device" blip. Null on older portals - treated as "unknown".
            _slotsByPort.TryGetValue(mapping.Rfc2217Port, out var slot);
            if (slot is not null) managed.DeviceSlot = slot;

            // Tell the running bridge how to reset this slot's board. For a
            // native USB-Serial-JTAG slot ("usb-jtag") the bridge translates the
            // host's classic DTR/RTS auto-reset into the correct USB sequence
            // inline (IDEs offer no --before override). Read fresh by the signal
            // loop, so this is a no-op for classic/UART slots.
            options.ResetMethod = ResolveResetMethod(slot);

            string devType = slot?.DeviceType ?? "unknown device type";
            string resetMethod = slot?.ResetProfile?.ResetMethod ?? "unknown reset method";
            _logger.LogInformation(
                "Bridge active: {UserPort} ({InternalPort}) <-> {Host}:{TcpPort} [{Label}] " +
                "- {DeviceType}, transport={Transport}, reset={ResetMethod}",
                mapping.UserPort, mapping.InternalPort, mapping.Host,
                mapping.Rfc2217Port, mapping.Label ?? "no label",
                devType, slot?.Transport ?? "?", resetMethod);

            // Native-USB ESP32s (S2/S3/C3/H2 - USB-Serial-JTAG or USB-OTG CDC)
            // enumerate on the Pi as /dev/ttyACM*. The serial console bridges
            // fine, but flashing over RFC 2217 typically fails: such chips
            // re-enumerate their USB device when reset into the download
            // bootloader, which tears down the Pi-side ser2net fd, so esptool
            // sees "No serial data received". Classic USB-UART boards (CP2102/
            // CH340 -> /dev/ttyUSB*) flash fine over the bridge. Prefer the
            // portal's transport classification when present, falling back to
            // the devnode heuristic for older portals. Surface this once so a
            // stuck "Connecting....." isn't mistaken for a bridge bug.
            bool nativeUsb =
                string.Equals(slot?.Transport, "native-usb", StringComparison.OrdinalIgnoreCase) ||
                (slot?.Devnode is { Length: > 0 } dev &&
                 dev.Contains("ttyACM", StringComparison.OrdinalIgnoreCase));
            if (nativeUsb)
            {
                _logger.LogInformation(
                    "{UserPort}: slot {Label} ({DeviceType}) is a native-USB (USB-Serial-JTAG) ESP32 " +
                    "(reset={ResetMethod}). The bridge translates the host's classic DTR/RTS auto-reset " +
                    "into the native USB-Serial-JTAG reset sequence inline, so esptool/IDE flashing works " +
                    "without a --before override. If a board re-enumerates its USB on reset, the bridge " +
                    "reconnects automatically.",
                    mapping.UserPort, mapping.Label ?? "?", devType, resetMethod);
            }
        }
        catch (Exception ex)
        {
            // StartAsync opens the Internal COM port before it connects, so on any
            // failure we must dispose the half-started bridge or we leak that
            // handle (which then blocks the next start attempt on the same port).
            await bridge.DisposeAsync();
            managed.LiveOptions = null;

            // Service shutting down - not a bridge error, stay quiet.
            if (ct.IsCancellationRequested) return;

            bool timedOut = ex is OperationCanceledException;
            managed.LastError = timedOut
                ? $"Connect to {mapping.Host}:{mapping.Rfc2217Port} timed out after {BridgeStartTimeoutSeconds}s"
                : $"Start failed: {ex.Message}";
            managed.State = BridgeState.Error;

            if (timedOut)
                _logger.LogWarning(
                    "Bridge start for {UserPort} ({InternalPort}) timed out: {Host}:{TcpPort} " +
                    "did not accept within {Timeout}s. The portal reports the slot present but " +
                    "ser2net may not be serving it yet; will retry next poll.",
                    mapping.UserPort, mapping.InternalPort, mapping.Host,
                    mapping.Rfc2217Port, BridgeStartTimeoutSeconds);
            else
                _logger.LogError(ex,
                    "Failed to start bridge for {UserPort} ({InternalPort}) <-> {Host}:{TcpPort}",
                    mapping.UserPort, mapping.InternalPort, mapping.Host, mapping.Rfc2217Port);
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
        managed.LiveOptions = null;
        managed.State = BridgeState.Stopped;
        managed.LastError = null;
    }

    /// <summary>
    /// Refreshes the cached Pi portal snapshot (reachability + per-slot devices).
    /// Never throws; on failure marks the portal unreachable and clears devices.
    /// </summary>
    private async Task PollPiAsync(CancellationToken ct)
    {
        if (_discovery is null) return;
        try
        {
            var result = await _discovery.PollAsync(ct);
            _piReachable = result.Reachable;
            var map = new Dictionary<int, PiSlot>();
            foreach (var s in result.Slots)
                map[s.TcpPort] = s;
            _slotsByPort = map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Pi poll threw unexpectedly");
            _piReachable = false;
        }
    }

    /// <summary>
    /// True if the Pi portal reports a live ESP32 on the given RFC2217 port. Uses
    /// the portal's authoritative <c>present</c> flag (never the device node,
    /// which can linger after a removal).
    /// </summary>
    private bool IsDevicePresent(int rfc2217Port) =>
        _slotsByPort.TryGetValue(rfc2217Port, out var s) && s.Present;

    /// <summary>
    /// Resolve the reset method the bridge should apply for a slot: prefer the
    /// portal's explicit <c>reset_profile.reset_method</c>, fall back to the
    /// transport ("native-usb" ⇒ "usb-jtag"), then the device node heuristic
    /// (a "ttyACM*" devnode ⇒ native USB ⇒ "usb-jtag" — current portals send
    /// neither profile nor transport, only devnode), then "classic" — the safe
    /// default for wired UART boards and for older portals that supply no profile.
    /// </summary>
    private static string ResolveResetMethod(PiSlot? slot)
    {
        string? method = slot?.ResetProfile?.ResetMethod;
        if (!string.IsNullOrWhiteSpace(method))
            return method.ToLowerInvariant();
        if (string.Equals(slot?.Transport, "native-usb", StringComparison.OrdinalIgnoreCase))
            return "usb-jtag";
        if (slot?.Devnode is { Length: > 0 } dev &&
            dev.Contains("ttyACM", StringComparison.OrdinalIgnoreCase))
            return "usb-jtag";
        return "classic";
    }

    /// <summary>
    /// Brings one bridge into line with the Pi's reported device presence:
    ///   portal reachable + device present -> ensure running
    ///   portal reachable + no device      -> ensure stopped, state NoDevice
    ///   portal unreachable                -> attempt start if idle (degraded mode)
    /// </summary>
    private async Task ReconcileBridgeAsync(string userPort, CancellationToken ct)
    {
        if (!_bridges.TryGetValue(userPort, out var managed))
            return;

        int port = managed.Mapping.Rfc2217Port;

        if (_piReachable)
        {
            if (IsDevicePresent(port))
            {
                if (managed.Bridge?.IsRunning != true)
                    await TryStartBridgeAsync(userPort, ct);
            }
            else
            {
                // Slot is empty on the Pi - don't bridge it, and don't surface a
                // socket error. It auto-starts when a device is plugged in.
                if (managed.Bridge is not null)
                    await StopBridgeAsync(userPort);
                if (managed.State != BridgeState.NoDevice)
                    _logger.LogInformation(
                        "{Port}: no device on slot {Label} (port {Tcp}); idle until one is connected.",
                        managed.Mapping.UserPort, managed.Mapping.Label ?? "?", port);
                managed.State = BridgeState.NoDevice;
                managed.LastError = null;
            }
        }
        else
        {
            // Portal unreachable: we can't tell presence. Try to bring up idle
            // bridges anyway so a portal-only outage still works; leave running
            // bridges to the liveness/reconnect logic.
            if (managed.Bridge?.IsRunning != true &&
                managed.State is BridgeState.Stopped or BridgeState.NoDevice)
                await TryStartBridgeAsync(userPort, ct);
        }
    }

    private async Task MonitorBridgesAsync(CancellationToken ct)
    {
        // Skip this pass entirely if a Configure is running - it owns the bridge
        // set and the com0com pairs while it tears them down and rebuilds them.
        if (!await _configureLock.WaitAsync(0, ct))
            return;
        try
        {
            await MonitorBridgesCoreAsync(ct);
        }
        finally
        {
            _configureLock.Release();
        }
    }

    private async Task MonitorBridgesCoreAsync(CancellationToken ct)
    {
        // Ask the Pi portal which slots currently have a device, so we can bring
        // bridges up/down to match reality instead of blindly retrying.
        await PollPiAsync(ct);

        // Read the live com0com side map once per pass (read-only) so we can
        // detect a pair that vanished underneath a running bridge - e.g. the
        // driver was reloaded or the pair was removed via the CLI. Mirrors the
        // runtime health-check from trusting-leavitt, but stays read-only here:
        // the CLI, not the service, owns registry mutation.
        HashSet<string>? exposed = null;
        if (_com0com is not null)
        {
            try
            {
                var sides = _com0com.ListSides();
                exposed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (subkey, status) in sides)
                {
                    string? enumName = _com0com.ReadExposedPortNameFromEnum(subkey);
                    string name = !string.IsNullOrEmpty(enumName)
                        ? enumName
                        : status.EffectivePortName;
                    if (!string.IsNullOrEmpty(name)) exposed.Add(name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Health-check: could not read com0com registry this pass");
            }
        }

        foreach (var entry in _bridges)
        {
            var managed = entry.Value;

            // Health check: if the underlying com0com pair is gone, the bridge
            // can't recover on its own. Tear it down so the reconcile step below
            // re-establishes it once the pair (and a device) reappear.
            if (exposed is not null && managed.State == BridgeState.Running &&
                (!exposed.Contains(managed.Mapping.UserPort) ||
                 !exposed.Contains(managed.Mapping.InternalPort)))
            {
                _logger.LogWarning(
                    "com0com pair for {User}/{Internal} is missing from the registry; restarting bridge.",
                    managed.Mapping.UserPort, managed.Mapping.InternalPort);
                await StopBridgeAsync(entry.Key);
                managed.State = BridgeState.Error;
            }
            else if (managed.State == BridgeState.Running &&
                     managed.Bridge is not null &&
                     !managed.Bridge.IsRunning)
            {
                _logger.LogWarning("Bridge for {Port} died, attempting restart", entry.Key);
                managed.Bridge = null;
                managed.LiveOptions = null;
                managed.State = BridgeState.Error;
            }

            // Bring the bridge into line with the Pi's reported device presence.
            await ReconcileBridgeAsync(entry.Key, ct);
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
            IpcCommand.ResetCounters => HandleResetCounters(request.Params),
            IpcCommand.Configure => await HandleConfigureAsync(request.Params, ct),
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
        return new IpcResponse
        {
            Success = true,
            Data = JsonSerializer.SerializeToElement(OrderedBridgeInfos())
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
            PiHost = _config.Pi.Host,
            PiReachable = _piReachable,
            Bridges = OrderedBridgeInfos()
        };

        return new IpcResponse
        {
            Success = true,
            Data = JsonSerializer.SerializeToElement(status)
        };
    }

    /// <summary>All bridges as <see cref="BridgeInfo"/>, ordered by COM number.</summary>
    private List<BridgeInfo> OrderedBridgeInfos() =>
        _bridges.Values
            .Select(ToBridgeInfo)
            .OrderBy(b => ComNumber(b.UserPort))
            .ThenBy(b => b.UserPort, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Parses the numeric part of a COM port name for sorting (COM41 -> 41).</summary>
    private static int ComNumber(string port)
    {
        string digits = new string(port.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : int.MaxValue;
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

        bool com0comPairExists = false;
        string? com0comError = null;
        try
        {
            var sides = _com0com!.ListSides();
            bool userOk = sides.Values.Any(s =>
                string.Equals(s.EffectivePortName, managed.Mapping.UserPort, StringComparison.OrdinalIgnoreCase));
            bool internalOk = sides.Values.Any(s =>
                string.Equals(s.EffectivePortName, managed.Mapping.InternalPort, StringComparison.OrdinalIgnoreCase));
            com0comPairExists = userOk && internalOk;
        }
        catch (Exception ex)
        {
            com0comError = ex.Message;
        }

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
            if (setParams.Raw.HasValue) managed.Raw = setParams.Raw.Value;

            // Apply to the running bridge live (no restart). The bridge reads
            // these flags fresh on each data/signal-loop iteration.
            var live = managed.LiveOptions;
            if (live is not null)
            {
                if (setParams.Verbose.HasValue) live.Verbose = setParams.Verbose.Value;
                if (setParams.HexDump.HasValue) live.HexDump = setParams.HexDump.Value;
                if (setParams.Raw.HasValue) live.Raw = setParams.Raw.Value;
            }

            string applied = live is not null ? "applied live" : "saved (bridge not running)";
            return new IpcResponse
            {
                Success = true,
                Message = $"Logging updated for {key}: verbose={managed.Verbose} " +
                          $"hexdump={managed.HexDump} raw={managed.Raw} ({applied})."
            };
        }

        return new IpcResponse { Success = false, Message = "UserPort is required" };
    }

    /// <summary>
    /// Zero the TX/RX byte counters for one bridge, or all of them when no port
    /// (or "all") is given. Diagnostics only — does not touch logs or the data path.
    /// </summary>
    private IpcResponse HandleResetCounters(JsonElement? paramsJson)
    {
        string? port = null;
        if (paramsJson is not null)
            port = paramsJson.Value.Deserialize<ResetCountersParams>()?.UserPort;

        if (string.IsNullOrWhiteSpace(port) ||
            port.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            int n = 0;
            foreach (var m in _bridges.Values)
                if (m.Bridge is not null) { m.Bridge.ResetByteCounters(); n++; }
            return new IpcResponse
            {
                Success = true,
                Message = $"Reset TX/RX counters on {n} running bridge(s)."
            };
        }

        string key = port.ToUpper();
        if (!_bridges.TryGetValue(key, out var managed))
            return new IpcResponse { Success = false, Message = $"{key} is not configured" };
        if (managed.Bridge is null)
            return new IpcResponse { Success = true, Message = $"{key} is not running; counters are already 0." };

        managed.Bridge.ResetByteCounters();
        return new IpcResponse { Success = true, Message = $"Reset TX/RX counters for {key}." };
    }

    /// <summary>
    /// Applies a machine configuration sent by the CLI. This is the SYSTEM-side
    /// of the configure flow: the CLI (non-elevated) reads the user's Local.json
    /// and hands it here; the service does the privileged work (registry,
    /// setupc.exe, pnputil) and rebuilds its own bridges. The service never
    /// stops itself - it only stops/starts its in-process bridges to release the
    /// Internal COM port handles around the com0com pair churn.
    /// </summary>
    private async Task<IpcResponse> HandleConfigureAsync(JsonElement? paramsJson, CancellationToken ct)
    {
        if (paramsJson is null)
            return new IpcResponse { Success = false, Message = "Missing parameters" };

        var cfgParams = paramsJson.Value.Deserialize<ConfigureParams>();
        if (cfgParams is null || string.IsNullOrWhiteSpace(cfgParams.LocalJson))
            return new IpcResponse { Success = false, Message = "Invalid parameters" };

        BridgeConfig newConfig;
        try
        {
            newConfig = ParseBridgeConfig(cfgParams.LocalJson);
        }
        catch (Exception ex)
        {
            return new IpcResponse { Success = false, Message = $"Could not parse config: {ex.Message}" };
        }

        if (newConfig.ComPortMapping.Count == 0)
            return new IpcResponse
            {
                Success = false,
                Message = "Config has no Bridge.ComPortMapping entries."
            };

        await _configureLock.WaitAsync(ct);
        try
        {
            var provisioner = new Provisioner(_loggerFactory);

            // Dry run: compute and return the plan without touching anything.
            if (cfgParams.DryRun)
            {
                var dry = provisioner.Apply(newConfig, dryRun: true);
                return ConfigureResponse(dry.Success, dry.Log);
            }

            // 1. Stop all current bridges so they release the Internal COM port
            //    handles before we add/remove/rename the underlying com0com pairs.
            _logger.LogInformation("Configure: stopping {Count} bridge(s) before provisioning.", _bridges.Count);
            foreach (var key in _bridges.Keys.ToList())
                await StopBridgeAsync(key);

            // 2. Privileged provisioning (registry + setupc + pnputil), as SYSTEM.
            var result = provisioner.Apply(newConfig, dryRun: false);

            // 3. Persist the new Local.json into the service install folder so the
            //    config survives a service restart / reboot. Non-fatal if it fails.
            var log = new List<string>(result.Log);
            try
            {
                string dest = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");
                await File.WriteAllTextAsync(dest, cfgParams.LocalJson, ct);
                log.Add($"Persisted config to {dest}");
            }
            catch (Exception ex)
            {
                log.Add($"WARNING: provisioning applied but persisting config failed: {ex.Message}");
            }

            // 4. Swap in the new config and rebuild the bridge set from its
            //    mappings, then start them against the freshly-provisioned pairs.
            _config = newConfig;

            _discovery?.Dispose();
            _discovery = new PiDeviceDiscovery(
                _config.Pi.Host,
                _config.Pi.PortalPort,
                _config.Pi.DiscoveryEndpoint,
                _loggerFactory.CreateLogger<PiDeviceDiscovery>());

            _com0com = new Com0comRegistry(
                _config.Registry.Com0comParametersKey,
                _loggerFactory.CreateLogger<Com0comRegistry>());

            _bridges.Clear();
            foreach (var mapping in _config.ComPortMapping)
            {
                var info = new BridgeMapping
                {
                    UserPort = mapping.User.PortName,
                    InternalPort = mapping.Internal.PortName,
                    Host = _config.Pi.Host,
                    Rfc2217Port = mapping.PiTcpPort,
                    Label = mapping.SlotLabel,
                    Description = null
                };
                _bridges[mapping.User.PortName.ToUpper()] = new ManagedBridge { Mapping = info };
            }

            await PollPiAsync(ct);
            foreach (var key in _bridges.Keys.ToList())
                await ReconcileBridgeAsync(key, ct);

            int running = _bridges.Values.Count(b => b.State == BridgeState.Running);
            int noDevice = _bridges.Values.Count(b => b.State == BridgeState.NoDevice);
            log.Add($"Bridges after configure: {running}/{_bridges.Count} running" +
                    (noDevice > 0 ? $", {noDevice} idle (no device on slot)." : "."));

            return ConfigureResponse(result.Success, log);
        }
        finally
        {
            _configureLock.Release();
        }
    }

    /// <summary>
    /// Self-heal: find any configured com0com pair that is registered but stuck
    /// OUTSIDE the Windows Ports class (its Ports-class migration didn't finish -
    /// e.g. an interrupted install) and re-run provisioning to complete it, so the
    /// COM port actually appears. SURGICAL: only the affected slot's bridge is
    /// bounced; the rest of the bench keeps running. Capped per pair-index per
    /// service run so a pair that can't migrate doesn't re-run setupc forever.
    /// No-op (cheap registry reads) when everything is healthy.
    /// </summary>
    private async Task SelfHealStuckComPortsAsync(CancellationToken ct)
    {
        if (_com0com is null || _config.ComPortMapping.Count == 0) return;

        var stuckKeys = new List<string>();
        foreach (var m in _config.ComPortMapping)
        {
            int idx = m.GetPairIndex();

            // A pair is healthy only when BOTH sides are real Ports-class devices
            // AND expose the configured COM name. Checking Ports-class membership
            // alone is not enough: an install with PortName=COM# auto-assigns a
            // name (e.g. COM17), lands the device in the Ports class, but the
            // rename to the desired name (COM48) can stay deferred. The old check
            // saw "in Ports class" and declared victory, so the rename pass never
            // ran and the wrong name stuck (SLOT8, 2026-06-15). The live-name check
            // makes self-heal retry the (cheap, no-reboot) rename until it applies.
            bool inPorts = _com0com.IsExposedInPortsClass($"CNCA{idx}")
                           && _com0com.IsExposedInPortsClass($"CNCB{idx}");
            string liveA = _com0com.ReadExposedPortNameFromEnum($"CNCA{idx}") ?? "";
            string liveB = _com0com.ReadExposedPortNameFromEnum($"CNCB{idx}") ?? "";
            bool nameOk = string.Equals(liveA, m.User.PortName, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(liveB, m.Internal.PortName, StringComparison.OrdinalIgnoreCase);

            if (inPorts && nameOk) continue;
            if (_selfHealAttempts.GetValueOrDefault(idx) >= SelfHealMaxAttempts)
            {
                if (_selfHealGaveUp.Add(idx))
                    _logger.LogWarning(
                        "Self-heal: {Port} still not healthy after {Max} attempts " +
                        "(in Ports class: {InPorts}, live CNCA={LiveA}/{WantA} CNCB={LiveB}/{WantB}); " +
                        "giving up - a reboot may be required to finish the Ports-class migration.",
                        m.User.PortName, SelfHealMaxAttempts, inPorts,
                        liveA, m.User.PortName, liveB, m.Internal.PortName);
                continue;
            }
            stuckKeys.Add(m.User.PortName.ToUpper());
        }
        if (stuckKeys.Count == 0) return;

        // Don't collide with a configure already in progress.
        if (!await _configureLock.WaitAsync(0, ct)) return;
        try
        {
            _logger.LogWarning(
                "Self-heal: {Ports} not a healthy Ports-class port with the configured " +
                "name (missing, stuck in CNCPorts, or auto-named); re-running provisioning " +
                "to finish the migration/rename.",
                string.Join(", ", stuckKeys));

            foreach (var m in _config.ComPortMapping)
            {
                if (!stuckKeys.Contains(m.User.PortName.ToUpper())) continue;
                int idx = m.GetPairIndex();
                _selfHealAttempts[idx] = _selfHealAttempts.GetValueOrDefault(idx) + 1;
            }

            // Release the affected slots' Internal COM handles before setupc runs.
            foreach (var key in stuckKeys)
                await StopBridgeAsync(key);

            // Apply against the CURRENT config: only the stuck pair(s) plan as
            // "reinstall"; healthy pairs are "ok" and left untouched.
            var result = new Provisioner(_loggerFactory).Apply(_config, dryRun: false);
            if (!result.Success)
                _logger.LogWarning("Self-heal: provisioning reported failure (see provisioner log).");

            // Re-open just the affected bridges against the freshly-migrated ports.
            await PollPiAsync(ct);
            foreach (var key in stuckKeys)
                await ReconcileBridgeAsync(key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Self-heal failed");
        }
        finally
        {
            _configureLock.Release();
        }
    }

    private static IpcResponse ConfigureResponse(bool success, List<string> log) => new()
    {
        Success = success,
        Message = success ? "Configuration applied." : "Configuration failed (see log).",
        Data = JsonSerializer.SerializeToElement(new ConfigureResult { Log = log })
    };

    /// <summary>
    /// Parses the "Bridge" section out of a raw appsettings.Local.json string
    /// into a <see cref="BridgeConfig"/>, the same way the host binds it at
    /// startup.
    /// </summary>
    private static BridgeConfig ParseBridgeConfig(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var conf = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();
        var bridge = new BridgeConfig();
        conf.GetSection("Bridge").Bind(bridge);
        return bridge;
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private BridgeInfo ToBridgeInfo(ManagedBridge managed)
    {
        _slotsByPort.TryGetValue(managed.Mapping.Rfc2217Port, out var slot);
        // Only surface the device node when the slot actually has a device -
        // the portal keeps the last devnode around after a removal.
        string? devicePath = slot is { Present: true } && !string.IsNullOrEmpty(slot.Devnode)
            ? slot.Devnode
            : null;

        // Device classification: prefer the live slot, fall back to the last
        // classification captured at bridge start so flash still has a profile
        // during a transient "no-device" blip. Either may be null on old portals.
        var classified = slot ?? managed.DeviceSlot;
        var profile = classified?.ResetProfile;

        return new BridgeInfo
        {
            UserPort = managed.Mapping.UserPort,
            InternalPort = managed.Mapping.InternalPort,
            Host = managed.Mapping.Host,
            Rfc2217Port = managed.Mapping.Rfc2217Port,
            Label = managed.Mapping.Label,
            Description = managed.Mapping.Description,
            State = managed.State,
            CurrentBaud = managed.Bridge?.CurrentBaudRate,
            DeviceStatus = slot?.State is { Length: > 0 } st ? st.ToLowerInvariant() : null,
            DevicePath = devicePath,
            BytesToDevice = managed.Bridge?.BytesToDevice ?? 0,
            BytesFromDevice = managed.Bridge?.BytesFromDevice ?? 0,
            LastError = managed.LastError,
            Verbose = managed.Verbose,
            HexDump = managed.HexDump,
            Raw = managed.Raw,
            Vid = classified?.Vid,
            Pid = classified?.UsbPid,
            UsbVendor = classified?.UsbVendor,
            UsbModel = classified?.UsbModel,
            Serial = classified?.Serial,
            DeviceType = classified?.DeviceType,
            Transport = classified?.Transport,
            ChipFamily = classified?.ChipFamily,
            DeviceId = classified?.DeviceId,
            ResetProfile = profile is null ? null : new ResetProfileInfo
            {
                Transport = profile.Transport,
                ChipFamily = profile.ChipFamily,
                ResetMethod = profile.ResetMethod,
                GpioBoot = profile.GpioBoot,
                GpioEn = profile.GpioEn,
                DownloadModeCapable = profile.DownloadModeCapable
            }
        };
    }

    private sealed class ManagedBridge
    {
        public required BridgeMapping Mapping { get; init; }
        public SerialBridge? Bridge { get; set; }
        public BridgeState State { get; set; } = BridgeState.Stopped;
        public string? LastError { get; set; }
        public bool Verbose { get; set; }
        public bool HexDump { get; set; }
        public bool Raw { get; set; }

        /// <summary>
        /// The live options instance handed to the running <see cref="Bridge"/>.
        /// SetLogging mutates this so verbose/hexdump/raw toggles take effect
        /// immediately without restarting the bridge. Null while stopped.
        /// </summary>
        public BridgeOptions? LiveOptions { get; set; }

        /// <summary>
        /// Last-known device classification + reset profile for this slot, as
        /// reported by the Pi portal. Captured when the bridge starts so flash
        /// commands can read the reset profile via IPC and it survives a
        /// transient "no-device" blip. Null until a classified device is seen
        /// (or always, on portals older than commit 9784ab3).
        /// </summary>
        public PiSlot? DeviceSlot { get; set; }
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
