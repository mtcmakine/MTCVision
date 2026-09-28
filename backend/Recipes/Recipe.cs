using System.Text.Json.Serialization;

namespace MTCVision.Backend.Recipes;

/// <summary>
/// A stored inspection setup: the camera settings the part was taught under, plus the windows that
/// judge it. Modelled on the Keyence IV2 idea of a recipe — camera conditions and a list of tools,
/// each confined to a rectangle and judged against limits.
/// </summary>
public sealed record Recipe
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }

    /// <summary>
    /// Camera settings this recipe was taught under. Lighting and exposure change what every
    /// measurement below means, so they belong to the recipe rather than to the machine.
    /// </summary>
    public RecipeCameraSettings Camera { get; init; } = new();

    public IReadOnlyList<InspectionWindow> Windows { get; init; } = [];

    /// <summary>
    /// The picture of a good part this recipe was taught on, if one has been registered.
    ///
    /// The image itself lives beside the recipe rather than inside it - a frame is megabytes and a
    /// recipe is read and rewritten on every edit. What is kept here is enough to say whether the
    /// stored file still belongs to this recipe and when it was taken.
    /// </summary>
    public MasterImage? Master { get; init; }

    /// <summary>Follows the part around the frame instead of assuming it never moves.</summary>
    public PositionAdjustment Position { get; init; } = new();

    /// <summary>How the individual window results add up to one OK or NG for the line.</summary>
    public OutputAssignment Output { get; init; } = new();
}

/// <summary>
/// What is known about the registered master image. The pixels are in the master store, keyed by
/// recipe id.
/// </summary>
public sealed record MasterImage(DateTime CapturedUtc, int Width, int Height, string Source);

/// <summary>
/// Finds a taught pattern in each frame and moves the windows by however far it has shifted.
///
/// Without this, a part that arrives a few pixels off its taught position drags every window off
/// its feature, and the only way to keep the line running is to widen the limits until they stop
/// meaning anything. Moving the windows instead keeps the limits tight and honest.
/// </summary>
public sealed record PositionAdjustment
{
    public bool Enabled { get; init; }

    /// <summary>
    /// The patch of the master image to look for: something with a strong, unambiguous shape that
    /// belongs to the part rather than to the background.
    /// </summary>
    public Roi? Pattern { get; init; }

    /// <summary>
    /// How far either side of the taught position to search, in pixels. Wider costs time and
    /// invites a false match on a repeating feature; narrower misses a part that shifted more.
    /// </summary>
    public int SearchMargin { get; init; } = 80;

    /// <summary>
    /// Match strength below which the pattern counts as not found, 0-100. A part that cannot be
    /// located is judged NG rather than measured in the wrong place.
    /// </summary>
    public double MinScore { get; init; } = 60;
}

/// <summary>
/// How the window results combine into one verdict.
///
/// Only the logic, deliberately. Driving a digital output line was the other half of this and the
/// camera cannot do it: LineSelector is read-only on this device and LineSource, UserOutputSelector
/// and UserOutputValue are absent from its node map entirely, so there is no GenICam route to a
/// software-controlled level. A signalling cell needs a separate I/O module, or a PLC that reads
/// the verdict over the network. Settings that cannot take effect are worse than missing ones, so
/// there are none here.
/// </summary>
public sealed record OutputAssignment
{
    public TotalJudgment Logic { get; init; } = TotalJudgment.All;
}

public enum TotalJudgment
{
    /// <summary>Every counted window has to pass. The normal case for an inspection.</summary>
    All,

    /// <summary>Any one counted window passing is enough - for "is it one of these" checks.</summary>
    Any,
}

public sealed record RecipeCameraSettings
{
    public double? ExposureTimeUs { get; init; }
    public double? Gain { get; init; }
    public double? FrameRateHz { get; init; }
}

/// <summary>
/// One rectangle plus the measurement taken inside it and the limits that measurement must fall
/// between. Keyence calls this a "Window"; the limits are its "Limit Adjustment".
/// </summary>
public sealed record InspectionWindow
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required Roi Region { get; init; }

    public InspectionToolKind Tool { get; init; } = InspectionToolKind.Ocr;

    public double LowerLimit { get; init; }

    public double UpperLimit { get; init; }

    /// <summary>
    /// What the OCR tool should find in this window. Ignored by the other tools.
    /// </summary>
    public string? ExpectedText { get; init; }

    /// <summary>
    /// Treats Turkish diacritics as equal to their plain letters when comparing OCR output.
    ///
    /// Off by default. It was on while the stock recogniser substituted the dotless i for a dotted
    /// one on parts whose image plainly showed which it was; the model trained for this line reads
    /// them, measured at 30 of 30 exact over five frames of the real panel, so folding them away now
    /// hides genuine misreads. Still offered, because a pack that has not been trained here — or a
    /// mark too shallow to resolve an accent — is a case where the distinction really is unfair.
    /// </summary>
    public bool IgnoreDiacritics { get; init; }

    /// <summary>
    /// Which OCR language pack reads this window. A recognition model only knows one script, so a
    /// part labelled in Arabic needs a different one from a part labelled in Turkish. Null means the
    /// default pack, which covers Latin scripts.
    /// </summary>
    public string? OcrLanguage { get; init; }

    /// <summary>
    /// Readings taken from parts the operator judged, used to derive limits. Kept with the recipe
    /// so the evidence behind a limit travels with it and can be added to later.
    /// </summary>
    public IReadOnlyList<ToolSample> Samples { get; init; } = [];

    /// <summary>
    /// Rectangles inside this window that take no part in the measurement, in window coordinates.
    ///
    /// Window-relative rather than frame-relative so a mask stays over the thing it was drawn on
    /// when position adjustment moves the window. What it is for: the panel edge or the pictogram
    /// that falls inside an otherwise well-drawn window and has no business in the reading.
    /// </summary>
    public IReadOnlyList<Roi> Mask { get; init; } = [];

    /// <summary>
    /// Whether this window's verdict counts towards the overall OK/NG. Off for a window that is
    /// being watched but should not stop the line on its own.
    /// </summary>
    public bool AffectsTotal { get; init; } = true;

    /// <summary>
    /// Whether this window moves with the position adjustment. Off for something fixed to the
    /// fixture rather than to the part.
    /// </summary>
    public bool FollowsPosition { get; init; } = true;

    /// <summary>Settings for whichever tool this window uses; the rest are ignored.</summary>
    public ToolOptions Options { get; init; } = new();
}

/// <summary>One measured reading from a part the operator labelled good or bad.</summary>
public sealed record ToolSample(string Id, DateTime CapturedUtc, double Value, bool IsGood);

/// <summary>Region of interest in sensor pixels, with the origin at the top-left of the frame.</summary>
public sealed record Roi(int X, int Y, int Width, int Height)
{
    /// <summary>Derived, so it is not part of what gets stored or sent.</summary>
    [JsonIgnore]
    public bool IsValid => Width > 0 && Height > 0 && X >= 0 && Y >= 0;

    /// <summary>Trims the region to what the frame actually contains.</summary>
    public Roi ClampTo(int frameWidth, int frameHeight)
    {
        var x = Math.Clamp(X, 0, Math.Max(0, frameWidth - 1));
        var y = Math.Clamp(Y, 0, Math.Max(0, frameHeight - 1));
        return new Roi(x, y, Math.Clamp(Width, 0, frameWidth - x), Math.Clamp(Height, 0, frameHeight - y));
    }
}

/// <remarks>
/// Brightness (mean grey level) and Contrast (grey-level standard deviation) tools were retired so
/// the console concentrates on OCR; recipes saved with them lose those windows on load, see
/// <see cref="RecipeStore"/>.
/// </remarks>
public enum InspectionToolKind
{
    /// <summary>
    /// Reads the text in the window and scores how closely it matches ExpectedText, 0-100. A
    /// percentage rather than a yes/no so one misread character does not read the same as a blank.
    /// </summary>
    Ocr,

    /// <summary>
    /// Share of the window covered by pixels inside a chosen grey range, 0-100.
    ///
    /// Presence and absence: a part that should fill the window, a stain that should not. Set the
    /// range to the tone being looked for; a limit near zero turns the same tool into "this tone
    /// must not appear here", which is what the reference console calls a prohibition tool.
    /// </summary>
    Area,

    /// <summary>
    /// How many separate marks in the grey range the window holds. Counting holes, pins, printed
    /// dots - anything where the answer is a number rather than an amount.
    /// </summary>
    BlobCount,

    /// <summary>
    /// How well the window's outlines match the master's, 0-100. Answers "is this the right shape",
    /// which brightness and area cannot: a part of the right size in the wrong shape passes both.
    /// </summary>
    Outline,

    /// <summary>
    /// Where the first strong edge sits, scanning across the window. Reports the distance from the
    /// edge the scan starts at, so a feature that drifts is caught before it leaves the window.
    /// </summary>
    Edge,

    /// <summary>Distance between the first and last edge across the window - a gap, a rib, a slot.</summary>
    Width,

    /// <summary>Mean spacing between repeated edges: teeth, pins, perforations.</summary>
    Pitch,

    /// <summary>Diameter of the roundest thing in the window - a hole, a boss, a cap.</summary>
    Diameter,
}

/// <summary>Which way a scanning tool crosses the window.</summary>
public enum ScanDirection
{
    LeftToRight,
    RightToLeft,
    TopToBottom,
    BottomToTop,
}

/// <summary>
/// The per-tool settings, gathered in one place.
///
/// One record rather than a shape per tool: every field has a sane default, only the tool that
/// needs a field reads it, and the stored recipe stays a flat piece of JSON that can be read and
/// diffed by eye. The grouping below is by which tools use what.
/// </summary>
public sealed record ToolOptions
{
    /// <summary>Grey range counted by the area and blob tools, 0-255 inclusive.</summary>
    public int LowerGrey { get; init; }

    public int UpperGrey { get; init; } = 128;

    /// <summary>Blobs smaller than this many pixels are noise, not marks.</summary>
    public int MinBlobArea { get; init; } = 25;

    /// <summary>
    /// Drops blobs touching the window border. A mark cut by the window edge has an arbitrary size
    /// and counting it makes the result depend on where the window was drawn.
    /// </summary>
    public bool ExcludeBoundaryBlobs { get; init; } = true;

    /// <summary>Which way the edge, width and pitch tools scan.</summary>
    public ScanDirection Direction { get; init; } = ScanDirection.LeftToRight;

    /// <summary>
    /// How steep a grey change has to be to count as an edge, in grey levels per pixel. Lower finds
    /// soft edges and also finds noise.
    /// </summary>
    public double EdgeSensitivity { get; init; } = 12;

    /// <summary>Radius bounds for the diameter tool, in pixels. Zero lets the tool choose.</summary>
    public int MinRadius { get; init; }

    public int MaxRadius { get; init; }

    /// <summary>
    /// Millimetres per pixel, for the tools that measure a distance. Zero reports pixels.
    ///
    /// Worth setting: a limit written in millimetres is a tolerance off the drawing, and it survives
    /// the camera being moved or the lens being changed in a way a pixel limit does not.
    /// </summary>
    public double MmPerPixel { get; init; }
}
