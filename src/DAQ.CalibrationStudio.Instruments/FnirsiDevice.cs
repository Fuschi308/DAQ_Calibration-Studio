using System.IO.Ports;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;
using Fnirsi.SG004A;

namespace DAQ.CalibrationStudio.Instruments;

/// <summary>The register ACK/readback is a programmed value. Electrical output state is unknown.</summary>
public sealed class FnirsiDevice(Func<string, IByteTransport> open)
    : InstrumentSession(DeviceKind.Fnirsi, "FNIRSI SG-004A", "USB / COM", "Protokollsimulation · Hardwareprüfung offen"),
        IManualSetpointSource
{
    public FnirsiDevice() : this(endpoint => new SerialByteTransport(endpoint)) { }

    private FnirsiClient? _client;
    private Quantity? _lastQuantity;
    private double _lastRequested;

    protected override bool IsOpen => _client?.IsConnected == true;
    protected override DeviceCapability ConnectedCapabilities => DeviceCapability.Diagnostics | DeviceCapability.SetpointOnly;

    public override Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // These are candidate ports. Only an explicit connection performs the register probe.
        return Task.FromResult<IReadOnlyList<string>>(SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    protected override async Task OpenAsync(string endpoint, CancellationToken ct)
    {
        _client = new FnirsiClient(open(endpoint), allowSetpointWrites: true);
        await _client.ConnectAsync(ct);
    }

    protected override async Task<DeviceIdentity> IdentifyCoreAsync(CancellationToken ct)
    {
        var info = await Client.GetDeviceInfoAsync(ct);
        return new DeviceIdentity("SG-004A (konfiguriert)", Firmware: $"Register 40001 = 0x{info.FirmwareRegisterRaw:X4}");
    }

    public bool Supports(Quantity quantity, double value) => false;

    public Task<SourceReading> ApplyAsync(Quantity quantity, double setpoint, bool enabled, CancellationToken ct) =>
        Task.FromException<SourceReading>(new NotSupportedException("Der FNIRSI-Ausgang muss am Gerät bedient werden. Automatische Prüfsequenzen mit diesem Adapter sind gesperrt."));

    public Task<SourceReading> ReadAsync(CancellationToken ct) => ExecuteAsync(async token =>
    {
        var status = await Client.GetStatusAsync(token);
        var quantity = status.OutputMode switch
        {
            FnirsiOutputMode.Voltage => Quantity.Voltage,
            FnirsiOutputMode.Current => Quantity.Current,
            _ => throw new InvalidOperationException($"Der am Gerät eingestellte Modus (0x{status.OutputModeRaw:X4}) ist weder Spannung noch Aktivstrom.")
        };
        var confirmed = await ReadTypedAsync(quantity, token);
        return new SourceReading(quantity, _lastQuantity == quantity ? _lastRequested : null, confirmed, OutputState.Unknown);
    }, ct);

    public Task<SourceReading> WriteSetpointAsync(Quantity quantity, double setpoint, CancellationToken ct) =>
        ExecuteAsync(async token =>
        {
            switch (quantity)
            {
                case Quantity.Voltage:
                    await Client.SetVoltageAsync(setpoint, token);
                    break;
                case Quantity.Current:
                    await Client.SetCurrentAsync(setpoint, token);
                    break;
                default:
                    throw new NotSupportedException("FNIRSI unterstützt nur Spannungs- und Aktivstromsollwerte.");
            }
            var confirmed = await ReadTypedAsync(quantity, token);
            _lastRequested = setpoint;
            _lastQuantity = quantity;
            return new SourceReading(quantity, setpoint, confirmed, OutputState.Unknown);
        }, ct);

    public Task<OutputState> DisableAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(OutputState.Unknown);
    }

    public Task<(ushort OutputSwitchRegister, float MeasuredInput)> ReadRawDiagnosticsAsync(CancellationToken ct) =>
        ExecuteAsync(async token =>
        {
            var status = await Client.GetStatusAsync(token);
            return (status.OutputSwitchRaw, await Client.GetMeasuredInputAsync(token));
        }, ct);

    private Task<float> ReadTypedAsync(Quantity quantity, CancellationToken ct) => quantity == Quantity.Voltage
        ? Client.GetVoltageSetpointAsync(ct)
        : Client.GetCurrentSetpointAsync(ct);

    private FnirsiClient Client => _client ?? throw new InvalidOperationException("FNIRSI ist getrennt.");

    protected override async Task CloseAsync()
    {
        var client = _client;
        _client = null;
        _lastQuantity = null;
        if (client is not null)
            await client.DisposeAsync();
    }
}
