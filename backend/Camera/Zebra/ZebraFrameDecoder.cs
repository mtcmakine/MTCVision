using System.Runtime.InteropServices;
using OpenCvSharp;

namespace MTCVision.Backend.Camera.Zebra;

/// <summary>
/// Turns an encoded image from the scanner (JPEG, BMP or PNG) into the 8-bit grayscale frame the
/// rest of the app works on: <see cref="FrameConverter"/> and the inspection tools only know
/// single-channel Mono formats.
/// </summary>
public static class ZebraFrameDecoder
{
    public static CameraFrame? Decode(byte[] encoded)
    {
        using var gray = Cv2.ImDecode(encoded, ImreadModes.Grayscale);
        if (gray.Empty())
        {
            return null;
        }

        var pixels = new byte[gray.Width * gray.Height];
        if (gray.IsContinuous())
        {
            Marshal.Copy(gray.Data, pixels, 0, pixels.Length);
        }
        else
        {
            using var contiguous = gray.Clone();
            Marshal.Copy(contiguous.Data, pixels, 0, pixels.Length);
        }

        return new CameraFrame(pixels, gray.Width, gray.Height, GvspPixelFormat.Mono8, DateTime.UtcNow);
    }
}
