using MTCVision.Backend.Recipes;
using OpenCvSharp;

namespace MTCVision.Backend.Camera;

/// <summary>Whole-frame and region measurements used outside the inspection tools.</summary>
public static class ImageStatistics
{
    public static double MeanGrey(CameraFrame frame)
    {
        using var image = FrameConverter.ToGrayscaleMat(frame);
        return Cv2.Mean(image).Val0;
    }

    /// <summary>
    /// Focus score for a region: the variance of the Laplacian, which rises as edges sharpen.
    ///
    /// This camera takes an ordinary manual C-mount lens, so there is no focus to command — the
    /// operator turns the ring. What software can do is give them a number to maximise, which is
    /// what this is: meaningful only relative to itself, on one scene, at one working distance.
    /// </summary>
    public static FocusScore Focus(CameraFrame frame, Roi? region)
    {
        using var image = FrameConverter.ToGrayscaleMat(frame);

        var area = (region ?? new Roi(0, 0, frame.Width, frame.Height)).ClampTo(image.Width, image.Height);
        if (!area.IsValid)
        {
            throw new ArgumentException("Netlik bölgesi görüntünün dışında kalıyor.", nameof(region));
        }

        using var roi = new Mat(image, new Rect(area.X, area.Y, area.Width, area.Height));

        // Denoise first, or the number measures sensor noise instead of focus: the same defocused
        // scene scored 6924 at gain 135x and 883 at gain 17x on this camera, so an operator
        // focusing by the raw figure would be rewarded for turning the gain up. A median filter
        // rather than a blur, because it removes the single-pixel shot noise high gain produces
        // while leaving the character edges that focus is actually about.
        using var denoised = new Mat();
        Cv2.MedianBlur(roi, denoised, 3);

        using var laplacian = new Mat();
        Cv2.Laplacian(denoised, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out Scalar _, out Scalar standardDeviation);

        Cv2.MeanStdDev(roi, out Scalar mean, out Scalar contrast);

        return new FocusScore(
            Region: area,
            Sharpness: Math.Round(standardDeviation.Val0 * standardDeviation.Val0, 1),
            Brightness: Math.Round(mean.Val0, 1),
            Contrast: Math.Round(contrast.Val0, 1));
    }
}

public sealed record FocusScore(Roi Region, double Sharpness, double Brightness, double Contrast);
