using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WorkbenchBridge.Ipc;

/// <summary>
/// Named pipe server that runs inside the Windows service. Listens for CLI
/// commands and dispatches them to a handler callback.
///
/// The server creates a new pipe instance for each connection, allowing
/// concurrent CLI sessions.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly ILogger<IpcServer> _logger;
    private readonly Func<IpcRequest, CancellationToken, Task<IpcResponse>> _handler;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public IpcServer(
        ILogger<IpcServer> logger,
        Func<IpcRequest, CancellationToken, Task<IpcResponse>> handler)
    {
        _logger = logger;
        _handler = handler;
    }

    /// <summary>
    /// Start listening for CLI connections on the named pipe.
    /// </summary>
    public void Start(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenTask = ListenLoopAsync(_cts.Token);
        _logger.LogInformation("IPC server started on pipe: {PipeName}", IpcProtocol.PipeName);
    }

    /// <summary>
    /// Stop listening and close all connections.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_listenTask is not null)
        {
            try { await _listenTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
        }
        _cts?.Dispose();
        _logger.LogInformation("IPC server stopped");
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipeServer();

                await pipe.WaitForConnectionAsync(ct);

                // Handle the connection on a separate task so we can accept
                // the next connection immediately.
                var connPipe = pipe;
                pipe = null; // Prevent disposal in finally
                _ = Task.Run(() => HandleConnectionAsync(connPipe, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting IPC connection");
                await Task.Delay(100, ct);
            }
            finally
            {
                if (pipe is not null)
                    await pipe.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Creates a new server pipe instance. On Windows the pipe is given an
    /// explicit security descriptor so non-elevated CLI processes can connect:
    /// the service runs as LocalSystem, and a pipe created with the default
    /// security descriptor only grants access to SYSTEM/Administrators, so a
    /// normal user hits UnauthorizedAccessException at connect time. We grant
    /// Authenticated Users read/write and keep FullControl for SYSTEM and the
    /// Administrators group.
    /// </summary>
    private static NamedPipeServerStream CreatePipeServer()
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();

            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                IpcProtocol.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity: security);
        }

        // Non-Windows fallback (not used in production; the service is Windows-only).
        return new NamedPipeServerStream(
            IpcProtocol.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await using (pipe)
            {
                // Read request (single JSON line)
                var requestBuilder = new StringBuilder();
                var buffer = new byte[4096];

                while (true)
                {
                    int bytesRead = await pipe.ReadAsync(buffer, ct);
                    if (bytesRead == 0) return;

                    requestBuilder.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                    if (requestBuilder.ToString().Contains('\n'))
                        break;
                }

                var requestJson = requestBuilder.ToString().TrimEnd();
                if (string.IsNullOrEmpty(requestJson)) return;

                var request = IpcProtocol.Deserialize<IpcRequest>(requestJson);
                if (request is null)
                {
                    await SendResponseAsync(pipe, new IpcResponse
                    {
                        Success = false,
                        Message = "Invalid request format"
                    }, ct);
                    return;
                }

                // Dispatch to handler
                IpcResponse response;
                try
                {
                    response = await _handler(request, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling IPC command {Command}", request.Command);
                    response = new IpcResponse
                    {
                        Success = false,
                        Message = $"Internal error: {ex.Message}"
                    };
                }

                await SendResponseAsync(pipe, response, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* Client disconnected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in IPC connection handler");
        }
    }

    private static async Task SendResponseAsync(
        NamedPipeServerStream pipe, IpcResponse response, CancellationToken ct)
    {
        var responseJson = IpcProtocol.Serialize(response) + "\n";
        var responseBytes = Encoding.UTF8.GetBytes(responseJson);
        await pipe.WriteAsync(responseBytes, ct);
        await pipe.FlushAsync(ct);
    }
}
