using MTCVision.Backend.Camera.GenICam;

namespace MTCVision.Backend.Camera;

public interface ICameraService
{
    bool IsConnected { get; }
    string? ConnectedIp { get; }

    /// <summary>Live reception counters, or null while disconnected.</summary>
    GvspStatistics? Statistics { get; }

    event EventHandler<CameraFrame>? FrameCaptured;
    event EventHandler<string>? StatusChanged;

    /// <summary>Scans one interface, or every IPv4 interface when <paramref name="localInterfaceIp"/> is null.</summary>
    Task<IReadOnlyList<GigeDeviceInfo>> DiscoverAsync(string? localInterfaceIp = null, CancellationToken ct = default);

    /// <summary>
    /// Connects and starts streaming. Resolution and pixel format are not parameters: they are read
    /// from each GVSP block's data leader, which is where the camera states them authoritatively.
    /// </summary>
    Task ConnectAsync(string cameraIp, string localInterfaceIp, CancellationToken ct = default);

    Task DisconnectAsync();

    Task<CameraSettings> GetSettingsAsync();

    Task ApplySettingsAsync(CameraSettingsUpdate update);

    /// <summary>Fires a software trigger and returns the single frame it produces.</summary>
    Task<CameraFrame> TriggerAsync(CancellationToken ct = default);


    /// <summary>Writes the camera's current settings into its own non-volatile user set.</summary>
    Task SaveToCameraAsync();

    /// <summary>The camera's name, model and boot-time network addressing.</summary>
    Task<CameraIdentity> GetIdentityAsync();

    /// <summary>Renames the camera and/or rewrites its persistent network settings.</summary>
    Task UpdateIdentityAsync(CameraIdentityUpdate update);

    Task<double> GetFeatureAsync(string featureName);

    Task SetFeatureAsync(string featureName, double value);
}
