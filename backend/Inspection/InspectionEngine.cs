using MTCVision.Backend.Camera;
using MTCVision.Backend.Inspection.Tools;
using MTCVision.Backend.Recipes;
using MTCVision.Backend.Vision;
using OpenCvSharp;

namespace MTCVision.Backend.Inspection;

/// <summary>
/// Runs a recipe's windows against one frame and judges each against its limits.
///
/// Every tool reduces its window to a single number, which is then compared to a lower and an upper
/// limit. Keeping that shape uniform is what lets the UI show one "measured value vs limits" row
/// per window, and lets OCR and anomaly scoring slot in later without changing the result model.
/// </summary>
public sealed class InspectionEngine(OcrEngine ocr, PositionAdjuster positions, MasterImageStore masters)
{
    public InspectionResult Run(Recipe recipe, CameraFrame frame)
    {
        using var image = FrameConverter.ToGrayscaleMat(frame);
        return Run(recipe, image, frame.TimestampUtc);
    }

    /// <summary>
    /// Judges an already-decoded image. This is what lets a stored JPEG be re-judged against an
    /// edited recipe without the camera being present — the limits are checked against the same
    /// pixels the operator saw when the image was taken.
    /// </summary>
    public InspectionResult Run(Recipe recipe, Mat image, DateTime frameTimestampUtc)
    {
        // Where the part is, before anything is measured: every window is placed relative to this.
        var fix = positions.Locate(recipe, image);

        // Loaded once per frame, not once per window: it is shared, cached, and the outline tool is
        // the only thing that wants it.
        var master = recipe.Windows.Any(window => window.Tool == InspectionToolKind.Outline)
            ? masters.Load(recipe.Id)
            : null;

        var results = new List<WindowResult>(recipe.Windows.Count);
        foreach (var window in recipe.Windows)
        {
            results.Add(Evaluate(window, image, fix, master));
        }

        return new InspectionResult
        {
            RecipeId = recipe.Id,
            RecipeName = recipe.Name,
            TimestampUtc = DateTime.UtcNow,
            FrameTimestampUtc = frameTimestampUtc,
            FrameWidth = image.Width,
            FrameHeight = image.Height,
            // A part that could not be located was not inspected. Reporting the window results as
            // if they meant something would be worse than reporting nothing: they were measured in
            // the wrong place.
            Passed = !fix.Failed && Judge(recipe, results),
            Windows = results,
            PositionApplied = fix.Applied,
            PositionOffsetX = fix.OffsetX,
            PositionOffsetY = fix.OffsetY,
            PositionScore = fix.Score,
            Message = fix.Message,
        };
    }

    /// <summary>
    /// Which windows have to pass. A window can be measured and reported without being allowed to
    /// stop the line, and a recipe can ask for any one of its windows rather than all of them.
    /// </summary>
    private static bool Judge(Recipe recipe, IReadOnlyList<WindowResult> results)
    {
        var counted = results.Where(result => result.AffectsTotal).ToList();
        if (counted.Count == 0)
        {
            // Nothing counted: an empty recipe passes, and a recipe whose every window was excluded
            // is judged on all of them rather than silently passing whatever happens.
            return results.All(result => result.Passed);
        }

        return recipe.Output.Logic == TotalJudgment.Any
            ? counted.Any(result => result.Passed)
            : counted.All(result => result.Passed);
    }

    private WindowResult Evaluate(InspectionWindow window, Mat image, PositionFix fix, Mat? master)
    {
        var placed = window.FollowsPosition ? fix.Move(window.Region) : window.Region;
        var region = placed.ClampTo(image.Width, image.Height);
        if (!region.IsValid)
        {
            return new WindowResult
            {
                WindowId = window.Id,
                Name = window.Name,
                Tool = window.Tool,
                Region = region,
                Value = 0,
                LowerLimit = window.LowerLimit,
                UpperLimit = window.UpperLimit,
                Passed = false,
                AffectsTotal = window.AffectsTotal,
                Message = "Pencere görüntünün dışında kalıyor.",
            };
        }

        using var roi = new Mat(image, new Rect(region.X, region.Y, region.Width, region.Height));
        using var mask = BuildMask(window.Mask, region.Width, region.Height);

        // The master is cropped at the window's taught position, not its moved one: the master shows
        // the part where it was when the window was drawn on it.
        using var masterRoi = CropMaster(master, window.Region);

        var reading = MeasureRoi(
            roi, mask, window.Tool, window.ExpectedText, window.IgnoreDiacritics, window.OcrLanguage,
            window.Options, masterRoi);

        return new WindowResult
        {
            WindowId = window.Id,
            Name = window.Name,
            Tool = window.Tool,
            Region = region,
            Value = Math.Round(reading.Value, 2),
            Text = reading.Text,
            LowerLimit = window.LowerLimit,
            UpperLimit = window.UpperLimit,
            Passed = reading.Value >= window.LowerLimit && reading.Value <= window.UpperLimit,
            AffectsTotal = window.AffectsTotal,
            Message = reading.Note,
        };
    }

    /// <summary>The master at the window's taught position, or null when there is no master.</summary>
    private static Mat? CropMaster(Mat? master, Roi region)
    {
        if (master is null)
        {
            return null;
        }

        var clamped = region.ClampTo(master.Width, master.Height);
        return clamped.IsValid
            ? new Mat(master, new Rect(clamped.X, clamped.Y, clamped.Width, clamped.Height))
            : null;
    }

    /// <summary>
    /// White where the window is measured, black where it is not. Null when nothing is excluded,
    /// so the ordinary case costs nothing.
    /// </summary>
    private static Mat? BuildMask(IReadOnlyList<Roi> excluded, int width, int height)
    {
        if (excluded.Count == 0)
        {
            return null;
        }

        var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(255));
        var used = false;

        foreach (var area in excluded)
        {
            var clamped = area.ClampTo(width, height);
            if (!clamped.IsValid)
            {
                continue;
            }

            Cv2.Rectangle(
                mask,
                new Rect(clamped.X, clamped.Y, clamped.Width, clamped.Height),
                Scalar.All(0),
                thickness: -1);
            used = true;
        }

        if (used)
        {
            return mask;
        }

        mask.Dispose();
        return null;
    }

    /// <summary>
    /// The one place a tool turns a cropped region into a number, shared by judging and by the
    /// try-it-out path so both always agree about what a tool measures.
    /// </summary>
    private ToolReading MeasureRoi(
        Mat roi,
        Mat? mask,
        InspectionToolKind tool,
        string? expectedText,
        bool ignoreDiacritics,
        string? languageId,
        ToolOptions options,
        Mat? masterRoi)
    {
        Cv2.MeanStdDev(roi, out Scalar mean, out _, mask ?? new Mat());

        return tool switch
        {
            InspectionToolKind.Ocr => ReadMaskedText(roi, mask, mean, expectedText, ignoreDiacritics, languageId),
            InspectionToolKind.Area => new ToolReading(RegionTools.AreaPercent(roi, mask, options), null, null),
            InspectionToolKind.BlobCount => new ToolReading(RegionTools.BlobCount(roi, mask, options), null, null),
            InspectionToolKind.Diameter => Scale(
                RegionTools.DiameterPixels(roi, options), options, "Daire bulunamadı."),
            InspectionToolKind.Outline => masterRoi is null
                ? new ToolReading(0, null, "Kontur aracı master görüntü ister; önce master kaydedin.")
                : new ToolReading(RegionTools.OutlineMatch(roi, masterRoi, options), null, null),
            InspectionToolKind.Edge or InspectionToolKind.Width or InspectionToolKind.Pitch =>
                ScanReading(roi, mask, tool, options),
            _ => throw new NotSupportedException($"'{tool}' aracı henüz desteklenmiyor."),
        };
    }

    /// <summary>
    /// The tools that read a distance along a scan: where the first edge is, how far apart the
    /// outer two are, and how evenly spaced a run of them is.
    /// </summary>
    private static ToolReading ScanReading(Mat roi, Mat? mask, InspectionToolKind tool, ToolOptions options)
    {
        var profile = EdgeProfile.Build(roi, mask, options.Direction);
        var edges = EdgeProfile.Find(profile, options.EdgeSensitivity);

        switch (tool)
        {
            case InspectionToolKind.Edge:
                return edges.Count >= 1
                    ? Scale(edges[0].Position, options, null) with { EdgeCount = edges.Count }
                    : new ToolReading(0, null, "Kenar bulunamadı; hassasiyeti düşürün.", 0);

            case InspectionToolKind.Width:
                return edges.Count >= 2
                    ? Scale(edges[^1].Position - edges[0].Position, options, null) with { EdgeCount = edges.Count }
                    : new ToolReading(0, null, $"Genişlik için iki kenar gerekli.", edges.Count);

            default:
                if (edges.Count < 3)
                {
                    return new ToolReading(0, null, "Adım için en az üç kenar gerekli.", edges.Count);
                }

                // The mean gap, which is what a pitch is; the count of gaps is one less than the
                // count of edges.
                var span = edges[^1].Position - edges[0].Position;
                return Scale(span / (edges.Count - 1), options, null) with { EdgeCount = edges.Count };
        }
    }

    /// <summary>
    /// Pixels, or millimetres when the window has been given a scale. A tolerance written off the
    /// drawing means nothing until the reading is in the drawing's units.
    /// </summary>
    private static ToolReading Scale(double pixels, ToolOptions options, string? noteWhenZero)
    {
        if (pixels <= 0 && noteWhenZero is not null)
        {
            return new ToolReading(0, null, noteWhenZero);
        }

        return options.MmPerPixel > 0
            ? new ToolReading(pixels * options.MmPerPixel, null, null)
            : new ToolReading(pixels, null, null);
    }

    /// <summary>
    /// Reads text with the excluded areas painted over.
    ///
    /// Painted in the average of what is kept, not in black: a black block is a hard edge, and the
    /// text detector would find it and try to read it. Filling with the surrounding tone makes the
    /// masked area disappear rather than become a new feature.
    /// </summary>
    private ToolReading ReadMaskedText(
        Mat roi,
        Mat? mask,
        Scalar keptMean,
        string? expectedText,
        bool ignoreDiacritics,
        string? languageId)
    {
        if (mask is null)
        {
            return ReadText(roi, expectedText, ignoreDiacritics, languageId);
        }

        using var painted = roi.Clone();
        using var excluded = new Mat();
        Cv2.BitwiseNot(mask, excluded);
        painted.SetTo(Scalar.All(keptMean.Val0), excluded);

        return ReadText(painted, expectedText, ignoreDiacritics, languageId);
    }

    private ToolReading ReadText(Mat roi, string? expectedText, bool ignoreDiacritics, string? languageId)
    {
        var reading = ocr.Read(roi, languageId);
        if (reading.Error is not null)
        {
            return new ToolReading(0, reading.Text, reading.Error);
        }

        // With nothing to compare against, the tool still has to produce a number, so it reports
        // how sure the engine was of what it read.
        if (string.IsNullOrWhiteSpace(expectedText))
        {
            return new ToolReading(
                Math.Round(reading.Confidence, 2),
                reading.Text,
                "Beklenen metin girilmedi; değer okuma güveni olarak raporlandı.");
        }

        var score = TextSimilarity.Percent(reading.Text, expectedText, ignoreDiacritics);

        // Say so when folding is the only reason this passed. The recognisers available for Latin
        // script normalise Turkish and other diacritics unpredictably — the same word can come back
        // with or without them from one frame to the next — so a reading that differs from the
        // expected text only in accents is a limit of the model, not a wrong part. Left unexplained,
        // an operator sees a perfect score next to visibly wrong text and stops trusting the tool.
        var note = ignoreDiacritics && score > TextSimilarity.Percent(reading.Text, expectedText, false)
            ? $"Okunan metin beklenenden yalnızca aksan işaretlerinde ayrılıyor ({expectedText})."
            : null;

        return new ToolReading(score, reading.Text, note);
    }

    /// <summary>
    /// EdgeCount is carried for the scanning tools and shown only while trying a tool out. How many
    /// edges were found is the single number that makes the sensitivity setting tunable - the same
    /// window read 41 and then 96 across a sensitivity sweep, and without the count there is nothing
    /// to tell an operator which of those was the row of separators and which was the lettering.
    /// </summary>
    private sealed record ToolReading(double Value, string? Text, string? Note, int? EdgeCount = null);

    /// <summary>
    /// Measures a region without judging it — what the UI calls on a live frame while the operator
    /// drags a window, so limits can be set from what the good part actually reads.
    /// </summary>
    public WindowMeasurement Measure(
        Roi region,
        InspectionToolKind tool,
        CameraFrame frame,
        string? expectedText = null,
        bool ignoreDiacritics = true,
        string? languageId = null,
        IReadOnlyList<Roi>? mask = null,
        ToolOptions? options = null,
        string? recipeId = null)
    {
        using var image = FrameConverter.ToGrayscaleMat(frame);
        var clamped = region.ClampTo(image.Width, image.Height);
        if (!clamped.IsValid)
        {
            throw new ArgumentException("Pencere görüntünün dışında kalıyor.", nameof(region));
        }

        using var roi = new Mat(image, new Rect(clamped.X, clamped.Y, clamped.Width, clamped.Height));
        using var excluded = BuildMask(mask ?? [], clamped.Width, clamped.Height);
        Cv2.MeanStdDev(roi, out Scalar mean, out Scalar standardDeviation, excluded ?? new Mat());

        // Trying the outline tool needs the same master the judged path would use, so the trial and
        // the judgement cannot disagree about what the part is being compared to.
        using var masterRoi = CropMaster(
            recipeId is null ? null : masters.Load(recipeId), region);

        var reading = MeasureRoi(
            roi, excluded, tool, expectedText, ignoreDiacritics, languageId,
            options ?? new ToolOptions(), masterRoi);
        return new WindowMeasurement
        {
            Region = clamped,
            Tool = tool,
            Brightness = Math.Round(mean.Val0, 2),
            Contrast = Math.Round(standardDeviation.Val0, 2),
            Value = Math.Round(reading.Value, 2),
            Text = reading.Text,
            Note = reading.Note,
            EdgeCount = reading.EdgeCount,
            IgnoreDiacritics = ignoreDiacritics,
        };
    }
}

public sealed record InspectionResult
{
    public required string RecipeId { get; init; }
    public required string RecipeName { get; init; }
    public DateTime TimestampUtc { get; init; }
    public DateTime FrameTimestampUtc { get; init; }
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }
    public bool Passed { get; init; }
    public IReadOnlyList<WindowResult> Windows { get; init; } = [];

    /// <summary>Whether the windows were moved to follow the part on this frame.</summary>
    public bool PositionApplied { get; init; }

    public int PositionOffsetX { get; init; }
    public int PositionOffsetY { get; init; }

    /// <summary>How strongly the taught pattern matched, 0-100.</summary>
    public double PositionScore { get; init; }

    /// <summary>Why the result is what it is, when something outside the windows decided it.</summary>
    public string? Message { get; init; }
}

public sealed record WindowResult
{
    public required string WindowId { get; init; }
    public required string Name { get; init; }
    public InspectionToolKind Tool { get; init; }
    public required Roi Region { get; init; }
    public double Value { get; init; }
    /// <summary>What the OCR tool read, when the tool reads text; null otherwise.</summary>
    public string? Text { get; init; }
    public double LowerLimit { get; init; }
    public double UpperLimit { get; init; }
    public bool Passed { get; init; }

    /// <summary>Whether this window's verdict counted towards the overall result.</summary>
    public bool AffectsTotal { get; init; } = true;

    public string? Message { get; init; }
}

public sealed record WindowMeasurement
{
    public required Roi Region { get; init; }
    public InspectionToolKind Tool { get; init; }
    public double Value { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    /// <summary>What the OCR tool read, so the operator can see it before setting a limit on it.</summary>
    public string? Text { get; init; }
    public string? Note { get; init; }

    /// <summary>
    /// How many edges the scan found, for the tools that scan. Shown while trying a tool out
    /// because it is what makes the sensitivity setting tunable; null for the other tools.
    /// </summary>
    public int? EdgeCount { get; init; }

    /// <summary>Whether Turkish diacritics were folded for the comparison that produced Value.</summary>
    public bool IgnoreDiacritics { get; init; }
}
