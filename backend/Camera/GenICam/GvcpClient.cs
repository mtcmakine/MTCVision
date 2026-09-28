using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MTCVision.Backend.Camera.GenICam;

/// <summary>
/// GVCP (GigE Vision Control Protocol) client: the register and memory access the camera's
/// GenICam node map is built on, plus control-channel ownership and its heartbeat.
/// </summary>
public sealed class GvcpClient : IDisposable
{
    public const int GvcpPort = 3956;

    private const ushort DiscoveryCmd = 0x0002;
    private const ushort ReadRegCmd = 0x0080;
    private const ushort WriteRegCmd = 0x0082;
    private const ushort ReadMemCmd = 0x0084;
    private const ushort WriteMemCmd = 0x0086;

    private const uint GevCcpAddress = 0x0A00;
    private const uint HeartbeatTimeoutAddress = 0x0938;
    private const uint FirstUrlAddress = 0x0200;

    /// <summary>Largest READMEM payload that still fits a non-jumbo GVCP datagram.</summary>
    private const ushort MaxMemChunk = 536;

    private readonly IPEndPoint _cameraEndPoint;
    private readonly UdpClient _udp;
    private readonly SemaphoreSlim _transactionLock = new(1, 1);

    // Seeded randomly, not from 1: reconnecting reuses the freed ephemeral source port often
    // enough that a fresh client restarting the sequence looks to the camera like a retransmit of
    // a command it already answered, and it stays silent.
    private ushort _requestId = (ushort)Random.Shared.Next(1, ushort.MaxValue);

    private Timer? _heartbeat;
    private bool _hasControl;

    /// <summary>Raised when the control channel could not be handed back cleanly on disconnect.</summary>
    public event EventHandler<string>? ReleaseFailed;

    public GvcpClient(string cameraIp, string localInterfaceIp)
    {
        _cameraEndPoint = new IPEndPoint(IPAddress.Parse(cameraIp), GvcpPort);
        _udp = new UdpClient(new IPEndPoint(IPAddress.Parse(localInterfaceIp), 0));
        _udp.Client.ReceiveTimeout = 2000;
        _udp.Client.SendTimeout = 2000;
    }

    /// <summary>
    /// Broadcasts a GVCP discovery and returns every camera that answers on this interface.
    /// </summary>
    public static async Task<IReadOnlyList<GigeDeviceInfo>> DiscoverAsync(string localInterfaceIp, int timeoutMs = 1000)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(localInterfaceIp), 0)) { EnableBroadcast = true };

        var request = new byte[8];
        request[0] = 0x42;
        request[1] = 0x11; // acknowledge required + allow broadcast ack
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), DiscoveryCmd);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6, 2), 1);
        await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Broadcast, GvcpPort));

        var devices = new List<GigeDeviceInfo>();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0)
            {
                break;
            }

            using var cts = new CancellationTokenSource(remaining);
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var device = ParseDiscoveryAck(result.Buffer);
            if (device is not null && devices.All(d => d.Ip != device.Ip))
            {
                devices.Add(device with { LocalIp = localInterfaceIp });
            }
        }

        return devices;
    }

    /// <summary>Discovery ack layout, GEV 1.2 §16.1.</summary>
    private static GigeDeviceInfo? ParseDiscoveryAck(byte[] ack)
    {
        const int PayloadOffset = 8;
        if (ack.Length < PayloadOffset + 0xF8 || BinaryPrimitives.ReadUInt16BigEndian(ack.AsSpan(2, 2)) != DiscoveryCmd + 1)
        {
            return null;
        }

        string Text(int offset, int length) =>
            Encoding.ASCII.GetString(ack, PayloadOffset + offset, length).TrimEnd('\0').Trim();

        // MAC sits at payload offset 0x0A: the 8-byte field at 0x08 is two reserved bytes first.
        var mac = ack.AsSpan(PayloadOffset + 0x0A, 6).ToArray();
        return new GigeDeviceInfo(
            Ip: new IPAddress(ack.AsSpan(PayloadOffset + 0x24, 4).ToArray()).ToString(),
            Mac: string.Join(':', mac.Select(b => b.ToString("X2"))),
            ManufacturerName: Text(0x48, 32),
            Model: Text(0x68, 32),
            DeviceVersion: Text(0x88, 32),
            SerialNumber: Text(0xD8, 16),
            UserDefinedName: Text(0xE8, 16));
    }

    public async Task<uint> ReadRegisterAsync(uint address)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, address);
        var ack = await TransactAsync(ReadRegCmd, payload);
        return BinaryPrimitives.ReadUInt32BigEndian(ack.AsSpan(0, 4));
    }

    public async Task WriteRegisterAsync(uint address, uint value)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), address);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), value);
        await TransactAsync(WriteRegCmd, payload);
    }

    /// <summary>Reads <paramref name="count"/> bytes, chunked to fit the GVCP datagram limit.</summary>
    public async Task<byte[]> ReadMemoryAsync(uint address, int count)
    {
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            // READMEM counts must be a multiple of 4 (GEV 1.2 §17.3).
            var remaining = count - offset;
            var chunk = (ushort)(Math.Min(remaining, MaxMemChunk) + 3) / 4 * 4;

            var payload = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), address + (uint)offset);
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), (ushort)chunk);
            var ack = await TransactAsync(ReadMemCmd, payload);

            // Ack payload is the echoed address followed by the data.
            var available = ack.Length - 4;
            var copy = Math.Min(available, remaining);
            Array.Copy(ack, 4, result, offset, copy);
            offset += copy;
        }

        return result;
    }

    public async Task WriteMemoryAsync(uint address, byte[] data)
    {
        var payload = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), address);
        data.CopyTo(payload.AsSpan(4));
        await TransactAsync(WriteMemCmd, payload);
    }

    /// <summary>
    /// Claims exclusive control (GevCCP = 2) and starts the heartbeat that keeps it. Without the
    /// heartbeat the camera reclaims control after GevHeartbeatTimeout and stops streaming.
    /// </summary>
    public async Task TakeControlAsync()
    {
        var timeoutMs = await ReadRegisterAsync(HeartbeatTimeoutAddress);

        // A control channel that was not released cleanly — a crashed client, or our own previous
        // connection — stays claimed until the camera's heartbeat timeout lapses, so keep trying
        // for a little longer than that rather than failing the connect outright.
        var giveUpAt = DateTime.UtcNow.AddMilliseconds(timeoutMs + 3000);
        while (true)
        {
            try
            {
                await WriteRegisterAsync(GevCcpAddress, 2);

                // The write can be accepted and still not stick while another host holds control,
                // so confirm rather than trusting the ack.
                if (await ReadRegisterAsync(GevCcpAddress) == 2)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is TimeoutException or GvcpException)
            {
                // Falls through to the retry below.
            }

            if (DateTime.UtcNow >= giveUpAt)
            {
                throw new InvalidOperationException(
                    $"Kamera kontrolü alınamadı. Kamerayı başka bir uygulama kullanıyor olabilir, ya da " +
                    $"önceki oturum düzgün kapanmadı — bu durumda kamera {timeoutMs} ms sonra kendini serbest bırakır.");
            }

            await Task.Delay(500);
        }

        _hasControl = true;

        var period = TimeSpan.FromMilliseconds(Math.Max(500, timeoutMs / 3.0));
        _heartbeat = new Timer(_ => _ = SendHeartbeatAsync(), null, period, period);
    }

    private async Task SendHeartbeatAsync()
    {
        try
        {
            // Any successful GVCP read resets the camera's control timer.
            await ReadRegisterAsync(GevCcpAddress);
        }
        catch
        {
            // A missed beat is recoverable; the camera only drops control after the full timeout.
        }
    }

    public async Task ReleaseControlAsync()
    {
        if (_heartbeat is not null)
        {
            await _heartbeat.DisposeAsync();
            _heartbeat = null;
        }

        if (_hasControl)
        {
            _hasControl = false;

            // Releasing cleanly is what lets the next connect succeed immediately instead of
            // waiting out the camera's heartbeat timeout, so it is worth a couple of tries.
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await WriteRegisterAsync(GevCcpAddress, 0);
                    break;
                }
                catch (Exception ex) when (ex is TimeoutException or GvcpException)
                {
                    if (attempt == 3)
                    {
                        ReleaseFailed?.Invoke(this,
                            $"Kamera kontrolü serbest bırakılamadı ({ex.Message}). Kamera kendini " +
                            "heartbeat zaman aşımıyla serbest bırakacak.");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reads the bootstrap First URL, which points at the GenICam XML held in device memory as
    /// "Local:&lt;name&gt;;&lt;hex address&gt;;&lt;hex length&gt;".
    /// </summary>
    public async Task<string> ReadXmlUrlAsync()
    {
        var raw = await ReadMemoryAsync(FirstUrlAddress, 512);
        return Encoding.ASCII.GetString(raw).TrimEnd('\0').Trim();
    }

    private async Task<byte[]> TransactAsync(ushort command, byte[] payload)
    {
        await _transactionLock.WaitAsync();
        try
        {
            var request = new byte[8 + payload.Length];
            request[0] = 0x42;
            request[1] = 0x01; // acknowledge required
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), command);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), (ushort)payload.Length);

            // Request ids must be non-zero and are echoed back, letting us drop stale acks.
            if (++_requestId == 0)
            {
                _requestId = 1;
            }

            var requestId = _requestId;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6, 2), requestId);
            payload.CopyTo(request.AsSpan(8));

            _udp.Send(request, request.Length, _cameraEndPoint);

            var deadline = DateTime.UtcNow.AddMilliseconds(2000);
            while (DateTime.UtcNow < deadline)
            {
                IPEndPoint? from = null;
                byte[] ack;
                try
                {
                    ack = _udp.Receive(ref from!);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    break;
                }

                if (ack.Length < 8 || BinaryPrimitives.ReadUInt16BigEndian(ack.AsSpan(6, 2)) != requestId)
                {
                    continue;
                }

                var status = BinaryPrimitives.ReadUInt16BigEndian(ack.AsSpan(0, 2));
                if (status != 0)
                {
                    throw new GvcpException(command, status);
                }

                return ack[8..];
            }

            throw new TimeoutException($"GVCP komutu yanıtsız kaldı (cmd 0x{command:X4}).");
        }
        finally
        {
            _transactionLock.Release();
        }
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
        _udp.Dispose();
        _transactionLock.Dispose();
    }
}

public sealed record GigeDeviceInfo(
    string Ip,
    string Mac,
    string ManufacturerName,
    string Model,
    string DeviceVersion,
    string SerialNumber,
    string UserDefinedName)
{
    /// <summary>
    /// The local interface address this camera answered on. Streaming must use an interface on the
    /// camera's own subnet, and this is the one known to reach it.
    /// </summary>
    public string LocalIp { get; init; } = string.Empty;
}

public sealed class GvcpException(ushort command, ushort status)
    : Exception($"GVCP komutu 0x{command:X4} hata döndürdü: 0x{status:X4} ({Describe(status)}).")
{
    public ushort Status { get; } = status;

    /// <summary>GEV 1.2 §15.4.1 status codes.</summary>
    private static string Describe(ushort status) => status switch
    {
        0x8001 => "komut desteklenmiyor",
        0x8002 => "geçersiz parametre — yazılan değer izin verilen aralığın dışında olabilir",
        0x8003 => "geçersiz adres",
        0x8004 => "yazma korumalı",
        0x8005 => "hizalama hatası",
        0x8006 => "erişim reddedildi",
        0x8007 => "cihaz meşgul",
        0x8008 => "cihazda yerel hata — değer bu anda kabul edilmiyor olabilir",
        0x800C => "paket kullanılamıyor",
        0x800D => "veri taşması",
        0x800E => "geçersiz başlık",
        0x800F => "yapılandırma bu işlemi desteklemiyor",
        0x8FFF => "genel hata",
        _ => "bilinmeyen",
    };
}
