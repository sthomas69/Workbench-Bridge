using WorkbenchBridge.Rfc2217;
using Xunit;

namespace WorkbenchBridge.Tests;

/// <summary>
/// Tests for the SLIP protocol baud rate sniffer.
///
/// These tests cover every bug that was found and fixed during development:
///   1. Consecutive C0 bytes between back-to-back SLIP frames
///   2. CHANGE_BAUDRATE payload split across 2 serial reads
///   3. CHANGE_BAUDRATE payload split across 3+ serial reads
///   4. Deferred baud change: frame end C0 must be seen before reporting
///   5. SLIP escaping in baud rate values
///   6. Non-CHANGE_BAUDRATE frames must be ignored
///   7. Reset clears all state
///
/// The sniffer processes chunks of bytes as they arrive from the serial port.
/// It returns non-null only when the COMPLETE CHANGE_BAUDRATE SLIP frame
/// (including the terminating C0) has been seen. This ensures the caller
/// forwards the entire frame at the old baud rate before switching.
/// </summary>
public class SlipBaudRateSnifferTests
{
    // Build a complete CHANGE_BAUDRATE SLIP frame.
    // Frame layout: C0 00 0F 08 00 [checksum 4B] [new_baud LE 4B] [old_baud LE 4B] C0
    private static byte[] BuildChangeBaudFrame(int newBaud, int oldBaud = 115200)
    {
        var frame = new byte[18];
        frame[0] = 0xC0;  // SLIP start
        frame[1] = 0x00;  // direction = request
        frame[2] = 0x0F;  // command = CHANGE_BAUDRATE
        frame[3] = 0x08;  // size low
        frame[4] = 0x00;  // size high
        // checksum (4 bytes, zeroed for tests)
        frame[5] = 0x00;
        frame[6] = 0x00;
        frame[7] = 0x00;
        frame[8] = 0x00;
        // new_baud LE
        frame[9]  = (byte)(newBaud & 0xFF);
        frame[10] = (byte)((newBaud >> 8) & 0xFF);
        frame[11] = (byte)((newBaud >> 16) & 0xFF);
        frame[12] = (byte)((newBaud >> 24) & 0xFF);
        // old_baud LE
        frame[13] = (byte)(oldBaud & 0xFF);
        frame[14] = (byte)((oldBaud >> 8) & 0xFF);
        frame[15] = (byte)((oldBaud >> 16) & 0xFF);
        frame[16] = (byte)((oldBaud >> 24) & 0xFF);
        frame[17] = 0xC0; // SLIP end
        return frame;
    }

    // Split a byte array into chunks at the given offsets.
    private static byte[][] SplitAt(byte[] data, params int[] splitPoints)
    {
        var chunks = new List<byte[]>();
        int start = 0;
        foreach (int point in splitPoints)
        {
            int len = point - start;
            var chunk = new byte[len];
            Array.Copy(data, start, chunk, 0, len);
            chunks.Add(chunk);
            start = point;
        }
        // Remaining bytes
        if (start < data.Length)
        {
            var last = new byte[data.Length - start];
            Array.Copy(data, start, last, 0, last.Length);
            chunks.Add(last);
        }
        return chunks.ToArray();
    }

    // ---------------------------------------------------------------
    // Complete frame in a single chunk
    // ---------------------------------------------------------------

    [Fact]
    public void CompleteFrame_SingleChunk_DetectsBaudRate()
    {
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(921600);

        int? result = sniffer.ProcessChunk(frame, frame.Length);

        Assert.Equal(921600, result);
    }

    [Theory]
    [InlineData(9600)]
    [InlineData(115200)]
    [InlineData(230400)]
    [InlineData(460800)]
    [InlineData(921600)]
    [InlineData(1500000)]
    [InlineData(2000000)]
    [InlineData(3000000)]
    public void CompleteFrame_VariousBaudRates(int baudRate)
    {
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(baudRate);

        int? result = sniffer.ProcessChunk(frame, frame.Length);

        Assert.Equal(baudRate, result);
    }

    // ---------------------------------------------------------------
    // Consecutive C0 bytes (back-to-back SLIP frames)
    // ---------------------------------------------------------------

    [Fact]
    public void ConsecutiveC0_PreviousFrameEnd_DetectsNextFrame()
    {
        // Simulates: ...previous_frame_data C0 C0 00 0F 08 00 ...
        // The first C0 ends the previous frame, the second starts the new one.
        var sniffer = new SlipBaudRateSniffer();

        var frame = BuildChangeBaudFrame(921600);
        // Prepend a previous frame's end byte
        var data = new byte[frame.Length + 1];
        data[0] = 0xC0; // end of previous frame
        Array.Copy(frame, 0, data, 1, frame.Length);

        int? result = sniffer.ProcessChunk(data, data.Length);

        Assert.Equal(921600, result);
    }

    [Fact]
    public void MultipleConsecutiveC0_StillDetectsFrame()
    {
        // Three C0 bytes before the frame body
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(460800);

        var data = new byte[frame.Length + 2];
        data[0] = 0xC0;
        data[1] = 0xC0;
        Array.Copy(frame, 0, data, 2, frame.Length);

        int? result = sniffer.ProcessChunk(data, data.Length);

        Assert.Equal(460800, result);
    }

    // ---------------------------------------------------------------
    // Frame split across 2 serial reads
    // ---------------------------------------------------------------

    [Fact]
    public void SplitAcross2Reads_HeaderAndPayload()
    {
        // Read 1: C0 00 0F 08 00 (header only, 5 bytes)
        // Read 2: checksum + new_baud + old_baud + C0 (13 bytes)
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(921600);
        var chunks = SplitAt(frame, 5);

        Assert.Equal(2, chunks.Length);
        Assert.Null(sniffer.ProcessChunk(chunks[0], chunks[0].Length));
        Assert.True(sniffer.HasPartialFrame);

        int? result = sniffer.ProcessChunk(chunks[1], chunks[1].Length);
        Assert.Equal(921600, result);
    }

    [Fact]
    public void SplitAcross2Reads_MidBaudRate()
    {
        // Split right in the middle of the new_baud field
        // Bytes 0-10: C0 + header + checksum + first 2 baud bytes
        // Bytes 11-17: last 2 baud bytes + old_baud + C0
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(921600);
        var chunks = SplitAt(frame, 11);

        Assert.Null(sniffer.ProcessChunk(chunks[0], chunks[0].Length));
        Assert.Equal(921600, sniffer.ProcessChunk(chunks[1], chunks[1].Length));
    }

    // ---------------------------------------------------------------
    // Frame split across 3 serial reads (the Arduino IDE bug)
    // ---------------------------------------------------------------

    [Fact]
    public void SplitAcross3Reads_ArduinoIdePattern()
    {
        // This is the exact pattern that caused the Arduino IDE upload failure:
        //   Read 1: C0 00 0F 08 00                   (5 bytes, header)
        //   Read 2: 00 00 00 00 00 10 0E 00           (8 bytes, checksum + new_baud)
        //   Read 3: 00 C2 01 00 C0                    (5 bytes, old_baud + frame end)
        //
        // The critical requirement: ProcessChunk must NOT return a baud rate
        // until Read 3 (which contains the terminating C0). If it returns on
        // Read 2, the caller would switch the Pi's baud rate before forwarding
        // Read 3, and the stub would never receive the complete frame.
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(921600);
        var chunks = SplitAt(frame, 5, 13);

        Assert.Equal(3, chunks.Length);

        // Read 1: header only. Should buffer partial frame.
        Assert.Null(sniffer.ProcessChunk(chunks[0], chunks[0].Length));
        Assert.True(sniffer.HasPartialFrame);

        // Read 2: checksum + new_baud. Baud extracted but frame end C0 not
        // yet seen. Must return null (deferred).
        Assert.Null(sniffer.ProcessChunk(chunks[1], chunks[1].Length));
        Assert.True(sniffer.HasDeferredBaudChange);
        Assert.False(sniffer.HasPartialFrame);

        // Read 3: old_baud + C0. Frame complete. NOW return the baud rate.
        int? result = sniffer.ProcessChunk(chunks[2], chunks[2].Length);
        Assert.Equal(921600, result);
        Assert.False(sniffer.HasDeferredBaudChange);
    }

    [Fact]
    public void SplitAcross4Reads_ExtremeFragmentation()
    {
        // Even more extreme: split into 4 tiny chunks.
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(460800);
        // Split at: 3, 7, 14 -> chunks of 3, 4, 7, 4
        var chunks = SplitAt(frame, 3, 7, 14);

        Assert.Equal(4, chunks.Length);
        Assert.Null(sniffer.ProcessChunk(chunks[0], chunks[0].Length));
        Assert.Null(sniffer.ProcessChunk(chunks[1], chunks[1].Length));
        Assert.Null(sniffer.ProcessChunk(chunks[2], chunks[2].Length));

        // Last chunk has the frame end C0
        int? result = sniffer.ProcessChunk(chunks[3], chunks[3].Length);
        Assert.Equal(460800, result);
    }

    [Fact]
    public void SplitAcross3Reads_DeferredWaitsForC0()
    {
        // Verify that the deferred state correctly waits for C0.
        // Reads 1+2 give enough data to extract baud but no C0.
        // Read 3 has data WITHOUT C0. Read 4 has the C0.
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(921600);

        // Remove the trailing C0 and split body + old_baud across 3 reads
        // Then send C0 separately as read 4
        var chunks = SplitAt(frame, 5, 13, 17);
        Assert.Equal(4, chunks.Length);
        // chunks[3] should be just {C0}
        Assert.Single(chunks[3]);
        Assert.Equal(0xC0, chunks[3][0]);

        Assert.Null(sniffer.ProcessChunk(chunks[0], chunks[0].Length)); // header
        Assert.Null(sniffer.ProcessChunk(chunks[1], chunks[1].Length)); // checksum + baud
        Assert.True(sniffer.HasDeferredBaudChange);

        // Read 3: old_baud bytes but NO C0
        Assert.Null(sniffer.ProcessChunk(chunks[2], chunks[2].Length));
        Assert.True(sniffer.HasDeferredBaudChange); // Still deferred

        // Read 4: just the C0
        Assert.Equal(921600, sniffer.ProcessChunk(chunks[3], chunks[3].Length));
        Assert.False(sniffer.HasDeferredBaudChange);
    }

    // ---------------------------------------------------------------
    // Non-CHANGE_BAUDRATE frames (must be ignored)
    // ---------------------------------------------------------------

    [Fact]
    public void NonChangeBaudFrame_IsIgnored()
    {
        var sniffer = new SlipBaudRateSniffer();

        // Build a SYNC frame (command 0x08, not 0x0F)
        var frame = new byte[] {
            0xC0,                               // SLIP start
            0x00,                               // direction
            0x08,                               // command = SYNC (not CHANGE_BAUDRATE)
            0x24, 0x00,                         // size = 36
            0x00, 0x00, 0x00, 0x00,             // checksum
            0x07, 0x07, 0x12, 0x20,             // payload bytes...
            0xC0                                // SLIP end
        };

        Assert.Null(sniffer.ProcessChunk(frame, frame.Length));
        Assert.False(sniffer.HasPartialFrame);
        Assert.False(sniffer.HasDeferredBaudChange);
    }

    [Fact]
    public void ResponseFrame_Direction01_IsIgnored()
    {
        var sniffer = new SlipBaudRateSniffer();

        // Build a response frame (direction = 0x01, device to host)
        // Even with command 0x0F, responses should not trigger a baud change
        var frame = new byte[18];
        frame[0] = 0xC0;
        frame[1] = 0x01;  // direction = response (not request)
        frame[2] = 0x0F;  // command = CHANGE_BAUDRATE
        frame[3] = 0x08;
        frame[4] = 0x00;
        // rest zeroed
        frame[17] = 0xC0;

        Assert.Null(sniffer.ProcessChunk(frame, frame.Length));
    }

    [Fact]
    public void WrongPayloadSize_IsIgnored()
    {
        var sniffer = new SlipBaudRateSniffer();

        // CHANGE_BAUDRATE with wrong payload size (should be 8)
        var frame = new byte[18];
        frame[0] = 0xC0;
        frame[1] = 0x00;
        frame[2] = 0x0F;
        frame[3] = 0x04;  // wrong size (4, not 8)
        frame[4] = 0x00;
        frame[17] = 0xC0;

        Assert.Null(sniffer.ProcessChunk(frame, frame.Length));
    }

    // ---------------------------------------------------------------
    // Data with no SLIP frames at all
    // ---------------------------------------------------------------

    [Fact]
    public void NoSlipFrames_ReturnsNull()
    {
        var sniffer = new SlipBaudRateSniffer();
        var data = new byte[] { 0x41, 0x42, 0x43, 0x44 };

        Assert.Null(sniffer.ProcessChunk(data, data.Length));
    }

    [Fact]
    public void EmptyChunk_ReturnsNull()
    {
        var sniffer = new SlipBaudRateSniffer();
        var data = new byte[0];

        Assert.Null(sniffer.ProcessChunk(data, 0));
    }

    [Fact]
    public void OnlyC0Bytes_ReturnsNull()
    {
        var sniffer = new SlipBaudRateSniffer();
        var data = new byte[] { 0xC0, 0xC0, 0xC0 };

        Assert.Null(sniffer.ProcessChunk(data, data.Length));
    }

    // ---------------------------------------------------------------
    // SLIP escaping in baud rate values
    // ---------------------------------------------------------------

    [Fact]
    public void SlipEscapedBaudRate_DB_InValue()
    {
        // Construct a baud rate where one of the bytes is 0xDB (SLIP ESC).
        // 0xDB needs to be escaped as DB DD in SLIP.
        // baud = 0x0000DBXX would have a 0xDB byte. Let's use a real example:
        // We need the LE bytes to contain 0xDB somewhere.
        // 56027 = 0x0000DADB -> LE bytes: DB DA 00 00
        // That has 0xDB as first byte, which in SLIP becomes DB DD.

        var sniffer = new SlipBaudRateSniffer();

        // Build frame manually with SLIP escaping
        var frame = new List<byte> {
            0xC0,                               // SLIP start
            0x00, 0x0F, 0x08, 0x00,             // header
            0x00, 0x00, 0x00, 0x00,             // checksum
            0xDB, 0xDD,                         // escaped 0xDB (first baud byte)
            0xDA, 0x00, 0x00,                   // remaining baud bytes (DA 00 00)
            0x00, 0xC2, 0x01, 0x00,             // old_baud = 115200
            0xC0                                // SLIP end
        };

        // Expected baud: 0x0000DADB = 56027
        int? result = sniffer.ProcessChunk(frame.ToArray(), frame.Count);
        Assert.Equal(56027, result);
    }

    [Fact]
    public void SlipEscapedBaudRate_C0_InValue()
    {
        // If a baud rate byte happens to be 0xC0, it must be escaped as DB DC.
        // baud = 0x000000C0 = 192 (unlikely but valid for testing)
        // LE bytes: C0 00 00 00 -> first byte is C0, escaped as DB DC

        var sniffer = new SlipBaudRateSniffer();

        var frame = new List<byte> {
            0xC0,                               // SLIP start
            0x00, 0x0F, 0x08, 0x00,             // header
            0x00, 0x00, 0x00, 0x00,             // checksum
            0xDB, 0xDC,                         // escaped 0xC0 (first baud byte)
            0x00, 0x00, 0x00,                   // remaining baud bytes
            0x00, 0xC2, 0x01, 0x00,             // old_baud = 115200
            0xC0                                // SLIP end
        };

        int? result = sniffer.ProcessChunk(frame.ToArray(), frame.Count);
        Assert.Equal(192, result);
    }

    // ---------------------------------------------------------------
    // Invalid baud rates
    // ---------------------------------------------------------------

    [Fact]
    public void InvalidBaudRate_Zero_ReturnsNull()
    {
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(0);

        // Baud rate 0 is invalid, sniffer should reject it
        Assert.Null(sniffer.ProcessChunk(frame, frame.Length));
    }

    [Fact]
    public void InvalidBaudRate_TooHigh_ReturnsNull()
    {
        var sniffer = new SlipBaudRateSniffer();
        // 5000000 exceeds the 4MHz limit in the sniffer
        var frame = BuildChangeBaudFrame(5000000);

        Assert.Null(sniffer.ProcessChunk(frame, frame.Length));
    }

    // ---------------------------------------------------------------
    // Reset behaviour
    // ---------------------------------------------------------------

    [Fact]
    public void Reset_ClearsPartialFrame()
    {
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(921600);
        var chunks = SplitAt(frame, 5);

        // Start buffering a partial frame
        sniffer.ProcessChunk(chunks[0], chunks[0].Length);
        Assert.True(sniffer.HasPartialFrame);

        // Reset should clear it
        sniffer.Reset();
        Assert.False(sniffer.HasPartialFrame);
        Assert.False(sniffer.HasDeferredBaudChange);
    }

    [Fact]
    public void Reset_ClearsDeferredBaudChange()
    {
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(921600);
        // Split so baud is extracted but C0 not seen
        var chunks = SplitAt(frame, 5, 13);

        sniffer.ProcessChunk(chunks[0], chunks[0].Length);
        sniffer.ProcessChunk(chunks[1], chunks[1].Length);
        Assert.True(sniffer.HasDeferredBaudChange);

        sniffer.Reset();
        Assert.False(sniffer.HasDeferredBaudChange);
    }

    [Fact]
    public void AfterReset_CanDetectNewFrame()
    {
        var sniffer = new SlipBaudRateSniffer();

        // Detect first frame
        var frame1 = BuildChangeBaudFrame(921600);
        Assert.Equal(921600, sniffer.ProcessChunk(frame1, frame1.Length));

        // Reset and detect a different baud rate
        sniffer.Reset();
        var frame2 = BuildChangeBaudFrame(460800);
        Assert.Equal(460800, sniffer.ProcessChunk(frame2, frame2.Length));
    }

    // ---------------------------------------------------------------
    // Sequential frames (two baud changes in a row)
    // ---------------------------------------------------------------

    [Fact]
    public void TwoConsecutiveFrames_BothDetected()
    {
        var sniffer = new SlipBaudRateSniffer();

        // First CHANGE_BAUDRATE
        var frame1 = BuildChangeBaudFrame(921600);
        Assert.Equal(921600, sniffer.ProcessChunk(frame1, frame1.Length));

        // Second CHANGE_BAUDRATE (e.g., esptool switching back)
        var frame2 = BuildChangeBaudFrame(115200, 921600);
        Assert.Equal(115200, sniffer.ProcessChunk(frame2, frame2.Length));
    }

    // ---------------------------------------------------------------
    // Buffer length parameter (partial buffer usage)
    // ---------------------------------------------------------------

    [Fact]
    public void LengthParameter_OnlyScansSpecifiedBytes()
    {
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(921600);

        // Put frame in a larger buffer but only tell sniffer about first 5 bytes
        var bigBuffer = new byte[256];
        Array.Copy(frame, bigBuffer, frame.Length);

        // Only 5 bytes: just the header, should buffer partial frame
        Assert.Null(sniffer.ProcessChunk(bigBuffer, 5));
        Assert.True(sniffer.HasPartialFrame);
    }

    // ---------------------------------------------------------------
    // Frame embedded in other data
    // ---------------------------------------------------------------

    [Fact]
    public void FrameAfterOtherData_StillDetected()
    {
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(921600);

        // Some random data followed by SLIP_END followed by the frame
        var data = new byte[10 + frame.Length];
        // Fill with non-C0 data
        for (int i = 0; i < 9; i++) data[i] = 0x42;
        data[9] = 0xC0; // end of some previous frame
        Array.Copy(frame, 0, data, 10, frame.Length);

        int? result = sniffer.ProcessChunk(data, data.Length);
        Assert.Equal(921600, result);
    }

    [Fact]
    public void FrameFollowedByOtherData_StillDetected()
    {
        var sniffer = new SlipBaudRateSniffer();
        var frame = BuildChangeBaudFrame(921600);

        // Frame followed by the start of another frame
        var data = new byte[frame.Length + 5];
        Array.Copy(frame, data, frame.Length);
        data[frame.Length] = 0xC0;
        data[frame.Length + 1] = 0x00;
        data[frame.Length + 2] = 0x08; // SYNC command
        data[frame.Length + 3] = 0x24;
        data[frame.Length + 4] = 0x00;

        int? result = sniffer.ProcessChunk(data, data.Length);
        Assert.Equal(921600, result);
    }

    // ---------------------------------------------------------------
    // Real-world byte patterns from actual esptool captures
    // ---------------------------------------------------------------

    [Fact]
    public void RealWorldPattern_StandaloneEsptool_921600()
    {
        // Standalone esptool.exe typically sends the complete frame in one chunk.
        // 921600 = 0x000E1000, LE bytes: 00 10 0E 00
        // 115200 = 0x0001C200, LE bytes: 00 C2 01 00
        var sniffer = new SlipBaudRateSniffer();

        var frame = new byte[] {
            0xC0,                               // SLIP start
            0x00,                               // direction = request
            0x0F,                               // command = CHANGE_BAUDRATE
            0x08, 0x00,                         // size = 8
            0x00, 0x00, 0x00, 0x00,             // checksum
            0x00, 0x10, 0x0E, 0x00,             // new_baud = 921600 LE
            0x00, 0xC2, 0x01, 0x00,             // old_baud = 115200 LE
            0xC0                                // SLIP end
        };

        Assert.Equal(921600, sniffer.ProcessChunk(frame, frame.Length));
    }

    [Fact]
    public void RealWorldPattern_ArduinoIde_3ReadSplit()
    {
        // Exact byte pattern from Arduino IDE's Python esptool that caused
        // the upload failure. The frame is split across 3 serial reads.
        var sniffer = new SlipBaudRateSniffer();

        byte[] read1 = { 0xC0, 0x00, 0x0F, 0x08, 0x00 };
        byte[] read2 = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x0E, 0x00 };
        byte[] read3 = { 0x00, 0xC2, 0x01, 0x00, 0xC0 };

        // Read 1: must not trigger baud change
        Assert.Null(sniffer.ProcessChunk(read1, read1.Length));

        // Read 2: must not trigger baud change (no frame end C0 yet)
        Assert.Null(sniffer.ProcessChunk(read2, read2.Length));

        // Read 3: contains the frame end C0, NOW trigger
        Assert.Equal(921600, sniffer.ProcessChunk(read3, read3.Length));
    }
}
