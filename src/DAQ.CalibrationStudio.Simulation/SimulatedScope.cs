using DAQ.CalibrationStudio.Application;

namespace DAQ.CalibrationStudio.Simulation;

public sealed class SimulatedScope(SimulatedDesk desk) : SimulatedSession(
    DeviceKind.PeakTech, "PeakTech P 1400", "USB · Bulk (sim.)",
    DeviceCapability.Scope | DeviceCapability.Diagnostics,
    new("P 1400", "SIM-PT-1400", "SIM 1.0")), IScopeDevice
{
    private ScopeSettings settings = new(0.0005, 1, 1, 1, 0, true);
    private ScopeAction action = ScopeAction.Run;
    private ScopeCapture? latest;

    public Task<ScopeSettings> ReadScopeAsync(CancellationToken ct) => Ready(ct, settings);

    public Task ConfigureScopeAsync(ScopeSettings next, CancellationToken ct)
    {
        Check(ct);
        if (!double.IsFinite(next.TimePerDiv) || next.TimePerDiv is < 0.0000001 or > 2 ||
            !double.IsFinite(next.VoltsPerDiv1) || next.VoltsPerDiv1 is < 0.01 or > 20 ||
            !double.IsFinite(next.VoltsPerDiv2) || next.VoltsPerDiv2 is < 0.01 or > 20 ||
            next.TriggerChannel is not (1 or 2) || !double.IsFinite(next.TriggerLevel) ||
            next.Coupling1 is not ("AC" or "DC" or "GND") || next.Coupling2 is not ("AC" or "DC" or "GND"))
            throw new ArgumentException("Ungültige Oszilloskopeinstellungen.");
        settings = next;
        return Task.CompletedTask;
    }

    public Task ControlScopeAsync(ScopeAction next, CancellationToken ct)
    {
        Check(ct);
        action = next;
        return Task.CompletedTask;
    }

    public Task<ScopeCapture> CaptureAsync(CancellationToken ct)
    {
        Check(ct);
        if (action == ScopeAction.Stop && latest is not null)
            return Task.FromResult(latest);
        var (wave1, wave2) = desk.Generator.CurrentPair();
        var triggerWave = settings.TriggerChannel == 1 ? wave1 : wave2;
        const int points = 700;
        var timeBase = settings.TimePerDiv * 10;
        var zero = TriggerTime(triggerWave);
        var times = Enumerable.Range(0, points).Select(i => timeBase * (i / (points - 1d) - 0.5)).ToArray();
        var traces = new List<ScopeTrace>();
        if (settings.Show1)
            traces.Add(Trace(wave1, settings.VoltsPerDiv1, settings.Coupling1));
        if (settings.Show2)
            traces.Add(Trace(wave2, settings.VoltsPerDiv2, settings.Coupling2));
        latest = new(desk.Timestamp, traces);
        if (action == ScopeAction.Single)
            action = ScopeAction.Stop;
        return Task.FromResult(latest);

        ScopeTrace Trace(WaveformSettings wave, double vertical, string coupling)
        {
            var lsb = vertical * 10 / 256;
            var filtered = wave with { Amplitude = wave.Amplitude / Math.Sqrt(1 + Math.Pow(wave.Frequency / 5_000_000, 2)) };
            var volts = times.Select(t =>
            {
                var voltage = coupling == "GND" ? 0 : SimulatedGenerator.ValueAt(filtered, t + zero) -
                    (coupling == "AC" && wave.Enabled ? wave.Offset : 0);
                return Math.Clamp(Math.Round(voltage / lsb), -128, 127) * lsb;
            }).ToArray();
            return new(wave.Channel, times, volts);
        }
    }

    private double TriggerTime(WaveformSettings trigger)
    {
        if (!trigger.Enabled || trigger.Frequency <= 0 || settings.TriggerLevel < trigger.Offset - trigger.Amplitude / 2 ||
            settings.TriggerLevel > trigger.Offset + trigger.Amplitude / 2)
            return 0;
        var previous = SimulatedGenerator.ValueAt(trigger, 0);
        for (var index = 1; index <= 512; index++)
        {
            var t = index / (512 * trigger.Frequency);
            var next = SimulatedGenerator.ValueAt(trigger, t);
            if (settings.Rising ? previous < settings.TriggerLevel && next >= settings.TriggerLevel
                : previous > settings.TriggerLevel && next <= settings.TriggerLevel)
                return t;
            previous = next;
        }
        return 0;
    }

    protected override void ResetOutputs()
    {
        latest = null;
        action = ScopeAction.Run;
    }
}
