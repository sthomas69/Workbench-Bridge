using System.Buffers;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace WorkbenchBridge.Rfc2217;

/// <summary>
/// RFC 2217 (Telnet COM Port Control) client.
/// Connects to an RFC 2217 server over TCP and provides methods to send/receive
/// serial data plus control commands (baud rate, DTR, RTS, etc.).
///
/// This replaces hub4com with a correct implementation that handles:
/// - Telnet IAC byte escaping (0xFF in data becomes 0xFF 0xFF)
/// - Baud rate switching via SET_BAUDRATE subnegotiation
/// - DTR/RTS control via SET_CONTROL subnegotiation
/// - Proper option negotiation (WILL/DO COM-PORT-OPTION, BINARY, SGA)
/// </summary>
public sealed class Rfc2217Client : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger<Rfc2217Client> _logger;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    // State tracking
    private bool _serverComPortOptionEnabled;
    private int _currentBaudRate;
    private byte _lastModemState;
    private byte _lastLineState;

    // Event raised when the TCP connection drops (for reconnection logic)
    private Action? _onDisconnected;

    // Callbacks for received serial data and state changes
    private Func<ReadOnlyMemory<byte>, CancellationToken, Task>? _onDataReceived;
    private Action<int>? _onBaudRateChanged;
    private Action<byte>? _onModemStateChanged;

    // Synchronisation for waiting on server responses
    private readonly SemaphoreSlim _baudRateAck = new(0, 1);
    private readonly SemaphoreSlim _controlAck = new(0, 1);
    private int _ackedBaudRate;
    private byte _ackedControlValue;

    public bool IsConnected => _tcp?.Connected == true && _stream is not null;
    public int CurrentBaudRate => _currentBaudRate;
    public byte LastModemState => _lastModemState;

    /// <summary>
    /// Set the callback for connection drop notifications (for reconnection logic).
    /// </summary>
    public void OnDisconnected(Action callback) => _onDisconnected = callback;

    public Rfc2217Client(string host, int port, ILogger<Rfc2217Client> logger)
    {
        _host = host;
        _port = port;
        _logger = logger;
    }

    /// <summary>
    /// Set the callback for received serial data (from the remote device).
    /// </summary>
    public void OnDataReceived(Func<ReadOnlyMemory<byte>, CancellationToken, Task> callback)
        => _onDataReceived = callback;

    /// <summary>
    /// Set the callback for baud rate change confirmations from the server.
    /// </summary>
    public void OnBaudRateChanged(Action<int> callback)
        => _onBaudRateChanged = callback;

    /// <summary>
    /// Set callback for modem state changes (CTS, DSR, RI, DCD).
    /// </summary>
    public void OnModemStateChanged(Action<byte> callback)
        => _onModemStateChanged = callback;

    /// <summary>
    /// Connect to the RFC 2217 server and perform Telnet option negotiation.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // Reset state for reconnection
        _serverComPortOptionEnabled = false;

        _tcp = new TcpClient
        {
            NoDelay = true,
            ReceiveBufferSize = 65536,
            SendBufferSize = 65536
        };

        // Enable TCP keepalive to prevent idle timeouts from the server or
        // intermediate network equipment. Sends a probe every 15 seconds after
        // 30 seconds of inactivity.
        _tcp.Client.SetSocketOption(
            System.Net.Sockets.SocketOptionLevel.Socket,
            System.Net.Sockets.SocketOptionName.KeepAlive,
            true);
        // On Windows, set keepalive timing via IOControl
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // struct tcp_keepalive: onoff (4 bytes), keepalivetime ms (4 bytes), keepaliveinterval ms (4 bytes)
                var keepAlive = new byte[12];
                BitConverter.GetBytes(1).CopyTo(keepAlive, 0);       // onoff = 1
                BitConverter.GetBytes(30_000).CopyTo(keepAlive, 4);   // 30s before first probe
                BitConverter.GetBytes(15_000).CopyTo(keepAlive, 8);   // 15s between probes
                _tcp.Client.IOControl(IOControlCode.KeepAliveValues, keepAlive, null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not set TCP keepalive timing (non-critical)");
            }
        }

        _logger.LogInformation("Connecting to RFC 2217 server at {Host}:{Port}", _host, _port);
        await _tcp.ConnectAsync(_host, _port, ct);
        _stream = _tcp.GetStream();

        _logger.LogDebug("Connected. Starting Telnet option negotiation.");

        // Start receive loop FIRST so we can process the server's responses
        // to our negotiation commands.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);

        // Phase 1: Negotiate binary mode and suppress-go-ahead.
        // Client WILL send binary / SGA, and requests server to DO the same.
        await SendTelnetCommandAsync(TelnetConstants.WILL, TelnetConstants.OPT_BINARY, ct);
        await SendTelnetCommandAsync(TelnetConstants.WILL, TelnetConstants.OPT_SGA, ct);
        await SendTelnetCommandAsync(TelnetConstants.DO, TelnetConstants.OPT_BINARY, ct);
        await SendTelnetCommandAsync(TelnetConstants.DO, TelnetConstants.OPT_SGA, ct);

        // Phase 2: Request the server to enable COM-PORT-OPTION.
        // NOTE: only DO COM-PORT is correct. The client must NOT send WILL COM-PORT
        // because COM-PORT-OPTION is a server-side capability. Sending WILL COM-PORT
        // confuses pyserial's PortManager and can cause it to close the connection.
        await SendTelnetCommandAsync(TelnetConstants.DO, TelnetConstants.OPT_COM_PORT, ct);

        // Wait for the server to confirm COM-PORT-OPTION before sending subnegotiations.
        // pyserial's RFC 2217 server needs time to process and respond.
        int waitMs = 0;
        while (!_serverComPortOptionEnabled && waitMs < 3000)
        {
            await Task.Delay(50, ct);
            waitMs += 50;
        }

        if (!_serverComPortOptionEnabled)
        {
            _logger.LogWarning(
                "Server did not confirm COM-PORT-OPTION within 3s. Proceeding anyway.");
        }
        else
        {
            _logger.LogDebug("Server confirmed COM-PORT-OPTION after {Ms}ms", waitMs);
        }

        // Phase 3: Now that COM-PORT-OPTION is negotiated, request state notifications.
        await SendSubnegotiationAsync(
            TelnetConstants.CPO_SET_MODEMSTATE_MASK,
            new byte[] { 0xFF },
            ct);
        await SendSubnegotiationAsync(
            TelnetConstants.CPO_SET_LINESTATE_MASK,
            new byte[] { 0xFF },
            ct);

        // Small settle time for the server to process the mask commands
        await Task.Delay(100, ct);
        _logger.LogInformation("RFC 2217 session established with {Host}:{Port}", _host, _port);
    }

    /// <summary>
    /// Send serial data to the remote device.
    /// Handles IAC escaping: any 0xFF byte in the data is doubled to 0xFF 0xFF.
    /// </summary>
    public async Task SendDataAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");

        // Fast path: check if any IAC bytes need escaping
        var span = data.Span;
        bool needsEscaping = false;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == TelnetConstants.IAC)
            {
                needsEscaping = true;
                break;
            }
        }

        if (!needsEscaping)
        {
            // No IAC bytes, send directly
            await _stream.WriteAsync(data, ct);
            return;
        }

        // Slow path: escape IAC bytes
        // Worst case: every byte is 0xFF, so output is 2x input
        var buffer = ArrayPool<byte>.Shared.Rent(data.Length * 2);
        try
        {
            int writePos = 0;
            for (int i = 0; i < span.Length; i++)
            {
                buffer[writePos++] = span[i];
                if (span[i] == TelnetConstants.IAC)
                {
                    buffer[writePos++] = TelnetConstants.IAC; // Double it
                }
            }

            await _stream.WriteAsync(buffer.AsMemory(0, writePos), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Set the baud rate on the remote serial port via RFC 2217.
    /// Waits for server acknowledgement.
    /// </summary>
    public async Task SetBaudRateAsync(int baudRate, CancellationToken ct = default)
    {
        _logger.LogDebug("Setting baud rate to {BaudRate}", baudRate);

        // Baud rate is sent as 4 bytes, big-endian (network byte order)
        var payload = new byte[4];
        payload[0] = (byte)((baudRate >> 24) & 0xFF);
        payload[1] = (byte)((baudRate >> 16) & 0xFF);
        payload[2] = (byte)((baudRate >> 8) & 0xFF);
        payload[3] = (byte)(baudRate & 0xFF);

        // Drain any stale ack
        _baudRateAck.Wait(0);

        await SendSubnegotiationAsync(TelnetConstants.CPO_SET_BAUDRATE, payload, ct);

        // Wait for server to acknowledge the baud rate change
        if (await _baudRateAck.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            _currentBaudRate = _ackedBaudRate;
            _logger.LogInformation("Baud rate set to {BaudRate} (server confirmed {Acked})",
                baudRate, _ackedBaudRate);
        }
        else
        {
            // Server did not acknowledge, assume it worked (some servers do not ack)
            _currentBaudRate = baudRate;
            _logger.LogWarning("Baud rate ack timeout. Assuming {BaudRate} is active.", baudRate);
        }
    }

    /// <summary>
    /// Set DTR state on the remote serial port. Waits for server ack.
    /// </summary>
    public async Task SetDtrAsync(bool enabled, CancellationToken ct = default)
    {
        var value = enabled ? TelnetConstants.CONTROL_DTR_ON : TelnetConstants.CONTROL_DTR_OFF;
        _logger.LogInformation("Setting DTR = {State}", enabled);
        await SendControlAsync(value, ct);
    }

    /// <summary>
    /// Set RTS state on the remote serial port. Waits for server ack.
    /// </summary>
    public async Task SetRtsAsync(bool enabled, CancellationToken ct = default)
    {
        var value = enabled ? TelnetConstants.CONTROL_RTS_ON : TelnetConstants.CONTROL_RTS_OFF;
        _logger.LogInformation("Setting RTS = {State}", enabled);
        await SendControlAsync(value, ct);
    }

    /// <summary>
    /// Set DTR state without waiting for ack. For timing-critical sequences.
    /// </summary>
    public async Task SetDtrNoWaitAsync(bool enabled, CancellationToken ct = default)
    {
        var value = enabled ? TelnetConstants.CONTROL_DTR_ON : TelnetConstants.CONTROL_DTR_OFF;
        _logger.LogInformation("Setting DTR = {State} (no-wait)", enabled);
        await SendControlFireAndForgetAsync(value, ct);
    }

    /// <summary>
    /// Set RTS state without waiting for ack. For timing-critical sequences.
    /// </summary>
    public async Task SetRtsNoWaitAsync(bool enabled, CancellationToken ct = default)
    {
        var value = enabled ? TelnetConstants.CONTROL_RTS_ON : TelnetConstants.CONTROL_RTS_OFF;
        _logger.LogInformation("Setting RTS = {State} (no-wait)", enabled);
        await SendControlFireAndForgetAsync(value, ct);
    }

    /// <summary>
    /// Set data format (data bits, parity, stop bits).
    /// </summary>
    public async Task SetDataFormatAsync(
        byte dataBits = TelnetConstants.DATASIZE_8,
        byte parity = TelnetConstants.PARITY_NONE,
        byte stopBits = TelnetConstants.STOPBITS_1,
        CancellationToken ct = default)
    {
        await SendSubnegotiationAsync(TelnetConstants.CPO_SET_DATASIZE, new[] { dataBits }, ct);
        await SendSubnegotiationAsync(TelnetConstants.CPO_SET_PARITY, new[] { parity }, ct);
        await SendSubnegotiationAsync(TelnetConstants.CPO_SET_STOPSIZE, new[] { stopBits }, ct);
    }

    /// <summary>
    /// Send a break signal.
    /// </summary>
    public async Task SendBreakAsync(bool on, CancellationToken ct = default)
    {
        var value = on ? TelnetConstants.CONTROL_BREAK_ON : TelnetConstants.CONTROL_BREAK_OFF;
        await SendControlAsync(value, ct);
    }

    /// <summary>
    /// Purge receive and/or transmit buffers.
    /// </summary>
    public async Task PurgeAsync(byte direction = TelnetConstants.PURGE_BOTH, CancellationToken ct = default)
    {
        await SendSubnegotiationAsync(TelnetConstants.CPO_PURGE_DATA, new[] { direction }, ct);
    }

    // ------------------------------------------------------------------
    // Telnet protocol implementation
    // ------------------------------------------------------------------

    private async Task SendTelnetCommandAsync(byte command, byte option, CancellationToken ct)
    {
        var buf = new byte[] { TelnetConstants.IAC, command, option };
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        await _stream.WriteAsync(buf, ct);
    }

    private async Task SendSubnegotiationAsync(byte command, byte[] payload, CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");

        // IAC SB COM-PORT-OPTION <command> <payload with IAC escaping> IAC SE
        // Payload must have IAC bytes escaped
        using var ms = new MemoryStream(6 + payload.Length * 2);
        ms.WriteByte(TelnetConstants.IAC);
        ms.WriteByte(TelnetConstants.SB);
        ms.WriteByte(TelnetConstants.OPT_COM_PORT);
        ms.WriteByte(command);

        for (int i = 0; i < payload.Length; i++)
        {
            ms.WriteByte(payload[i]);
            if (payload[i] == TelnetConstants.IAC)
                ms.WriteByte(TelnetConstants.IAC); // Escape IAC in subneg payload
        }

        ms.WriteByte(TelnetConstants.IAC);
        ms.WriteByte(TelnetConstants.SE);

        var data = ms.ToArray();
        await _stream.WriteAsync(data, ct);
    }

    private async Task SendControlAsync(byte controlValue, CancellationToken ct)
    {
        _controlAck.Wait(0); // Drain stale
        await SendSubnegotiationAsync(TelnetConstants.CPO_SET_CONTROL, new[] { controlValue }, ct);

        // Wait briefly for ack but do not block operations if server is slow
        await _controlAck.WaitAsync(TimeSpan.FromSeconds(2), ct);
    }

    /// <summary>
    /// Send a control command without waiting for the server ack.
    /// Used for timing-critical DTR/RTS sequences (ESP32 bootloader entry)
    /// where waiting for acks between signals would destroy the required timing.
    /// </summary>
    private async Task SendControlFireAndForgetAsync(byte controlValue, CancellationToken ct)
    {
        await SendSubnegotiationAsync(TelnetConstants.CPO_SET_CONTROL, new[] { controlValue }, ct);
    }

    // ------------------------------------------------------------------
    // Receive loop: reads from TCP, parses Telnet commands, extracts data
    // ------------------------------------------------------------------

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var dataBuffer = new List<byte>(4096);

        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                int bytesRead = await _stream.ReadAsync(buffer, ct);
                if (bytesRead == 0)
                {
                    _logger.LogWarning("TCP connection closed by server.");
                    _onDisconnected?.Invoke();
                    break;
                }

                // Parse the received bytes, separating Telnet commands from data
                dataBuffer.Clear();
                int pos = 0;
                while (pos < bytesRead)
                {
                    byte b = buffer[pos++];

                    if (b == TelnetConstants.IAC)
                    {
                        // Flush any accumulated data before processing command
                        if (dataBuffer.Count > 0)
                        {
                            await FlushDataBufferAsync(dataBuffer, ct);
                        }

                        if (pos >= bytesRead) break; // Need more data
                        byte next = buffer[pos++];

                        switch (next)
                        {
                            case TelnetConstants.IAC:
                                // Escaped IAC = literal 0xFF byte in data
                                dataBuffer.Add(TelnetConstants.IAC);
                                break;

                            case TelnetConstants.WILL:
                            case TelnetConstants.WONT:
                            case TelnetConstants.DO:
                            case TelnetConstants.DONT:
                                if (pos >= bytesRead) break;
                                byte option = buffer[pos++];
                                HandleOptionNegotiation(next, option);
                                break;

                            case TelnetConstants.SB:
                                // Subnegotiation: read until IAC SE
                                pos = HandleSubnegotiation(buffer, pos, bytesRead);
                                break;

                            default:
                                // Other Telnet command, ignore
                                _logger.LogTrace("Telnet command: IAC {Cmd}", next);
                                break;
                        }
                    }
                    else
                    {
                        dataBuffer.Add(b);
                    }
                }

                // Flush remaining data
                if (dataBuffer.Count > 0)
                {
                    await FlushDataBufferAsync(dataBuffer, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "TCP connection lost in receive loop");
            _onDisconnected?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in receive loop");
            _onDisconnected?.Invoke();
        }
    }

    private async Task FlushDataBufferAsync(List<byte> dataBuffer, CancellationToken ct)
    {
        if (_onDataReceived is not null)
        {
            var data = dataBuffer.ToArray();
            await _onDataReceived(data, ct);
        }
        dataBuffer.Clear();
    }

    private void HandleOptionNegotiation(byte command, byte option)
    {
        string cmdName = command switch
        {
            TelnetConstants.WILL => "WILL",
            TelnetConstants.WONT => "WONT",
            TelnetConstants.DO   => "DO",
            TelnetConstants.DONT => "DONT",
            _ => $"?{command}"
        };

        _logger.LogTrace("Telnet: {Cmd} {Option}", cmdName, option);

        switch (option)
        {
            case TelnetConstants.OPT_COM_PORT:
                if (command == TelnetConstants.WILL || command == TelnetConstants.DO)
                    _serverComPortOptionEnabled = true;
                break;

            case TelnetConstants.OPT_BINARY:
                // Binary mode negotiated, no action needed
                break;
        }
    }

    private int HandleSubnegotiation(byte[] buffer, int pos, int length)
    {
        // Read subneg data until IAC SE
        // First byte should be the option (44 for COM-PORT-OPTION)
        if (pos >= length) return pos;
        byte option = buffer[pos++];

        if (option != TelnetConstants.OPT_COM_PORT)
        {
            // Skip non-COM-PORT subnegotiations
            while (pos < length - 1)
            {
                if (buffer[pos] == TelnetConstants.IAC && buffer[pos + 1] == TelnetConstants.SE)
                {
                    pos += 2;
                    return pos;
                }
                pos++;
            }
            return pos;
        }

        // COM-PORT-OPTION subnegotiation
        if (pos >= length) return pos;
        byte command = buffer[pos++];

        // Collect payload bytes (with IAC unescaping) until IAC SE
        var payload = new List<byte>();
        while (pos < length)
        {
            if (buffer[pos] == TelnetConstants.IAC)
            {
                if (pos + 1 < length)
                {
                    if (buffer[pos + 1] == TelnetConstants.SE)
                    {
                        pos += 2; // Consume IAC SE
                        break;
                    }
                    else if (buffer[pos + 1] == TelnetConstants.IAC)
                    {
                        payload.Add(TelnetConstants.IAC); // Escaped IAC in subneg
                        pos += 2;
                        continue;
                    }
                }
                pos++;
                continue;
            }

            payload.Add(buffer[pos++]);
        }

        ProcessComPortResponse(command, payload.ToArray());
        return pos;
    }

    private void ProcessComPortResponse(byte command, byte[] payload)
    {
        switch (command)
        {
            case TelnetConstants.CPO_SERVER_SET_BAUDRATE:
                if (payload.Length >= 4)
                {
                    _ackedBaudRate = (payload[0] << 24) | (payload[1] << 16) |
                                     (payload[2] << 8) | payload[3];
                    _logger.LogDebug("Server confirmed baud rate: {BaudRate}", _ackedBaudRate);
                    _onBaudRateChanged?.Invoke(_ackedBaudRate);
                    try { _baudRateAck.Release(); } catch (SemaphoreFullException) { }
                }
                break;

            case TelnetConstants.CPO_SERVER_SET_CONTROL:
                if (payload.Length >= 1)
                {
                    _ackedControlValue = payload[0];
                    _logger.LogInformation("Server confirmed control: {Value}", _ackedControlValue);
                    try { _controlAck.Release(); } catch (SemaphoreFullException) { }
                }
                break;

            case TelnetConstants.CPO_SERVER_NOTIFY_MODEMSTATE:
                if (payload.Length >= 1)
                {
                    _lastModemState = payload[0];
                    _onModemStateChanged?.Invoke(_lastModemState);
                    _logger.LogTrace("Modem state: 0x{State:X2}", _lastModemState);
                }
                break;

            case TelnetConstants.CPO_SERVER_NOTIFY_LINESTATE:
                if (payload.Length >= 1)
                {
                    _lastLineState = payload[0];
                    _logger.LogTrace("Line state: 0x{State:X2}", _lastLineState);
                }
                break;

            default:
                _logger.LogTrace("COM-PORT response: cmd={Cmd} payload={Len} bytes",
                    command, payload.Length);
                break;
        }
    }

    /// <summary>
    /// Tear down the current connection without disposing the semaphores.
    /// Used by the bridge's reconnection logic before calling ConnectAsync again.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_receiveTask is not null)
        {
            try { await _receiveTask; }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            _receiveTask = null;
        }

        _stream?.Dispose();
        _stream = null;
        _tcp?.Dispose();
        _tcp = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _baudRateAck.Dispose();
        _controlAck.Dispose();
    }
}
