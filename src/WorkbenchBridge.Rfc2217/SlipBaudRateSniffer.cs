using Microsoft.Extensions.Logging;

namespace WorkbenchBridge.Rfc2217;

/// <summary>
/// Detects esptool CHANGE_BAUDRATE commands in a SLIP-framed byte stream.
///
/// The ESP32 stub flasher uses SLIP protocol framing. When esptool sends a
/// CHANGE_BAUDRATE command (opcode 0x0F), we need to detect it so the bridge
/// can switch the remote side's baud rate to match.
///
/// SLIP frame format for CHANGE_BAUDRATE:
///   C0                          SLIP frame start/end delimiter
///   00                          direction = request (host to device)
///   0F                          command = CHANGE_BAUDRATE
///   08 00                       payload size = 8 (LE uint16)
///   xx xx xx xx                 checksum (4 bytes, ignored)
///   [new_baud LE 4 bytes]       e.g. 00 10 0E 00 = 921600
///   [old_baud LE 4 bytes]       e.g. 00 C2 01 00 = 115200
///   C0                          SLIP frame end
///
/// Key design constraint: the baud rate must NOT be reported until the
/// complete SLIP frame (including the terminating C0) has been seen in the
/// data stream. This ensures the entire frame gets forwarded at the old
/// baud rate before the remote side switches.
///
/// The byte stream may be split across arbitrary read boundaries:
///   Read 1: C0 00 0F 08 00       (header only)
///   Read 2: 00 00 00 00 00 10... (checksum + partial baud)
///   Read 3: 0E 00 00 C2 01 00 C0 (rest of baud + old_baud + frame end)
///
/// The sniffer handles all split patterns via partial frame buffering
/// and a deferred baud change state machine.
/// </summary>
public sealed class SlipBaudRateSniffer
{
    private readonly ILogger? _logger;
    private readonly bool _verbose;

    private const byte SLIP_END = 0xC0;
    private const byte ESP_CMD_CHANGE_BAUDRATE = 0x0F;

    // Partial SLIP frame buffer for when the CHANGE_BAUDRATE header has been
    // found but the payload is split across reads.
    private byte[]? _partialSlipFrame;
    private int _partialSlipLength;

    // Deferred baud change: baud rate extracted but the frame's terminating
    // C0 has not been seen yet. We must wait for it before reporting the
    // baud rate, so the caller can forward the complete frame at the old rate.
    private int _deferredBaudChange;

    public SlipBaudRateSniffer(ILogger? logger = null, bool verbose = false)
    {
        _logger = logger;
        _verbose = verbose;
    }

    /// <summary>
    /// Process a chunk of data from the serial stream and check for a
    /// CHANGE_BAUDRATE command.
    ///
    /// Call this for each chunk BEFORE forwarding it. If the return value
    /// is non-null, the complete CHANGE_BAUDRATE frame (including the
    /// terminating C0) is contained within the chunks processed so far.
    /// The caller should forward this chunk at the OLD baud rate, then
    /// switch to the returned baud rate.
    /// </summary>
    /// <param name="buffer">The data chunk to scan.</param>
    /// <param name="length">Number of valid bytes in the buffer.</param>
    /// <returns>The new baud rate if a complete CHANGE_BAUDRATE frame has
    /// been seen, or null if no change is needed yet.</returns>
    public int? ProcessChunk(byte[] buffer, int length)
    {
        // Phase 1: check deferred baud change (waiting for frame end C0).
        int deferred = _deferredBaudChange;
        if (deferred > 0)
        {
            bool hasEnd = Array.IndexOf(buffer, SLIP_END, 0, length) >= 0;
            if (hasEnd)
            {
                if (_verbose)
                    _logger?.LogDebug(
                        "SLIP sniffer: frame end C0 found, promoting deferred baud change {Baud}",
                        deferred);
                _deferredBaudChange = 0;
                return deferred;
            }

            if (_verbose)
                _logger?.LogDebug(
                    "SLIP sniffer: still waiting for frame end C0, deferred baud = {Baud}",
                    deferred);
            return null;
        }

        // Phase 2: complete a partial frame from previous reads.
        if (_partialSlipFrame is not null)
        {
            return CompletePartialFrame(buffer, length);
        }

        // Phase 3: scan for new SLIP frames in this chunk.
        return ScanForChangeBaudRate(buffer, length);
    }

    /// <summary>
    /// Reset all internal state. Call when the connection is reset or
    /// the bridge reconnects.
    /// </summary>
    public void Reset()
    {
        _partialSlipFrame = null;
        _partialSlipLength = 0;
        _deferredBaudChange = 0;
    }

    /// <summary>
    /// Whether a deferred baud change is pending (waiting for frame end).
    /// Useful for diagnostics.
    /// </summary>
    public bool HasDeferredBaudChange => _deferredBaudChange > 0;

    /// <summary>
    /// Whether we are buffering a partial CHANGE_BAUDRATE frame.
    /// Useful for diagnostics.
    /// </summary>
    public bool HasPartialFrame => _partialSlipFrame is not null;

    private int? CompletePartialFrame(byte[] buffer, int length)
    {
        int combined = _partialSlipLength + length;
        var merged = new byte[combined];
        Array.Copy(_partialSlipFrame!, 0, merged, 0, _partialSlipLength);
        Array.Copy(buffer, 0, merged, _partialSlipLength, length);

        if (_verbose)
            _logger?.LogDebug(
                "SLIP sniffer: completing partial frame, had {Old} bytes + {New} bytes = {Total}",
                _partialSlipLength, length, combined);

        _partialSlipFrame = null;
        _partialSlipLength = 0;

        if (TryExtractBaudRate(merged, 0, combined, out int newBaud))
        {
            _logger?.LogInformation(
                "SLIP sniffer: detected CHANGE_BAUDRATE (reassembled), new baud = {Baud}",
                newBaud);

            // Check if the frame end C0 is in the merged data.
            // Body bytes start after the initial C0 (not in merged buffer),
            // so any C0 here is the frame terminator.
            bool frameComplete = Array.IndexOf(merged, SLIP_END, 0, combined) >= 0;
            if (frameComplete)
                return newBaud;

            // Frame end is in a later read. Defer.
            if (_verbose)
                _logger?.LogDebug(
                    "SLIP sniffer: baud extracted but frame end C0 not yet seen, deferring");
            _deferredBaudChange = newBaud;
            return null;
        }

        // Still not enough bytes. Keep buffering.
        if (_verbose)
            _logger?.LogDebug(
                "SLIP sniffer: still incomplete after merge ({Len} bytes), continuing to buffer",
                combined);
        _partialSlipFrame = merged;
        _partialSlipLength = combined;
        return null;
    }

    private int? ScanForChangeBaudRate(byte[] buffer, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (buffer[i] != SLIP_END)
                continue;

            // Skip consecutive C0 bytes (end of previous + start of new frame).
            while (i + 1 < length && buffer[i + 1] == SLIP_END)
                i++;

            int bodyStart = i + 1;

            // Need at least 4 bytes for direction + command + size(2).
            if (bodyStart + 4 > length)
            {
                // Not enough bytes to read header. Buffer what we have if there are any.
                int partialLen = length - bodyStart;
                if (partialLen > 0)
                {
                    if (_verbose)
                        _logger?.LogDebug(
                            "SLIP sniffer: frame start found but header incomplete, buffering {Len} bytes",
                            partialLen);
                    _partialSlipFrame = new byte[partialLen];
                    Array.Copy(buffer, bodyStart, _partialSlipFrame, 0, partialLen);
                    _partialSlipLength = partialLen;
                    break;
                }
                // No bytes to buffer (C0 at end of chunk), continue looking
                continue;
            }

            byte direction = buffer[bodyStart];
            byte command = buffer[bodyStart + 1];
            byte sizeLo = buffer[bodyStart + 2];
            byte sizeHi = buffer[bodyStart + 3];

            if (_verbose && direction == 0x00 && length <= 256)
            {
                _logger?.LogDebug(
                    "SLIP sniffer: frame at offset {Offset}, dir=0x{Dir:X2} cmd=0x{Cmd:X2} size={Size}",
                    bodyStart, direction, command, sizeLo | (sizeHi << 8));
            }

            if (direction == 0x00 && command == ESP_CMD_CHANGE_BAUDRATE &&
                sizeLo == 0x08 && sizeHi == 0x00)
            {
                int bodyLength = length - bodyStart;

                if (TryExtractBaudRate(buffer, bodyStart, length, out int newBaud))
                {
                    _logger?.LogInformation(
                        "SLIP sniffer: detected CHANGE_BAUDRATE, new baud = {Baud}",
                        newBaud);

                    bool frameComplete = Array.IndexOf(
                        buffer, SLIP_END, bodyStart, length - bodyStart) >= 0;
                    if (frameComplete)
                        return newBaud;

                    if (_verbose)
                        _logger?.LogDebug(
                            "SLIP sniffer: baud extracted but frame end C0 not yet seen, deferring");
                    _deferredBaudChange = newBaud;
                    return null;
                }

                // Partial frame. Buffer for next read.
                if (_verbose)
                    _logger?.LogDebug(
                        "SLIP sniffer: CHANGE_BAUDRATE header found but payload split across reads, buffering {Len} bytes",
                        bodyLength);
                _partialSlipFrame = new byte[bodyLength];
                Array.Copy(buffer, bodyStart, _partialSlipFrame, 0, bodyLength);
                _partialSlipLength = bodyLength;
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Extract the new baud rate from a CHANGE_BAUDRATE SLIP frame body.
    /// Body layout: dir(1) + cmd(1) + size(2) + checksum(4) + new_baud(4) + old_baud(4)
    /// Handles SLIP escaping (DB DC = C0, DB DD = DB).
    /// </summary>
    private bool TryExtractBaudRate(byte[] buffer, int bodyStart, int bufferLength, out int newBaud)
    {
        newBaud = 0;

        // Need at least 12 bytes: header(4) + checksum(4) + new_baud(4)
        int baudOffset = bodyStart + 4 + 4;

        if (baudOffset + 4 > bufferLength)
            return false;

        var baudBytes = new byte[4];
        int srcPos = baudOffset;
        int destPos = 0;
        while (destPos < 4 && srcPos < bufferLength && buffer[srcPos] != SLIP_END)
        {
            byte b = buffer[srcPos++];
            if (b == 0xDB && srcPos < bufferLength)
            {
                byte escaped = buffer[srcPos++];
                baudBytes[destPos++] = escaped switch
                {
                    0xDC => SLIP_END,    // DB DC = C0
                    0xDD => 0xDB,        // DB DD = DB
                    _ => escaped
                };
            }
            else
            {
                baudBytes[destPos++] = b;
            }
        }

        if (destPos < 4)
            return false;

        newBaud = baudBytes[0] | (baudBytes[1] << 8) |
                  (baudBytes[2] << 16) | (baudBytes[3] << 24);

        if (newBaud <= 0 || newBaud > 4_000_000)
        {
            _logger?.LogWarning(
                "SLIP sniffer: CHANGE_BAUDRATE with invalid baud = {Baud}", newBaud);
            newBaud = 0;
            return false;
        }

        return true;
    }
}
