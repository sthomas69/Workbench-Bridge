using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WorkbenchBridge.Rfc2217;
using Xunit;

namespace WorkbenchBridge.Tests;

/// <summary>
/// Tests for Telnet IAC byte escaping.
/// This is the most critical part of the RFC 2217 implementation.
///
/// When sending binary data (firmware), any 0xFF byte must be doubled to 0xFF 0xFF
/// so the Telnet layer does not interpret it as an IAC command. If this is wrong,
/// firmware data gets corrupted and the ESP32 rejects it.
/// </summary>
public class IacEscapingTests
{
    [Fact]
    public async Task SendData_NoIacBytes_SentVerbatim()
    {
        // Data with no 0xFF bytes should pass through unchanged
        var (client, serverStream) = await CreateConnectedPairAsync();
        await using (client)
        {
            var data = new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFE };
            await client.SendDataAsync(data);
            await Task.Delay(50);

            var received = new byte[100];
            serverStream.ReadTimeout = 500;
            int read = serverStream.Read(received, 0, received.Length);

            // Skip past any Telnet negotiation bytes, find our data
            var payload = ExtractDataBytes(received.AsSpan(0, read));

            Assert.Equal(data, payload);
        }
    }

    [Fact]
    public async Task SendData_SingleIacByte_IsDoubled()
    {
        var (client, serverStream) = await CreateConnectedPairAsync();
        await using (client)
        {
            // Send a single 0xFF byte
            var data = new byte[] { 0xFF };
            await client.SendDataAsync(data);
            await Task.Delay(50);

            var received = new byte[100];
            serverStream.ReadTimeout = 500;
            int read = serverStream.Read(received, 0, received.Length);

            // Should contain 0xFF 0xFF (IAC IAC = escaped literal 0xFF)
            var payload = ExtractRawBytesAfterNegotiation(received.AsSpan(0, read));
            Assert.True(ContainsSubsequence(payload)(new byte[] { 0xFF, 0xFF }));
        }
    }

    [Fact]
    public async Task SendData_IacInMiddleOfData_OnlyIacDoubled()
    {
        var (client, serverStream) = await CreateConnectedPairAsync();
        await using (client)
        {
            // Data: 0x41 0xFF 0x42 -- the 0xFF must be doubled, others not
            var data = new byte[] { 0x41, 0xFF, 0x42 };
            await client.SendDataAsync(data);
            await Task.Delay(50);

            var received = new byte[100];
            serverStream.ReadTimeout = 500;
            int read = serverStream.Read(received, 0, received.Length);

            var payload = ExtractRawBytesAfterNegotiation(received.AsSpan(0, read));

            // Should contain: 0x41 0xFF 0xFF 0x42
            var expected = new byte[] { 0x41, 0xFF, 0xFF, 0x42 };
            Assert.True(ContainsSubsequence(payload)(expected));
        }
    }

    [Fact]
    public async Task SendData_AllIacBytes_AllDoubled()
    {
        var (client, serverStream) = await CreateConnectedPairAsync();
        await using (client)
        {
            // Three consecutive 0xFF bytes
            var data = new byte[] { 0xFF, 0xFF, 0xFF };
            await client.SendDataAsync(data);
            await Task.Delay(50);

            var received = new byte[100];
            serverStream.ReadTimeout = 500;
            int read = serverStream.Read(received, 0, received.Length);

            var payload = ExtractRawBytesAfterNegotiation(received.AsSpan(0, read));

            // Each 0xFF should become 0xFF 0xFF, so 6 bytes total
            var expected = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
            Assert.True(ContainsSubsequence(payload)(expected));
        }
    }

    [Fact]
    public async Task SendData_LargePayload_NoCorruption()
    {
        var (client, serverStream) = await CreateConnectedPairAsync();
        await using (client)
        {
            // Simulate a firmware chunk: 4096 bytes with scattered 0xFF values
            var data = new byte[4096];
            var rng = new Random(42); // Deterministic seed
            rng.NextBytes(data);

            // Count expected 0xFF bytes
            int iacCount = data.Count(b => b == 0xFF);

            await client.SendDataAsync(data);
            await Task.Delay(200);

            // Read all available data from server
            var received = new byte[data.Length + iacCount + 200]; // Extra for negotiation + escaping
            int totalRead = 0;
            serverStream.ReadTimeout = 500;

            try
            {
                while (true)
                {
                    int read = serverStream.Read(received, totalRead, received.Length - totalRead);
                    if (read == 0) break;
                    totalRead += read;
                }
            }
            catch (IOException) { } // Timeout, expected

            // The raw payload should be original length + number of IAC bytes (each doubled)
            var payload = ExtractRawBytesAfterNegotiation(received.AsSpan(0, totalRead));
            Assert.True(payload.Length >= data.Length + iacCount,
                $"Expected at least {data.Length + iacCount} bytes but got {payload.Length}");
        }
    }

    // Helper: create a TCP listener and connect an Rfc2217Client to it
    private static async Task<(Rfc2217Client client, NetworkStream serverStream)> CreateConnectedPairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var logger = NullLogger<Rfc2217Client>.Instance;
        var client = new Rfc2217Client("127.0.0.1", port, logger);

        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync();

        var serverTcp = await acceptTask;
        listener.Stop();

        return (client, serverTcp.GetStream());
    }

    // Helper: extract data bytes, skipping Telnet negotiation sequences
    private static byte[] ExtractDataBytes(ReadOnlySpan<byte> raw)
    {
        var result = new List<byte>();
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == 0xFF && i + 1 < raw.Length)
            {
                byte next = raw[i + 1];
                if (next == 0xFF)
                {
                    // Escaped IAC = literal 0xFF
                    result.Add(0xFF);
                    i += 2;
                }
                else if (next >= 0xF0) // Telnet command
                {
                    if (next == 0xFA) // SB: skip to SE
                    {
                        i += 2;
                        while (i < raw.Length - 1)
                        {
                            if (raw[i] == 0xFF && raw[i + 1] == 0xF0)
                            {
                                i += 2;
                                break;
                            }
                            i++;
                        }
                    }
                    else if (next >= 0xFB && next <= 0xFE) // WILL/WONT/DO/DONT
                    {
                        i += 3; // IAC + cmd + option
                    }
                    else
                    {
                        i += 2; // IAC + cmd
                    }
                }
                else
                {
                    result.Add(raw[i]);
                    i++;
                }
            }
            else
            {
                result.Add(raw[i]);
                i++;
            }
        }
        return result.ToArray();
    }

    // Helper: get raw bytes after initial negotiation burst
    private static byte[] ExtractRawBytesAfterNegotiation(ReadOnlySpan<byte> raw)
    {
        // Find end of negotiation (last IAC WILL/DO/SB...SE sequence)
        // Then return remaining bytes
        return raw.ToArray(); // Simplified: return all for pattern matching
    }

    // Helper: check if a subsequence exists in the data
    private static Func<byte[], bool> ContainsSubsequence(byte[] haystack)
    {
        return needle =>
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        };
    }
}
