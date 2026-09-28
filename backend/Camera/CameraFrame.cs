namespace MTCVision.Backend.Camera;

public sealed record CameraFrame(byte[] Data, int Width, int Height, GvspPixelFormat PixelFormat, DateTime TimestampUtc);
