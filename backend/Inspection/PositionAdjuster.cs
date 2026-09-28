using MTCVision.Backend.Recipes;
using OpenCvSharp;

namespace MTCVision.Backend.Inspection;

/// <summary>
/// Finds the taught pattern in the current frame and reports how far the part has moved.
///
/// This is the piece that lets limits stay tight. A part that arrives a few pixels off its taught
/// position drags every window off the feature it was measuring, and the usual response - widening
/// the limits until the line stops stopping - throws away the inspection. Moving the windows by the
/// measured shift keeps each window over its own feature and leaves the limits meaning what they
/// were set to mean.
///
/// Translation only, for now. Normalised cross-correlation finds a shifted pattern reliably and
/// cheaply; a rotated one needs a search over angles, which is a different tool and a different
/// cost. A part that rotates is reported as not found rather than silently measured askew.
/// </summary>
public sealed class PositionAdjuster(MasterImageStore masters)
{
    public PositionFix Locate(Recipe recipe, Mat image)
    {
        var settings = recipe.Position;
        if (!settings.Enabled)
        {
            return PositionFix.Off;
        }

        if (settings.Pattern is not { } pattern || !pattern.IsValid)
        {
            return PositionFix.NotFound("Konum düzeltme açık ama desen seçilmemiş.");
        }

        var master = masters.Load(recipe.Id);
        if (master is null)
        {
            return PositionFix.NotFound("Konum düzeltme için master görüntü kayıtlı değil.");
        }

        if (master.Width != image.Width || master.Height != image.Height)
        {
            return PositionFix.NotFound(
                $"Master görüntü {master.Width}x{master.Height}, kare {image.Width}x{image.Height} — " +
                "aynı çözünürlükte olmalı.");
        }

        var patternRect = ToRect(pattern.ClampTo(master.Width, master.Height));
        if (patternRect.Width < 8 || patternRect.Height < 8)
        {
            return PositionFix.NotFound("Konum deseni çok küçük (en az 8x8 piksel).");
        }

        // The search area is the taught position opened out by the margin. Searching the whole
        // frame would be slower and would happily match a repeating feature somewhere else.
        var margin = Math.Max(0, settings.SearchMargin);
        var search = Rect.Intersect(
            new Rect(
                patternRect.X - margin,
                patternRect.Y - margin,
                patternRect.Width + 2 * margin,
                patternRect.Height + 2 * margin),
            new Rect(0, 0, image.Width, image.Height));

        if (search.Width < patternRect.Width || search.Height < patternRect.Height)
        {
            return PositionFix.NotFound("Arama alanı desenden küçük kaldı.");
        }

        using var template = new Mat(master, patternRect);
        using var haystack = new Mat(image, search);
        using var scores = new Mat();

        // Normalised correlation, so a part that is simply lit a little brighter than the master
        // still matches: the alternative scores brightness as if it were shape.
        Cv2.MatchTemplate(haystack, template, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out _, out var best, out _, out var bestAt);

        var score = Math.Round(best * 100, 2);
        if (score < settings.MinScore)
        {
            return new PositionFix(false, true, 0, 0, score,
                $"Konum deseni bulunamadı (eşleşme %{score:0.#}, eşik %{settings.MinScore:0.#}).");
        }

        return new PositionFix(
            true,
            false,
            search.X + bestAt.X - patternRect.X,
            search.Y + bestAt.Y - patternRect.Y,
            score,
            null);
    }

    private static Rect ToRect(Roi roi) => new(roi.X, roi.Y, roi.Width, roi.Height);
}

/// <summary>
/// Where the part turned out to be. <see cref="Failed"/> is not the same as not applied: the first
/// means the inspection cannot be trusted and the part is NG, the second that the recipe never
/// asked for position adjustment at all.
/// </summary>
public sealed record PositionFix(
    bool Applied,
    bool Failed,
    int OffsetX,
    int OffsetY,
    double Score,
    string? Message)
{
    public static readonly PositionFix Off = new(false, false, 0, 0, 0, null);

    /// <summary>Named for the reason rather than the flag, so call sites read as English.</summary>
    public static PositionFix NotFound(string message) => new(false, true, 0, 0, 0, message);

    public Roi Move(Roi region) =>
        Applied ? region with { X = region.X + OffsetX, Y = region.Y + OffsetY } : region;
}
