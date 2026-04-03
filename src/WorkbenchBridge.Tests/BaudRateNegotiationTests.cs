using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WorkbenchBridge.Rfc2217;
using Xunit;

namespace WorkbenchBridge.Tests;

/// <summary>
/// Tests for RFC 2217 baud rate negotiation.
/// This validates that when the IDE changes baud rate (e.g. from 115200 to 460800
/// during firmware upload), the SET_BAUDRATE command is sent correctly and the
/// server response is parsed properly.
/// </summary>
public class BaudRateNegotiationTests
{
    [Theory]
    [InlineData(9600)]
    [InlineData(115200)]
    [InlineData(230400)]
    [InlineData(460800)]
    [InlineData(921600)]
    [InlineData(1500000)]
    public async Task SetBaudRate_SendsCorrectSubnegotiation(int baudRate)
    {
        var (client, serverStream) = await CreateConnectedPairAsync();
        await using (client)
        {
            // Start a task to simulate server response
            var serverTask = Task.Run(async () =>
            {
                // Read from server until we find the SET_BAUDRATE subnegotiation
                var buf = new byte[256];
                var allBytes = new List<byte>();

                serverStream.ReadTimeout = 2000;
                try
                {
                    while (true)
                    {
                        int read = serverStream.Read(buf, 0, buf.Length);
                        if (read == 0) break;

                        for (int i = 0; i < read; i++)
                            allBytes.Add(buf[i]);

                        // Look for IAC SB COM_PORT SET_BAUDRATE <4 bytes> IAC SE
                        if (FindBaudRateCommand(allBytes, out int foundBaud))
                        {
                            Assert.Equal(baudRate, foundBaud);

                            // Send server response: IAC SB COM_PORT SERVER_SET_BAUDRATE <4 bytes> IAC SE
                            var response = BuildBaudRateResponse(baudRate);
                            serverStream.Write(response, 0, response.Length);
                            return;
                        }
                    }
                }
                catch (IOException) { }

                Assert.Fail("Did not receive SET_BAUDRATE command from client");
            });

            await client.SetBaudRateAsync(baudRate);
            await serverTask;

            Assert.Equal(baudRate, client.CurrentBaudRate);
        }
    }

    [Fact]
    public async Task BaudRate_EncodedAsBigEndian()
    {
        // 115200 = 0x0001C200
        // Big endian bytes: 0x00, 0x01, 0xC2, 0x00
        var (client, serverStream) = await CreateConnectedPairAsync();
        await using (client)
        {
            var serverTask = Task.Run(() =>
            {
                var buf = new byte[256];
                var allBytes = new List<byte>();
                serverStream.ReadTimeout = 2000;

                try
                {
                    while (true)
                    {
                        int read = serverStream.Read(buf, 0, buf.Length);
                        if (read == 0) break;
                        for (int i = 0; i < read; i++)
                            allBytes.Add(buf[i]);

                        // Find the baud rate bytes in the subneg
                        var baudBytes = FindBaudRateBytes(allBytes);
                        if (baudBytes is not null)
                        {
                            Assert.Equal(0x00, baudBytes[0]);
                            Assert.Equal(0x01, baudBytes[1]);
                            Assert.Equal(0xC2, baudBytes[2]);
                            Assert.Equal(0x00, baudBytes[3]);

                            // Send ack
                            serverStream.Write(BuildBaudRateResponse(115200));
                            return;
                        }
                    }
                }
                catch (IOException) { }
            });

            await client.SetBaudRateAsync(115200);
            await serverTask;
        }
    }

    // Helper: find IAC SB 44 1 <4 bytes> IAC SE in the byte stream
    private static bool FindBaudRateCommand(List<byte> data, out int baudRate)
    {
        baudRate = 0;
        for (int i = 0; i <= data.Count - 9; i++)
        {
            if (data[i] == TelnetConstants.IAC &&
                data[i + 1] == TelnetConstants.SB &&
                data[i + 2] == TelnetConstants.OPT_COM_PORT &&
                data[i + 3] == TelnetConstants.CPO_SET_BAUDRATE)
            {
                // Next 4 bytes are baud rate (big endian), but may have IAC escaping
                var baudBytes = ExtractPayloadBytes(data, i + 4);
                if (baudBytes.Count >= 4)
                {
                    baudRate = (baudBytes[0] << 24) | (baudBytes[1] << 16) |
                               (baudBytes[2] << 8) | baudBytes[3];
                    return true;
                }
            }
        }
        return false;
    }

    private static byte[]? FindBaudRateBytes(List<byte> data)
    {
        for (int i = 0; i <= data.Count - 9; i++)
        {
            if (data[i] == TelnetConstants.IAC &&
                data[i + 1] == TelnetConstants.SB &&
                data[i + 2] == TelnetConstants.OPT_COM_PORT &&
                data[i + 3] == TelnetConstants.CPO_SET_BAUDRATE)
            {
                var bytes = ExtractPayloadBytes(data, i + 4);
                if (bytes.Count >= 4)
                    return bytes.Take(4).ToArray();
            }
        }
        return null;
    }

    // Extract payload bytes from subneg, handling IAC escaping
    private static List<byte> ExtractPayloadBytes(List<byte> data, int startPos)
    {
        var result = new List<byte>();
        int pos = startPos;
        while (pos < data.Count)
        {
            if (data[pos] == TelnetConstants.IAC)
            {
                if (pos + 1 < data.Count)
                {
                    if (data[pos + 1] == TelnetConstants.SE)
                        break; // End of subneg
                    if (data[pos + 1] == TelnetConstants.IAC)
                    {
                        result.Add(TelnetConstants.IAC); // Escaped
                        pos += 2;
                        continue;
                    }
                }
                break;
            }
            result.Add(data[pos]);
            pos++;
        }
        return result;
    }

    // Build server baud rate response
    private static byte[] BuildBaudRateResponse(int baudRate)
    {
        return new byte[]
        {
            TelnetConstants.IAC, TelnetConstants.SB, TelnetConstants.OPT_COM_PORT,
            TelnetConstants.CPO_SERVER_SET_BAUDRATE,
            (byte)((baudRate >> 24) & 0xFF),
            (byte)((baudRate >> 16) & 0xFF),
            (byte)((baudRate >> 8) & 0xFF),
            (byte)(baudRate & 0xFF),
            TelnetConstants.IAC, TelnetConstants.SE
        };
    }

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
}
