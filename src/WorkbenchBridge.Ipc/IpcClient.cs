using System.IO.Pipes;
using System.Text;

namespace WorkbenchBridge.Ipc;

/// <summary>
/// Named pipe client for sending commands from the CLI to the Windows service.
/// Each method opens a connection, sends one request, reads one response, and closes.
/// </summary>
public sealed class IpcClient : IDisposable
{
    private readonly int _timeoutMs;

    public IpcClient(int timeoutMs = 5000)
    {
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Send a request to the service and return the response.
    /// Throws TimeoutException if the service does not respond.
    /// Throws IOException if the pipe is not available (service not running).
    /// </summary>
    public async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(_timeoutMs, ct);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                "Could not connect to the WorkbenchBridge service. Is it running?");
        }

        // Send request as a single JSON line
        var requestJson = IpcProtocol.Serialize(request) + "\n";
        var requestBytes = Encoding.UTF8.GetBytes(requestJson);
        await pipe.WriteAsync(requestBytes, ct);
        await pipe.FlushAsync(ct);

        // Read response (single JSON line)
        var responseBuilder = new StringBuilder();
        var buffer = new byte[4096];
        while (true)
        {
            int bytesRead = await pipe.ReadAsync(buffer, ct);
            if (bytesRead == 0) break;

            responseBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
            if (responseBuilder.ToString().Contains('\n'))
                break;
        }

        var responseJson = responseBuilder.ToString().TrimEnd();
        if (string.IsNullOrEmpty(responseJson))
        {
            return new IpcResponse
            {
                Success = false,
                Message = "Empty response from service"
            };
        }

        return IpcProtocol.Deserialize<IpcResponse>(responseJson)
            ?? new IpcResponse { Success = false, Message = "Invalid response from service" };
    }

    public void Dispose()
    {
        // Nothing to dispose; pipes are disposed per-request via await using.
    }
}
