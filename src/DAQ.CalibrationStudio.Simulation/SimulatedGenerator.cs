using DAQ.CalibrationStudio.Application;

namespace DAQ.CalibrationStudio.Simulation;

public sealed class SimulatedGenerator(SimulatedDesk desk) : SimulatedSession(
    DeviceKind.Owon, "OWON DGE2035", "USB · USBTMC (sim.)",
    DeviceCapability.Waveform | DeviceCapability.Sweep | DeviceCapability.Quadrature | DeviceCapability.Diagnostics,
    new("DGE2035", "SIM-OW-2035", "SIM 1.0")), IWaveformGenerator
{
    private readonly WaveformSettings[] channels =
    [
        new(1, SignalShape.Sine, 1_000, 5, 0, 0, 50, null, false),
        new(2, SignalShape.Sine, 1_000, 5, 0, 90, 50, null, false)
    ];
    private readonly SweepSettings?[] sweeps = new SweepSettings?[2];
    private readonly double?[] sweepStarted = new double?[2];
    private EncoderMotion? motion;
    private double motionStart;

    public Task<WaveformSettings> ReadWaveformAsync(int channel, CancellationToken ct) => Ready(ct, Current(channel));

    public Task<WaveformSettings> ApplyWaveformAsync(WaveformSettings settings, CancellationToken ct)
    {
        Check(ct);
        Validate(settings);
        if (motion is not null)
            throw new InvalidOperationException("Encoder-Simulation vor der manuellen Kanalsteuerung stoppen.");
        var index = Index(settings.Channel);
        channels[index] = settings;
        if (!settings.Enabled)
            sweepStarted[index] = null;
        return Task.FromResult(settings);
    }

    public Task<OutputState> DisableAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Status.Connected)
            return Task.FromResult(OutputState.Unknown);
        ResetOutputs();
        return Task.FromResult(OutputState.Off);
    }

    public Task<SweepReading> ReadSweepAsync(int channel, CancellationToken ct)
    {
        var index = Index(channel);
        return Ready(ct, new SweepReading(sweeps[index], sweepStarted[index].HasValue));
    }

    public Task SetSweepAsync(SweepSettings settings, bool enabled, CancellationToken ct)
    {
        Check(ct);
        var index = Index(settings.Channel);
        if (enabled)
        {
            if (motion is not null || !channels[index].Enabled)
                throw new InvalidOperationException("Für den Sweep muss der ausgewählte Ausgang eingeschaltet und der Encoder gestoppt sein.");
            if (!double.IsFinite(settings.StartHz) || !double.IsFinite(settings.StopHz) || !double.IsFinite(settings.Seconds) ||
                settings.StartHz <= 0 || settings.StopHz <= 0 || settings.StartHz == settings.StopHz ||
                Math.Max(settings.StartHz, settings.StopHz) > 35_000_000 || settings.Seconds is < 0.01 or > 300)
                throw new ArgumentException("Ungültiger Sweep-Bereich oder ungültige Dauer.");
            sweeps[index] = settings;
        }
        sweepStarted[index] = enabled ? desk.Seconds : null;
        return Task.CompletedTask;
    }

    public void StartEncoder(EncoderMotion settings, CancellationToken ct)
    {
        Check(ct);
        if (settings.PeriodsPerTurn is < 1 or > 1_000_000 || !double.IsFinite(settings.StartRpm) || !double.IsFinite(settings.EndRpm) ||
            Math.Min(settings.StartRpm, settings.EndRpm) < 0 || settings.PeakHz > 1_000_000 ||
            !double.IsFinite(settings.RampSeconds) || !double.IsFinite(settings.DecelerationSeconds) ||
            settings.RampSeconds is < 0 or > 300 || settings.DecelerationSeconds is < 0 or > 300 ||
            !double.IsFinite(settings.Phase) || settings.Phase is < 1 or > 179)
            throw new ArgumentException("Encoderparameter außerhalb des simulierten Arbeitsbereichs.");
        ResetOutputs();
        motion = settings;
        motionStart = desk.Seconds;
    }

    public WaveformSettings Current(int channel) => CurrentAt(channel, desk.Seconds);

    public (WaveformSettings A, WaveformSettings B) CurrentPair()
    {
        var now = desk.Seconds;
        return (CurrentAt(1, now), CurrentAt(2, now));
    }

    private WaveformSettings CurrentAt(int channel, double now)
    {
        var index = Index(channel);
        if (motion is { } encoder)
        {
            var (rpm, active) = encoder.At(now - motionStart);
            var frequency = rpm * encoder.PeriodsPerTurn / 60;
            return new(channel, SignalShape.Square, frequency, 5, 2.5,
                channel == 1 ? 0 : encoder.Phase * (encoder.Reverse ? -1 : 1), 50, null, active);
        }

        var settings = channels[index];
        if (sweeps[index] is not { } sweep || sweepStarted[index] is not double started)
            return settings;
        var position = ((now - started) % sweep.Seconds) / sweep.Seconds;
        var hz = sweep.Logarithmic ? sweep.StartHz * Math.Pow(sweep.StopHz / sweep.StartHz, position)
            : sweep.StartHz + (sweep.StopHz - sweep.StartHz) * position;
        return settings with { Frequency = hz };
    }

    public static double ValueAt(WaveformSettings settings, double seconds)
    {
        if (!settings.Enabled)
            return 0;
        var phase = seconds * settings.Frequency + settings.Phase / 360;
        var cycle = phase - Math.Floor(phase);
        var unit = settings.Shape switch
        {
            SignalShape.Sine => Math.Sin(2 * Math.PI * cycle),
            SignalShape.Square => cycle < 0.5 ? 1 : -1,
            SignalShape.Triangle => 1 - 4 * Math.Abs(cycle - 0.5),
            SignalShape.Ramp => 2 * cycle - 1,
            SignalShape.Pulse => cycle < settings.Duty / 100 ? 1 : -1,
            SignalShape.Noise => Math.Sin(179 * cycle) * Math.Cos(557 * cycle),
            _ => 0
        };
        return settings.Offset + unit * settings.Amplitude / 2;
    }

    protected override void ResetOutputs()
    {
        motion = null;
        for (var index = 0; index < channels.Length; index++)
        {
            channels[index] = channels[index] with { Enabled = false };
            sweepStarted[index] = null;
        }
    }

    private static int Index(int channel) => channel is 1 or 2 ? channel - 1 : throw new ArgumentOutOfRangeException(nameof(channel));

    private static void Validate(WaveformSettings settings)
    {
        if (!double.IsFinite(settings.Frequency) || settings.Frequency is <= 0 or > 35_000_000 ||
            !double.IsFinite(settings.Amplitude) || settings.Amplitude < 0 ||
            !double.IsFinite(settings.Offset) || Math.Abs(settings.Offset) + settings.Amplitude / 2 > 10 ||
            !double.IsFinite(settings.Phase) || settings.Phase is < -360 or > 360 ||
            !double.IsFinite(settings.Duty) || settings.Duty is <= 0 or >= 100 ||
            settings.LoadOhms is double load && (!double.IsFinite(load) || load is < 50 or > 10_000))
            throw new ArgumentException("Frequenz, Pegel oder Pulsparameter außerhalb des simulierten Arbeitsbereichs.");
    }
}

public enum EncoderMode { Constant, Ramp, Coast, Cycle }

public sealed record EncoderMotion(EncoderMode Mode, int PeriodsPerTurn, double StartRpm, double EndRpm,
    double Phase, bool Reverse, double RampSeconds, double DecelerationSeconds)
{
    public double PeakHz => Math.Max(StartRpm, EndRpm) * PeriodsPerTurn / 60;

    public (double Rpm, bool Active) At(double elapsed) => Mode switch
    {
        EncoderMode.Constant => (EndRpm, true),
        EncoderMode.Ramp when elapsed <= RampSeconds => (Interpolate(StartRpm, EndRpm, elapsed, RampSeconds), true),
        EncoderMode.Coast when elapsed <= DecelerationSeconds => (Interpolate(EndRpm, StartRpm, elapsed, DecelerationSeconds), true),
        EncoderMode.Cycle when elapsed <= RampSeconds => (Interpolate(StartRpm, EndRpm, elapsed, RampSeconds), true),
        EncoderMode.Cycle when elapsed <= RampSeconds + DecelerationSeconds =>
            (Interpolate(EndRpm, StartRpm, elapsed - RampSeconds, DecelerationSeconds), true),
        _ => (0, false)
    };

    private static double Interpolate(double start, double end, double elapsed, double duration) =>
        duration > 0 ? start + (end - start) * (elapsed / duration) : end;
}
