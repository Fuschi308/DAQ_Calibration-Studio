using DAQ.CalibrationStudio.Application;
using Owon.DGE;
using OutputState = DAQ.CalibrationStudio.Application.OutputState;
using OwonOutput = Owon.DGE.OutputState;

namespace DAQ.CalibrationStudio.Instruments;

public sealed class OwonDevice(Func<string, IScpiTransport> open)
    : InstrumentSession(DeviceKind.Owon, "OWON DGE", "USB-TMC / VISA",
        "SCPI-Simulation · physischer VISA-Zugriff und Ausgänge ungeprüft"),
        IWaveformGenerator
{
    public OwonDevice() : this(endpoint => new VisaUsbTmcTransport(endpoint, TimeSpan.FromMilliseconds(750))) { }

    private OwonDgeClient? _client;
    private readonly HashSet<int> _possiblyEnabled = [];

    protected override bool IsOpen => _client?.IsConnected == true;
    protected override DeviceCapability ConnectedCapabilities => DeviceCapability.Diagnostics | DeviceCapability.Waveform | DeviceCapability.Sweep;

    public override Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct) => VisaUsbTmcTransport.DiscoverAsync(ct);

    protected override async Task OpenAsync(string endpoint, CancellationToken ct)
    {
        _client = new OwonDgeClient(open(endpoint),
            allowExternalWrites: true, timeout: TimeSpan.FromSeconds(8));
        await _client.ConnectAsync(ct);
    }

    protected override async Task<DeviceIdentity> IdentifyCoreAsync(CancellationToken ct)
    {
        var info = await Client.GetDeviceInfoAsync(ct);
        if (Status.Identity?.Serial is { } serial && info.SerialNumber != serial)
            throw new InvalidDataException("OWON-Seriennummer hat sich während der Verbindung geändert.");
        return new DeviceIdentity(info.Model, info.SerialNumber, info.Firmware);
    }

    public Task<string> ReadNextErrorAsync(CancellationToken ct) => ExecuteAsync(token => Client.GetNextErrorAsync(token), ct);

    public Task<WaveformSettings> ReadWaveformAsync(int channel, CancellationToken ct) =>
        ExecuteAsync(token => ReadWaveformCoreAsync(channel, token), ct);

    public Task<WaveformSettings> ApplyWaveformAsync(WaveformSettings settings, CancellationToken ct) =>
        ExecuteAsync(async token =>
        {
            var channel = settings.Channel;
            var shape = settings.Shape switch
            {
                SignalShape.Sine => WaveformType.Sine,
                SignalShape.Square => WaveformType.Square,
                SignalShape.Pulse => WaveformType.Pulse,
                SignalShape.Ramp => WaveformType.Ramp,
                _ => throw new NotSupportedException("Real bedienbar sind Sinus, Rechteck, Puls und Rampe.")
            };
            if (settings.LoadOhms.HasValue && settings.LoadOhms != 50)
                throw new ArgumentOutOfRangeException(nameof(settings), "Die geprüften Pegelgrenzen gelten für 50 Ω und High-Z.");
            Client.ValidateRequestedWaveform(channel, shape, settings.Frequency, settings.Amplitude, settings.Offset,
                settings.Phase, settings.Duty, settings.LoadOhms is null);
            await ConfirmOffAsync(channel, token);
            await Client.StopSweepAsync(channel, token);
            if ((await Client.ReadSweepAsync(channel, token)).Active)
                throw new InvalidOperationException("Sweep ist weiterhin aktiv.");
            // Reduce level before shape, load or frequency transitions. All changes happen with OFF read back.
            await Client.SetWaveformAsync(channel, shape, token);
            await Client.SetAmplitudeAsync(channel, 0.001, token);
            await Client.SetOffsetAsync(channel, 0, token);
            if (settings.LoadOhms is null)
                await Client.SetOutputHighImpedanceAsync(channel, token);
            else
                await Client.SetOutputLoadOhmsAsync(channel, 50, token);
            await Client.SetFrequencyAsync(channel, settings.Frequency, token);
            await Client.SetOffsetAsync(channel, settings.Offset, token);
            await Client.SetAmplitudeAsync(channel, settings.Amplitude, token);
            await Client.SetPhaseAsync(channel, settings.Phase, token);
            if (shape == WaveformType.Pulse)
                await Client.SetPulseDutyCycleAsync(channel, settings.Duty, token);

            var confirmed = await ReadWaveformCoreAsync(channel, token);
            VerifySettings(settings, confirmed);
            if (!settings.Enabled) return confirmed;

            _possiblyEnabled.Add(channel);
            try
            {
                await Client.EnableOutputAsync(channel, token);
                if (await ChannelOutputAsync(channel, token) != OwonOutput.On)
                    throw new InvalidOperationException($"OWON CH{channel} meldet den Ausgang nicht als aktiv.");
                return confirmed with { Enabled = true };
            }
            catch
            {
                await TryConfirmOffAsync(channel);
                throw;
            }
        }, ct);

    private async Task<WaveformSettings> ReadWaveformCoreAsync(int channel, CancellationToken ct)
    {
        var shape = await Client.GetWaveformAsync(channel, ct);
        var uiShape = shape switch
        {
            WaveformType.Sine => SignalShape.Sine,
            WaveformType.Square => SignalShape.Square,
            WaveformType.Pulse => SignalShape.Pulse,
            WaveformType.Ramp => SignalShape.Ramp,
            _ => throw new NotSupportedException("Rauschen hat keine belegte Trägerfrequenz und Phase. Am Gerät eine feste Signalform wählen.")
        };
        var frequency = await Client.GetFrequencyAsync(channel, ct);
        var amplitude = await Client.GetAmplitudeAsync(channel, ct);
        var offset = await Client.GetOffsetAsync(channel, ct);
        var phase = await Client.GetPhaseDegreesAsync(channel, ct);
        var load = await Client.GetOutputLoadSettingAsync(channel, ct);
        var duty = shape == WaveformType.Pulse ? await Client.GetPulseDutyCycleAsync(channel, ct) : 50;
        var output = await ChannelOutputAsync(channel, ct);
        if (output == OwonOutput.Off) _possiblyEnabled.Remove(channel);
        return new WaveformSettings(channel, uiShape, frequency, amplitude, offset, phase, duty, load.Ohms, output == OwonOutput.On);
    }

    private static void VerifySettings(WaveformSettings expected, WaveformSettings actual)
    {
        var phaseDifference = (expected.Phase - actual.Phase) % 360;
        if (expected.Shape != actual.Shape || actual.Enabled
            || expected.LoadOhms.HasValue != actual.LoadOhms.HasValue
            || expected.LoadOhms is { } load && !Close(actual.LoadOhms!.Value, load, 0.1)
            || !Close(expected.Frequency, actual.Frequency, 0.000001)
            || !Close(expected.Amplitude, actual.Amplitude, 0.001)
            || !Close(expected.Offset, actual.Offset, 0.001)
            || Math.Min(Math.Abs(phaseDifference), 360 - Math.Abs(phaseDifference)) > 0.01
            || expected.Shape == SignalShape.Pulse && !Close(expected.Duty, actual.Duty, 0.01))
            throw new InvalidOperationException($"OWON CH{expected.Channel} hat die Konfiguration abweichend zurückgelesen. Ausgang bleibt aus.");
    }

    private static bool Close(double expected, double actual, double resolution) =>
        Math.Abs(expected - actual) <= Math.Max(resolution, Math.Abs(expected) * 1e-8);

    public Task DisableChannelAsync(int channel, CancellationToken ct) => ExecuteAsync(token => ConfirmOffAsync(channel, token), ct);

    public async Task<OutputState> DisableAsync(CancellationToken ct)
    {
        if (!Status.Connected) return OutputState.Unknown;
        return await ExecuteAsync(async token =>
        {
            // Send both OFF attempts before querying both states.
            Exception? failure = null;
            foreach (var channel in new[] { 1, 2 })
            {
                try { await Client.DisableOutputAsync(channel, token); }
                catch (Exception error) when (error is not OperationCanceledException) { failure ??= error; }
            }
            if (failure is not null) throw new IOException("OWON-Ausgänge konnten nicht sicher ausgeschaltet werden.", failure);
            var status = await Client.GetStatusAsync(token);
            if (status.Channel1 == OwonOutput.Off) _possiblyEnabled.Remove(1);
            if (status.Channel2 == OwonOutput.Off) _possiblyEnabled.Remove(2);
            return status.Channel1 == OwonOutput.Off && status.Channel2 == OwonOutput.Off ? OutputState.Off : OutputState.On;
        }, ct);
    }

    public Task<SweepReading> ReadSweepAsync(int channel, CancellationToken ct) => ExecuteAsync(async token =>
    {
        var sweep = await Client.ReadSweepAsync(channel, token);
        return new SweepReading(new SweepSettings(channel, sweep.StartHz, sweep.StopHz, sweep.DurationSeconds,
            sweep.Spacing == SweepSpacing.Logarithmic), sweep.Active);
    }, ct);

    public Task SetSweepAsync(SweepSettings settings, bool enabled, CancellationToken ct) => ExecuteAsync(async token =>
    {
        int channel = settings.Channel;
        await ConfirmOffAsync(channel, token);
        await Client.StopSweepAsync(channel, token);
        var initial = await Client.ReadSweepAsync(channel, token);
        if (initial.Active) throw new InvalidOperationException($"OWON CH{channel} meldet Sweep weiterhin aktiv.");
        if (!enabled) return;

        var spacing = settings.Logarithmic ? SweepSpacing.Logarithmic : SweepSpacing.Linear;
        await Client.ConfigureSweepAsync(new SweepConfiguration(channel, settings.StartHz, settings.StopHz, settings.Seconds, spacing), token);
        var confirmed = await Client.ReadSweepAsync(channel, token);
        if (confirmed.Active || confirmed.Spacing != spacing
            || !Close(settings.StartHz, confirmed.StartHz, 0.000001)
            || !Close(settings.StopHz, confirmed.StopHz, 0.000001)
            || !Close(settings.Seconds, confirmed.DurationSeconds, 0.001))
            throw new InvalidOperationException("Sweep-Einstellungen weichen von der Rücklesung ab.");

        _possiblyEnabled.Add(channel);
        try
        {
            // Existing client verifies fixed-carrier settings before enabling, and sweep endpoints before starting.
            await Client.EnableOutputAsync(channel, token);
            await Client.StartSweepAsync(channel, token);
            if (!(await Client.ReadSweepAsync(channel, token)).Active || await ChannelOutputAsync(channel, token) != OwonOutput.On)
                throw new InvalidOperationException("OWON meldet den programmierten Sweep und Ausgang nicht als aktiv.");
        }
        catch
        {
            await TryConfirmOffAsync(channel);
            throw;
        }
    }, ct);

    private async Task<OwonOutput> ChannelOutputAsync(int channel, CancellationToken ct)
    {
        var status = await Client.GetStatusAsync(ct);
        return channel == 1 ? status.Channel1 : status.Channel2;
    }

    private async Task ConfirmOffAsync(int channel, CancellationToken ct)
    {
        await Client.DisableOutputAsync(channel, ct);
        if (await ChannelOutputAsync(channel, ct) != OwonOutput.Off)
            throw new IOException($"OWON CH{channel} meldet nach OFF einen aktiven Ausgang. Ausgang am Gerät kontrollieren.");
        _possiblyEnabled.Remove(channel);
    }

    private async Task TryConfirmOffAsync(int channel)
    {
        if (!IsOpen) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await ConfirmOffAsync(channel, timeout.Token); }
        catch (Exception) { /* Keep the channel marked as potentially enabled for the disconnect path. */ }
    }

    private OwonDgeClient Client => _client ?? throw new InvalidOperationException("OWON ist getrennt.");

    protected override async Task CloseAsync()
    {
        var client = _client;
        if (client is null) return;
        var candidates = _possiblyEnabled.ToArray();
        try
        {
            foreach (var channel in candidates)
                await TryConfirmOffAsync(channel);
            if (_possiblyEnabled.Count > 0)
                throw new IOException("OWON-Ausgangszustand unbekannt. Beide Ausgänge am Gerät kontrollieren.");
        }
        finally
        {
            _client = null;
            _possiblyEnabled.Clear();
            await client.DisposeAsync();
        }
    }
}
