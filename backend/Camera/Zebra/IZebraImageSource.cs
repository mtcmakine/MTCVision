namespace MTCVision.Backend.Camera.Zebra;

/// <summary>Where the images from a triggered Zebra job come from — the scanner's HMI channel or an FTP push.</summary>
public interface IZebraImageSource
{
    event EventHandler<CameraFrame>? ImageReceived;

    event EventHandler<string>? Status;

    /// <summary>Begins listening for the scanner at <paramref name="deviceIp"/>; called when the camera is connected.</summary>
    void Start(string deviceIp);

    /// <summary>Stops listening; called when the camera is disconnected.</summary>
    void Stop();
}
