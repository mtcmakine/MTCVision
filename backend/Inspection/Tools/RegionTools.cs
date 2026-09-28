using MTCVision.Backend.Recipes;
using OpenCvSharp;

namespace MTCVision.Backend.Inspection.Tools;

/// <summary>
/// The tools that answer a question about the window as a whole rather than about a line across it:
/// how much of it is a given tone, how many separate marks it holds, how round the roundest thing
/// in it is, and how closely its outlines match the master's.
/// </summary>
public static class RegionTools
{
    /// <summary>
    /// Share of the measured window that falls inside the grey range, 0-100.
    ///
    /// A share rather than a pixel count, so a limit survives the window being redrawn slightly
    /// larger - which it will be, the first time someone nudges it.
    /// </summary>
    public static double AreaPercent(Mat roi, Mat? mask, ToolOptions options)
    {
        using var selected = InRange(roi, mask, options);
        var measured = mask is null ? roi.Rows * roi.Cols : Cv2.CountNonZero(mask);
        return measured > 0 ? 100.0 * Cv2.CountNonZero(selected) / measured : 0;
    }

    /// <summary>
    /// How many separate marks in the grey range the window holds.
    ///
    /// Marks smaller than the noise floor are dropped, and - by default - so are marks touching the
    /// window border: one cut by the edge has an arbitrary size, and counting it makes the answer
    /// depend on exactly where the window was drawn rather than on the part.
    /// </summary>
    public static double BlobCount(Mat roi, Mat? mask, ToolOptions options)
    {
        using var selected = InRange(roi, mask, options);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();

        var found = Cv2.ConnectedComponentsWithStats(selected, labels, stats, centroids, PixelConnectivity.Connectivity8);

        var counted = 0;
        for (var label = 1; label < found; label++)
        {
            var area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            if (area < Math.Max(1, options.MinBlobArea))
            {
                continue;
            }

            if (options.ExcludeBoundaryBlobs)
            {
                var left = stats.At<int>(label, (int)ConnectedComponentsTypes.Left);
                var top = stats.At<int>(label, (int)ConnectedComponentsTypes.Top);
                var width = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
                var height = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);

                if (left <= 0 || top <= 0 || left + width >= roi.Cols || top + height >= roi.Rows)
                {
                    continue;
                }
            }

            counted++;
        }

        return counted;
    }

    /// <summary>
    /// Diameter of the strongest circle in the window, in pixels, or zero if there is none.
    ///
    /// Blurred first: the gradient vote behind circle detection is thrown by sensor noise, and this
    /// camera runs at a gain where there is plenty of it.
    /// </summary>
    public static double DiameterPixels(Mat roi, ToolOptions options)
    {
        using var smoothed = new Mat();
        Cv2.MedianBlur(roi, smoothed, 5);

        var shortest = Math.Min(roi.Rows, roi.Cols);
        var minRadius = options.MinRadius > 0 ? options.MinRadius : Math.Max(3, shortest / 12);
        var maxRadius = options.MaxRadius > 0 ? options.MaxRadius : Math.Max(minRadius + 1, shortest / 2);

        // The accumulator threshold follows the edge sensitivity, so one slider governs "how sure
        // do I have to be" for every tool that hunts edges.
        var votes = Math.Clamp(options.EdgeSensitivity * 2.0, 10, 200);

        var circles = Cv2.HoughCircles(
            smoothed,
            HoughModes.Gradient,
            dp: 1,
            minDist: Math.Max(8, shortest / 4.0),
            param1: 120,
            param2: votes,
            minRadius: minRadius,
            maxRadius: maxRadius);

        // HoughCircles returns its candidates strongest first.
        return circles.Length > 0 ? circles[0].Radius * 2.0 : 0;
    }

    /// <summary>
    /// How closely the window's outlines match the master's, 0-100.
    ///
    /// Outlines, not brightness: both images are reduced to their edges first, so a part that is
    /// simply lit differently still matches while one of the wrong shape does not - which is the
    /// whole point, because brightness and area both pass a part of the right size in the wrong
    /// shape.
    ///
    /// The master patch is trimmed slightly and slid over the live one, which absorbs a pixel or
    /// two of residual misalignment. Anything larger than that is the position adjustment's job,
    /// not this tool's, and a match that has to search far to succeed is not a match.
    /// </summary>
    public static double OutlineMatch(Mat roi, Mat masterRoi, ToolOptions options)
    {
        const int slack = 4;

        if (masterRoi.Rows <= 2 * slack + 4 || masterRoi.Cols <= 2 * slack + 4)
        {
            return 0;
        }

        var lower = Math.Clamp(options.EdgeSensitivity * 4, 20, 200);

        using var liveEdges = new Mat();
        using var masterEdges = new Mat();
        Cv2.Canny(roi, liveEdges, lower, lower * 2);
        Cv2.Canny(masterRoi, masterEdges, lower, lower * 2);

        // Softened, so two edges a pixel apart still score as near-agreement instead of as a total
        // miss - a hard edge map scores itself perfectly and everything else at nothing.
        using var liveSoft = new Mat();
        using var masterSoft = new Mat();
        Cv2.GaussianBlur(liveEdges, liveSoft, new Size(5, 5), 0);
        Cv2.GaussianBlur(masterEdges, masterSoft, new Size(5, 5), 0);

        using var template = new Mat(
            masterSoft,
            new Rect(slack, slack, masterSoft.Cols - 2 * slack, masterSoft.Rows - 2 * slack));

        using var scores = new Mat();
        Cv2.MatchTemplate(liveSoft, template, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out double _, out double best);

        return Math.Clamp(best * 100, 0, 100);
    }

    /// <summary>The window reduced to "in the chosen grey range, and not masked out".</summary>
    private static Mat InRange(Mat roi, Mat? mask, ToolOptions options)
    {
        var lower = Math.Clamp(Math.Min(options.LowerGrey, options.UpperGrey), 0, 255);
        var upper = Math.Clamp(Math.Max(options.LowerGrey, options.UpperGrey), 0, 255);

        var selected = new Mat();
        Cv2.InRange(roi, lower, upper, selected);

        if (mask is not null)
        {
            Cv2.BitwiseAnd(selected, mask, selected);
        }

        return selected;
    }
}
