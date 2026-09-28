using MTCVision.Backend.Recipes;
using OpenCvSharp;

namespace MTCVision.Backend.Inspection.Tools;

/// <summary>
/// Turns a window into a one-dimensional brightness profile across the scan direction, and finds
/// the edges in it. The edge, width and pitch tools are all this plus a different question.
///
/// Averaging down to a profile before looking for edges is what makes these tools steady. Hunting
/// edges in the two-dimensional image finds every speck of sensor noise; averaging a few hundred
/// pixels perpendicular to the scan drops that noise by the square root of the count while leaving
/// a real edge - which runs across the whole window - exactly as sharp as it was.
/// </summary>
public static class EdgeProfile
{
    /// <summary>
    /// Mean grey along each step of the scan, in scan order. Masked-out pixels take no part; a
    /// column that is entirely masked carries its neighbour's value rather than a hole.
    /// </summary>
    public static double[] Build(Mat roi, Mat? mask, ScanDirection direction)
    {
        var horizontal = direction is ScanDirection.LeftToRight or ScanDirection.RightToLeft;

        using var values = new Mat();
        roi.ConvertTo(values, MatType.CV_32F);

        using var weight = new Mat(roi.Rows, roi.Cols, MatType.CV_32F, Scalar.All(1));
        mask?.ConvertTo(weight, MatType.CV_32F, 1.0 / 255.0);

        using var weighted = values.Mul(weight);

        // Reduce along the axis perpendicular to the scan: for a left-to-right scan that is down
        // the columns, leaving one number per column.
        var axis = horizontal ? ReduceDimension.Row : ReduceDimension.Column;
        using var sums = new Mat();
        using var counts = new Mat();
        Cv2.Reduce(weighted, sums, axis, ReduceTypes.Sum, MatType.CV_32F);
        Cv2.Reduce(weight, counts, axis, ReduceTypes.Sum, MatType.CV_32F);

        var length = horizontal ? roi.Cols : roi.Rows;
        var profile = new double[length];
        var previous = 0.0;

        for (var i = 0; i < length; i++)
        {
            var sum = horizontal ? sums.At<float>(0, i) : sums.At<float>(i, 0);
            var count = horizontal ? counts.At<float>(0, i) : counts.At<float>(i, 0);
            previous = count > 0.5f ? sum / count : previous;
            profile[i] = previous;
        }

        if (direction is ScanDirection.RightToLeft or ScanDirection.BottomToTop)
        {
            Array.Reverse(profile);
        }

        return profile;
    }

    /// <summary>
    /// Positions along the profile where the brightness changes fast enough to count as an edge.
    ///
    /// The position is refined between samples by fitting a parabola to the gradient peak: a real
    /// edge rarely lands on a whole pixel, and a width that jumps a pixel at a time cannot hold a
    /// tolerance tighter than a pixel.
    /// </summary>
    public static List<EdgeHit> Find(double[] profile, double sensitivity)
    {
        var hits = new List<EdgeHit>();
        if (profile.Length < 5)
        {
            return hits;
        }

        var gradient = new double[profile.Length];
        for (var i = 1; i < profile.Length - 1; i++)
        {
            gradient[i] = (profile[i + 1] - profile[i - 1]) / 2.0;
        }

        var threshold = Math.Max(0.5, sensitivity);
        for (var i = 2; i < gradient.Length - 2; i++)
        {
            var here = Math.Abs(gradient[i]);
            if (here < threshold)
            {
                continue;
            }

            // Only the peak of a run counts, or a single soft edge is reported several times.
            if (here < Math.Abs(gradient[i - 1]) || here <= Math.Abs(gradient[i + 1]))
            {
                continue;
            }

            var left = Math.Abs(gradient[i - 1]);
            var right = Math.Abs(gradient[i + 1]);
            var denominator = left - 2 * here + right;
            var offset = Math.Abs(denominator) > 1e-6 ? 0.5 * (left - right) / denominator : 0.0;

            hits.Add(new EdgeHit(i + Math.Clamp(offset, -1, 1), gradient[i]));
        }

        return hits;
    }
}

/// <summary>An edge found in the profile: where it is, and which way the brightness went.</summary>
public sealed record EdgeHit(double Position, double Gradient)
{
    public bool RisingIntoLight => Gradient > 0;
}
