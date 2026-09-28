using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MTCVision.Backend.Camera;

/// <summary>
/// Minimal GVSP (GigE Vision Stream Protocol) receiver.
///
/// GigeVision.Core ships its own stream receiver, but it needs Width/Height supplied up front and
/// takes them from Camera.SyncParameters(), whose GenICam XML resolution returns 0 on the Teledyne
/// DALSA Genie Nano — only the fixed-address bootstrap registers read back correctly on that camera.
/// Feeding it a guessed resolution made it throw ArgumentOutOfRangeException during reassembly.
///
/// Every GVSP block opens with a data-leader packet carrying size_x/size_y/pixel_format for that
/// exact block, so this receiver reads the geometry off the wire and never asks the camera's XML.
/// </summary>
public sealed class GvspStreamReceiver : IDisposable
{
    // GVSP packet formats (GEV 1.2 §22.3) — low nibble of header byte 4.
    private const int FormatDataLeader = 1;
    private const int FormatDataTrailer = 2;

    private const ushort PayloadTypeImage = 0x0001;
    private const int GvspHeaderLength = 8;

    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    // Assembly state for the block currently in flight; only the receive thread touches it.
    private byte[] _assembly = [];
    private int _currentBlockId = -1;
    private int _receivedPackets;
    private int _packetStride;
    private int _width;
    private int _height;
    private GvspPixelFormat _pixelFormat;

    private long _framesCompleted;
    private long _framesIncomplete;
    private long _blocksAborted;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private long _fpsWindowStartMs;
    private long _fpsWindowFrames;
    private double _fps;

    public GvspStreamReceiver(string localInterfaceIp)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            // A 2064x1544 Mono8 frame is 2264 packets arriving back-to-back at line rate; the
            // default ~64 KB receive buffer overflows long before the thread can drain it.
            ReceiveBufferSize = 64 * 1024 * 1024,
            ReceiveTimeout = 500,
        };
        _socket.Bind(new IPEndPoint(IPAddress.Parse(localInterfaceIp), 0));
        PortRx = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    /// <summary>Ephemeral UDP port the camera must be pointed at (GevSCPHostPort).</summary>
    public int PortRx { get; }

    public event EventHandler<CameraFrame>? FrameReady;
    public event EventHandler<string>? Status;

    public GvspStatistics GetStatistics() => new(
        _width,
        _height,
        _pixelFormat.ToString(),
        Interlocked.Read(ref _framesCompleted),
        Interlocked.Read(ref _framesIncomplete),
        Interlocked.Read(ref _blocksAborted),
        Math.Round(_fps, 1));

    public void Start()
    {
        _thread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "GVSP Rx",
            // Reassembly has to keep up with a line-rate packet burst; losing the CPU mid-block
            // costs the whole frame.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private void ReceiveLoop()
    {
        var packet = new byte[16 * 1024];
        long received = 0;
        while (!_cts.IsCancellationRequested)
        {
            int length;
            try
            {
                length = _socket.Receive(packet);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                continue;
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                {
                    // Not silent: a receiver that stops reading leaves the camera streaming into a
                    // full socket buffer, and from outside that looks like "no frames, no error".
                    Status?.Invoke(this, $"GVSP alıcısı durdu: {ex.Message}");
                }

                break;
            }

            received++;
            if (received <= 3)
            {
                Status?.Invoke(this, $"GVSP paketi #{received}: {length} bayt, başlık {Convert.ToHexString(packet.AsSpan(0, Math.Min(length, 16)))}");
            }
            else if (received == 2000)
            {
                var stats = GetStatistics();
                Status?.Invoke(this, $"GVSP: {received} paket alındı, tamamlanan kare {stats.FramesCompleted}, eksik {stats.FramesIncomplete}.");
            }

            if (length >= GvspHeaderLength)
            {
                try
                {
                    HandlePacket(packet, length);
                }
                catch (Exception ex)
                {
                    Status?.Invoke(this, $"GVSP paketi işlenemedi: {ex.Message}");
                }
            }
        }
    }

    private void HandlePacket(byte[] packet, int length)
    {
        var blockId = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        var format = packet[4] & 0x0F;
        var packetId = (packet[5] << 16) | (packet[6] << 8) | packet[7];

        switch (format)
        {
            case FormatDataLeader:
                BeginBlock(blockId, packet.AsSpan(GvspHeaderLength, length - GvspHeaderLength));
                break;

            case FormatDataTrailer:
                EndBlock(blockId, packetId);
                break;

            default:
                AppendPayload(blockId, packetId, packet, length);
                break;
        }
    }

    private void BeginBlock(int blockId, ReadOnlySpan<byte> leader)
    {
        // Image data leader (GEV 1.2 §22.4.1): payload_type @2, pixel_format @12, size_x @16, size_y @20.
        if (leader.Length < 36 || BinaryPrimitives.ReadUInt16BigEndian(leader.Slice(2, 2)) != PayloadTypeImage)
        {
            _currentBlockId = -1;
            return;
        }

        if (_currentBlockId != -1)
        {
            // Previous block never got its trailer.
            Interlocked.Increment(ref _framesIncomplete);
        }

        var pixelFormat = (GvspPixelFormat)BinaryPrimitives.ReadUInt32BigEndian(leader.Slice(12, 4));
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(leader.Slice(16, 4));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(leader.Slice(20, 4));

        if (width <= 0 || height <= 0)
        {
            _currentBlockId = -1;
            return;
        }

        if (width != _width || height != _height || pixelFormat != _pixelFormat)
        {
            Status?.Invoke(this, $"Görüntü formatı: {width}x{height} {pixelFormat}");
            _width = width;
            _height = height;
            _pixelFormat = pixelFormat;
        }

        var frameLength = width * height * BytesPerPixel(pixelFormat);
        if (_assembly.Length != frameLength)
        {
            _assembly = new byte[frameLength];
        }

        _currentBlockId = blockId;
        _receivedPackets = 0;
        _packetStride = 0;
    }

    private void AppendPayload(int blockId, int packetId, byte[] packet, int length)
    {
        if (_currentBlockId != blockId || packetId < 1)
        {
            return;
        }

        var payloadLength = length - GvspHeaderLength;
        if (payloadLength <= 0)
        {
            return;
        }

        // Every payload packet but the last carries the same number of bytes, so the first one
        // establishes the stride that turns a packet id into a byte offset.
        if (_packetStride == 0)
        {
            _packetStride = payloadLength;
        }

        var offset = (long)(packetId - 1) * _packetStride;
        if (offset >= _assembly.Length)
        {
            return;
        }

        var copyLength = (int)Math.Min(payloadLength, _assembly.Length - offset);
        Buffer.BlockCopy(packet, GvspHeaderLength, _assembly, (int)offset, copyLength);
        _receivedPackets++;
    }

    private void EndBlock(int blockId, int trailerPacketId)
    {
        if (_currentBlockId != blockId)
        {
            _currentBlockId = -1;
            return;
        }

        _currentBlockId = -1;
        var expectedPackets = trailerPacketId - 1;

        if (expectedPackets <= 0)
        {
            // Leader and trailer with nothing between them: the camera opened a block and then
            // discarded the image instead of transmitting it, which it does when the link cannot
            // carry the configured frame rate. Not a reception fault on our side.
            Interlocked.Increment(ref _blocksAborted);
            return;
        }

        if (_receivedPackets < expectedPackets)
        {
            Interlocked.Increment(ref _framesIncomplete);
            return;
        }

        Interlocked.Increment(ref _framesCompleted);
        UpdateFps();

        // Hand the consumer its own copy — the next leader reuses _assembly immediately.
        var frameData = GC.AllocateUninitializedArray<byte>(_assembly.Length);
        Buffer.BlockCopy(_assembly, 0, frameData, 0, _assembly.Length);

        FrameReady?.Invoke(this, new CameraFrame(frameData, _width, _height, _pixelFormat, DateTime.UtcNow));
    }

    private void UpdateFps()
    {
        _fpsWindowFrames++;
        var elapsedMs = _uptime.ElapsedMilliseconds - _fpsWindowStartMs;
        if (elapsedMs >= 1000)
        {
            _fps = _fpsWindowFrames * 1000.0 / elapsedMs;
            _fpsWindowFrames = 0;
            _fpsWindowStartMs = _uptime.ElapsedMilliseconds;
        }
    }

    /// <summary>
    /// Bits 16-23 of a GVSP pixel format code are the bit depth per pixel (GEV 1.2, pixel format
    /// naming convention), so Mono8 (0x01080001) gives 8 bits and Mono12 (0x01100005) gives 16.
    /// </summary>
    private static int BytesPerPixel(GvspPixelFormat pixelFormat)
    {
        var bitsPerPixel = ((uint)pixelFormat >> 16) & 0xFF;
        return bitsPerPixel == 0 ? 1 : (int)((bitsPerPixel + 7) / 8);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            // Closing the socket is what unblocks the receive thread.
            _socket.Close();
        }
        catch
        {
            // Already torn down; nothing left to unblock.
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _socket.Dispose();
        _cts.Dispose();
    }
}

public sealed record GvspStatistics(
    int Width,
    int Height,
    string PixelFormat,
    long FramesCompleted,
    long FramesIncomplete,
    long BlocksAbortedByCamera,
    double Fps);
