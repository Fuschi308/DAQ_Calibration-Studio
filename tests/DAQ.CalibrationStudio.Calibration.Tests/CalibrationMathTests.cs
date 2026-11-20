using DAQ.CalibrationStudio.Domain;
using Xunit;

namespace DAQ.CalibrationStudio.Calibration.Tests;

public sealed class CalibrationMathTests
{
    [Fact]
    public void RegressionAndOffsetFitsRecoverAnalyticCorrection()
    {
        var measured = new[] { 1d, 2d, 3d };
        var points = measured.Select(value => Point(value, 2 * value + 1)).ToArray();
        var fit = CalibrationMath.Fit("AI-V1", points, FitMethod.Regression, Coefficient.Identity)!;
        Assert.Equal(2, fit.Proposed.Gain, 10);
        Assert.Equal(1, fit.Proposed.Offset, 10);
        Assert.InRange(fit.MaxResidual, 0, 1e-10);
        Assert.Equal(4, fit.Tur);

        var offset = CalibrationMath.Fit("AI-V1", points, FitMethod.OffsetOnly, new Coefficient(2, -10, 7))!;
        Assert.Equal(2, offset.Proposed.Gain);
        Assert.Equal(1, offset.Proposed.Offset, 10);
        Assert.Equal(8, offset.Proposed.Version);
        Assert.Null(CalibrationMath.Fit("AI-V1", points, FitMethod.SinglePoint, Coefficient.Identity));
    }

    [Fact]
    public void TwoPointFitRejectsInsufficientReferenceSpread()
    {
        var error = Assert.Throws<InvalidOperationException>(() => CalibrationMath.Fit("AI-V1",
            [Point(1, 3), Point(2, 3)], FitMethod.TwoPoint, Coefficient.Identity));
        Assert.Contains("zwei unterschiedliche Referenzpunkte", error.Message);
    }

    [Fact]
    public void FrequencyToleranceIsPpmAndOtherTolerancesRemainAbsolute()
    {
        var frequency = ChannelCatalog.Get("ENC1");
        var voltage = ChannelCatalog.Get("AI-V1");
        Assert.Equal(5, CalibrationMath.AbsoluteTolerance(frequency, 100_000, 50));
        Assert.Equal(0.008, CalibrationMath.AbsoluteTolerance(voltage, 5, 0.008));
        Assert.True(CalibrationMath.IsWithinTolerance(-5, 5));
        Assert.False(CalibrationMath.IsWithinTolerance(5.01, 5));
        Assert.False(CalibrationMath.IsWithinTolerance(double.NaN, 5));
    }

    [Fact]
    public void PercentagesUnitsAndExpandedUncertaintyUseCorrectScales()
    {
        Assert.Equal(2, CalibrationMath.PercentOfReading(0.1, 5));
        Assert.Null(CalibrationMath.PercentOfReading(0.1, 0));
        Assert.Equal(1, CalibrationMath.PercentOfSpan(0.1, 10));
        Assert.Null(CalibrationMath.PercentOfSpan(0.1, 0));
        Assert.Equal(0.012, ChannelCatalog.ToBaseUnit(12, Quantity.Current), 12);
        Assert.Equal(12, ChannelCatalog.FromBaseUnit(0.012, Quantity.Current), 12);

        var stats = new SampleStatistics(4, 3, 0.2, 2.7, 3.3);
        Assert.Equal(2 * Math.Sqrt(0.3 * 0.3 + 0.2 * 0.2 / 4),
            CalibrationMath.ExpandedUncertainty(0.3, stats), 12);
    }

    [Fact]
    public void SampleAccumulatorCombinesBlocksAndChecksStability()
    {
        var accumulator = new SampleAccumulator();
        accumulator.Add(1);
        accumulator.Add(2);
        accumulator.Add(3);
        var stats = accumulator.Statistics;
        Assert.Equal(3, stats.Count);
        Assert.Equal(2, stats.Mean, 12);
        Assert.Equal(1, stats.StandardDeviation, 12);
        Assert.Equal(2, stats.Spread, 12);
        Assert.False(CalibrationMath.IsStable(stats, 0.9));
        Assert.True(CalibrationMath.IsStable(stats, 1.1));
        Assert.False(CalibrationMath.IsStable(new SampleStatistics(1, 2, 0, 2, 2), 0.1));

        var blocks = new SampleAccumulator();
        blocks.Add(2, samples: 3, deviation: 0.2, minimum: 1.8, maximum: 2.2);
        blocks.Add(4, samples: 3, deviation: 0.2, minimum: 3.8, maximum: 4.2);
        Assert.Equal(6, blocks.Statistics.Count);
        Assert.Equal(3, blocks.Statistics.Mean, 12);
        Assert.InRange(blocks.Statistics.StandardDeviation, 1.109, 1.111);
        Assert.Equal(2.4, blocks.Statistics.Spread, 12);
        Assert.Throws<ArgumentException>(() => blocks.Add(double.NaN));
    }

    private static PointResult Point(double uncorrected, double reference) => new(
        "AI-V1", Quantity.Voltage, reference, reference, null, reference, uncorrected, uncorrected,
        0.01, 10, uncorrected - reference, null, null, 2, true, ExpandedUncertainty: 0.5);
}
