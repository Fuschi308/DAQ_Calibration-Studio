using System.Diagnostics;
using System.Globalization;
using Daq.Service.Protocol;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.Daq;

public sealed record UsbDaqDiagnostics(
    string AcquisitionState, uint StartBlocks, uint Faults, bool Warning, uint CalibrationGeneration,
    double? InputVolts, double? Analog5Volts, double? VddaVolts, double? CpuCelsius, double? BoardCelsius);

public sealed record UsbDaqCounters(
    bool Initialized, int Encoder, ulong Hsc4, ulong Hsc5, uint Hsc6, uint Overcapture, ulong Received, ulong Produced);

/// <summary>Service-protocol snapshots and explicit acquisition control over a selected RevB CDC port.</summary>
public sealed class UsbDaqDevice : IDaqAcquisitionControl
{
    private static readonly string[] AnalogChannels = ["AI-V1", "AI-V2", "AI-V3", "AI-I1", "AI-I2", "AI-I3"];
    private readonly Func<string, ILineTransport> _open;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ServiceClient? _client;
    private long _lastSequence;
    private bool _disposed;

    public UsbDaqDevice() : this(port => new SerialLineTransport(port)) { }

    public UsbDaqDevice(Func<string, ILineTransport> open) => _open = open;

    public DeviceStatus Status { get; private set; } = new(DeviceKind.Daq, "STM32H7-DAQ RevB", "USB CDC / COM",
        DeviceState.Disconnected, DeviceCapability.None, "Firmware- und Hosttests · am Board offen");

    public UsbDaqCounters? LastCounters { get; private set; }

    public Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(SerialLineTransport.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        return RunAsync(() =>
        {
            if (_client is not null) throw new InvalidOperationException("DAQ zuerst trennen.");
            Status = Status with { State = DeviceState.Connecting, Identity = null, Error = null };
            _client = new ServiceClient(_open(endpoint.Trim()));
            return IdentifyCore(ct);
        }, ct, requireConnection: false);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Close();
            Status = Status with { State = DeviceState.Disconnected, Identity = null, Error = null, Capabilities = DeviceCapability.None };
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<DeviceIdentity> IdentifyAsync(CancellationToken ct) => RunAsync(() => IdentifyCore(ct), ct);

    private DeviceIdentity IdentifyCore(CancellationToken ct)
    {
        var reply = Client.Query(ServiceProtocol.Identify, ct);
        var model = reply.Text("model");
        var revision = reply.Text("hw");
        var version = reply.UInt32("proto");
        if (model != "DAQ" || revision != "B" || version != ServiceProtocol.Version)
            throw new InvalidDataException($"DAQ RevB mit USB-Serviceprotokoll 1 erwartet. Antwort: {model}, Rev {revision}, Protokoll {version}.");
        var serial = reply.Text("serial");
        var identity = new DeviceIdentity("STM32H7-DAQ", serial == "-" ? null : serial, reply.Text("fw"), revision);
        if (Status.Identity is { Serial: { } original } && original != identity.Serial)
            throw new InvalidDataException("DAQ-Seriennummer hat sich während der Verbindung geändert.");
        var capabilities = DeviceCapability.Diagnostics;
        if (reply.OptionalFlag("readings")) capabilities |= DeviceCapability.Measurements;
        if (reply.OptionalFlag("control")) capabilities |= DeviceCapability.AcquisitionControl;
        Status = Status with { State = DeviceState.Connected, Identity = identity, Capabilities = capabilities, Error = null };
        return identity;
    }

    public Task SetAcquisitionAsync(bool enabled, CancellationToken ct) => RunAsync(() =>
    {
        if (!Status.Has(DeviceCapability.AcquisitionControl))
            throw new NotSupportedException("Diese Firmware bietet keine Erfassungssteuerung an.");
        Client.Query(enabled ? ServiceProtocol.Start : ServiceProtocol.Stop, ct);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var fields = Client.Query(ServiceProtocol.State, ct);
            string state = fields.Text("state");
            bool reading = state is "RUNNING" or "CLAMP_SETTLE" or "DISCARD";
            if (enabled && state == "RUNNING" || !enabled && !reading)
                return true;
            if (state == "FAULT" || Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(3))
                throw new InvalidOperationException($"Erfassung meldet {state}. Start-Sperren: 0x{fields.HexMask("blocked"):X4}.");
            if (ct.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100)))
                ct.ThrowIfCancellationRequested();
        }
    }, ct);

    public Task<DaqFrame> ReadFrameAsync(CancellationToken ct) => RunAsync(() =>
    {
        if (!Status.Has(DeviceCapability.Measurements))
            throw new NotSupportedException("Diese Firmware meldet keine Live-Messwerte.");
        var samples = new List<ChannelSample>(22);
        AddAnalog(samples, Client.Query(ServiceProtocol.Analog, ct));
        AddRtd(samples, Client.Query(ServiceProtocol.Rtd, ct));
        AddInputs(samples, Client.Query(ServiceProtocol.InputsAndCounters, ct));
        return new DaqFrame(++_lastSequence, DateTimeOffset.UtcNow, samples, DeviceTimed: false);
    }, ct);

    private static void AddAnalog(List<ChannelSample> samples, ProtocolFields fields)
    {
        if (!fields.Flag("valid"))
        {
            foreach (var channel in AnalogChannels)
                samples.Add(new ChannelSample(channel, null, null, null, null, null, null, Count: 0));
            return;
        }
        var flags = fields.HexMask("flags");
        var codes = ReadList(fields, "codes", AnalogChannels.Length, ParseAdcCode);
        var scaled = ReadList(fields, "values", AnalogChannels.Length, ProtocolFields.ParseMeasurement);
        for (int index = 0; index < AnalogChannels.Length; index++)
        {
            // Firmware values already include the current DAQ calibration. Suppress settling/gap/overrange frames.
            var value = (flags & 1u) != 0 && (flags & (4u | 8u | 16u)) == 0 ? scaled[index] : null;
            samples.Add(new ChannelSample(AnalogChannels[index], codes[index], null, value,
                null, null, null, Count: value.HasValue ? 1 : 0));
        }
    }

    private static int ParseAdcCode(string text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code)
            && code is >= -8388608 and <= 8388607 ? code : throw new InvalidDataException("Ungültiger 24-Bit-ADC-Code.");

    private static void AddRtd(List<ChannelSample> samples, ProtocolFields fields)
    {
        var resistance = ReadList(fields, "ohm", 4, ProtocolFields.ParseMeasurement);
        var temperatures = ReadList(fields, "temp", 4, ProtocolFields.ParseMeasurement);
        var mask = fields.HexMask("valid");
        for (int index = 0; index < resistance.Length; index++)
        {
            var value = (mask & (1u << index)) != 0 && temperatures[index] is not null ? resistance[index] : null;
            samples.Add(new ChannelSample($"RTD{index + 1}", null, null, value, null, null, null, Count: value.HasValue ? 1 : 0));
        }
    }

    private void AddInputs(List<ChannelSample> samples, ProtocolFields fields)
    {
        var inputs = fields.HexMask("di");
        if (inputs > byte.MaxValue) throw new InvalidDataException("Ungültige Maske für Digitaleingänge.");
        bool valid = fields.Flag("di_valid");
        for (int index = 0; index < ServiceProtocol.DigitalInputCount; index++)
        {
            int? value = valid ? (int)((inputs >> index) & 1u) : null;
            samples.Add(new ChannelSample($"DI{index + 1}", value, null, value, null, null, null, Count: valid ? 1 : 0));
        }

        var counters = new UsbDaqCounters(fields.Flag("counters"), unchecked((int)fields.HexMask("enc")),
            fields.UInt64("hsc4"), fields.UInt64("hsc5"), fields.UInt32("hsc6"), fields.UInt32("overcapture"),
            fields.UInt64("received"), fields.UInt64("produced"));
        LastCounters = counters;
        AddCounterSample(samples, "ENC1", counters.Initialized ? counters.Encoder : null);
        AddCounterSample(samples, "HSC4", counters.Initialized && counters.Hsc4 <= int.MaxValue ? (int)counters.Hsc4 : null);
        AddCounterSample(samples, "HSC5", counters.Initialized && counters.Hsc5 <= int.MaxValue ? (int)counters.Hsc5 : null);
        AddCounterSample(samples, "HSC6", counters.Initialized && counters.Hsc6 <= int.MaxValue ? (int)counters.Hsc6 : null);
    }

    private static void AddCounterSample(List<ChannelSample> samples, string channel, int? count) =>
        samples.Add(new ChannelSample(channel, count, null, null, null, null, null, Count: count.HasValue ? 1 : 0));

    private static T[] ReadList<T>(ProtocolFields fields, string key, int length, Func<string, T> parse)
    {
        var values = fields.Text(key).Split(',');
        if (values.Length != length)
            throw new InvalidDataException($"Antwortfeld {key} enthält {values.Length} statt {length} Werte.");
        return Array.ConvertAll(values, new Converter<string, T>(parse));
    }

    public Task<UsbDaqDiagnostics> ReadDiagnosticsAsync(CancellationToken ct) => RunAsync(() =>
    {
        var state = Client.Query(ServiceProtocol.State, ct);
        var fault = Client.Query(ServiceProtocol.Fault, ct);
        var monitor = Client.Query(ServiceProtocol.Monitor, ct);
        bool powerValid = monitor.Flag("mon_valid");
        return new UsbDaqDiagnostics(state.Text("state"), state.HexMask("blocked"), fault.HexMask("faults"),
            fault.Flag("warning"), state.UInt32("cal_gen"), powerValid ? monitor.Measurement("vin") : null,
            powerValid ? monitor.Measurement("v5a") : null, powerValid ? monitor.Measurement("vdda") : null,
            monitor.Flag("cpu_valid") ? monitor.Measurement("cpu") : null,
            monitor.Flag("board_valid") ? monitor.Measurement("board") : null);
    }, ct);

    public Task SetRouteAsync(string channelId, CancellationToken ct) =>
        Task.FromException(new NotSupportedException("Die Verbindung zur Referenzquelle muss am Prüfling hergestellt werden. USB CDC bietet kein Routing."));

    public Task SetDigitalOutputAsync(string outputId, bool value, CancellationToken ct) =>
        Task.FromException(new NotSupportedException("Der USB-Service bietet keine digitalen Ausgangsbefehle an."));

    public Task<Coefficient> ReadCoefficientAsync(string channelId, CancellationToken ct) =>
        Task.FromException<Coefficient>(new NotSupportedException("USB CDC meldet die aktive Kalibriergeneration, nicht den geladenen Koeffizientensatz."));

    public Task WriteCoefficientsAsync(IReadOnlyDictionary<string, Coefficient> coefficients, bool persistent, CancellationToken ct) =>
        Task.FromException(new NotSupportedException("Die Firmware kann nur intern erfasste Zweipunktentwürfe speichern. Eine Übernahme von Studio-Koeffizienten wird nicht angeboten."));

    private ServiceClient Client => _client ?? throw new InvalidOperationException("DAQ ist getrennt.");

    private async Task<T> RunAsync<T>(Func<T> operation, CancellationToken ct, bool requireConnection = true)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (requireConnection && _client is null)
                throw new InvalidOperationException("DAQ ist getrennt.");
            try
            {
                var result = await Task.Run(operation, ct);
                return result;
            }
            catch (Exception error) when (Status.State == DeviceState.Connecting ||
                error is OperationCanceledException or TimeoutException or InvalidDataException || (error is IOException and not ServiceCommandException))
            {
                string message = error.Message;
                try { Close(); }
                catch (IOException closeError) { message += $" Trennen fehlgeschlagen. {closeError.Message}"; }
                Status = Status with
                {
                    State = DeviceState.Faulted, Identity = null, Capabilities = DeviceCapability.None, Error = message
                };
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Close()
    {
        var client = _client;
        _client = null;
        LastCounters = null;
        client?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            Close();
            Status = Status with { State = DeviceState.Disconnected, Identity = null, Capabilities = DeviceCapability.None };
        }
        finally
        {
            _gate.Release();
        }
    }
}
