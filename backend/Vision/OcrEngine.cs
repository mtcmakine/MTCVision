using System.Collections.Concurrent;
using System.Text.Json;
using OpenCvSharp;
using RapidOcrNet;
using SkiaSharp;

namespace MTCVision.Backend.Vision;

/// <summary>
/// Reads text out of a cropped region using PP-OCRv5 (PaddleOCR) through ONNX Runtime.
///
/// This replaced Tesseract after a measured comparison on images built to match what this camera
/// actually sees — shallow marks, sensor noise at high gain, uneven lighting, glare, slight defocus.
/// Over seven such cases Tesseract averaged 79% character similarity and read three exactly, and
/// returned nothing at all for a defocused low-contrast mark; PP-OCRv5 read all seven exactly.
///
/// The gap is architectural rather than incidental. Tesseract binarises and matches shapes, which
/// needs contrast a laser mark does not have, while PP-OCR is a text detector plus a convolutional
/// recogniser trained on degraded real-world text. It also handles light marks on dark surfaces
/// natively, so the region is passed through almost untouched: the manual thresholding and polarity
/// flipping the old engine needed would now throw away information the network can use.
///
/// Recognition is per script — one alphabet's model cannot read another's — so each language pack
/// is its own model, loaded the first time a recipe asks for it. Detection and line orientation are
/// shared, being script-independent.
/// </summary>
public sealed class OcrEngine : IDisposable
{
    /// <summary>
    /// Detection runs on a downscaled copy, so a small crop is enlarged first. Left alone, a
    /// 30-pixel-tall code is often not detected as a text line at all.
    /// </summary>
    private const int TargetTextHeightPx = 96;

    private const int MaxUpscale = 6;

    /// <summary>
    /// Air left around a detected line, as a fraction of its height. Generous above and below,
    /// where a clipped ascender costs a letter, and mean to the sides, where the next thing along
    /// - the dark edge of a panel, the neighbouring label - is what gets pulled in.
    /// </summary>
    private const double BoxPaddingX = 0.03;

    private const double BoxPaddingY = 0.12;

    /// <summary>Anything smaller than this is a speck, not a line of text.</summary>
    private const int MinLinePx = 6;

    /// <summary>Below this the stock recogniser is saying the box is not text.</summary>
    private const float MinTextScore = 0.5f;

    /// <summary>Narrower than this, relative to its height, a box beside real text is an edge.</summary>
    private const double MinAspect = 0.4;

    /// <summary>A narrow box this large next to the biggest one is content, not a border.</summary>
    private const double MinAreaShare = 0.25;

    /// <summary>Taller than this multiple of its width, a line is written down the part.</summary>
    private const double UprightAspect = 1.5;

    private readonly string _bundledModels;
    private readonly string _languageModels;
    private readonly ConcurrentDictionary<string, Lazy<LoadedLanguage>> _engines =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lazy<RapidOcr?> _detector;
    private readonly SemaphoreSlim _detectorGate = new(1, 1);

    private IReadOnlyList<OcrLanguage>? _languages;
    private string? _catalogueError;

    public OcrEngine()
    {
        // Two sources. Detection and line orientation come from the RapidOcrNet package; the
        // recognisers and their dictionaries are ours. Resolved from the base directory rather than
        // the working directory, because a Windows service starts in system32 and would find
        // nothing there.
        _bundledModels = Path.Combine(AppContext.BaseDirectory, "models", "v5");
        _languageModels = Path.Combine(AppContext.BaseDirectory, "Vision", "models");

        _detector = new Lazy<RapidOcr?>(CreateDetector, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The language packs present on disk. Read from a manifest rather than hard-coded, so another
    /// script is added by dropping its model, its dictionary and one entry into the folder.
    /// </summary>
    public IReadOnlyList<OcrLanguage> Languages => _languages ??= LoadCatalogue();

    public string? CatalogueError
    {
        get
        {
            _ = Languages;
            return _catalogueError;
        }
    }

    public string DefaultLanguageId =>
        Languages.FirstOrDefault(language => language.IsDefault)?.Id
        ?? Languages.FirstOrDefault()?.Id
        ?? "latin";

    /// <summary>
    /// Reads the region and reports what it found. Never throws for an unreadable image — an empty
    /// read is a legitimate result that the caller turns into a failed inspection.
    /// </summary>
    public OcrReading Read(Mat region, string? languageId = null)
    {
        var id = string.IsNullOrWhiteSpace(languageId) ? DefaultLanguageId : languageId;

        LoadedLanguage loaded;
        try
        {
            loaded = _engines.GetOrAdd(
                id,
                key => new Lazy<LoadedLanguage>(() => Load(key), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
        catch (Exception ex)
        {
            // A pack that fails to load must not be retried on every frame of a running line.
            return new OcrReading(string.Empty, 0, ex.Message, id);
        }

        // One recognition at a time per language: an ONNX session is not safe to share across
        // threads, and the inspection loop can overlap with an operator trying a tool by hand.
        loaded.Gate.Wait();
        try
        {
            if (loaded.Line is not null)
            {
                return ReadLines(region, loaded.Line, id);
            }

            using var prepared = Enlarge(region);
            Cv2.ImEncode(".png", prepared, out var png);
            using var bitmap = SKBitmap.Decode(png);
            if (bitmap is null)
            {
                return new OcrReading(string.Empty, 0, "Bölge görüntüsü çözülemedi.", id);
            }

            var result = loaded.Detector!.Detect(bitmap, RapidOcrOptions.Default, CancellationToken.None);
            var blocks = result.TextBlocks;
            if (blocks.Length == 0)
            {
                return new OcrReading(string.Empty, 0, null, id);
            }

            // A window may hold more than one line; they are joined in the order the detector found
            // them so a two-line code still compares against the expected string.
            var text = Collapse(string.Join(' ', blocks.Select(block => block.Text)));

            // Per-character recognition scores, not the detector box score: a box can be located
            // confidently while the characters inside it are guesses.
            var scores = blocks.SelectMany(block => block.CharScores ?? []).ToList();
            var confidence = scores.Count > 0 ? scores.Average() * 100 : 0;

            return new OcrReading(text, confidence, null, id);
        }
        catch (Exception ex)
        {
            return new OcrReading(string.Empty, 0, ex.Message, id);
        }
        finally
        {
            loaded.Gate.Release();
        }
    }

    /// <summary>
    /// Locates the text lines inside the window, then reads each one with our own recogniser.
    ///
    /// Detection is used for where the text is, not for what it says. Reading the whole window as
    /// one line is right only when the operator drew it tightly; measured on a window drawn
    /// generously around one word - 521x129 for text 450x70 - reading it whole returned "I" where
    /// the located line returned "Perde". Windows get drawn generously, so the line has to be found
    /// rather than assumed.
    /// </summary>
    private OcrReading ReadLines(Mat region, LineRecognizer recognizer, string id)
    {
        var boxes = Locate(region);
        if (boxes.Count == 0)
        {
            // Nothing found is not the same as nothing there: a faint mark the detector misses is
            // still often readable, so the window is tried whole before giving up on it.
            var (whole, wholeConfidence) = recognizer.Read(region);
            return new OcrReading(Collapse(whole), wholeConfidence, null, id);
        }

        var readings = new List<(Rect Box, string Text, double Confidence)>();
        foreach (var box in MergeRows(Winnow(boxes)))
        {
            using var line = new Mat(region, box);
            var (text, confidence) = ReadOriented(recognizer, line);
            if (text.Length > 0)
            {
                readings.Add((box, text, confidence));
            }
        }

        if (readings.Count == 0)
        {
            return new OcrReading(string.Empty, 0, null, id);
        }

        // Reading order, so a two-line code compares against the expected string the right way
        // round: down the window a line at a time, left to right within a line.
        var lineHeight = Math.Max(1, (int)readings.Average(reading => reading.Box.Height));
        var ordered = readings
            .OrderBy(reading => (reading.Box.Y + reading.Box.Height / 2) / lineHeight)
            .ThenBy(reading => reading.Box.X)
            .ToList();

        return new OcrReading(
            Collapse(string.Join(' ', ordered.Select(reading => reading.Text))),
            ordered.Average(reading => reading.Confidence),
            null,
            id);
    }

    /// <summary>
    /// Reads a line, turning it upright first if it is written down the part rather than across it.
    ///
    /// The stock pipeline classifies line orientation before recognising; reading the region
    /// directly skipped that, and a label printed sideways - "Ek programlar" running bottom to top
    /// in a 154x421 window - came back as a single "I". A recogniser fed a tall strip has no way to
    /// say "this is sideways", so the orientation has to be settled before it is asked.
    ///
    /// Which way up is decided by trying both and keeping the more confident reading. A quarter turn
    /// the wrong way puts every letter on its head, which the model scores far lower - it is a
    /// cheap question to ask and the answer is unambiguous.
    /// </summary>
    private static (string Text, double Confidence) ReadOriented(LineRecognizer recognizer, Mat line)
    {
        if (line.Height <= line.Width * UprightAspect)
        {
            return recognizer.Read(line);
        }

        using var clockwise = new Mat();
        Cv2.Rotate(line, clockwise, RotateFlags.Rotate90Clockwise);
        var forwards = recognizer.Read(clockwise);

        using var anticlockwise = new Mat();
        Cv2.Rotate(line, anticlockwise, RotateFlags.Rotate90Counterclockwise);
        var backwards = recognizer.Read(anticlockwise);

        // A reading with no characters in it is not a reading, however confident the average of no
        // characters comes out.
        if (forwards.Text.Length == 0)
        {
            return backwards;
        }

        if (backwards.Text.Length == 0)
        {
            return forwards;
        }

        return backwards.Confidence > forwards.Confidence ? backwards : forwards;
    }

    /// <summary>
    /// Drops tall, narrow boxes when there is other text in the window.
    ///
    /// A window drawn wide enough to catch the dark edge of the panel gives the detector a single
    /// upright stroke, and a recogniser asked what it says will answer "I". Nothing about the stroke
    /// says it is not a letter, so the discriminator is context: a box narrower than it is tall,
    /// standing next to real words, is a border. Alone in the window it is left be, because then it
    /// may genuinely be the mark being inspected.
    /// </summary>
    private static List<Rect> Winnow(List<Rect> boxes)
    {
        if (boxes.Count < 2)
        {
            return boxes;
        }

        // Narrowness alone is not the test: a label printed sideways is also tall and narrow. What
        // separates a border from sideways text is how much of the window it fills - an edge is a
        // sliver beside the real content, a sideways word is the content.
        var largest = boxes.Max(box => (long)box.Width * box.Height);
        var kept = boxes
            .Where(box => box.Width >= MinAspect * box.Height
                || (long)box.Width * box.Height >= MinAreaShare * largest)
            .ToList();

        return kept.Count > 0 ? kept : boxes;
    }

    /// <summary>
    /// Joins boxes that sit on the same line of text into one.
    ///
    /// Detection splits a line wherever the spacing widens, so "Sikma/Bosaltma" comes back as two
    /// boxes. Reading each separately hands the recogniser half a word with no context and it
    /// showed: the halves came back as "Sikma/" and "GBoSat". One crop per line is also what the
    /// model was trained on.
    /// </summary>
    private static List<Rect> MergeRows(List<Rect> boxes)
    {
        var rows = new List<Rect>();
        foreach (var box in boxes.OrderBy(candidate => candidate.Y).ThenBy(candidate => candidate.X))
        {
            // Two boxes on the same row belong to the same line only when the text runs across the
            // window. Sideways lines sit side by side and merging them would splice two words.
            var index = box.Height > box.Width * UprightAspect
                ? -1
                : rows.FindIndex(row => row.Height <= row.Width * UprightAspect && SameRow(row, box));

            if (index >= 0)
            {
                rows[index] = Rect.Union(rows[index], box);
            }
            else
            {
                rows.Add(box);
            }
        }

        return rows;
    }

    /// <summary>
    /// Two boxes belong to the same line when they overlap vertically over most of the shorter one.
    /// Comparing centres alone merges a subscript into the line above it.
    /// </summary>
    private static bool SameRow(Rect a, Rect b)
    {
        var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return overlap > 0 && overlap >= 0.5 * Math.Min(a.Height, b.Height);
    }

    /// <summary>
    /// The text lines in the window, in the window's own pixels. Empty when detection is
    /// unavailable or finds nothing.
    /// </summary>
    private List<Rect> Locate(Mat region)
    {
        var detector = _detector.Value;
        if (detector is null)
        {
            return [];
        }

        var scale = UpscaleFactor(region);
        using var enlarged = Enlarge(region, scale);
        Cv2.ImEncode(".png", enlarged, out var png);
        using var bitmap = SKBitmap.Decode(png);
        if (bitmap is null)
        {
            return [];
        }

        _detectorGate.Wait();
        OcrResult result;
        try
        {
            result = detector.Detect(bitmap, RapidOcrOptions.Default, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCR] Satir bulma basarisiz, pencere butun olarak okunacak: {ex.Message}");
            return [];
        }
        finally
        {
            _detectorGate.Release();
        }

        var boxes = new List<Rect>();
        foreach (var block in result.TextBlocks)
        {
            var points = block.BoxPoints;
            if (points is null || points.Length == 0)
            {
                continue;
            }

            // The stock recogniser has already read this box; its score is used only to decide
            // whether the box holds text at all. A window drawn wide enough to include the dark
            // edge of the panel gives the detector a tall vertical stroke, which our recogniser
            // will happily call an I - the stock model scores it low, and that is the signal.
            var stockScores = block.CharScores;
            var stockScore = stockScores is { Length: > 0 } ? stockScores.Average() : 0;
            if (stockScore < MinTextScore)
            {
                continue;
            }

            var left = points.Min(point => point.X) / scale;
            var right = points.Max(point => point.X) / scale;
            var top = points.Min(point => point.Y) / scale;
            var bottom = points.Max(point => point.Y) / scale;

            // The detector hugs the ink. A little air around it matches how the recogniser was
            // trained and measurably steadies the reading.
            var padX = (bottom - top) * BoxPaddingX;
            var padY = (bottom - top) * BoxPaddingY;

            var x = (int)Math.Max(0, Math.Floor(left - padX));
            var y = (int)Math.Max(0, Math.Floor(top - padY));
            var width = (int)Math.Min(region.Width - x, Math.Ceiling(right + padX) - x);
            var height = (int)Math.Min(region.Height - y, Math.Ceiling(bottom + padY) - y);

            if (width >= MinLinePx && height >= MinLinePx)
            {
                boxes.Add(new Rect(x, y, width, height));
            }
        }

        return boxes;
    }

    private static double UpscaleFactor(Mat region) =>
        Math.Clamp((double)TargetTextHeightPx / Math.Max(1, region.Height), 1, MaxUpscale);

    private static Mat Enlarge(Mat region) => Enlarge(region, UpscaleFactor(region));

    private static Mat Enlarge(Mat region, double scale)
    {
        if (scale <= 1.001)
        {
            return region.Clone();
        }

        var enlarged = new Mat();
        Cv2.Resize(region, enlarged, new Size(0, 0), scale, scale, InterpolationFlags.Cubic);
        return enlarged;
    }

    /// <summary>
    /// Detection and line orientation are script-independent, so one instance serves every language
    /// pack. It is built around a stock recogniser because the runtime insists on having one; only
    /// the boxes it produces are used.
    /// </summary>
    private RapidOcr? CreateDetector()
    {
        var detection = Path.Combine(_bundledModels, "ch_PP-OCRv5_mobile_det.onnx");
        var orientation = Path.Combine(_bundledModels, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");

        var stock = Languages.FirstOrDefault(language => !language.LineModel);
        if (stock is null || !File.Exists(detection) || !File.Exists(orientation))
        {
            Console.WriteLine("[OCR] Satir bulma modeli yok; pencereler butun olarak okunacak.");
            return null;
        }

        try
        {
            var engine = new RapidOcr();
            engine.InitModels(
                detection,
                orientation,
                Path.Combine(_languageModels, stock.Model),
                Path.Combine(_languageModels, stock.Dictionary),
                Math.Max(1, Environment.ProcessorCount - 1));
            return engine;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OCR] Satir bulma modeli yuklenemedi: {ex.Message}");
            return null;
        }
    }

    /// <summary>Runs of whitespace and newlines carry no meaning here; a single space does.</summary>
    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private IReadOnlyList<OcrLanguage> LoadCatalogue()
    {
        var manifest = Path.Combine(_languageModels, "languages.json");
        if (!File.Exists(manifest))
        {
            _catalogueError = $"Dil listesi bulunamadı: {manifest}";
            return [];
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<OcrLanguage>>(
                File.ReadAllText(manifest),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

            // A language whose files were not deployed is worse than one that is absent: it would be
            // offered in the recipe editor and then fail when the line is running.
            var present = entries
                .Where(entry => File.Exists(Path.Combine(_languageModels, entry.Model))
                    && File.Exists(Path.Combine(_languageModels, entry.Dictionary)))
                .ToList();

            if (entries.Count != present.Count)
            {
                Console.WriteLine($"[OCR] {entries.Count - present.Count} dil paketinin dosyaları eksik, listelenmedi.");
            }

            return present;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _catalogueError = $"Dil listesi okunamadı: {ex.Message}";
            return [];
        }
    }

    private LoadedLanguage Load(string languageId)
    {
        var language = Languages.FirstOrDefault(
                entry => string.Equals(entry.Id, languageId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"'{languageId}' dil paketi tanımlı değil.");

        var recognitionOnly = Path.Combine(_languageModels, language.Model);
        var dictionaryOnly = Path.Combine(_languageModels, language.Dictionary);

        // One core left free: the inspection loop and the GVSP receive thread still have to run
        // while a recognition is in flight.
        var threads = Math.Max(1, Environment.ProcessorCount - 1);

        if (language.LineModel)
        {
            var absent = new[] { recognitionOnly, dictionaryOnly }
                .Where(path => !File.Exists(path))
                .Select(Path.GetFileName)
                .ToList();

            if (absent.Count > 0)
            {
                throw new InvalidOperationException($"OCR model dosyaları bulunamadı: {string.Join(", ", absent)}");
            }

            Console.WriteLine($"[OCR] '{language.Name}' dil paketi yüklendi (satır modeli).");
            return new LoadedLanguage(null, new LineRecognizer(recognitionOnly, dictionaryOnly, threads));
        }

        var detection = Path.Combine(_bundledModels, "ch_PP-OCRv5_mobile_det.onnx");
        var orientation = Path.Combine(_bundledModels, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
        var recognition = Path.Combine(_languageModels, language.Model);
        var dictionary = Path.Combine(_languageModels, language.Dictionary);

        var missing = new[] { detection, orientation, recognition, dictionary }
            .Where(path => !File.Exists(path))
            .Select(Path.GetFileName)
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"OCR model dosyaları bulunamadı: {string.Join(", ", missing)}");
        }

        var engine = new RapidOcr();
        engine.InitModels(detection, orientation, recognition, dictionary, threads);

        Console.WriteLine($"[OCR] '{language.Name}' dil paketi yüklendi.");
        return new LoadedLanguage(engine, null);
    }

    public void Dispose()
    {
        foreach (var loaded in _engines.Values.Where(entry => entry.IsValueCreated))
        {
            loaded.Value.Detector?.Dispose();
            loaded.Value.Line?.Dispose();
            loaded.Value.Gate.Dispose();
        }

        _engines.Clear();

        if (_detector.IsValueCreated)
        {
            _detector.Value?.Dispose();
        }

        _detectorGate.Dispose();
    }

    /// <summary>Exactly one of the two is set: a full pipeline, or a bare line recogniser.</summary>
    private sealed record LoadedLanguage(RapidOcr? Detector, LineRecognizer? Line)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}

/// <summary>One recognition model and the alphabet it can produce.</summary>
public sealed record OcrLanguage(
    string Id,
    string Name,
    string Description,
    string Model,
    string Dictionary,
    bool IsDefault = false,

    /// <summary>
    /// Read the region directly, without running text detection first. Set for models trained on
    /// whole inspection windows; the stock packs expect the detector in front of them.
    /// </summary>
    bool LineModel = false);

public sealed record OcrReading(string Text, double Confidence, string? Error, string LanguageId);

public static class TextSimilarity
{
    /// <summary>
    /// Latin letters carrying a diacritic, folded onto the plain letter underneath.
    ///
    /// The recogniser drops or plainly substitutes these on real parts even when the picture clearly
    /// shows which one it is, and it does so in both directions — the Turkish dotted and dotless i
    /// differ by two pixels at the sizes a part is photographed at. Folding keeps a correct label
    /// from being rejected over a distinction the image cannot settle.
    ///
    /// Only Latin script is folded. Arabic and Cyrillic have no plain-letter equivalent to fold
    /// onto, so a substitution there is a genuine misread and should count as one.
    /// </summary>
    private static readonly Dictionary<char, char> DiacriticFolding = new()
    {
        // Turkish. Invariant uppercasing turns a dotted i into I but leaves the dotless one alone,
        // so both have to be named here.
        ['Ş'] = 'S', ['Ğ'] = 'G', ['İ'] = 'I', ['ı'] = 'I',

        // Shared across German, French, Spanish, Italian, Portuguese and Scandinavian text.
        ['Ü'] = 'U', ['Ö'] = 'O', ['Ç'] = 'C', ['Ä'] = 'A', ['ß'] = 'S',
        ['Á'] = 'A', ['À'] = 'A', ['Â'] = 'A', ['Ã'] = 'A', ['Å'] = 'A',
        ['É'] = 'E', ['È'] = 'E', ['Ê'] = 'E', ['Ë'] = 'E',
        ['Í'] = 'I', ['Ì'] = 'I', ['Î'] = 'I', ['Ï'] = 'I',
        ['Ó'] = 'O', ['Ò'] = 'O', ['Ô'] = 'O', ['Õ'] = 'O', ['Ø'] = 'O',
        ['Ú'] = 'U', ['Ù'] = 'U', ['Û'] = 'U',
        ['Ñ'] = 'N', ['Ý'] = 'Y',
    };

    /// <summary>
    /// How closely a read string matches the expected one, 0-100.
    ///
    /// Compared case- and space-insensitively: an inspection cares whether the right code is on the
    /// part, not whether OCR agreed about spacing. A percentage rather than a yes/no because a
    /// single misread character on an otherwise perfect line should not read the same as a blank.
    /// </summary>
    public static double Percent(string read, string expected, bool ignoreDiacritics = false)
    {
        var a = Normalise(read, ignoreDiacritics);
        var b = Normalise(expected, ignoreDiacritics);

        if (b.Length == 0)
        {
            return a.Length == 0 ? 100 : 0;
        }

        var distance = Levenshtein(a, b);
        return Math.Max(0, 100.0 * (1.0 - (double)distance / Math.Max(a.Length, b.Length)));
    }

    private static string Normalise(string text, bool ignoreDiacritics)
    {
        var upper = text.Where(c => !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant);
        if (!ignoreDiacritics)
        {
            return new string(upper.ToArray());
        }

        return new string(upper.Select(c => DiacriticFolding.GetValueOrDefault(c, c)).ToArray());
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        // Two rows are enough: each cell only needs the row above and the cell to its left.
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
