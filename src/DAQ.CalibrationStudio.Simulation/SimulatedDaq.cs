using System.Text.Json;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.Simulation;

public sealed class SimulatedDaq : SimulatedSession, IDaqDevice
{
    private readonly SimulatedDesk desk;
    private readonly string? coefficientPath;
    private readonly Random noise = new(38_192);
    private static readonly AnalogModel[] Analog =
    [
        new(0.99874, 0.0118, 0.00030, 0.00042, 0.001, 0.0004, 5, new(2026, 9, 14)),
        new(1.00121, -0.0074, 0.00025, 0.00045, -0.001, -0.0005, 10, new(2026, 9, 14)),
        new(0.99702, 0.0215, 0.00040, 0.00050, 0.015, 0.002, 2.5, new(2025, 10, 30)),
        new(1.00233, 0.0142, 0.00030, 0.00090, 0.004, 0.0005, 4, new(2026, 9, 14)),
        new(0.99811, -0.0096, 0.00035, 0.00095, 0.006, 0.001, 12, new(2026, 9, 14)),
        new(1.00066, 0.0051, 0.00025, 0.00085, 0.002, 0.0001, 20, new(2026, 9, 14)),
        new(1.00042, 0.031, 0.00002, 0.003, 0.006, 0.00002, 100, new(2026, 8, 21)),
        new(0.99961, -0.022, 0.00002, 0.0032, 0.010, 0.00003, 138.506, new(2026, 8, 21)),
        new(1.00030, 0.21, 0.00002, 0.025, 0.05, 0.00002, 1000, new(2026, 8, 21)),
        new(1.00051, 0.38, 0.00002, 0.026, 0, 0, 1000, null)
    ];

    private readonly Dictionary<string, double> stimuli = [];
    private readonly Dictionary<string, bool> digital = new() { ["DO1"] = false, ["DO2"] = false, ["DO3"] = false, ["DO4"] = false };
    private Dictionary<string, Coefficient> flash;
    private Dictionary<string, Coefficient> active;
    private long sequence;
    private volatile string? selectedRoute;
    private double noiseFactor = 1;

    public SimulatedDaq(SimulatedDesk desk, string? coefficientPath) : base(
        DeviceKind.Daq, "STM32H7-DAQ", "USB · CDC (sim.)",
        DeviceCapability.Measurements | DeviceCapability.Route | DeviceCapability.DigitalOutput |
        DeviceCapability.Coefficients | DeviceCapability.Diagnostics,
        new("PBD-DAQ H7", "DAQ-H7-SIM-0042", "SIM 1.0", "C.2"))
    {
        this.desk = desk;
        this.coefficientPath = coefficientPath;
        flash = new Dictionary<string, Coefficient>();
        for (var i = 0; i < Analog.Length; i++)
        {
            var channel = ChannelCatalog.All[i];
            var model = Analog[i];
            stimuli[channel.Id] = model.Stimulus;
            var gain = (1 + model.ResidualGain) / model.HardwareGain;
            flash[channel.Id] = model.CalibratedOn is null ? Coefficient.Identity
                : new(gain, model.ResidualOffset - gain * model.HardwareOffset, 1, model.CalibratedOn);
        }
        if (coefficientPath is not null && File.Exists(coefficientPath))
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, Coefficient>>(File.ReadAllText(coefficientPath))
                ?? throw new InvalidDataException("Simulierter Flash enthält keine Koeffizienten.");
            foreach (var (id, coefficient) in saved)
            {
                ValidateCoefficient(id, coefficient);
                flash[id] = coefficient;
            }
        }
        active = new(flash);
    }

    public string? SelectedRoute => selectedRoute;
    public double NoiseFactor
    {
        get => noiseFactor;
        set => noiseFactor = double.IsFinite(value) && value is >= 0 and <= 10 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public Task<DaqFrame> ReadFrameAsync(CancellationToken ct)
    {
        Check(ct);
        var samples = ChannelCatalog.All.Select((channel, index) => index < Analog.Length
            ? AnalogSample(channel, Analog[index]) : OtherSample(channel)).ToArray();
        return Task.FromResult(new DaqFrame(++sequence, desk.Timestamp, samples));
    }

    public Task SetRouteAsync(string channelId, CancellationToken ct)
    {
        Check(ct);
        if (ChannelCatalog.Get(channelId).Group != ChannelGroup.Analog)
            throw new ArgumentException("Der Prüfadapter schaltet nur Spannung und Strom.");
        if (desk.Source.Enabled)
            throw new InvalidOperationException("Prüfadapter nur bei deaktiviertem FNIRSI umschalten.");
        selectedRoute = channelId;
        return Task.CompletedTask;
    }

    public void SetStimulus(string channelId, double value, CancellationToken ct)
    {
        Check(ct);
        var channel = ChannelCatalog.Get(channelId);
        if (!stimuli.ContainsKey(channelId) || !double.IsFinite(value) || value < channel.Minimum || value > channel.Maximum)
            throw new ArgumentOutOfRangeException(nameof(value), "Eingangswert außerhalb des konfigurierten Bereichs.");
        stimuli[channelId] = value;
    }

    public Task SetDigitalOutputAsync(string outputId, bool value, CancellationToken ct)
    {
        Check(ct);
        if (!digital.ContainsKey(outputId))
            throw new ArgumentException("Unbekannter simulierter Digitalausgang.");
        digital[outputId] = value;
        return Task.CompletedTask;
    }

    public Task<Coefficient> ReadCoefficientAsync(string channelId, CancellationToken ct) =>
        Ready(ct, active.GetValueOrDefault(channelId) ?? Coefficient.Identity);

    public async Task WriteCoefficientsAsync(IReadOnlyDictionary<string, Coefficient> coefficients, bool persistent, CancellationToken ct)
    {
        Check(ct);
        foreach (var (id, coefficient) in coefficients)
            ValidateCoefficient(id, coefficient);
        if (persistent)
        {
            var next = new Dictionary<string, Coefficient>(flash);
            foreach (var (id, coefficient) in coefficients)
                next[id] = coefficient;
            if (coefficientPath is not null)
                await WriteFlashAsync(coefficientPath, next, ct).ConfigureAwait(false);
            flash = next;
        }
        foreach (var (id, coefficient) in coefficients)
            active[id] = coefficient;
    }

    protected override void ResetOutputs()
    {
        selectedRoute = null;
        foreach (var id in digital.Keys.ToArray())
            digital[id] = false;
        active = new(flash);
    }

    private ChannelSample AnalogSample(ChannelDefinition channel, AnalogModel model)
    {
        var routed = selectedRoute == channel.Id;
        var sourceMatched = desk.Source.Enabled && desk.Source.Mode == channel.Quantity;
        var stimulus = routed ? (sourceMatched ? desk.Source.Actual : 0) : stimuli[channel.Id];
        var setpoint = routed ? (sourceMatched ? desk.Source.Confirmed : (double?)null) : stimulus;
        var fraction = (stimulus - channel.Minimum) / channel.Span;
        var center = stimulus * model.HardwareGain + model.HardwareOffset +
            model.Inl * fraction * (1 - fraction) * channel.Span;
        var coefficient = active[channel.Id];
        var values = new SampleAccumulator();
        var rawCode = 0;
        const double codeMax = 8_388_608;
        var factor = channel.AdcScale * codeMax / 2.4;
        for (var i = 0; i < 16; i++)
        {
            var reading = center + Gaussian() * model.Noise * noiseFactor;
            rawCode = (int)Math.Clamp(Math.Round(reading * factor), -codeMax, codeMax - 1);
            values.Add(rawCode / factor);
        }
        var stats = values.Statistics;
        return new(channel.Id, rawCode, stats.Mean, coefficient.Correct(stats.Mean), coefficient.Correct(stats.Min),
            coefficient.Correct(stats.Max), setpoint, stats.StandardDeviation, (int)stats.Count);
    }

    private ChannelSample OtherSample(ChannelDefinition channel)
    {
        var (a, b) = desk.Generator.CurrentPair();
        double? expected = channel.Id switch
        {
            "ENC1" when desk.Generator.Status.Connected && a.Enabled && b.Enabled &&
                a.Frequency == b.Frequency && a.Amplitude >= 2 && b.Amplitude >= 2 &&
                (a.Shape is SignalShape.Square or SignalShape.Pulse) && (b.Shape is SignalShape.Square or SignalShape.Pulse) &&
                Math.Abs(Math.IEEERemainder(b.Phase - a.Phase, 180)) >= 15 => a.Frequency,
            _ when channel.DigitalOutput is string output => digital[output] ? 1 : 0,
            _ => null
        };
        var measured = channel.Quantity == Quantity.Frequency && expected > 0
            ? expected.Value * (1 + 18e-6) + Gaussian() * expected.Value * 0.7e-6
            : expected ?? 0;
        return new(channel.Id, null, measured, measured, measured, measured, expected);
    }

    private double Gaussian() => Math.Sqrt(-2 * Math.Log(1 - noise.NextDouble())) * Math.Cos(2 * Math.PI * noise.NextDouble());

    private void ValidateCoefficient(string channelId, Coefficient coefficient)
    {
        if (!stimuli.ContainsKey(channelId) || !double.IsFinite(coefficient.Gain) || coefficient.Gain is < 0.5 or > 1.5 ||
            !double.IsFinite(coefficient.Offset) || Math.Abs(coefficient.Offset) > ChannelCatalog.Get(channelId).Span / 5 ||
            coefficient.Version < 0)
            throw new ArgumentException($"Unplausible Koeffizienten für {channelId}.");
    }

    private static async Task WriteFlashAsync(string path, Dictionary<string, Coefficient> coefficients, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(coefficients), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record AnalogModel(double HardwareGain, double HardwareOffset, double Inl, double Noise,
        double ResidualOffset, double ResidualGain, double Stimulus, DateOnly? CalibratedOn);
}
