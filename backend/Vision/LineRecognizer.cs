using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MTCVision.Backend.Vision;

/// <summary>
/// Runs a recognition model straight on the region, with no detection step in front of it.
///
/// The stock PP-OCR packs are used through the full detect-then-recognise pipeline, which is right
/// for a photograph of a whole scene. An inspection window is not that: the operator drew it around
/// one line of text, so the line is already located. Putting a detector in front of it re-crops and
/// re-scales what was handed in, and a recogniser trained on our own framing measurably loses that
/// argument — the model we trained read six live panel regions at 96% character similarity when fed
/// the region directly against 51% through the detector, on the same frame in the same minute.
///
/// The preprocessing matches what the model was trained on exactly: greyscale, scaled to a fixed
/// height with the aspect kept, mapped to [-1, 1], and repeated across three channels. A mismatch
/// here does not fail loudly, it just reads the wrong letters, so it is worth keeping literal.
/// </summary>
public sealed class LineRecognizer : IDisposable
{
    private const int InputHeight = 48;
    private const int MinWidth = 16;

    /// <summary>Beyond this the line is squeezed rather than grown, to bound one inference.</summary>
    private const int MaxWidth = 1600;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string[] _dictionary;

    public LineRecognizer(string modelPath, string dictionaryPath, int threads)
    {
        // Qualified: ASP.NET Core has a SessionOptions of its own in scope here.
        var options = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
        };

        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();

        // One character per line. Blank lines are dropped, but a line holding a single space is a
        // legitimate character and is kept.
        _dictionary = File.ReadAllLines(dictionaryPath).Where(line => line.Length > 0).ToArray();
        if (_dictionary.Length == 0)
        {
            throw new InvalidOperationException($"Sözlük boş: {Path.GetFileName(dictionaryPath)}");
        }
    }

    public (string Text, double Confidence) Read(Mat region)
    {
        using var gray = region.Channels() == 1
            ? region.Clone()
            : region.CvtColor(ColorConversionCodes.BGR2GRAY);

        var width = Math.Clamp(
            (int)Math.Round(gray.Width * (double)InputHeight / Math.Max(1, gray.Height)),
            MinWidth,
            MaxWidth);

        using var resized = new Mat();
        Cv2.Resize(gray, resized, new Size(width, InputHeight), 0, 0, InterpolationFlags.Linear);

        using var normalised = new Mat();
        resized.ConvertTo(normalised, MatType.CV_32FC1, 1.0 / 255.0);

        var tensor = new DenseTensor<float>([1, 3, InputHeight, width]);
        for (var y = 0; y < InputHeight; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // The same shift the training pipeline applied: (value - 0.5) / 0.5.
                var value = (normalised.At<float>(y, x) - 0.5f) * 2.0f;
                tensor[0, 0, y, x] = value;
                tensor[0, 1, y, x] = value;
                tensor[0, 2, y, x] = value;
            }
        }

        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        return Decode(results.First().AsTensor<float>());
    }

    /// <summary>
    /// Greedy CTC: take the best class at each step, drop blanks, and collapse runs of the same
    /// class. Class 0 is the blank and the class past the end of the dictionary is a space, which
    /// is the layout the model was trained with.
    /// </summary>
    private (string Text, double Confidence) Decode(Tensor<float> output)
    {
        var steps = output.Dimensions[1];
        var classes = output.Dimensions[2];
        var space = _dictionary.Length + 1;

        if (classes != _dictionary.Length + 2)
        {
            throw new InvalidOperationException(
                $"Model {classes} sınıf üretiyor, sözlükte {_dictionary.Length} karakter var.");
        }

        var glyphs = new List<(string Glyph, double Confidence)>();
        var previous = -1;

        for (var step = 0; step < steps; step++)
        {
            var best = 0;
            var highest = float.NegativeInfinity;
            for (var index = 0; index < classes; index++)
            {
                var score = output[0, step, index];
                if (score > highest)
                {
                    highest = score;
                    best = index;
                }
            }

            if (best != previous && best != 0)
            {
                // The model emits logits, so the score only means something once it is softmaxed
                // against the rest of the step.
                var total = 0.0;
                for (var index = 0; index < classes; index++)
                {
                    total += Math.Exp(output[0, step, index] - highest);
                }

                glyphs.Add((best == space ? " " : _dictionary[best - 1], 1.0 / total));
            }

            previous = best;
        }

        if (glyphs.Count == 0)
        {
            return (string.Empty, 0);
        }

        var text = string.Concat(glyphs.Select(glyph => glyph.Glyph)).Trim();
        return (text, glyphs.Average(glyph => glyph.Confidence) * 100);
    }

    public void Dispose() => _session.Dispose();
}
