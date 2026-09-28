namespace MTCVision.Backend.Camera;

/// <summary>
/// Brings the image to a target brightness by adjusting exposure, then gain — the equivalent of the
/// reference console's "Automatic Brightness Adjustment" step.
///
/// The camera has its own regulator (autoBrightnessMode), but on a scene this dark it walked the
/// exposure the wrong way and never converged. A closed loop of our own is deterministic, bounded,
/// and reports what it did; it also ends with fixed exposure and gain, which matters because a
/// recipe's taught limits only mean something under the conditions they were taught with.
/// </summary>
public sealed class AutoBrightnessTuner(ICameraService camera, LatestFrameStore frames)
{
    /// <summary>Close enough: chasing a mean grey level more precisely than this is noise.</summary>
    private const double ToleranceGrey = 5;

    private const int MaxIterations = 12;

    /// <summary>
    /// Per-step limit once there is a reading to steer by. Correcting in one jump overshoots into
    /// saturation, from which the mean gives no useful direction back.
    /// </summary>
    private const double MaxStepFactor = 4;

    /// <summary>A black frame carries no ratio, so it is walked up blind in larger strides.</summary>
    private const double BlindStepFactor = 8;

    /// <param name="minFrameRateHz">
    /// The floor the frame rate may be lowered to in order to buy exposure time. Without one, a dark
    /// scene converges on a multi-second exposure that is technically correct and operationally
    /// useless, so the caller states what rate the line actually needs.
    /// </param>
    public async Task<AutoBrightnessResult> RunAsync(
        int target,
        double minFrameRateHz = 1.0,
        CancellationToken ct = default)
    {
        var before = await camera.GetSettingsAsync();

        // Make the floor a real guarantee before converging. A camera left on a multi-second
        // exposure cannot simply be told to run faster: the exposure has to come down first, in its
        // own call, or the new frame rate is clamped straight back down again.
        if ((before.FrameRateHz ?? 0) < minFrameRateHz)
        {
            await camera.ApplySettingsAsync(new CameraSettingsUpdate(ExposureTimeUs: 0.8 * 1_000_000 / minFrameRateHz));
            await camera.ApplySettingsAsync(new CameraSettingsUpdate(FrameRateHz: minFrameRateHz));
        }

        var current = await camera.GetSettingsAsync();
        var steps = new List<AutoBrightnessStep>();

        var exposure = current.ExposureTimeUs ?? 0;
        var gain = current.Gain ?? 1;

        for (var iteration = 1; iteration <= MaxIterations; iteration++)
        {
            var measured = await MeasureFreshAsync(ct);
            steps.Add(new AutoBrightnessStep(iteration, Math.Round(measured, 1), Math.Round(exposure), Math.Round(gain, 2)));

            if (Math.Abs(measured - target) <= ToleranceGrey)
            {
                break;
            }

            var factor = measured < 1
                ? BlindStepFactor
                : Math.Clamp(target / measured, 1 / MaxStepFactor, MaxStepFactor);

            var settings = await camera.GetSettingsAsync();
            var wantedExposure = exposure * factor;

            // The camera reports exposure's absolute ceiling (16 s) regardless of frame rate, and
            // silently drops the frame rate to honour whatever exposure is written. So the frame
            // rate floor has to be enforced here, as a ceiling on exposure; gain covers the rest.
            var rateLimitedExposureMax = 0.9 * 1_000_000 / minFrameRateHz;
            var exposureMax = Math.Min(settings.ExposureRange?.Max ?? exposure, rateLimitedExposureMax);

            var newExposure = Math.Clamp(wantedExposure, settings.ExposureRange?.Min ?? 1, exposureMax);

            // Whatever exposure could not deliver is asked of gain.
            var remaining = newExposure > 0 ? wantedExposure / newExposure : 1;
            var newGain = Math.Clamp(
                gain * remaining,
                settings.GainRange?.Min ?? 1,
                settings.GainRange?.Max ?? gain);

            var exposureSettled = Math.Abs(newExposure - exposure) < 1;
            var gainSettled = Math.Abs(newGain - gain) < 0.01;
            if (exposureSettled && gainSettled)
            {
                // Both controls are against their stops; the scene simply cannot reach the target.
                break;
            }

            exposure = newExposure;
            gain = newGain;
            await camera.ApplySettingsAsync(new CameraSettingsUpdate(ExposureTimeUs: exposure, Gain: gain));

            var applied = await camera.GetSettingsAsync();
            exposure = applied.ExposureTimeUs ?? exposure;
            gain = applied.Gain ?? gain;
        }

        var after = await camera.GetSettingsAsync();
        var finalBrightness = steps.Count > 0 ? steps[^1].Brightness : (double?)null;
        var reached = finalBrightness is not null && Math.Abs(finalBrightness.Value - target) <= ToleranceGrey;

        return new AutoBrightnessResult(
            Target: target,
            ExposureTimeUsBefore: before.ExposureTimeUs,
            GainBefore: before.Gain,
            ExposureTimeUs: after.ExposureTimeUs,
            Gain: after.Gain,
            FrameRateHz: after.FrameRateHz,
            MeasuredBrightness: finalBrightness,
            Reached: reached,
            Message: Explain(reached, finalBrightness, target, after, minFrameRateHz),
            Steps: steps);
    }

    private static string? Explain(
        bool reached,
        double? brightness,
        int target,
        CameraSettings after,
        double minFrameRateHz)
    {
        if (reached)
        {
            return null;
        }

        if (brightness is not null && brightness < target)
        {
            var atGainCeiling = after.Gain is not null
                && after.GainRange?.Max is not null
                && after.Gain >= after.GainRange.Max - 0.01;

            return atGainCeiling || after.FrameRateHz <= minFrameRateHz + 0.01
                ? $"Sahne bu kare hızında ({minFrameRateHz:0.##} Hz alt sınırı) hedefe ulaşacak kadar aydınlık değil. " +
                  "Aydınlatma ekleyin veya lens diyaframını açın."
                : "Hedefe ulaşılamadı; ayarları elle inceleyin.";
        }

        return "Hedefin üstünde kalındı; sahne bu ayarlarla fazla parlak.";
    }

    /// <summary>
    /// Measures a frame captured after the settings change, not the one already in hand — the store
    /// still holds the previous exposure's image until the camera delivers the next.
    /// </summary>
    private async Task<double> MeasureFreshAsync(CancellationToken ct)
    {
        var stale = frames.Latest?.TimestampUtc;
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var frame = frames.Latest;
            if (frame is not null && frame.TimestampUtc != stale)
            {
                return ImageStatistics.MeanGrey(frame);
            }

            await Task.Delay(100, ct);
        }

        return ImageStatistics.MeanGrey(frames.Require());
    }
}

public sealed record AutoBrightnessResult(
    int Target,
    double? ExposureTimeUsBefore,
    double? GainBefore,
    double? ExposureTimeUs,
    double? Gain,
    double? FrameRateHz,
    double? MeasuredBrightness,
    bool Reached,
    /// <summary>Why the target was not met, when it was not; null on success.</summary>
    string? Message,
    IReadOnlyList<AutoBrightnessStep> Steps);

public sealed record AutoBrightnessStep(int Iteration, double Brightness, double ExposureTimeUs, double Gain);
