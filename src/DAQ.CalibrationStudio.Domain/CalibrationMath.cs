namespace DAQ.CalibrationStudio.Domain;

public readonly record struct SampleStatistics(long Count, double Mean, double StandardDeviation, double Min, double Max)
{
    public double Spread => Max - Min;
}

public sealed class SampleAccumulator
{
    private long count;
    private double mean;
    private double squared;
    private double min = double.PositiveInfinity;
    private double max = double.NegativeInfinity;

    public void Add(double value, long samples = 1, double deviation = 0, double? minimum = null, double? maximum = null)
    {
        if (!double.IsFinite(value) || !double.IsFinite(deviation) || deviation < 0 || samples < 1 ||
            minimum is double low && (!double.IsFinite(low) || low > value) ||
            maximum is double high && (!double.IsFinite(high) || high < value))
            throw new ArgumentException("Ungültiger Messblock.");
        var total = checked(count + samples);
        var delta = value - mean;
        squared += deviation * deviation * (samples - 1) + delta * delta * ((double)count * samples / total);
        mean += delta * samples / total;
        min = Math.Min(min, minimum ?? value);
        max = Math.Max(max, maximum ?? value);
        count = total;
    }

    public SampleStatistics Statistics => count > 0
        ? new(count, mean, Math.Sqrt(squared / Math.Max(1, count - 1)), min, max)
        : throw new InvalidOperationException("Keine Messwerte verfügbar.");
}

public static class CalibrationMath
{
    public static double? PercentOfReading(double error, double reference) =>
        reference == 0 ? null : error / Math.Abs(reference) * 100;

    public static double? PercentOfSpan(double error, double span) => span > 0 ? error / span * 100 : null;

    public static bool IsStable(SampleStatistics stats, double criterion) =>
        stats.Count >= 2 && stats.StandardDeviation <= criterion && stats.Spread <= 6 * criterion;

    public static double AbsoluteTolerance(ChannelDefinition channel, double reference, double tolerance) =>
        channel.Quantity == Quantity.Frequency ? Math.Abs(reference) * tolerance / 1_000_000 : tolerance;

    public static bool IsWithinTolerance(double error, double absoluteTolerance) =>
        double.IsFinite(error) && double.IsFinite(absoluteTolerance) && absoluteTolerance >= 0 &&
        Math.Abs(error) <= absoluteTolerance + 1e-12;

    public static Adjustment? Fit(string channel, IReadOnlyList<PointResult> points, FitMethod method, Coefficient active)
    {
        if (method == FitMethod.SinglePoint || points.Count == 0)
            return null;
        if (!double.IsFinite(active.Gain) || active.Gain <= 0 || !double.IsFinite(active.Offset) ||
            points.Any(point => !double.IsFinite(point.Uncorrected) || !double.IsFinite(point.Reference)))
            throw new ArgumentException("Ungültige Koeffizienten oder Messwerte.");

        double gain;
        double offset;
        if (method == FitMethod.OffsetOnly)
        {
            gain = active.Gain;
            offset = points.Average(point => point.Reference - gain * point.Uncorrected);
        }
        else
        {
            var sorted = points.OrderBy(point => point.Reference).ToArray();
            var low = sorted.First();
            var high = sorted.Last();
            if (high.Reference <= low.Reference)
                throw new InvalidOperationException($"{channel}: Für den Gain sind zwei unterschiedliche Referenzpunkte erforderlich.");

            var meanReference = method == FitMethod.TwoPoint ? low.Reference : points.Average(point => point.Reference);
            var meanMeasured = method == FitMethod.TwoPoint ? low.Uncorrected : points.Average(point => point.Uncorrected);
            var slope = method == FitMethod.TwoPoint
                ? (high.Uncorrected - low.Uncorrected) / (high.Reference - low.Reference)
                : points.Sum(point => (point.Reference - meanReference) * (point.Uncorrected - meanMeasured))
                    / points.Sum(point => Math.Pow(point.Reference - meanReference, 2));
            if (!double.IsFinite(slope) || slope <= 0)
                throw new InvalidOperationException($"{channel}: Steigung ist nicht plausibel.");
            gain = 1 / slope;
            offset = meanReference - gain * meanMeasured;
        }

        var residual = points.Max(point => Math.Abs(gain * point.Uncorrected + offset - point.Reference));
        var ratios = points.Where(point => point.ExpandedUncertainty is > 0)
            .Select(point => point.Tolerance / point.ExpandedUncertainty!.Value).ToArray();
        return new Adjustment(channel, active, new Coefficient(gain, offset, checked(active.Version + 1)),
            residual, ratios.Length == points.Count ? ratios.Min() : null);
    }

    public static double ExpandedUncertainty(double referenceStandard, SampleStatistics stats) =>
        2 * Math.Sqrt(referenceStandard * referenceStandard + stats.StandardDeviation * stats.StandardDeviation / stats.Count);
}
