using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.Simulation;

public sealed class SimulatedSource(SimulatedDesk desk) : SimulatedSession(
    DeviceKind.Fnirsi, "FNIRSI SG-004A", "USB · COM (sim.)",
    DeviceCapability.AnalogSource | DeviceCapability.Diagnostics,
    new("SG-004A", "SIM-SG-018", "SIM 1.0")), IAnalogSource
{
    private Quantity quantity;
    private double requested;
    private double confirmed;
    private bool enabled;
    private double previous;
    private double changed;

    public Quantity Mode => quantity;
    public bool Enabled => enabled;
    public double Confirmed => confirmed;

    public bool Supports(Quantity mode, double value) =>
        double.IsFinite(value) && value >= 0 && mode switch
        {
            Quantity.Voltage => value <= 10,
            Quantity.Current => value <= 20,
            _ => false
        };

    public Task<SourceReading> ApplyAsync(Quantity mode, double setpoint, bool output, CancellationToken ct)
    {
        Check(ct);
        if (!Supports(mode, setpoint))
            throw new ArgumentOutOfRangeException(nameof(setpoint), "Prüfbereich der simulierten Quelle: 0–10 V / 0–20 mA.");
        if (enabled && mode != quantity)
            throw new InvalidOperationException("Ausgang vor dem Moduswechsel ausschalten.");
        previous = output ? Actual : 0;
        changed = desk.Seconds;
        quantity = mode;
        requested = setpoint;
        confirmed = Math.Round(setpoint, 3, MidpointRounding.AwayFromZero);
        enabled = output;
        return Task.FromResult(Readback());
    }

    public Task<SourceReading> ReadAsync(CancellationToken ct) => Ready(ct, Readback());

    public Task<OutputState> DisableAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Status.Connected)
            return Task.FromResult(OutputState.Unknown);
        ResetOutputs();
        return Task.FromResult(OutputState.Off);
    }

    internal double Actual
    {
        get
        {
            if (!enabled)
                return 0;
            var steady = quantity == Quantity.Voltage ? confirmed * 1.00004 + 0.0002 : confirmed * 0.99994 + 0.0008;
            return steady + (previous - steady) * Math.Exp(-(desk.Seconds - changed) / 0.06);
        }
    }

    protected override void ResetOutputs()
    {
        enabled = false;
        requested = confirmed = previous = 0;
        changed = desk.Seconds;
    }

    private SourceReading Readback() =>
        new(quantity, requested, confirmed, Status.Connected ? (enabled ? OutputState.On : OutputState.Off) : OutputState.Unknown);
}
