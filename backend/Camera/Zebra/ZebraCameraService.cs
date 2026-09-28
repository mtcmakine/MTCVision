using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using MTCVision.Backend.Camera.GenICam;

namespace MTCVision.Backend.Camera.Zebra;

/// <summary>
/// <see cref="ICameraService"/> for a Zebra FS40/S40 running its base (unlicensed) Aurora Focus
/// firmware: no GigE Vision/GenICam, so there is no feature tree, no live GVSP stream and no
/// exposure/gain control from here — Aurora Focus owns all of that.
///
/// What this gives MTCVision is exactly what the rest of the app needs to keep working unmodified:
/// a <see cref="CameraFrame"/> per <see cref="TriggerAsync"/> call, delivered by sending Aurora
/// Focus's configured TCP/IP trigger string and waiting for the image its job pushes back over FTP
/// (see <see cref="ZebraFtpImageReceiver"/>).
/// </summary>
public sealed class ZebraCameraService : ICameraService, IDisposable
{
    private readonly ZebraCameraOptions _options;
    private readonly IZebraImageSource _receiver;

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private TcpClient? _triggerClient;

    private long _framesCompleted;
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private long _fpsWindowStartMs;
    private long _fpsWindowFrames;
    private double _fps;
    private CameraFrame? _lastFrame;

    public ZebraCameraService(IOptions<ZebraCameraOptions> options, IZebraImageSource receiver)
    {
        _options = options.Value;
        _receiver = receiver;
        _receiver.Status += (_, message) => StatusChanged?.Invoke(this, message);
    }

    public bool IsConnected { get; private set; }
    public string? ConnectedIp { get; private set; }

    public event EventHandler<CameraFrame>? FrameCaptured;
    public event EventHandler<string>? StatusChanged;

    public GvspStatistics? Statistics => IsConnected
        ? new GvspStatistics(
            _lastFrame?.Width ?? 0,
            _lastFrame?.Height ?? 0,
            _lastFrame?.PixelFormat.ToString() ?? "Mono8",
            Interlocked.Read(ref _framesCompleted),
            FramesIncomplete: 0,
            BlocksAbortedByCamera: 0,
            Math.Round(_fps, 1))
        : null;

    /// <summary>
    /// There is no GVCP-style broadcast discovery for this camera: it just reports back whether the
    /// configured device answers on its TCP/IP Control port, so the existing "scan, then pick from
    /// the list" flow in the frontend keeps working unchanged.
    /// </summary>
    public async Task<IReadOnlyList<GigeDeviceInfo>> DiscoverAsync(string? localInterfaceIp = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.DeviceIp))
        {
            return [];
        }

        try
        {
            using var probe = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            await probe.ConnectAsync(_options.DeviceIp, _options.TriggerPort, timeoutCts.Token);
        }
        catch (Exception)
        {
            return [];
        }

        return
        [
            new GigeDeviceInfo(
                Ip: _options.DeviceIp,
                Mac: "",
                ManufacturerName: "Zebra Technologies",
                Model: "FS40/S40",
                DeviceVersion: "",
                SerialNumber: "",
                UserDefinedName: "")
            { LocalIp = localInterfaceIp ?? "" },
        ];
    }

    public async Task ConnectAsync(string cameraIp, string localInterfaceIp, CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            await DisconnectCoreAsync();
            await OpenTriggerConnectionAsync(cameraIp, ct);
            _receiver.Start(cameraIp);

            IsConnected = true;
            ConnectedIp = cameraIp;
            StatusChanged?.Invoke(this, $"Zebra tetikleme bağlantısı açıldı ({cameraIp}:{_options.TriggerPort}).");
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private Task DisconnectCoreAsync()
    {
        _receiver.Stop();
        _triggerClient?.Dispose();
        _triggerClient = null;
        IsConnected = false;
        ConnectedIp = null;
        return Task.CompletedTask;
    }

    private async Task OpenTriggerConnectionAsync(string cameraIp, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(cameraIp, _options.TriggerPort, ct);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new CameraConfigurationException(
                $"Zebra kamerasının TCP/IP Control portuna bağlanılamadı ({cameraIp}:{_options.TriggerPort}): {ex.Message}. " +
                "Aurora Focus'ta 'Enable TCP/IP Control' açık mı ve Control Port Number bu ayarla eşleşiyor mu kontrol edin.");
        }

        _triggerClient = client;
        _ = Task.Run(() => ReadResultsAsync(client));
    }

    /// <summary>
    /// With "Use Same Port For Control And Result" on, the camera answers each job run on the same
    /// connection (e.g. "PASS"). Nothing needs the text, but reading it keeps the socket's receive
    /// buffer from filling on a long-running line and shows in the log that a trigger really ran.
    /// </summary>
    private async Task ReadResultsAsync(TcpClient client)
    {
        var buffer = new byte[1024];
        try
        {
            var stream = client.GetStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    return;
                }

                var text = Encoding.ASCII.GetString(buffer, 0, read).Trim();
                if (text.Length > 0)
                {
                    StatusChanged?.Invoke(this, $"Zebra job sonucu: {text}");
                }
            }
        }
        catch (Exception)
        {
            // The connection was closed or replaced; a later trigger reopens it.
        }
    }

    /// <summary>
    /// Sends Aurora Focus's configured trigger string and waits for the image its job pushes back
    /// over FTP. Subscribes before sending: on a fast job, the file can land before the TCP write
    /// even returns.
    /// </summary>
    public async Task<CameraFrame> TriggerAsync(CancellationToken ct = default)
    {
        if (_triggerClient is null)
        {
            throw new InvalidOperationException("Kamera bağlı değil.");
        }

        var completion = new TaskCompletionSource<CameraFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnImage(object? sender, CameraFrame frame) => completion.TrySetResult(frame);

        _receiver.ImageReceived += OnImage;
        try
        {
            await SendTriggerAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_options.TriggerTimeoutMs));
            var frame = await completion.Task.WaitAsync(timeoutCts.Token);

            _lastFrame = frame;
            Interlocked.Increment(ref _framesCompleted);
            UpdateFps();
            FrameCaptured?.Invoke(this, frame);
            return frame;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var whereFrom = string.Equals(_options.ImageSource, "Ftp", StringComparison.OrdinalIgnoreCase)
                ? "Connect > Save Image çıktısı bu sunucunun FTP'sine mi yönlendirilmiş"
                : "job deploy edilmiş ve çalışıyor mu, 'Show Image' açık mı";
            throw new InvalidOperationException(
                "Tetiklemeden sonra Aurora Focus'tan görüntü gelmedi. Job'un Connect > Triggers > Trigger Source " +
                $"'TCP/IP' mi, {whereFrom}, ve tetikleme dizesi/terminatörü appsettings.json'daki " +
                "Camera:Zebra ayarlarıyla eşleşiyor mu kontrol edin.");
        }
        finally
        {
            _receiver.ImageReceived -= OnImage;
        }
    }

    private async Task SendTriggerAsync(CancellationToken ct)
    {
        var payload = Encoding.ASCII.GetBytes(_options.TriggerString + _options.TriggerTerminator);
        try
        {
            var stream = _triggerClient!.GetStream();
            await stream.WriteAsync(payload, ct);
        }
        catch (Exception) when (ConnectedIp is not null)
        {
            // The device closes idle TCP/IP Control connections after a while; reopen once and
            // retry rather than failing a trigger over a connection that just needed refreshing.
            await OpenTriggerConnectionAsync(ConnectedIp, ct);
            var stream = _triggerClient!.GetStream();
            await stream.WriteAsync(payload, ct);
        }
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

    public Task<CameraSettings> GetSettingsAsync() => Task.FromResult(new CameraSettings(
        ExposureTimeUs: null,
        Gain: null,
        BlackLevel: null,
        FrameRateHz: null,
        Width: _lastFrame?.Width,
        Height: _lastFrame?.Height,
        OffsetX: null,
        OffsetY: null,
        ExposureRange: null,
        GainRange: null,
        BlackLevelRange: null,
        FrameRateRange: null,
        TriggerMode: "On",
        // To the console this is a software trigger: the app fires it (over the scanner's TCP/IP
        // control port). "TCP/IP" as a trigger source means the opposite here — a message *arriving*
        // at the console — and reporting it for the Zebra as well would make the two indistinguishable.
        TriggerSource: "Software",
        TriggerActivation: null,
        TriggerDelayUs: null,
        TriggerDelayRange: null,
        AutoBrightness: null,
        AutoBrightnessTarget: null,
        AutoBrightnessTargetRange: null,
        ReverseX: false,
        ReverseY: false,
        DeviceTemperatureC: null));

    public Task ApplySettingsAsync(CameraSettingsUpdate update) => throw new CameraConfigurationException(
        "Bu kamerada pozlama/kazanç/kare hızı MTCVision'dan değil, doğrudan Aurora Focus'tan ayarlanıyor.");

    public Task SaveToCameraAsync() => throw new CameraConfigurationException(
        "Bu kamerada kaydedilecek bir MTCVision ayarı yok; ayarlar Aurora Focus'un kendi job'unda tutuluyor.");

    public Task<CameraIdentity> GetIdentityAsync() => Task.FromResult(new CameraIdentity(
        UserId: null,
        ManufacturerName: "Zebra Technologies",
        ModelName: "FS40/S40",
        SerialNumber: null,
        DeviceVersion: null,
        IpConfiguration: null,
        PersistentIp: null,
        PersistentSubnetMask: null,
        PersistentGateway: null,
        CurrentIp: ConnectedIp,
        Mac: null));

    public Task UpdateIdentityAsync(CameraIdentityUpdate update) => throw new CameraConfigurationException(
        "Bu kameranın adı ve ağ ayarları MTCVision'dan değil, Aurora Focus'tan değiştiriliyor.");

    public Task<double> GetFeatureAsync(string featureName) => throw new CameraConfigurationException(
        "Bu kamerada GenICam özellik erişimi yok (GenICam/GigE Vision lisansı aktif değil).");

    public Task SetFeatureAsync(string featureName, double value) => throw new CameraConfigurationException(
        "Bu kamerada GenICam özellik erişimi yok (GenICam/GigE Vision lisansı aktif değil).");

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _connectionLock.Dispose();
    }
}
