namespace MTCVision.Backend.Camera;

/// <summary>
/// Keeps the most recent frame the camera delivered.
///
/// The live stream is fire-and-forget over a WebSocket, but inspection and snapshots need to ask
/// for "the image as of now" at a moment of their own choosing. Holding one frame here decouples
/// the two without buffering a backlog: an inspection always runs on the newest image available.
/// </summary>
public sealed class LatestFrameStore
{
    private CameraFrame? _latest;

    public LatestFrameStore(ICameraService cameraService)
    {
        cameraService.FrameCaptured += (_, frame) => _latest = frame;
    }

    /// <summary>The newest frame, or null if none has arrived since the camera connected.</summary>
    public CameraFrame? Latest => _latest;

    public CameraFrame Require() =>
        _latest ?? throw new InvalidOperationException(
            "Henüz kameradan görüntü alınmadı. Kameraya bağlanın ve yayının başlamasını bekleyin.");

    public void Clear() => _latest = null;
}
