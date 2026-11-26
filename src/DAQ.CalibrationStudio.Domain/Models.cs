using System.Text.Json.Serialization;

namespace DAQ.CalibrationStudio.Domain;

public enum Quantity { Voltage, Current, Resistance, Frequency, Digital }
public enum ChannelGroup { Analog, Rtd, Counter, Digital }
public enum FitMethod { SinglePoint, OffsetOnly, TwoPoint, Regression }
public enum RunKind { Functional, Calibration, Verification }
public enum RunOutcome { Passed, Failed, Aborted, Error }
public enum StepKind { AnalogPoint, FrequencyPoint, DigitalPoint, Wait }

public sealed record ChannelDefinition(
    string Id, string Name, Quantity Quantity, ChannelGroup Group,
    double Minimum, double Maximum, double Tolerance, string Range,
    string Source, double AdcScale = 0, string? DigitalOutput = null, bool Unwired = false)
{
    public double Span => Maximum - Minimum;
    public string Unit => ChannelCatalog.Unit(Quantity);
}

public sealed record ChannelSample(
    string ChannelId, int? Raw, double? Uncorrected, double? Corrected,
    double? Min, double? Max, double? Setpoint, double? StdDev = null, int Count = 1);

public sealed record DaqFrame(
    long Sequence, DateTimeOffset Time, IReadOnlyList<ChannelSample> Samples, bool DeviceTimed = false);

public sealed record TestStep(
    StepKind Kind, string ChannelId, double Value = 0, double Duration = 1,
    double Tolerance = 0, double Stability = 0.001, double Window = 1, string Label = "");

public sealed record TestProfile(
    Guid Id, int Version, string Name, RunKind Kind, FitMethod Method, IReadOnlyList<TestStep> Steps,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SnapshotOnly = false)
{
    public override string ToString() => Name;
}

public sealed record ReferenceInfo(
    string Model, string? Serial = null, string? Accuracy = null, DateOnly? CalibratedOn = null,
    string? Certificate = null, double? Uncertainty = null, bool Traceable = false);

public sealed record DaqIdentity(
    string Model, string? Serial = null, string? Firmware = null, string? Revision = null);

public sealed record PointResult(
    string ChannelId, Quantity Quantity, double Requested, double? Confirmed, double? IndependentReference,
    double Reference, double Measured, double Uncorrected, double StandardDeviation, int Samples,
    double Error, double? PercentReading, double? PercentSpan, double Tolerance, bool Passed,
    double? ExpandedUncertainty = null);

public sealed record Coefficient(double Gain, double Offset, int Version = 0, DateOnly? CalibratedOn = null)
{
    public static Coefficient Identity { get; } = new(1, 0);
    public double Correct(double value) => Gain * value + Offset;
}

public sealed record Adjustment(
    string ChannelId, Coefficient Original, Coefficient Proposed, double MaxResidual, double? Tur = null);

public sealed record RunResult(
    Guid Id, DateTimeOffset Started, DateTimeOffset Finished, TestProfile Profile,
    DaqIdentity Daq, IReadOnlyList<ReferenceInfo> References,
    string Operator, string Conditions, bool Simulated, RunOutcome Outcome,
    IReadOnlyList<PointResult> Points, IReadOnlyList<Adjustment> Adjustments, string? Error = null,
    IReadOnlyList<ChannelDefinition>? Channels = null, IReadOnlyList<int>? SkippedSteps = null)
{
    public string DisplayId => $"PR-{Started:yyyy}-{Id.ToString("N")[..8].ToUpperInvariant()}";
}

public sealed record Approval(
    Guid Id, Guid RunId, DateTimeOffset Time, string Operator, string Target,
    IReadOnlyList<Adjustment> Adjustments, bool Verified, string? Error = null);

public sealed record StudioSettings(
    string Operator = "", string Conditions = "", bool LightTheme = false,
    IReadOnlyList<ReferenceInfo>? References = null,
    IReadOnlyDictionary<string, double>? Tolerances = null,
    string DataDirectory = "");
