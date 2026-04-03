using System.IO.Ports;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace WorkbenchBridge.Rfc2217;

/// <summary>
/// Diagnostic options for the serial bridge.
/// --verbose enables SLIP frame header logging and extra diagnostics.
/// --hexdump (requires --verbose) additionally logs raw hex bytes of TX/RX data.
/// </summary>
public sealed class BridgeOptions
{
    /// <summary>
    /// Enable verbose logging: SLIP frame headers, signal change details,
    /// reconnect diagnostics. Off by default for clean console output.
    /// </summary>
    public bool Verbose { get; init; }

    /// <summary>
    /// Enable hex dump logging of TX/RX data packets. Only effective when
    /// Verbose is also true. Produces a lot of output during flash operations.
    /// </summary>
    public bool HexDump { get; init; }
}

/// <summary>
/// Bridges a local COM port (com0com internal side) to an RFC 2217 server.
///
/// Data flow:
///   IDE -> COM41 (com0com user side) -> COM241 (com0com internal side) -> this bridge -> TCP -> Pi RFC2217 -> ESP32
///   ESP32 -> Pi RFC2217 -> TCP -> this bridge -> COM241 -> COM41 -> IDE
///
/// When the IDE changes baud rate on COM41, com0com reflects that on COM241.
/// This bridge detects the change and sends SET_BAUDRATE to the Pi via RFC 2217.
///
/// When the IDE toggles DTR/RTS (for ESP32 bootloader entry), com0com reflects
/// those signals on COM241. This bridge detects the changes and sends SET_CONTROL
/// to the Pi via RFC 2217.
/// </summary>
public sealed class SerialBridge : IAsyncDisposable
{
    private readonly string _localPortName;
    private readonly string _remoteHost;
    private readonly int _remotePort;
    private readonly ILogger<SerialBridge> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BridgeOptions _options;

    private SerialPort? _localPort;
    private Rfc2217Client? _rfc2217;
    private CancellationTokenSource? _cts;
    private Task? _localReadTask;
    private Task? _signalPollTask;
    private Task? _reconnectTask;

    // Track signal states to detect changes.
    // IMPORTANT: com0com is a null modem, so signals cross over:
    //   COM41 DTR (output) -> COM241 DSR + DCD (input)
    //   COM41 RTS (output) -> COM241 CTS (input)
    // To detect what the IDE sets on COM41, we read MODEM INPUT pins on COM241:
    //   DsrHolding = remote DTR state
    //   CtsHolding = remote RTS state
    private bool _lastRemoteDtr;  // read via DsrHolding on COM241
    private bool _lastRemoteRts;  // read via CtsHolding on COM241
    private int _lastBaudRate;
    private int _lastDataBits;
    private Parity _lastParity;
    private StopBits _lastStopBits;

    // Reconnection state
    private volatile bool _reconnecting;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private const int MaxReconnectAttempts = 10;
    private const int ReconnectBaseDelayMs = 1000;

    // Traffic counters for logging
    private long _bytesToDevice;
    private long _bytesFromDevice;
    private long _lastLoggedToDevice;
    private long _lastLoggedFromDevice;
    private DateTime _lastStatsLog = DateTime.UtcNow;

    // SLIP protocol sniffer for detecting esptool CHANGE_BAUDRATE commands.
    // Extracted into its own class for testability. See SlipBaudRateSniffer.cs.
    private readonly SlipBaudRateSniffer _slipSniffer;

    public string LocalPortName => _localPortName;
    public string RemoteEndpoint => $"{_remoteHost}:{_remotePort}";
    public bool IsRunning => _localReadTask is not null && !_localReadTask.IsCompleted;

    public SerialBridge(
        string localPortName,
        string remoteHost,
        int remotePort,
        ILoggerFactory loggerFactory,
        BridgeOptions? options = null)
    {
        _localPortName = localPortName;
        _remoteHost = remoteHost;
        _remotePort = remotePort;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SerialBridge>();
        _options = options ?? new BridgeOptions();
        _slipSniffer = new SlipBaudRateSniffer(_logger, _options.Verbose);
    }

    /// <summary>
    /// Start the bridge: open local COM port, connect to RFC 2217 server,
    /// begin bidirectional data pumping.
    /// </summary>
    public async Task StartAsync(int initialBaudRate = 115200, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Starting bridge: {LocalPort} <-> {Host}:{Port}",
            _localPortName, _remoteHost, _remotePort);

        // Open the local COM port (com0com internal side)
        _localPort = new SerialPort(_localPortName)
        {
            BaudRate = initialBaudRate,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 5000,
            DtrEnable = true,
            RtsEnable = true,
            ReadBufferSize = 65536,
            WriteBufferSize = 65536
        };
        _localPort.Open();

        _lastBaudRate = _localPort.BaudRate;
        _lastDataBits = _localPort.DataBits;
        _lastParity = _localPort.Parity;
        _lastStopBits = _localPort.StopBits;

        // Read the modem input pins to get the initial state of what the
        // other side (COM41) has set. Since nobody has opened COM41 yet,
        // these will typically be false.
        _lastRemoteDtr = _localPort.DsrHolding || _localPort.CDHolding;
        _lastRemoteRts = _localPort.CtsHolding;

        _logger.LogInformation(
            "Initial pin state on {Port}: DSR={DSR} CTS={CTS} DCD={DCD} BaudRate={Baud}",
            _localPortName,
            _localPort.DsrHolding,
            _localPort.CtsHolding,
            _localPort.CDHolding,
            _localPort.BaudRate);

        // Create and connect the RFC 2217 client
        _rfc2217 = new Rfc2217Client(
            _remoteHost, _remotePort,
            _loggerFactory.CreateLogger<Rfc2217Client>());

        WireRfc2217Callbacks();

        await _rfc2217.ConnectAsync(ct);

        // Set initial baud rate on the remote side
        await _rfc2217.SetBaudRateAsync(initialBaudRate, ct);
        await _rfc2217.SetDataFormatAsync(ct: ct);

        // Start the data pump and signal monitor
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _localReadTask = Task.Run(() => LocalReadLoopAsync(_cts.Token), _cts.Token);
        _signalPollTask = Task.Run(() => SignalPollLoopAsync(_cts.Token), _cts.Token);

        _logger.LogInformation("Bridge running: {LocalPort} <-> {Host}:{Port}",
            _localPortName, _remoteHost, _remotePort);
    }

    /// <summary>
    /// Wire up the RFC 2217 client callbacks for data forwarding and disconnect handling.
    /// </summary>
    private void WireRfc2217Callbacks()
    {
        if (_rfc2217 is null) return;

        // When RFC 2217 receives data from the ESP32, write it to the local port.
        // If nothing is reading COM41 (no IDE or terminal open), the com0com buffer
        // fills up and writes block. We catch the timeout and discard data rather
        // than crashing the bridge. Once an IDE opens COM41, the buffer drains and
        // writes succeed again.
        _rfc2217.OnDataReceived(async (data, token) =>
        {
            Interlocked.Add(ref _bytesFromDevice, data.Length);
            try
            {
                if (_localPort?.IsOpen == true)
                {
                    _localPort.BaseStream.Write(data.Span);
                }
            }
            catch (TimeoutException)
            {
                // Buffer full, nothing reading COM41. Discard data silently.
            }
            catch (IOException)
            {
                _logger.LogWarning("Local port {Port} write failed, port may be closed", _localPortName);
            }
            catch (InvalidOperationException)
            {
                // Port not open
            }
        });

        // When the TCP connection drops, trigger automatic reconnection.
        _rfc2217.OnDisconnected(() =>
        {
            if (!_reconnecting && _cts is not null && !_cts.IsCancellationRequested)
            {
                _logger.LogWarning("RFC 2217 connection lost. Triggering reconnect.");
                _reconnectTask = Task.Run(() => ReconnectLoopAsync(_cts.Token));
            }
        });
    }

    /// <summary>
    /// Attempt to reconnect to the RFC 2217 server with exponential backoff.
    /// The local COM port stays open so the IDE does not lose its handle on COM41.
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        if (!await _reconnectLock.WaitAsync(0, ct))
            return; // Another reconnect is already in progress

        try
        {
            _reconnecting = true;

            for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
            {
                if (ct.IsCancellationRequested) break;

                int delayMs = Math.Min(ReconnectBaseDelayMs * attempt, 10_000);
                _logger.LogInformation(
                    "Reconnect attempt {Attempt}/{Max} in {Delay}ms",
                    attempt, MaxReconnectAttempts, delayMs);
                await Task.Delay(delayMs, ct);

                try
                {
                    // Tear down the old connection
                    if (_rfc2217 is not null)
                        await _rfc2217.DisconnectAsync();

                    // Reconnect
                    await _rfc2217!.ConnectAsync(ct);

                    // Restore the current baud rate and data format.
                    // Use _lastBaudRate (tracks SLIP sniffer changes) rather than
                    // _localPort.BaudRate (always 115200, com0com does not propagate).
                    if (_localPort?.IsOpen == true)
                    {
                        await _rfc2217.SetBaudRateAsync(_lastBaudRate, ct);

                        byte dataSizeVal = (byte)_localPort.DataBits;
                        byte parityVal = _localPort.Parity switch
                        {
                            Parity.None  => TelnetConstants.PARITY_NONE,
                            Parity.Odd   => TelnetConstants.PARITY_ODD,
                            Parity.Even  => TelnetConstants.PARITY_EVEN,
                            Parity.Mark  => TelnetConstants.PARITY_MARK,
                            Parity.Space => TelnetConstants.PARITY_SPACE,
                            _ => TelnetConstants.PARITY_NONE
                        };
                        byte stopVal = _localPort.StopBits switch
                        {
                            StopBits.One          => TelnetConstants.STOPBITS_1,
                            StopBits.Two          => TelnetConstants.STOPBITS_2,
                            StopBits.OnePointFive => TelnetConstants.STOPBITS_1_5,
                            _ => TelnetConstants.STOPBITS_1
                        };

                        await _rfc2217.SetDataFormatAsync(dataSizeVal, parityVal, stopVal, ct);

                        // Restore DTR/RTS from the last known remote state
                        await _rfc2217.SetDtrNoWaitAsync(_lastRemoteDtr, ct);
                        await _rfc2217.SetRtsNoWaitAsync(_lastRemoteRts, ct);
                    }

                    _logger.LogInformation("Reconnected to RFC 2217 server successfully.");
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Reconnect attempt {Attempt} failed", attempt);
                }
            }

            _logger.LogError(
                "Failed to reconnect after {Max} attempts. Bridge is down.",
                MaxReconnectAttempts);
        }
        finally
        {
            _reconnecting = false;
            _reconnectLock.Release();
        }
    }

    /// <summary>
    /// Read data from the local COM port and send to the RFC 2217 server.
    /// </summary>
    private async Task LocalReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        try
        {
            while (!ct.IsCancellationRequested && _localPort?.IsOpen == true)
            {
                int bytesRead;
                try
                {
                    bytesRead = await _localPort.BaseStream.ReadAsync(buffer, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    _logger.LogWarning("Local port {Port} read error, likely closed", _localPortName);
                    break;
                }

                if (bytesRead > 0 && _rfc2217 is not null)
                {
                    Interlocked.Add(ref _bytesToDevice, bytesRead);

                    // Log hex dump of small packets when --hexdump is enabled.
                    // Large data transfers (firmware blobs) are skipped to avoid spam.
                    if (_options.HexDump)
                    {
                        if (bytesRead <= 64)
                        {
                            _logger.LogDebug(
                                "TX {Len} bytes: {Hex}",
                                bytesRead,
                                Convert.ToHexString(buffer, 0, bytesRead));
                        }
                        else if (bytesRead <= 256)
                        {
                            _logger.LogDebug(
                                "TX {Len} bytes (first 64): {Hex}",
                                bytesRead,
                                Convert.ToHexString(buffer, 0, 64));
                        }
                    }

                    // Sniff for SLIP CHANGE_BAUDRATE command before forwarding.
                    // ProcessChunk returns non-null only after the complete frame
                    // (including terminating C0) has been seen, ensuring the frame
                    // gets forwarded at the old baud rate before we switch.
                    int? detectedBaud = _slipSniffer.ProcessChunk(buffer, bytesRead);

                    try
                    {
                        if (_rfc2217.IsConnected && !_reconnecting)
                        {
                            await _rfc2217.SendDataAsync(buffer.AsMemory(0, bytesRead), ct);

                            // Apply baud rate change AFTER the data has been sent.
                            // The stub receives the complete CHANGE_BAUDRATE frame at
                            // the old baud rate, processes it, and switches its UART.
                            // Brief delay for the stub to actually switch, then we
                            // change the Pi's side too.
                            if (detectedBaud is int newBaud)
                            {
                                _logger.LogInformation(
                                    "SLIP baud rate change detected: switching Pi to {Baud}", newBaud);
                                await Task.Delay(50, ct);
                                await _rfc2217.SetBaudRateAsync(newBaud, ct);
                                _lastBaudRate = newBaud;
                            }
                        }
                        // If reconnecting, silently drop data. The IDE will retry
                        // or the user will re-trigger the operation.
                    }
                    catch (IOException)
                    {
                        // TCP connection dropped mid-write. The disconnect callback
                        // will handle reconnection. Keep the read loop alive so we
                        // do not lose the local COM port handle.
                        _logger.LogDebug("RFC 2217 send failed (connection lost), data dropped");
                    }
                    catch (InvalidOperationException)
                    {
                        // Not connected, skip
                    }
                    catch (SocketException)
                    {
                        _logger.LogDebug("RFC 2217 socket error on send, data dropped");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in local read loop for {Port}", _localPortName);
        }
    }

    /// <summary>
    /// Poll the local COM port for signal and settings changes.
    /// When the IDE changes baud rate, DTR, or RTS on COM41, com0com reflects
    /// those on COM241. We detect the change here and forward via RFC 2217.
    ///
    /// Polling interval is 5ms for responsive signal forwarding.
    /// The ESP32 bootloader entry sequence is: DTR=1 RTS=0, then DTR=0 RTS=1,
    /// toggled rapidly. We need to catch and forward this quickly.
    /// </summary>
    private async Task SignalPollLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _localPort?.IsOpen == true && _rfc2217 is not null)
            {
                await Task.Delay(5, ct); // 5ms poll interval for responsive signal forwarding

                try
                {
                    // Skip signal forwarding while reconnecting
                    if (_reconnecting || _rfc2217 is null || !_rfc2217.IsConnected)
                        continue;

                    // NOTE: Baud rate polling is intentionally NOT done here.
                    // com0com does NOT propagate baud rate changes between paired
                    // ports (even with EmuBR=yes, it only throttles data). Reading
                    // _localPort.BaudRate always returns OUR initial value (115200),
                    // never what the IDE set on COM41. Baud rate changes are detected
                    // exclusively via the SLIP protocol sniffer in LocalReadLoopAsync,
                    // which intercepts the esptool CHANGE_BAUDRATE command.

                    // Read ALL modem input pins in a single snapshot.
                    // com0com null-modem crosses:
                    //   COM41 DTR -> COM241 DSR + DCD
                    //   COM41 RTS -> COM241 CTS
                    bool remoteDtr = _localPort.DsrHolding || _localPort.CDHolding;
                    bool remoteRts = _localPort.CtsHolding;

                    bool dtrChanged = remoteDtr != _lastRemoteDtr;
                    bool rtsChanged = remoteRts != _lastRemoteRts;

                    if (dtrChanged || rtsChanged)
                    {
                        // Log the change with full pin state
                        if (dtrChanged)
                        {
                            _logger.LogInformation(
                                "Remote DTR: {Old} -> {New} | Remote RTS: {RtsOld} -> {RtsNew}",
                                _lastRemoteDtr, remoteDtr, _lastRemoteRts, remoteRts);
                        }
                        if (rtsChanged)
                        {
                            _logger.LogInformation(
                                "Remote RTS: {Old} -> {New} | Remote DTR: {DtrOld} -> {DtrNew}",
                                _lastRemoteRts, remoteRts, _lastRemoteDtr, remoteDtr);
                        }

                        _lastRemoteDtr = remoteDtr;
                        _lastRemoteRts = remoteRts;

                        // Send BOTH changes using fire-and-forget (no ack wait).
                        // Waiting for acks between DTR and RTS changes destroys the
                        // precise timing the ESP32 bootloader entry sequence requires.
                        // The signals must arrive at the Pi as close together in time
                        // as possible to preserve the esptool timing relationship.
                        if (dtrChanged)
                            await _rfc2217.SetDtrNoWaitAsync(remoteDtr, ct);
                        if (rtsChanged)
                            await _rfc2217.SetRtsNoWaitAsync(remoteRts, ct);

                        // Detect hard-reset sequence: RTS falling edge (true->false) indicates
                        // the end of the reset pulse. At this point the ESP32 reboots back to
                        // its ROM bootloader at 115200. If the Pi is at a different baud rate
                        // (from a previous esptool session at high speed), reset it to 115200
                        // so the next esptool session can sync properly.
                        if (rtsChanged && !remoteRts && _lastBaudRate != 115200)
                        {
                            _logger.LogInformation(
                                "Hard reset detected (RTS falling edge) while Pi at {Baud}, resetting to 115200",
                                _lastBaudRate);
                            await _rfc2217.SetBaudRateAsync(115200, ct);
                            _lastBaudRate = 115200;
                            _slipSniffer.Reset();
                        }
                    }

                    // Check data format changes
                    int currentDataBits = _localPort.DataBits;
                    Parity currentParity = _localPort.Parity;
                    StopBits currentStopBits = _localPort.StopBits;

                    if (currentDataBits != _lastDataBits ||
                        currentParity != _lastParity ||
                        currentStopBits != _lastStopBits)
                    {
                        _logger.LogDebug("Data format changed: {Bits}/{Parity}/{Stop}",
                            currentDataBits, currentParity, currentStopBits);

                        _lastDataBits = currentDataBits;
                        _lastParity = currentParity;
                        _lastStopBits = currentStopBits;

                        byte dataSizeVal = (byte)currentDataBits;
                        byte parityVal = currentParity switch
                        {
                            Parity.None  => TelnetConstants.PARITY_NONE,
                            Parity.Odd   => TelnetConstants.PARITY_ODD,
                            Parity.Even  => TelnetConstants.PARITY_EVEN,
                            Parity.Mark  => TelnetConstants.PARITY_MARK,
                            Parity.Space => TelnetConstants.PARITY_SPACE,
                            _ => TelnetConstants.PARITY_NONE
                        };
                        byte stopVal = currentStopBits switch
                        {
                            StopBits.One          => TelnetConstants.STOPBITS_1,
                            StopBits.Two          => TelnetConstants.STOPBITS_2,
                            StopBits.OnePointFive => TelnetConstants.STOPBITS_1_5,
                            _ => TelnetConstants.STOPBITS_1
                        };

                        await _rfc2217.SetDataFormatAsync(dataSizeVal, parityVal, stopVal, ct);
                    }

                    // Periodic traffic stats (every 2 seconds if there is activity)
                    var now = DateTime.UtcNow;
                    if ((now - _lastStatsLog).TotalSeconds >= 2)
                    {
                        long toDevice = Interlocked.Read(ref _bytesToDevice);
                        long fromDevice = Interlocked.Read(ref _bytesFromDevice);
                        if (toDevice != _lastLoggedToDevice || fromDevice != _lastLoggedFromDevice)
                        {
                            long deltaTx = toDevice - _lastLoggedToDevice;
                            long deltaRx = fromDevice - _lastLoggedFromDevice;
                            _logger.LogInformation(
                                "Traffic: IDE->ESP32 {TxDelta} bytes ({TxTotal} total), ESP32->IDE {RxDelta} bytes ({RxTotal} total)",
                                deltaTx, toDevice, deltaRx, fromDevice);
                            _lastLoggedToDevice = toDevice;
                            _lastLoggedFromDevice = fromDevice;
                        }
                        _lastStatsLog = now;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Port was closed by another thread
                    break;
                }
                catch (InvalidOperationException)
                {
                    // Port not open
                    break;
                }
                catch (IOException)
                {
                    // TCP gone, reconnect handler will deal with it
                    _logger.LogDebug("Signal poll: send failed (connection lost)");
                }
                catch (SocketException)
                {
                    _logger.LogDebug("Signal poll: socket error (connection lost)");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in signal poll loop for {Port}", _localPortName);
        }
    }

    /// <summary>
    /// Stop the bridge and release all resources.
    /// </summary>
    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping bridge for {Port}", _localPortName);

        _cts?.Cancel();

        // Dispose the RFC 2217 client first (closes TCP, cancels receive loop).
        // This is quick and does not deadlock.
        if (_rfc2217 is not null)
        {
            try
            {
                await _rfc2217.DisposeAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
        }

        // Close the serial port to break the pending BaseStream.ReadAsync.
        // IMPORTANT: SerialPort.Close() on Windows can deadlock when called from
        // the same thread context as a pending read. We run it on a thread pool
        // thread with a timeout to avoid hanging the shutdown.
        if (_localPort is not null)
        {
            var closeTask = Task.Run(() =>
            {
                try
                {
                    if (_localPort.IsOpen)
                        _localPort.BaseStream.Close();
                }
                catch { }
                try
                {
                    _localPort.Close();
                }
                catch { }
            });

            try { await closeTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
        }

        // Await the background tasks with timeouts. They should exit quickly
        // now that the port and TCP connection are both closed.
        if (_localReadTask is not null)
        {
            try { await _localReadTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
        }
        if (_signalPollTask is not null)
        {
            try { await _signalPollTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
        }
        if (_reconnectTask is not null)
        {
            try { await _reconnectTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
        }

        try { _localPort?.Dispose(); } catch { }
        _logger.LogInformation("Bridge stopped for {Port}", _localPortName);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
        _reconnectLock.Dispose();
    }
}
