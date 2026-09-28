using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace MTCVision.Backend.Camera.Zebra;

/// <summary>
/// Takes the scanner's images from the same socket.io channel its Aurora Focus web HMI listens on,
/// which needs neither the FTP push nor any license: after each job run the device broadcasts a
/// binary "job_result_data" message carrying the result image, to every connected client.
///
/// Engine.IO v4 / Socket.IO framing is small enough to speak directly over a WebSocket: join the
/// default namespace, answer pings, and read a binary event as a text header frame followed by one
/// binary frame per attachment. The device needs no login for this channel (the HMI itself only
/// signs in afterwards, for its REST calls).
///
/// Listens only while the camera is connected: <see cref="Start"/> and <see cref="Stop"/> follow the
/// camera service's connect and disconnect, so a console set up for a different camera never
/// keeps knocking on the scanner's address.
/// </summary>
public sealed class ZebraHmiImageSource(IOptions<ZebraCameraOptions> options) : IZebraImageSource, IDisposable
{
    private const string JobResultEvent = "job_result_data";

    private readonly ZebraCameraOptions _options = options.Value;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private string? _deviceIp;

    public event EventHandler<CameraFrame>? ImageReceived;
    public event EventHandler<string>? Status;

    public void Start(string deviceIp)
    {
        lock (_gate)
        {
            if (_cts is not null && _deviceIp == deviceIp)
            {
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _deviceIp = deviceIp;

            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(deviceIp, token));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            _deviceIp = null;
        }
    }

    public void Dispose() => Stop();

    private async Task RunAsync(string deviceIp, CancellationToken ct)
    {
        var lastFailure = string.Empty;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(deviceIp, ct);
                lastFailure = string.Empty;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Reported once per distinct failure, not on every retry: an unplugged camera would
                // otherwise fill the log with the same line every few seconds.
                if (ex.Message != lastFailure)
                {
                    lastFailure = ex.Message;
                    Status?.Invoke(this, $"Zebra HMI görüntü kanalına bağlanılamadı: {ex.Message}");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunSessionAsync(string deviceIp, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        var uri = new Uri($"ws://{deviceIp}:{_options.HmiPort}/socket.io/?EIO=4&transport=websocket");

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(uri, connectTimeout.Token);
        }

        var chunk = new byte[64 * 1024];
        string? pendingBinaryEvent = null;

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(chunk, 0, received.Count);
            }
            while (!received.EndOfMessage);

            if (received.MessageType == WebSocketMessageType.Text)
            {
                pendingBinaryEvent = await HandleTextAsync(socket, deviceIp, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), ct);
            }
            else
            {
                if (pendingBinaryEvent == JobResultEvent)
                {
                    HandleJobResult(message.GetBuffer().AsSpan(0, (int)message.Length));
                }

                pendingBinaryEvent = null;
            }
        }
    }

    /// <returns>The event name a following binary frame belongs to, if this text frame announced one.</returns>
    private async Task<string?> HandleTextAsync(ClientWebSocket socket, string deviceIp, string text, CancellationToken ct)
    {
        if (text.StartsWith("0{", StringComparison.Ordinal))
        {
            await SendAsync(socket, "40", ct);
            return null;
        }

        if (text == "2")
        {
            await SendAsync(socket, "3", ct);
            return null;
        }

        if (text.StartsWith("40", StringComparison.Ordinal))
        {
            Status?.Invoke(this, $"Zebra HMI görüntü kanalı bağlandı ({deviceIp}).");
            return null;
        }

        // "45<n>-[...]" is a binary event; its attachments follow as raw binary frames.
        if (text.StartsWith("45", StringComparison.Ordinal) && text.Contains($"\"{JobResultEvent}\"", StringComparison.Ordinal))
        {
            return JobResultEvent;
        }

        return null;
    }

    private void HandleJobResult(ReadOnlySpan<byte> payload)
    {
        try
        {
            var result = ZebraJobResultParser.Parse(payload);
            if (result.Image is null)
            {
                return;
            }

            var frame = ZebraFrameDecoder.Decode(result.Image);
            if (frame is null)
            {
                Status?.Invoke(this, "Zebra HMI sonuç görüntüsü çözülemedi.");
                return;
            }

            Status?.Invoke(this, $"Zebra HMI görüntüsü alındı ({frame.Width}x{frame.Height}).");
            ImageReceived?.Invoke(this, frame);
        }
        catch (Exception ex)
        {
            Status?.Invoke(this, $"Zebra HMI sonucu işlenemedi: {ex.Message}");
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string text, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);
}
