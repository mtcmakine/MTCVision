using OpenCvSharp;

namespace MTCVision.Backend.Camera;

public static class FrameJpegEncoder
{
    public static byte[] Encode(CameraFrame frame, int quality = 80)
    {
        using var image = FrameConverter.ToGrayscaleMat(frame);
        Cv2.ImEncode(".jpg", image, out var jpegBytes, [new ImageEncodingParam(ImwriteFlags.JpegQuality, quality)]);
        return jpegBytes;
    }
}
