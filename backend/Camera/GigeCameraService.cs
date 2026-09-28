using MTCVision.Backend.Camera.GenICam;

namespace MTCVision.Backend.Camera;

/// <summary>
/// Owns the camera: GVCP control and its GenICam feature tree for settings, and
/// <see cref="GvspStreamReceiver"/> for the image stream.
/// </summary>
public sealed class GigeCameraService : ICameraService, IDisposable
{
    /// <summary>
    /// GVSP stream packet size. The usual "one image row per packet" default assumes jumbo frames;
    /// this NIC is capped at a 1500-byte MTU, where that size produced WSAEMSGSIZE. GVSP spreads a
    /// frame over as many packets as it needs, so a non-jumbo size only costs packet count.
    /// </summary>
    private const uint SafeNonJumboPacketSize = 1444;

    private const uint GevSCPHostPortAddress = 0x0D00;
    private const uint GevSCPSPacketSizeAddress = 0x0D04;
    private const uint GevSCDAAddress = 0x0D18;

    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private GvcpClient? _gvcp;
    private GenApiNodeMap? _nodeMap;
    private GvspStreamReceiver? _receiver;

    public bool IsConnected { get; private set; }
    public string? ConnectedIp { get; private set; }

    public event EventHandler<CameraFrame>? FrameCaptured;
    public event EventHandler<string>? StatusChanged;

    public GvspStatistics? Statistics => _receiver?.GetStatistics();

    /// <summary>
    /// Discovers cameras on one interface, or on every IPv4 interface when none is named. Each
    /// result carries the interface it answered on, which is the one that can stream from it.
    /// </summary>
    public async Task<IReadOnlyList<GigeDeviceInfo>> DiscoverAsync(string? localInterfaceIp = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(localInterfaceIp))
        {
            return await GvcpClient.DiscoverAsync(localInterfaceIp);
        }

        var devices = new List<GigeDeviceInfo>();
        foreach (var candidate in LocalNetworkInterfaces.List().Where(ni => !ni.IsLoopback))
        {
            IReadOnlyList<GigeDeviceInfo> found;
            try
            {
                found = await GvcpClient.DiscoverAsync(candidate.Ip);
            }
            catch (Exception ex)
            {
                // One unusable interface (a disconnected virtual adapter, say) must not stop the scan.
                StatusChanged?.Invoke(this, $"{candidate.Name} ({candidate.Ip}) taranamadı: {ex.Message}");
                continue;
            }

            // Prefer the interface that shares the camera's subnet: a camera on the same wire but a
            // different subnet also answers, and picking that one would break streaming.
            foreach (var device in found)
            {
                var existing = devices.FindIndex(d => d.Ip == device.Ip);
                if (existing < 0)
                {
                    devices.Add(device);
                }
                else if (candidate.Covers(device.Ip))
                {
                    devices[existing] = device;
                }
            }
        }

        return devices;
    }

    public async Task ConnectAsync(string cameraIp, string localInterfaceIp, CancellationToken ct = default)
    {
        RequireInterfaceReachesCamera(cameraIp, localInterfaceIp);

        await _connectionLock.WaitAsync(ct);
        try
        {
            await DisconnectCoreAsync();

            var gvcp = new GvcpClient(cameraIp, localInterfaceIp);
            GvspStreamReceiver? receiver = null;

            try
            {
                gvcp.ReleaseFailed += OnStatus;
                await gvcp.TakeControlAsync();

                var nodeMap = await GenApiNodeMap.LoadAsync(gvcp);
                StatusChanged?.Invoke(this, $"GenICam ağacı yüklendi: {nodeMap.FeatureNames.Count} düğüm ({nodeMap.XmlFileName}).");

                receiver = new GvspStreamReceiver(localInterfaceIp);
                receiver.FrameReady += OnFrameReady;
                receiver.Status += OnStatus;

                await gvcp.WriteRegisterAsync(GevSCPHostPortAddress, (uint)receiver.PortRx);
                await gvcp.WriteRegisterAsync(GevSCDAAddress, IpToNumber(localInterfaceIp));
                await gvcp.WriteRegisterAsync(GevSCPSPacketSizeAddress, SafeNonJumboPacketSize);

                // Listen before acquisition starts so the first block's data leader isn't missed.
                receiver.Start();
                await StartAcquisitionAsync(nodeMap);

                _gvcp = gvcp;
                _nodeMap = nodeMap;
                _receiver = receiver;
                IsConnected = true;
                ConnectedIp = cameraIp;

                StatusChanged?.Invoke(this, $"Yayın başlatıldı (rx port {receiver.PortRx}).");
            }
            catch
            {
                if (receiver is not null)
                {
                    receiver.FrameReady -= OnFrameReady;
                    receiver.Status -= OnStatus;
                    receiver.Dispose();
                }

                await gvcp.ReleaseControlAsync();
                gvcp.ReleaseFailed -= OnStatus;
                gvcp.Dispose();
                throw;
            }
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

    private async Task DisconnectCoreAsync()
    {
        var gvcp = _gvcp;
        var nodeMap = _nodeMap;
        var receiver = _receiver;
        _gvcp = null;
        _nodeMap = null;
        _receiver = null;
        IsConnected = false;
        ConnectedIp = null;

        if (nodeMap is not null)
        {
            try
            {
                await StopAcquisitionAsync(nodeMap);
            }
            catch (Exception ex)
            {
                // Releasing control below stops the stream anyway, once the heartbeat lapses.
                StatusChanged?.Invoke(this, $"AcquisitionStop gönderilemedi: {ex.Message}");
            }
        }

        if (gvcp is not null)
        {
            await gvcp.ReleaseControlAsync();
            gvcp.ReleaseFailed -= OnStatus;
            gvcp.Dispose();
        }

        if (receiver is not null)
        {
            receiver.FrameReady -= OnFrameReady;
            receiver.Status -= OnStatus;
            receiver.Dispose();
        }
    }

    public async Task<CameraSettings> GetSettingsAsync()
    {
        var nodeMap = RequireNodeMap();
        return new CameraSettings(
            ExposureTimeUs: await nodeMap.TryGetValueAsync("ExposureTime"),
            Gain: await nodeMap.TryGetValueAsync("Gain"),
            BlackLevel: await nodeMap.TryGetValueAsync("BlackLevel"),
            FrameRateHz: await nodeMap.TryGetValueAsync("AcquisitionFrameRate"),
            Width: (int?)await nodeMap.TryGetValueAsync("Width"),
            Height: (int?)await nodeMap.TryGetValueAsync("Height"),
            OffsetX: (int?)await nodeMap.TryGetValueAsync("OffsetX"),
            OffsetY: (int?)await nodeMap.TryGetValueAsync("OffsetY"),
            ExposureRange: await TryGetRangeAsync(nodeMap, "ExposureTime"),
            GainRange: await TryGetRangeAsync(nodeMap, "Gain"),
            BlackLevelRange: await TryGetRangeAsync(nodeMap, "BlackLevel"),
            FrameRateRange: await TryGetRangeAsync(nodeMap, "AcquisitionFrameRate"),
            TriggerMode: await nodeMap.TryGetEnumerationAsync("TriggerMode"),
            TriggerSource: await nodeMap.TryGetEnumerationAsync("TriggerSource"),
            TriggerActivation: await nodeMap.TryGetEnumerationAsync("TriggerActivation"),
            TriggerDelayUs: await nodeMap.TryGetValueAsync("TriggerDelay"),
            TriggerDelayRange: await TryGetRangeAsync(nodeMap, "TriggerDelay"),
            AutoBrightness: await nodeMap.TryGetEnumerationAsync("autoBrightnessMode"),
            AutoBrightnessTarget: (int?)await nodeMap.TryGetValueAsync("autoBrightnessTarget"),
            AutoBrightnessTargetRange: await TryGetRangeAsync(nodeMap, "autoBrightnessTarget"),
            ReverseX: await nodeMap.TryGetValueAsync("ReverseX") is { } rx && rx != 0,
            ReverseY: await nodeMap.TryGetValueAsync("ReverseY") is { } ry && ry != 0,
            DeviceTemperatureC: await nodeMap.TryGetValueAsync("DeviceTemperature"));
    }

    private static async Task<FeatureRange?> TryGetRangeAsync(GenApiNodeMap nodeMap, string featureName)
    {
        try
        {
            return await nodeMap.GetRangeAsync(featureName);
        }
        catch (Exception)
        {
            // Limits are advisory here; a feature whose bounds cannot be read is still writable.
            return null;
        }
    }

    public async Task ApplySettingsAsync(CameraSettingsUpdate update)
    {
        var nodeMap = RequireNodeMap();

        // Acquisition parameters are latched when streaming starts: writing AcquisitionFrameRate
        // mid-stream is accepted but leaves the camera emitting empty blocks until acquisition is
        // cycled. So stop, write, and start again — with the stop inside the try, so that a failed
        // write can never leave the camera parked with acquisition off.
        try
        {
            await StopAcquisitionAsync(nodeMap);

            // Frame rate first: it caps how long an exposure the camera will accept, and exposure
            // in turn caps the frame rate, so applying them in this order lets a slow-and-bright
            // combination land in one call.
            if (update.FrameRateHz is not null)
            {
                await nodeMap.SetValueClampedAsync("AcquisitionFrameRate", update.FrameRateHz.Value);
            }

            if (update.ExposureTimeUs is not null)
            {
                await nodeMap.SetValueClampedAsync("ExposureTime", update.ExposureTimeUs.Value);
            }

            if (update.Gain is not null)
            {
                await nodeMap.SetValueClampedAsync("Gain", update.Gain.Value);
            }

            if (update.BlackLevel is not null)
            {
                await nodeMap.SetValueClampedAsync("BlackLevel", update.BlackLevel.Value);
            }

            if (update.AutoBrightnessTarget is not null)
            {
                await nodeMap.SetValueClampedAsync("autoBrightnessTarget", update.AutoBrightnessTarget.Value);
            }

            if (update.AutoBrightness is not null)
            {
                await nodeMap.SetEnumerationAsync("autoBrightnessMode", update.AutoBrightness);
            }

            if (update.ReverseX is not null)
            {
                await nodeMap.SetValueAsync("ReverseX", update.ReverseX.Value ? 1 : 0);
            }

            if (update.ReverseY is not null)
            {
                await nodeMap.SetValueAsync("ReverseY", update.ReverseY.Value ? 1 : 0);
            }

            if (update.TriggerActivation is not null)
            {
                await nodeMap.SetEnumerationAsync("TriggerActivation", update.TriggerActivation);
            }

            if (update.TriggerDelayUs is not null)
            {
                await nodeMap.SetValueClampedAsync("TriggerDelay", update.TriggerDelayUs.Value);
            }

            if (update.TriggerMode is not null || update.TriggerSource is not null)
            {
                // Trigger features are scoped by the selector, so state which trigger is meant
                // before touching them. On a camera that offers only one, the selector is a fixed
                // value in the XML and there is nothing to scope.
                try
                {
                    await nodeMap.SetEnumerationAsync("TriggerSelector", "FrameStart");
                }
                catch (GenApiReadOnlyException)
                {
                }

                if (update.TriggerSource is not null)
                {
                    await nodeMap.SetEnumerationAsync("TriggerSource", update.TriggerSource);
                }

                if (update.TriggerMode is not null)
                {
                    await nodeMap.SetEnumerationAsync("TriggerMode", update.TriggerMode);
                }
            }
        }
        finally
        {
            await StartAcquisitionAsync(nodeMap);
        }
    }

    private static Task StopAcquisitionAsync(GenApiNodeMap nodeMap) =>
        RunAcquisitionCommandAsync(nodeMap, "AcquisitionStop");

    private static Task StartAcquisitionAsync(GenApiNodeMap nodeMap) =>
        RunAcquisitionCommandAsync(nodeMap, "AcquisitionStart");

    /// <summary>
    /// AcquisitionStop only takes effect at the end of the frame in flight, and while the camera is
    /// finishing that frame it refuses AcquisitionStart outright. With a long exposure that window
    /// is most of a second, so retry across it instead of failing the whole settings change.
    /// </summary>
    private static async Task RunAcquisitionCommandAsync(GenApiNodeMap nodeMap, string commandName)
    {
        var giveUpAt = DateTime.UtcNow + await FrameSettlingTimeAsync(nodeMap);
        while (true)
        {
            try
            {
                await nodeMap.ExecuteAsync(commandName);
                return;
            }
            catch (GenApiException) when (DateTime.UtcNow < giveUpAt)
            {
                await Task.Delay(150);
            }
        }
    }

    /// <summary>How long to allow for the frame in flight to finish, from the camera's own timing.</summary>
    private static async Task<TimeSpan> FrameSettlingTimeAsync(GenApiNodeMap nodeMap)
    {
        var exposureUs = await nodeMap.TryGetValueAsync("ExposureTime") ?? 0;
        var frameRateHz = await nodeMap.TryGetValueAsync("AcquisitionFrameRate") ?? 0;
        var framePeriod = frameRateHz > 0 ? 1000.0 / frameRateHz : 0;

        var settling = TimeSpan.FromMilliseconds(2 * (exposureUs / 1000.0 + framePeriod) + 1000);
        return TimeSpan.FromSeconds(Math.Clamp(settling.TotalSeconds, 3, 20));
    }

    /// <summary>
    /// Fires a software trigger and returns the frame it produces.
    ///
    /// This is the production shape of acquisition: with TriggerMode On the camera sits idle and
    /// exposes exactly one frame per trigger, so an inspection judges the part that was in front of
    /// the camera at that moment rather than whichever free-running frame happened to be newest.
    /// </summary>
    public async Task<CameraFrame> TriggerAsync(CancellationToken ct = default)
    {
        var nodeMap = RequireNodeMap();

        var completion = new TaskCompletionSource<CameraFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? sender, CameraFrame frame) => completion.TrySetResult(frame);

        // Subscribe before triggering: at a short exposure the frame can arrive before the write
        // to TriggerSoftware has even returned.
        FrameCaptured += OnFrame;
        try
        {
            await nodeMap.ExecuteAsync("TriggerSoftware");
            return await completion.Task.WaitAsync(await TriggerTimeoutAsync(nodeMap), ct);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                "Tetiklemeden sonra kare gelmedi. Tetikleme modu açık mı ve tetikleme kaynağı 'Software' mı?");
        }
        finally
        {
            FrameCaptured -= OnFrame;
        }
    }

    /// <summary>
    /// How long a triggered frame may reasonably take: the exposure, plus the time to move a full
    /// frame over a link this camera has already shown us the speed of, plus slack.
    /// </summary>
    private static async Task<TimeSpan> TriggerTimeoutAsync(GenApiNodeMap nodeMap)
    {
        var exposureMs = (await nodeMap.TryGetValueAsync("ExposureTime") ?? 0) / 1000.0;
        return TimeSpan.FromMilliseconds(Math.Clamp(exposureMs * 2 + 5000, 5000, 40000));
    }

    public async Task<CameraIdentity> GetIdentityAsync()
    {
        var nodeMap = RequireNodeMap();
        var gvcp = _gvcp ?? throw new InvalidOperationException("Kamera bağlı değil.");

        async Task<string?> AddressAsync(string feature) =>
            await nodeMap.TryGetValueAsync(feature) is { } value ? IpAddressNumber.ToDotted(value) : null;

        return new CameraIdentity(
            UserId: await nodeMap.TryGetStringAsync("DeviceUserID"),
            ManufacturerName: await nodeMap.TryGetStringAsync("DeviceVendorName"),
            ModelName: await nodeMap.TryGetStringAsync("DeviceModelName"),
            SerialNumber: await nodeMap.TryGetStringAsync("DeviceSerialNumber"),
            DeviceVersion: await nodeMap.TryGetStringAsync("DeviceVersion"),
            IpConfiguration: await nodeMap.TryGetEnumerationAsync("GevCurrentIPConfiguration"),
            PersistentIp: await AddressAsync("GevPersistentIPAddress"),
            PersistentSubnetMask: await AddressAsync("GevPersistentSubnetMask"),
            PersistentGateway: await AddressAsync("GevPersistentDefaultGateway"),
            CurrentIp: ConnectedIp,
            Mac: await ReadMacAsync(gvcp));
    }

    /// <summary>
    /// MAC address from the bootstrap registers: the low 16 bits of 0x0008 followed by 0x000C.
    /// Read here rather than kept from discovery so it survives a reconnect.
    /// </summary>
    private static async Task<string?> ReadMacAsync(GvcpClient gvcp)
    {
        try
        {
            var high = await gvcp.ReadRegisterAsync(0x0008);
            var low = await gvcp.ReadRegisterAsync(0x000C);
            var bytes = new[]
            {
                (byte)(high >> 8), (byte)high,
                (byte)(low >> 24), (byte)(low >> 16), (byte)(low >> 8), (byte)low,
            };

            return string.Join(':', bytes.Select(b => b.ToString("X2")));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Renames the camera and/or rewrites its boot-time addressing.
    ///
    /// The name takes effect at once. The address does not: GigE Vision has no way to move a camera
    /// to a new address while it is streaming, so the persistent settings are written and the camera
    /// picks them up on its next power cycle. The caller has to say so.
    /// </summary>
    public async Task UpdateIdentityAsync(CameraIdentityUpdate update)
    {
        var nodeMap = RequireNodeMap();

        if (update.UserId is not null)
        {
            if (update.UserId.Length > 15)
            {
                throw new CameraConfigurationException("Kamera adı en fazla 15 karakter olabilir.");
            }

            await nodeMap.SetStringAsync("DeviceUserID", update.UserId);
        }

        if (update.PersistentIp is not null)
        {
            await nodeMap.SetValueAsync("GevPersistentIPAddress", IpAddressNumber.FromDotted(update.PersistentIp));
        }

        if (update.PersistentSubnetMask is not null)
        {
            await nodeMap.SetValueAsync("GevPersistentSubnetMask", IpAddressNumber.FromDotted(update.PersistentSubnetMask));
        }

        if (update.PersistentGateway is not null)
        {
            await nodeMap.SetValueAsync("GevPersistentDefaultGateway", IpAddressNumber.FromDotted(update.PersistentGateway));
        }

        // Written last: switching the camera to PersistentIP before the address is in place would
        // point it at whatever the old persistent value happened to be.
        if (update.IpConfiguration is not null)
        {
            await nodeMap.SetEnumerationAsync("GevCurrentIPConfiguration", update.IpConfiguration);
        }
    }

    public async Task SaveToCameraAsync()
    {
        var nodeMap = RequireNodeMap();

        // UserSetSelector picks which of the camera's stored sets is written; user set 1 is the
        // one it can be told to load at power-up.
        try
        {
            await nodeMap.SetEnumerationAsync("UserSetSelector", "UserSet1");
        }
        catch (Exception ex) when (ex is KeyNotFoundException or GenApiReadOnlyException)
        {
            // A camera with a single writable set has nothing to select.
        }

        await nodeMap.ExecuteAsync("UserSetSave");
    }

    /// <summary>
    /// Reads any GenICam feature by name — the escape hatch for tuning and diagnostics. Unlike the
    /// settings summary this surfaces the failure instead of reporting the feature as absent.
    /// </summary>
    public async Task<double> GetFeatureAsync(string featureName) =>
        await RequireNodeMap().GetValueAsync(featureName);

    public async Task SetFeatureAsync(string featureName, double value) =>
        await RequireNodeMap().SetValueAsync(featureName, value);

    /// <summary>
    /// The camera streams to whatever address we hand it, so an interface outside the camera's
    /// subnet leaves it with no route back and the connect dies on a GVCP timeout with nothing to
    /// explain why. Catch that here, where we can say what to do instead.
    /// </summary>
    private static void RequireInterfaceReachesCamera(string cameraIp, string localInterfaceIp)
    {
        var selected = LocalNetworkInterfaces.List().FirstOrDefault(ni => ni.Ip == localInterfaceIp);
        if (selected is null)
        {
            throw new CameraConfigurationException($"'{localInterfaceIp}' adresli bir ağ arayüzü bulunamadı.");
        }

        if (selected.Covers(cameraIp))
        {
            return;
        }

        var usable = LocalNetworkInterfaces.ReachingSubnetOf(cameraIp);
        var suggestion = usable.Count > 0
            ? $"Bunun yerine {string.Join(" veya ", usable.Select(ni => $"{ni.Ip} ({ni.Name})"))} kullanın."
            : $"Bu makinede {cameraIp} ile aynı alt ağda bir adres yok; kameranın alt ağında ikincil bir IP tanımlayın.";

        throw new CameraConfigurationException(
            $"Seçilen ağ arayüzü {localInterfaceIp} ile kamera {cameraIp} aynı alt ağda değil, " +
            $"bu yüzden kamera görüntüyü geri gönderemez. {suggestion}");
    }

    private GenApiNodeMap RequireNodeMap() =>
        _nodeMap ?? throw new InvalidOperationException("Kamera bağlı değil.");

    /// <summary>GevSCDA wants the destination IP as a big-endian 32-bit number.</summary>
    private static uint IpToNumber(string ip)
    {
        var octets = System.Net.IPAddress.Parse(ip).GetAddressBytes();
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }

    private void OnFrameReady(object? sender, CameraFrame frame) => FrameCaptured?.Invoke(this, frame);

    private void OnStatus(object? sender, string message) => StatusChanged?.Invoke(this, message);

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _connectionLock.Dispose();
    }
}

public sealed record CameraSettings(
    double? ExposureTimeUs,
    double? Gain,
    double? BlackLevel,
    double? FrameRateHz,
    int? Width,
    int? Height,
    int? OffsetX,
    int? OffsetY,
    FeatureRange? ExposureRange,
    FeatureRange? GainRange,
    FeatureRange? BlackLevelRange,
    FeatureRange? FrameRateRange,
    /// <summary>"On" while the camera waits for a trigger, "Off" while it free-runs.</summary>
    string? TriggerMode,
    /// <summary>GenICam trigger source name, e.g. "Software" or "Line1".</summary>
    string? TriggerSource,
    /// <summary>Which edge or level of a hardware trigger line starts a frame.</summary>
    string? TriggerActivation,
    double? TriggerDelayUs,
    FeatureRange? TriggerDelayRange,
    /// <summary>The camera's own brightness regulation: "Active" or "Off".</summary>
    string? AutoBrightness,
    int? AutoBrightnessTarget,
    FeatureRange? AutoBrightnessTargetRange,
    bool ReverseX,
    bool ReverseY,
    double? DeviceTemperatureC);

/// <summary>A request that cannot work as asked, rejected before the camera is touched.</summary>
public sealed class CameraConfigurationException(string message) : Exception(message);

public sealed record CameraSettingsUpdate(
    double? ExposureTimeUs = null,
    double? Gain = null,
    double? BlackLevel = null,
    double? FrameRateHz = null,
    string? TriggerMode = null,
    string? TriggerSource = null,
    string? TriggerActivation = null,
    double? TriggerDelayUs = null,
    string? AutoBrightness = null,
    int? AutoBrightnessTarget = null,
    bool? ReverseX = null,
    bool? ReverseY = null);
