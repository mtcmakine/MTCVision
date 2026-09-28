using MTCVision.Backend.Recipes;

namespace MTCVision.Backend.Inspection;

/// <summary>
/// Derives a window's limits from readings taken off parts the operator judged good or bad — the
/// equivalent of the reference console's Tool Auto Tuning step.
///
/// Limits guessed by hand are the usual reason an inspection is either blind or over-sensitive.
/// Taught from samples they sit where the parts actually are, and the caller is told plainly when
/// the good and bad readings overlap, because then no threshold on this measurement can separate
/// them and the answer is a different tool or better lighting rather than a different number.
/// </summary>
public static class ToolAutoTuner
{
    /// <summary>Margin around a good-only cluster, as a fraction of its spread.</summary>
    private const double SpreadMargin = 0.5;

    /// <summary>Fallback margin when every good reading is identical, so the band is never zero-width.</summary>
    private const double MinimumMargin = 2;

    public static AutoLimits Compute(IReadOnlyList<ToolSample> samples)
    {
        var good = samples.Where(s => s.IsGood).Select(s => s.Value).OrderBy(v => v).ToList();
        var bad = samples.Where(s => !s.IsGood).Select(s => s.Value).OrderBy(v => v).ToList();

        if (good.Count == 0)
        {
            return new AutoLimits(0, 0, false,
                "Limit hesaplamak için en az bir iyi parça örneği gerekir.");
        }

        var goodMin = good[0];
        var goodMax = good[^1];
        var margin = Math.Max(MinimumMargin, (goodMax - goodMin) * SpreadMargin);

        if (bad.Count == 0)
        {
            return new AutoLimits(
                Math.Round(goodMin - margin, 2),
                Math.Round(goodMax + margin, 2),
                true,
                "Yalnızca iyi parça örneği var; limitler ölçülen aralığın çevresine kuruldu. " +
                "Kötü parça da öğretirseniz sınır daha isabetli olur.");
        }

        // Bad readings that fall inside the good range cannot be excluded by any threshold.
        if (bad.Any(value => value >= goodMin && value <= goodMax))
        {
            return new AutoLimits(
                Math.Round(goodMin - margin, 2),
                Math.Round(goodMax + margin, 2),
                false,
                "İyi ve kötü parçaların ölçümleri iç içe geçiyor; bu araçla ayrılamazlar. " +
                "Pencereyi daraltmayı, başka bir araç seçmeyi veya aydınlatmayı gözden geçirin.");
        }

        // Put each boundary midway between the good cluster and the nearest bad reading on that
        // side, so both have equal room before a borderline part flips the verdict.
        var badBelow = bad.Where(value => value < goodMin).ToList();
        var badAbove = bad.Where(value => value > goodMax).ToList();

        var lower = badBelow.Count > 0 ? (goodMin + badBelow[^1]) / 2 : goodMin - margin;
        var upper = badAbove.Count > 0 ? (goodMax + badAbove[0]) / 2 : goodMax + margin;

        return new AutoLimits(
            Math.Round(lower, 2),
            Math.Round(upper, 2),
            true,
            $"{good.Count} iyi, {bad.Count} kötü örnekten hesaplandı.");
    }
}

public sealed record AutoLimits(
    double LowerLimit,
    double UpperLimit,
    /// <summary>False when the samples cannot be separated by a threshold on this measurement.</summary>
    bool Separable,
    string Message);
