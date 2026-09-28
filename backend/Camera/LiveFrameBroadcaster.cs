using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace MTCVision.Backend.Camera;

public sealed class LiveFrameBroadcaster
{
    private readonly ConcurrentDictionary<Guid, ClientState> _clients = new();

    public LiveFrameBroadcaster(ICameraService cameraService)
    {
        cameraService.FrameCaptured += OnFrameCaptured;
    }

    public async Task HandleClientAsync(WebSocket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _clients[id] = new ClientState(socket);
        try
        {
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    private void OnFrameCaptured(object? sender, CameraFrame frame)
    {
        if (_clients.IsEmpty)
        {
            return;
        }

        byte[] jpeg;
        try
        {
            jpeg = FrameJpegEncoder.Encode(frame);
        }
        catch
        {
            return;
        }

        foreach (var (id, client) in _clients)
        {
            if (client.Socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }

            // Non-blocking: if the client is still receiving the previous frame, drop this one
            // rather than queueing up a backlog of stale frames.
            if (!client.SendGate.Wait(0))
            {
                continue;
            }

            _ = SendAndReleaseAsync(id, client, jpeg);
        }
    }

    private async Task SendAndReleaseAsync(Guid id, ClientState client, byte[] jpeg)
    {
        try
        {
            await client.Socket.SendAsync(jpeg, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        }
        catch
        {
            _clients.TryRemove(id, out _);
        }
        finally
        {
            client.SendGate.Release();
        }
    }

    private sealed class ClientState(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }
}
