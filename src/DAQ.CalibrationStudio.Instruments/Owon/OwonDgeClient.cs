using System.Globalization;

namespace Owon.DGE;

public enum ConnectionKind { Simulation, ExternalScpi }
public enum OutputState { Unknown, Off, On }
public enum WaveformType { Sine, Square, Pulse, Ramp, Noise }
public enum SweepSpacing { Linear, Logarithmic }
public enum SweepTrigger { Timer, Manual, External }

public sealed record DeviceInfo(string Manufacturer, string Model, string SerialNumber, string Firmware, ConnectionKind Kind);
public sealed record DeviceStatus(OutputState Channel1, OutputState Channel2, bool IsConnected);
public sealed record OutputLoadSetting(bool IsHighImpedance, double? Ohms);
public sealed record SweepStatus(double StartHz, double StopHz, double DurationSeconds, SweepSpacing Spacing, bool Active);
public sealed record SweepConfiguration(int Channel, double StartHz, double StopHz, double DurationSeconds,
    SweepSpacing Spacing = SweepSpacing.Linear, SweepTrigger Trigger = SweepTrigger.Timer);

public sealed record ModelProfile(string Model, double MaxSineHz, double MaxSquareHz, double MaxPulseHz,
    double MaxRampHz, double MaxArbitraryHz, bool SupportsExternalSweepTrigger)
{
    public static ModelProfile Dge2035 { get; } = new("DGE2035", 35e6, 15e6, 15e6, 1e6, 10e6, false);
    public static ModelProfile Dge2070 { get; } = new("DGE2070", 70e6, 20e6, 20e6, 2e6, 10e6, false);
    public static ModelProfile Dge3062 { get; } = new("DGE3062", 60e6, 20e6, 20e6, 2e6, 10e6, true);
    public double MaximumFor(WaveformType waveform) => waveform switch
    {
        WaveformType.Sine => MaxSineHz,
        WaveformType.Square => MaxSquareHz,
        WaveformType.Pulse => MaxPulseHz,
        WaveformType.Ramp => MaxRampHz,
        _ => throw new NotSupportedException("Für Rauschen ist keine feste Trägerfrequenz belegt.")
    };
}

/// <summary>SCPI message transport. Implementations must not invent USB framing or enable outputs on open.</summary>
public interface IScpiTransport : IAsyncDisposable
{
    ConnectionKind Kind { get; }
    bool IsOpen { get; }
    Task OpenAsync(CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
    Task WriteAsync(string command, CancellationToken cancellationToken);
    Task<string> QueryAsync(string command, CancellationToken cancellationToken);
}

/// <summary>Independent DGE protocol client. Real writes require explicit opt-in after the device is identified.</summary>
public sealed class OwonDgeClient : IAsyncDisposable
{
    private readonly IScpiTransport transport;
    private readonly ModelProfile? configuredProfile;
    private ModelProfile profile = ModelProfile.Dge2035;
    private readonly bool allowExternalWrites;
    private readonly TimeSpan timeout;
    private readonly Action<string>? log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DeviceInfo? info;
    private OutputState output1 = OutputState.Unknown, output2 = OutputState.Unknown;
    private bool disposed, requiresNewClient;

    public OwonDgeClient(IScpiTransport transport, ModelProfile? profile = null, bool allowExternalWrites = false,
        TimeSpan? timeout = null, Action<string>? log = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        configuredProfile = profile;
        this.allowExternalWrites = allowExternalWrites;
        this.timeout = timeout ?? TimeSpan.FromSeconds(3);
        if (this.timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (transport is VisaUsbTmcTransport visa && visa.IoTimeout > this.timeout)
            throw new ArgumentException("VISA-I/O-Timeout darf nicht größer als der Client-Timeout sein.", nameof(timeout));
        this.log = log;
    }

    public bool IsConnected => info is not null && transport.IsOpen;
    public ConnectionKind ConnectionKind => transport.Kind;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async ct =>
        {
            if (requiresNewClient) throw new InvalidOperationException("Transport konnte nach einem Fehler nicht sicher geschlossen werden. Neue Client-Instanz erforderlich.");
            if (IsConnected) return;
            await transport.OpenAsync(ct);
            try
            {
                var parsed = ParseIdentification(await transport.QueryAsync("*IDN?", ct), transport.Kind);
                profile = configuredProfile ?? (parsed.Model.ToUpperInvariant() switch
                {
                    "DGE2035" => ModelProfile.Dge2035,
                    "DGE2070" => ModelProfile.Dge2070,
                    "DGE3062" => ModelProfile.Dge3062,
                    _ => throw new NotSupportedException($"Unbekanntes OWON-Modell {parsed.Model}.")
                });
                if (!string.Equals(parsed.Model, profile.Model, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Gerätemodell {parsed.Model} entspricht nicht dem Profil {profile.Model}.");
                info = parsed;
                output1 = output2 = OutputState.Unknown;
                log?.Invoke($"Verbunden mit {parsed.Model} über {transport.Kind}. Ausgangsstatus unbekannt.");
            }
            catch
            {
                try { await transport.CloseAsync(CancellationToken.None); }
                catch { requiresNewClient = true; }
                if (transport.IsOpen) requiresNewClient = true;
                throw;
            }
        }, cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => RunAsync(async ct =>
    {
        await transport.CloseAsync(ct);
        info = null;
        output1 = output2 = OutputState.Unknown;
        log?.Invoke("Verbindung geschlossen. Physischer Ausgangsstatus unbekannt.");
    }, cancellationToken);

    public Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        RequireConnected();
        var fresh = ParseIdentification(await transport.QueryAsync("*IDN?", token), transport.Kind);
        if (!string.Equals(fresh.Model, profile.Model, StringComparison.OrdinalIgnoreCase))
        {
            info = null;
            try { await transport.CloseAsync(CancellationToken.None); }
            catch { requiresNewClient = true; }
            if (transport.IsOpen) requiresNewClient = true;
            throw new InvalidOperationException("Geräteidentifikation hat sich geändert.");
        }
        info = fresh;
        return info;
    }, ct);

    public Task<DeviceStatus> GetStatusAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        RequireConnected();
        output1 = ParseOutput(await transport.QueryAsync("OUTPut1:STATe?", token));
        output2 = ParseOutput(await transport.QueryAsync("OUTPut2:STATe?", token));
        return new DeviceStatus(output1, output2, true);
    }, ct);

    public Task<string> GetNextErrorAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        RequireConnected();
        return (await transport.QueryAsync("SYSTem:ERRor:NEXT?", token)).Trim();
    }, ct);

    public Task<double> GetFrequencyAsync(int channel, CancellationToken ct = default) => QueryNumberAsync(channel, "FREQuency:FIXed", ct);
    public Task<double> GetAmplitudeAsync(int channel, CancellationToken ct = default) => QueryNumberAsync(channel, "VOLTage:LEVel:IMMediate:AMPLitude", ct);
    public Task<double> GetOffsetAsync(int channel, CancellationToken ct = default) => QueryNumberAsync(channel, "VOLTage:LEVel:IMMediate:OFFSet", ct);
    public Task<double> GetPhaseDegreesAsync(int channel, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); RequireConnected();
        var radians = ParseNumber(await transport.QueryAsync($"SOURce{channel}:PHASe:ADJust?", token));
        return radians * 180.0 / Math.PI;
    }, ct);
    public Task<double> GetPulseDutyCycleAsync(int channel, CancellationToken ct = default) => QueryNumberAsync(channel, "PULSe:DCYCle", ct);
    public Task<double> GetOutputLoadOhmsAsync(int channel, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); RequireConnected();
        return ParseNumber(await transport.QueryAsync($"OUTPut{channel}:IMPedance?", token));
    }, ct);
    public Task<OutputLoadSetting> GetOutputLoadSettingAsync(int channel, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); RequireConnected();
        var reported = ParseNumber(await transport.QueryAsync($"OUTPut{channel}:IMPedance?", token));
        return reported >= 1e30 ? new OutputLoadSetting(true, null) : new OutputLoadSetting(false, reported);
    }, ct);

    public Task SetOutputHighImpedanceAsync(int channel, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); EnsureWriteAllowed();
        await EnsureOutputOffForConfigurationAsync(channel, token);
        await WriteCoreAsync($"OUTPut{channel}:IMPedance INFinity", token);
    }, ct);

    public Task SetOutputLoadOhmsAsync(int channel, int ohms, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); EnsureWriteAllowed();
        if (ohms is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(ohms));
        await EnsureOutputOffForConfigurationAsync(channel, token);
        await WriteCoreAsync($"OUTPut{channel}:IMPedance {ohms}OHM", token);
    }, ct);

    public Task SetPulseDutyCycleAsync(int channel, double percent, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); EnsureWriteAllowed();
        await EnsureOutputOffForConfigurationAsync(channel, token);
        if (await QueryWaveformCoreAsync(channel, token) != WaveformType.Pulse)
            throw new InvalidOperationException("Tastverhältnis ist nur für PULSe dokumentiert.");
        var frequency = ParseNumber(await transport.QueryAsync($"SOURce{channel}:FREQuency:FIXed?", token));
        var minimum = 100 * 24e-9 * frequency;
        var maximum = 100 * (1 - 2 * 24e-9 * frequency);
        ValidateRange(percent, minimum, maximum, nameof(percent));
        await WriteCoreAsync($"SOURce{channel}:PULSe:DCYCle {Number(percent)}PCT", token);
    }, ct);

    public Task SetFrequencyAsync(int channel, double frequencyHz, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); EnsureWriteAllowed();
        await EnsureFixedCarrierOrOutputOffAsync(channel, token);
        var shape = await QueryWaveformCoreAsync(channel, token);
        if (shape == WaveformType.Noise) throw new NotSupportedException("Rauschsignal hat keine dokumentierte feste Frequenz.");
        ValidateRange(frequencyHz, 1e-6, profile.MaximumFor(shape), nameof(frequencyHz));
        var amplitude = ParseNumber(await transport.QueryAsync($"SOURce{channel}:VOLTage:LEVel:IMMediate:AMPLitude?", token));
        var offset = ParseNumber(await transport.QueryAsync($"SOURce{channel}:VOLTage:LEVel:IMMediate:OFFSet?", token));
        var highZ = await QuerySupportedLoadModeAsync(channel, token);
        ValidateVoltageEnvelope(amplitude, offset, frequencyHz, highZ);
        await WriteCoreAsync($"SOURce{channel}:FREQuency:FIXed {Number(frequencyHz)}Hz", token);
    }, ct);

    public Task SetAmplitudeAsync(int channel, double amplitudeVpp, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); EnsureWriteAllowed();
        await EnsureFixedCarrierOrOutputOffAsync(channel, token);
        var frequency = ParseNumber(await transport.QueryAsync($"SOURce{channel}:FREQuency:FIXed?", token));
        var shape = await QueryWaveformCoreAsync(channel, token);
        if (shape == WaveformType.Noise) throw new NotSupportedException("Sichere Rauschpegelsteuerung ist nicht implementiert.");
        ValidateRange(frequency, 1e-6, profile.MaximumFor(shape), nameof(frequency));
        var offset = ParseNumber(await transport.QueryAsync($"SOURce{channel}:VOLTage:LEVel:IMMediate:OFFSet?", token));
        var highZ = await QuerySupportedLoadModeAsync(channel, token);
        ValidateVoltageEnvelope(amplitudeVpp, offset, frequency, highZ);
        await WriteCoreAsync($"SOURce{channel}:VOLTage:LEVel:IMMediate:AMPLitude {Number(amplitudeVpp)}Vpp", token);
    }, ct);

    public Task SetOffsetAsync(int channel, double offsetV, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); EnsureWriteAllowed();
        await EnsureFixedCarrierOrOutputOffAsync(channel, token);
        var frequency = ParseNumber(await transport.QueryAsync($"SOURce{channel}:FREQuency:FIXed?", token));
        var shape = await QueryWaveformCoreAsync(channel, token);
        if (shape == WaveformType.Noise) throw new NotSupportedException("Sichere Rauschpegelsteuerung ist nicht implementiert.");
        ValidateRange(frequency, 1e-6, profile.MaximumFor(shape), nameof(frequency));
        var amplitude = ParseNumber(await transport.QueryAsync($"SOURce{channel}:VOLTage:LEVel:IMMediate:AMPLitude?", token));
        var highZ = await QuerySupportedLoadModeAsync(channel, token);
        ValidateVoltageEnvelope(amplitude, offsetV, frequency, highZ);
        await WriteCoreAsync($"SOURce{channel}:VOLTage:LEVel:IMMediate:OFFSet {Number(offsetV)}V", token);
    }, ct);

    public Task SetPhaseAsync(int channel, double phaseDegrees, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); ValidateRange(phaseDegrees, 0, 360, nameof(phaseDegrees)); EnsureWriteAllowed();
        await EnsureOutputOffForConfigurationAsync(channel, token);
        if (await QueryWaveformCoreAsync(channel, token) == WaveformType.Noise)
            throw new NotSupportedException("Phase für Rauschen ist laut Handbuch nicht verfügbar.");
        await WriteCoreAsync($"SOURce{channel}:PHASe:ADJust {Number(phaseDegrees)}DEG", token);
    }, ct);

    public Task<WaveformType> GetWaveformAsync(int channel, CancellationToken ct = default) => RunAsync(token =>
    {
        ValidateChannel(channel); RequireConnected();
        return QueryWaveformCoreAsync(channel, token);
    }, ct);

    public Task SetWaveformAsync(int channel, WaveformType waveform, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); EnsureWriteAllowed();
        if (!Enum.IsDefined(waveform)) throw new ArgumentOutOfRangeException(nameof(waveform));
        await EnsureOutputOffForConfigurationAsync(channel, token);
        var tokenValue = waveform switch
        {
            WaveformType.Sine => "SINusoid", WaveformType.Square => "SQUare", WaveformType.Pulse => "PULSe",
            WaveformType.Ramp => "RAMP", WaveformType.Noise => "PRNoise", _ => throw new ArgumentOutOfRangeException(nameof(waveform))
        };
        // OWON kann bei Signalformwechsel die Frequenz automatisch anpassen. Deshalb keine stärkere Zusage.
        await WriteCoreAsync($"SOURce{channel}:FUNCtion:SHAPe {tokenValue}", token);
    }, ct);

    public Task EnableOutputAsync(int channel, CancellationToken ct = default) => SetOutputAsync(channel, true, ct);
    public Task DisableOutputAsync(int channel, CancellationToken ct = default) => SetOutputAsync(channel, false, ct);
    private Task SetOutputAsync(int channel, bool on, CancellationToken ct) => RunAsync(async token =>
    {
        ValidateChannel(channel); EnsureWriteAllowed();
        if (on) await VerifyCurrentOutputConfigurationAsync(channel, token);
        await WriteCoreAsync($"OUTPut{channel}:STATe {(on ? "ON" : "OFF")}", token);
        if (channel == 1) output1 = OutputState.Unknown; else output2 = OutputState.Unknown;
        // Kein Rückschluss auf den physikalischen Ausgang allein aus einem SCPI-Schreibaufruf.
    }, ct);

    public Task ConfigureSweepAsync(SweepConfiguration config, CancellationToken ct = default) => RunAsync(async token =>
    {
        ArgumentNullException.ThrowIfNull(config); ValidateChannel(config.Channel); EnsureWriteAllowed();
        if (!Enum.IsDefined(config.Spacing)) throw new ArgumentOutOfRangeException(nameof(config.Spacing));
        if (!Enum.IsDefined(config.Trigger)) throw new ArgumentOutOfRangeException(nameof(config.Trigger));
        if (config.Trigger == SweepTrigger.External && !profile.SupportsExternalSweepTrigger)
            throw new NotSupportedException("Externer Sweep-Trigger ist für dieses Modell im Benutzerhandbuch nicht bestätigt.");
        await EnsureOutputOffForConfigurationAsync(config.Channel, token);
        var shape = await QueryWaveformCoreAsync(config.Channel, token);
        if (shape is WaveformType.Noise or WaveformType.Pulse)
            throw new NotSupportedException("Das DGE2000-Handbuch nennt diese Signalform nicht für Sweep.");
        ValidateRange(config.StartHz, 1e-6, profile.MaximumFor(shape), nameof(config.StartHz));
        ValidateRange(config.StopHz, 1e-6, profile.MaximumFor(shape), nameof(config.StopHz));
        ValidateRange(config.DurationSeconds, 0.001, 500, nameof(config.DurationSeconds));
        var prefix = $"SOURce{config.Channel}";
        await WriteCoreAsync($"{prefix}:FREQuency:STARt {Number(config.StartHz)}Hz", token);
        await WriteCoreAsync($"{prefix}:FREQuency:STOP {Number(config.StopHz)}Hz", token);
        await WriteCoreAsync($"{prefix}:SWEep:TIME {Number(config.DurationSeconds)}s", token);
        await WriteCoreAsync($"{prefix}:SWEep:SPACing {(config.Spacing == SweepSpacing.Linear ? "LINear" : "LOGarithmic")}", token);
        await WriteCoreAsync($"{prefix}:SWEep:SOURce {config.Trigger switch { SweepTrigger.Timer => "TIMer", SweepTrigger.Manual => "MANual", SweepTrigger.External => "EXTernal", _ => throw new ArgumentOutOfRangeException(nameof(config)) }}", token);
    }, ct);

    public Task<SweepStatus> ReadSweepAsync(int channel, CancellationToken ct = default) => RunAsync(async token =>
    {
        ValidateChannel(channel); RequireConnected();
        var prefix = $"SOURce{channel}";
        var start = ParseNumber(await transport.QueryAsync($"{prefix}:FREQuency:STARt?", token));
        var stop = ParseNumber(await transport.QueryAsync($"{prefix}:FREQuency:STOP?", token));
        var seconds = ParseNumber(await transport.QueryAsync($"{prefix}:SWEep:TIME?", token));
        var law = (await transport.QueryAsync($"{prefix}:SWEep:SPACing?", token)).Trim().ToUpperInvariant() switch
        {
            "LIN" or "LINEAR" => SweepSpacing.Linear,
            "LOG" or "LOGARITHMIC" => SweepSpacing.Logarithmic,
            _ => throw new FormatException("Unbekannter Sweep-Modus.")
        };
        bool active = ParseOutput(await transport.QueryAsync($"{prefix}:SWEep:STATe?", token)) == OutputState.On;
        return new SweepStatus(start, stop, seconds, law, active);
    }, ct);

    public Task StartSweepAsync(int channel, CancellationToken ct = default) => SetSweepStateAsync(channel, true, ct);
    public Task StopSweepAsync(int channel, CancellationToken ct = default) => SetSweepStateAsync(channel, false, ct);
    private Task SetSweepStateAsync(int channel, bool on, CancellationToken ct) => RunAsync(async token =>
    {
        ValidateChannel(channel); EnsureWriteAllowed();
        if (on)
        {
            await VerifyCurrentOutputConfigurationAsync(channel, token);
            var start = ParseNumber(await transport.QueryAsync($"SOURce{channel}:FREQuency:STARt?", token));
            var stop = ParseNumber(await transport.QueryAsync($"SOURce{channel}:FREQuency:STOP?", token));
            var shape = await QueryWaveformCoreAsync(channel, token);
            if (shape is WaveformType.Noise or WaveformType.Pulse) throw new NotSupportedException("Sweep-Signalform ist nicht belegt.");
            ValidateRange(start, 1e-6, profile.MaximumFor(shape), nameof(start));
            ValidateRange(stop, 1e-6, profile.MaximumFor(shape), nameof(stop));
            var amp = ParseNumber(await transport.QueryAsync($"SOURce{channel}:VOLTage:LEVel:IMMediate:AMPLitude?", token));
            var offset = ParseNumber(await transport.QueryAsync($"SOURce{channel}:VOLTage:LEVel:IMMediate:OFFSet?", token));
            var highZ = await QuerySupportedLoadModeAsync(channel, token);
            ValidateVoltageEnvelope(amp, offset, Math.Max(start, stop), highZ);
        }
        await WriteCoreAsync($"SOURce{channel}:SWEep:STATe {(on ? "ON" : "OFF")}", token);
    }, ct);

    private async Task VerifyCurrentOutputConfigurationAsync(int channel, CancellationToken token)
    {
        await EnsureNoActiveModesAsync(channel, token);
        var highZ = await QuerySupportedLoadModeAsync(channel, token);
        var shape = await QueryWaveformCoreAsync(channel, token);
        if (shape == WaveformType.Noise)
            throw new NotSupportedException("Sichere Aktivierung von Rauschen ist nicht implementiert.");
        var frequency = ParseNumber(await transport.QueryAsync($"SOURce{channel}:FREQuency:FIXed?", token));
        ValidateRange(frequency, 1e-6, profile.MaximumFor(shape), nameof(frequency));
        var amplitude = ParseNumber(await transport.QueryAsync($"SOURce{channel}:VOLTage:LEVel:IMMediate:AMPLitude?", token));
        var offset = ParseNumber(await transport.QueryAsync($"SOURce{channel}:VOLTage:LEVel:IMMediate:OFFSet?", token));
        ValidateVoltageEnvelope(amplitude, offset, frequency, highZ);
    }

    private async Task<bool> QuerySupportedLoadModeAsync(int channel, CancellationToken token)
    {
        var impedance = ParseNumber(await transport.QueryAsync($"OUTPut{channel}:IMPedance?", token));
        if (impedance >= 1e30) return true;
        if (Math.Abs(impedance - 50) <= 0.1) return false;
        throw new InvalidOperationException("Nur 50-Ohm- und High-Z-Lastanzeige haben im Client geprüfte Pegelhüllkurven.");
    }

    private async Task EnsureOutputOffForConfigurationAsync(int channel, CancellationToken token)
    {
        if (ParseOutput(await transport.QueryAsync($"OUTPut{channel}:STATe?", token)) != OutputState.Off)
            throw new InvalidOperationException("Konfiguration nur bei rückgelesenem ausgeschaltetem Ausgang ändern.");
    }
    private async Task EnsureFixedCarrierOrOutputOffAsync(int channel, CancellationToken token)
    {
        var state = ParseOutput(await transport.QueryAsync($"OUTPut{channel}:STATe?", token));
        if (state == OutputState.Off) return;
        await EnsureNoActiveModesAsync(channel, token);
    }
    private async Task EnsureNoActiveModesAsync(int channel, CancellationToken token)
    {
        foreach (var mode in new[] { "SWEep", "AM", "FM", "PM", "BURSt", "MODE" })
        {
            var response = await transport.QueryAsync($"SOURce{channel}:{mode}:STATe?", token);
            if (ParseOutput(response) != OutputState.Off)
                throw new InvalidOperationException("Aktive Sweep- oder Modulationsfunktion verhindert eine sichere Direktänderung.");
        }
    }

    private Task<double> QueryNumberAsync(int channel, string suffix, CancellationToken ct) => RunAsync(async token =>
    {
        ValidateChannel(channel); RequireConnected();
        return ParseNumber(await transport.QueryAsync($"SOURce{channel}:{suffix}?", token));
    }, ct);
    private async Task<WaveformType> QueryWaveformCoreAsync(int channel, CancellationToken ct)
    {
        var response = (await transport.QueryAsync($"SOURce{channel}:FUNCtion:SHAPe?", ct)).Trim().ToUpperInvariant();
        return response switch
        {
            "SIN" or "SINUSOID" => WaveformType.Sine, "SQU" or "SQUARE" => WaveformType.Square,
            "PUL" or "PULSE" => WaveformType.Pulse, "RAMP" => WaveformType.Ramp,
            "PRN" or "PRNOISE" => WaveformType.Noise, _ => throw new FormatException($"Unbekannte Signalform-Antwort {response}.")
        };
    }
    private async Task WriteCoreAsync(string command, CancellationToken ct)
    {
        log?.Invoke($"SCPI {command}");
        await transport.WriteAsync(command, ct);
    }
    private void EnsureWriteAllowed()
    {
        RequireConnected();
        if (transport.Kind == ConnectionKind.ExternalScpi && !allowExternalWrites)
            throw new InvalidOperationException("Reale Schreibbefehle sind bis zur expliziten Freigabe gesperrt.");
    }
    private void RequireConnected()
    {
        if (!IsConnected) throw new InvalidOperationException("Keine identifizierte Verbindung.");
    }
    internal void ValidateRequestedWaveform(int channel, WaveformType shape, double frequency, double amplitude,
        double offset, double phase, double duty, bool highZ)
    {
        ValidateChannel(channel);
        ValidateRange(frequency, 1e-6, profile.MaximumFor(shape), nameof(frequency));
        ValidateVoltageEnvelope(amplitude, offset, frequency, highZ);
        ValidateRange(phase, 0, 360, nameof(phase));
        if (shape == WaveformType.Pulse)
            ValidateRange(duty, 100 * 24e-9 * frequency, 100 * (1 - 2 * 24e-9 * frequency), nameof(duty));
    }

    private static void ValidateChannel(int channel)
    {
        if (channel is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(channel));
    }
    private static void ValidateRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"Erlaubt ist {minimum} bis {maximum}.");
    }
    private static void ValidateVoltageEnvelope(double amplitudeVpp, double offsetV, double frequencyHz, bool highZ)
    {
        ValidateRange(frequencyHz, 1e-6, 70e6, nameof(frequencyHz));
        // DGE2000-Grenzen sind auch für DGE3062 eine konservative Untermenge.
        var maxAmplitude = frequencyHz <= 10e6 ? (highZ ? 20.0 : 10.0) : (highZ ? 8.0 : 4.0);
        var maxPeak = frequencyHz <= 10e6 ? (highZ ? 10.0 : 5.0) : (highZ ? 4.0 : 2.0);
        ValidateRange(amplitudeVpp, 0.001, maxAmplitude, nameof(amplitudeVpp));
        if (!double.IsFinite(offsetV) || Math.Abs(offsetV) + amplitudeVpp / 2 > maxPeak)
            throw new ArgumentOutOfRangeException(nameof(offsetV), "Offset und Amplitude überschreiten die dokumentierte Pegelhüllkurve.");
    }
    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static double ParseNumber(string value)
    {
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || !double.IsFinite(result))
            throw new FormatException($"Ungültige numerische SCPI-Antwort {value}.");
        return result;
    }
    private static OutputState ParseOutput(string value) => value.Trim() switch
    {
        "0" => OutputState.Off, "1" => OutputState.On,
        _ => throw new FormatException($"Ungültige Ausgangsantwort {value}.")
    };
    private static DeviceInfo ParseIdentification(string response, ConnectionKind kind)
    {
        var parts = response.Trim().Split(',', 4);
        if (parts.Length != 4 || parts.Any(string.IsNullOrWhiteSpace)) throw new FormatException("*IDN?-Antwort enthält nicht vier Felder.");
        return new DeviceInfo(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), parts[3].Trim(), kind);
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        await RunAsync(async token => { await action(token); return true; }, ct);
    }
    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        await gate.WaitAsync(linked.Token);
        try { return await action(linked.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or FormatException)
        {
            output1 = output2 = OutputState.Unknown;
            info = null;
            log?.Invoke($"Kommunikation unterbrochen. Physischer Ausgangsstatus unbekannt. {ex.GetType().Name}");
            try { await transport.CloseAsync(CancellationToken.None); }
            catch (Exception closeError) { requiresNewClient = true; log?.Invoke($"Transport konnte nicht sauber geschlossen werden. {closeError.GetType().Name}"); }
            if (transport.IsOpen) requiresNewClient = true;
            throw;
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await transport.DisposeAsync();
        gate.Dispose();
    }
}
