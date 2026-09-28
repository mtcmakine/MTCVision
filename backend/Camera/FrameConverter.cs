using OpenCvSharp;

namespace MTCVision.Backend.Camera;

/// <summary>
/// Turns a raw camera frame into an 8-bit grayscale <see cref="Mat"/>.
///
/// Both the live JPEG stream and the inspection tools need the same view of a frame, and they must
/// agree: a measurement taught against what the operator saw on screen has to be taken on the same
/// pixels.
/// </summary>
public static class FrameConverter
{
    public static Mat ToGrayscaleMat(CameraFrame frame)
    {
        switch (frame.PixelFormat)
        {
            case GvspPixelFormat.Mono8:
                return Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC1, frame.Data, 0);

            case GvspPixelFormat.Mono16:
            case GvspPixelFormat.Mono14:
            case GvspPixelFormat.Mono12:
            case GvspPixelFormat.Mono10:
            {
                using var deep = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_16UC1, frame.Data, 0);
                var eightBit = new Mat();
                deep.ConvertTo(eightBit, MatType.CV_8UC1, 1.0 / 256.0, 0);
                return eightBit;
            }

            default:
                throw new NotSupportedException(
                    $"Piksel formatı henüz desteklenmiyor: {frame.PixelFormat}. Kamerayı Mono8 formatına ayarlayın.");
        }
    }
}
