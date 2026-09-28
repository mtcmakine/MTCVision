using MTCVision.Backend.Camera.GenICam;
using MTCVision.Backend.Camera.Zebra;
using MTCVision.Backend.Integration;

namespace MTCVision.Backend.Camera;

public enum CameraBackendKind
{
    Gige,
    Zebra,
}

public sealed record CameraBackendInfo(string Id, string Name, string Description);

/// <summary>
/// The <see cref="ICameraService"/> the rest of the app sees: it forwards every call to whichever
/// camera type is currently selected, so the choice between a GigE Vision camera and the Zebra
/// scanner is made from the console at run time instead of in a config file plus a restart.
///
/// The selection is kept in the session file, so a restart reconnects to the same kind of camera
/// it was using. Both cameras' events are forwarded, since only the active one ever raises any.
/// </summary>
public sealed class SwitchableCameraService : ICameraService
{
    public static readonly IReadOnlyList<CameraBackendInfo> Available =
    [
        new(nameof(CameraBackendKind.Gige), "GigE Vision kamera", "Teledyne DALSA Genie Nano ve diğer GigE Vision / GenICam kameralar"),
        new(nameof(CameraBackendKind.Zebra), "Zebra FS40 / S40", "Aurora Focus tarayıcı: TCP ile tetik, görüntü kameranın kendi arayüz kanalından"),
    ];

    private readonly GigeCameraService _gige;
    private readonly ZebraCameraService _zebra;
    /// <summary>What the console shows as the trigger source when the camera is waiting for a TCP/IP message.</summary>
    public const string TcpTriggerSource = "TCP/IP";

    private readonly SessionStore _sessions;
    private readonly OperatingMode _mode;
    private ICameraService _active;

    public SwitchableCameraService(
        GigeCameraService gige,
        ZebraCameraService zebra,
        SessionStore sessions,
        OperatingMode mode,
        IConfiguration configuration)
    {
        _gige = gige;
        _zebra = zebra;
        _sessions = sessions;
        _mode = mode;

        foreach (var inner in new ICameraService[] { gige, zebra })
        {
            inner.FrameCaptured += (_, frame) => FrameCaptured?.Invoke(this, frame);
            inner.StatusChanged += (_, message) => StatusChanged?.Invoke(this, message);
        }

        // The operator's last choice wins; the config file only decides the very first start.
        Current = Parse(sessions.Read().CameraBackend ?? configuration["Camera:Backend"]);
        _active = Select(Current);
    }

    public CameraBackendKind Current { get; private set; }

    public bool IsConnected => _active.IsConnected;
    public string? ConnectedIp => _active.ConnectedIp;
    public GvspStatistics? Statistics => _active.Statistics;

    public event EventHandler<CameraFrame>? FrameCaptured;
    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// Disconnects the current camera and makes <paramref name="kind"/> the active one. The address
    /// remembered for a restart belonged to the previous camera, so it is forgotten.
    /// </summary>
    public async Task SwitchToAsync(CameraBackendKind kind)
    {
        if (kind == Current)
        {
            return;
        }

        await _active.DisconnectAsync();

        Current = kind;
        _active = Select(kind);
        _sessions.Update(state => state with { CameraBackend = kind.ToString(), CameraIp = null, LocalIp = null });
        StatusChanged?.Invoke(this, $"Kamera tipi değiştirildi: {kind}");
    }

    private static CameraBackendKind Parse(string? value) =>
        value is not null && value.StartsWith("Zebra", StringComparison.OrdinalIgnoreCase)
            ? CameraBackendKind.Zebra
            : CameraBackendKind.Gige;

    private ICameraService Select(CameraBackendKind kind) => kind == CameraBackendKind.Zebra ? _zebra : _gige;

    public Task<IReadOnlyList<GigeDeviceInfo>> DiscoverAsync(string? localInterfaceIp = null, CancellationToken ct = default) =>
        _active.DiscoverAsync(localInterfaceIp, ct);

    public Task ConnectAsync(string cameraIp, string localInterfaceIp, CancellationToken ct = default) =>
        _active.ConnectAsync(cameraIp, localInterfaceIp, ct);

    public Task DisconnectAsync() => _active.DisconnectAsync();

    /// <summary>
    /// A GigE camera in software-trigger mode is "internal trigger" to the camera but "TCP/IP" to
    /// the operator when that is the source they chose, so the two are told apart here. (The Zebra
    /// reports TCP/IP itself.)
    /// </summary>
    public async Task<CameraSettings> GetSettingsAsync()
    {
        var settings = await _active.GetSettingsAsync();
        return Current == CameraBackendKind.Gige && _mode.TcpTrigger
            && settings.TriggerMode == "On" && settings.TriggerSource == "Software"
                ? settings with { TriggerSource = TcpTriggerSource }
                : settings;
    }

    public Task ApplySettingsAsync(CameraSettingsUpdate update)
    {
        // Choosing a trigger is what decides it: an update that does not touch the trigger (a
        // recipe's exposure, the auto-brightness tuner) leaves the choice alone.
        if (Current == CameraBackendKind.Gige && (update.TriggerMode is not null || update.TriggerSource is not null))
        {
            var tcp = update.TriggerMode == "On" && update.TriggerSource == TcpTriggerSource;
            _mode.SetTcpTrigger(tcp);

            // The camera only knows a software trigger; the message that fires it is ours.
            if (tcp)
            {
                update = update with { TriggerSource = "Software" };
            }
        }

        return _active.ApplySettingsAsync(update);
    }

    public Task<CameraFrame> TriggerAsync(CancellationToken ct = default) => _active.TriggerAsync(ct);

    public Task SaveToCameraAsync() => _active.SaveToCameraAsync();

    public Task<CameraIdentity> GetIdentityAsync() => _active.GetIdentityAsync();

    public Task UpdateIdentityAsync(CameraIdentityUpdate update) => _active.UpdateIdentityAsync(update);

    public Task<double> GetFeatureAsync(string featureName) => _active.GetFeatureAsync(featureName);

    public Task SetFeatureAsync(string featureName, double value) => _active.SetFeatureAsync(featureName, value);
}
