using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.Application;

public enum DeviceKind { Daq, Fnirsi, Owon, PeakTech }
public enum DeviceState { Disconnected, Connecting, Connected, Faulted }
public enum OutputState { Off, On, Unknown }

[Flags]
public enum DeviceCapability
{
    None = 0,
    Measurements = 1,
    AnalogSource = 2,
    Waveform = 4,
    Sweep = 8,
    Quadrature = 16,
    Scope = 32,
    Coefficients = 64,
    Diagnostics = 128,
    Route = 256,
    DigitalOutput = 512,
    RawStreaming = 1024,
    SetpointOnly = 2048, AcquisitionControl = 4096
}

public sealed record DeviceIdentity(string Model, string? Serial = null, string? Firmware = null, string? Revision = null);
public sealed record DeviceStatus(
    DeviceKind Kind, string Name, string Interface, DeviceState State,
    DeviceCapability Capabilities, string Verification, DeviceIdentity? Identity = null, string? Error = null)
{
    public bool Connected => State == DeviceState.Connected;
    public bool Has(DeviceCapability capability) => (Capabilities & capability) == capability;
}

public interface IDeviceSession : IAsyncDisposable
{
    DeviceStatus Status { get; }
    Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct);
    Task ConnectAsync(string endpoint, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task<DeviceIdentity> IdentifyAsync(CancellationToken ct);
}

public sealed record SourceReading(
    Quantity Quantity, double? Requested, double? Confirmed, OutputState Output, double? IndependentReference = null);

public interface IAnalogSource : IDeviceSession
{
    bool Supports(Quantity quantity, double value);
    Task<SourceReading> ApplyAsync(Quantity quantity, double setpoint, bool enabled, CancellationToken ct);
    Task<SourceReading> ReadAsync(CancellationToken ct);
    Task<OutputState> DisableAsync(CancellationToken ct);
}

public interface IManualSetpointSource : IAnalogSource
{
    Task<SourceReading> WriteSetpointAsync(Quantity quantity, double value, CancellationToken ct);
}

public enum SignalShape { Sine, Square, Triangle, Ramp, Pulse, Dc, Noise }
public sealed record WaveformSettings(
    int Channel, SignalShape Shape, double Frequency, double Amplitude,
    double Offset, double Phase, double Duty, double? LoadOhms, bool Enabled);

public sealed record SweepSettings(int Channel, double StartHz, double StopHz, double Seconds, bool Logarithmic = false);
public sealed record SweepReading(SweepSettings? Settings, bool? Active);

public interface IWaveformGenerator : IDeviceSession
{
    Task<WaveformSettings> ReadWaveformAsync(int channel, CancellationToken ct);
    Task<WaveformSettings> ApplyWaveformAsync(WaveformSettings settings, CancellationToken ct);
    Task<OutputState> DisableAsync(CancellationToken ct);
    Task<SweepReading> ReadSweepAsync(int channel, CancellationToken ct);
    Task SetSweepAsync(SweepSettings settings, bool enabled, CancellationToken ct);
}

public sealed record ScopeSettings(
    double TimePerDiv, double VoltsPerDiv1, double VoltsPerDiv2,
    int TriggerChannel, double TriggerLevel, bool Rising,
    string Coupling1 = "DC", string Coupling2 = "DC", bool Show1 = true, bool Show2 = true);

public enum ScopeAction { Run, Stop, Single }
public sealed record ScopeTrace(int Channel, double[] Seconds, double[] Volts, double[]? MinimumVolts = null, double[]? MaximumVolts = null);
public sealed record ScopeCapture(DateTimeOffset Time, IReadOnlyList<ScopeTrace> Traces);

public interface IScopeDevice : IDeviceSession
{
    Task<ScopeSettings> ReadScopeAsync(CancellationToken ct);
    Task ConfigureScopeAsync(ScopeSettings settings, CancellationToken ct);
    Task ControlScopeAsync(ScopeAction action, CancellationToken ct);
    Task<ScopeCapture> CaptureAsync(CancellationToken ct);
}

public interface IDaqDevice : IDeviceSession
{
    Task<DaqFrame> ReadFrameAsync(CancellationToken ct);
    Task SetRouteAsync(string channelId, CancellationToken ct);
    Task SetDigitalOutputAsync(string outputId, bool value, CancellationToken ct);
    Task<Coefficient> ReadCoefficientAsync(string channelId, CancellationToken ct);
    Task WriteCoefficientsAsync(IReadOnlyDictionary<string, Coefficient> coefficients, bool persistent, CancellationToken ct);
}


public interface IDaqAcquisitionControl : IDaqDevice
{
    Task SetAcquisitionAsync(bool enabled, CancellationToken ct);
}
