namespace DAQ.CalibrationStudio.Domain;

public sealed record TraceStatistics(
    double Min, double Max, double Average, double Rms, double PeakToPeak, double? Frequency, double? Duty,
    double? FirstRise);

public static class ScopeMath
{
    public static TraceStatistics Analyze(IReadOnlyList<double> times, IReadOnlyList<double> volts, bool enveloped = false)
    {
        if (volts.Count != times.Count || volts.Count < 2)
            throw new ArgumentException("Ein Oszillogramm benötigt Zeit- und Spannungswerte gleicher Länge.");
        if (times.Any(time => !double.IsFinite(time)) || volts.Any(value => !double.IsFinite(value)) ||
            times.Zip(times.Skip(1), (first, second) => second <= first).Any(bad => bad))
            throw new ArgumentException("Oszillogramm enthält ungültige Werte oder Zeitstempel.");

        var min = volts.Min();
        var max = volts.Max();
        var pp = max - min;
        var rising = new List<double>();
        var falling = new List<double>();
        if (!enveloped && pp > 0)
        {
            var midpoint = (min + max) / 2;
            var armed = volts[0] < midpoint - pp * 0.15;
            for (var index = 1; index < times.Count; index++)
            {
                var previous = volts[index - 1] - midpoint;
                var current = volts[index] - midpoint;
                if (current < -pp * 0.15)
                    armed = true;
                if (armed && previous < 0 && current >= 0)
                {
                    rising.Add(Crossing(index, previous, current));
                    armed = false;
                }
                else if (previous > 0 && current <= 0)
                    falling.Add(Crossing(index, previous, current));
            }
        }

        var periods = rising.Zip(rising.Skip(1), (first, second) => second - first).ToArray();
        var interval = (times[^1] - times[0]) / (times.Count - 1);
        double? frequency = null;
        double? duty = null;
        if (periods.Length >= 2 && periods.Min() >= 8 * interval && periods.Max() <= periods.Min() * 1.15)
        {
            frequency = 1 / periods.Average();
            var highDurations = rising.Take(rising.Count - 1)
                .SelectMany((rise, i) => falling.Where(fall => fall > rise && fall < rising[i + 1])
                    .Take(1).Select(fall => (fall - rise) / periods[i]));
            var widths = highDurations.ToArray();
            if (widths.Length == periods.Length)
                duty = widths.Average() * 100;
        }
        return new(min, max, volts.Average(), Math.Sqrt(volts.Average(value => value * value)),
            pp, frequency, duty, frequency.HasValue ? rising[0] : null);

        double Crossing(int index, double previous, double current) =>
            times[index - 1] + (times[index] - times[index - 1]) * (-previous) / (current - previous);
    }

    public static double? Phase(TraceStatistics first, TraceStatistics second)
    {
        if (first.Frequency is not double frequency || second.Frequency is not double other ||
            Math.Abs(frequency / other - 1) > 0.03 || first.FirstRise is not double t1 || second.FirstRise is not double t2)
            return null;
        return Math.IEEERemainder((t1 - t2) * frequency * 360, 360);
    }
}
