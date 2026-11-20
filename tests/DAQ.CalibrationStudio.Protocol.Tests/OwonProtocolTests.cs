using Owon.DGE;
using Owon.DGE.Simulator;

namespace DAQ.CalibrationStudio.Protocol.Tests;

public sealed class OwonProtocolTests
{
    [Fact(DisplayName = "Verbindungsaufbau ohne Ausgangsaktivierung")]
    public async Task ScpiCase1()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync();
        Equal("DGE2035", (await c.GetDeviceInfoAsync()).Model);
        var status = await c.GetStatusAsync(); Equal(OutputState.Off, status.Channel1); Equal(OutputState.Off, status.Channel2);
        True(!s.Commands.Any(x => x.Contains("STATe ON", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact(DisplayName = "Frequenzformat und Rücklesen")]
    public async Task ScpiCase2()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.SetWaveformAsync(1, WaveformType.Square); await c.SetFrequencyAsync(1, 100000);
        Equal(100000d, await c.GetFrequencyAsync(1));
        True(s.Commands.Contains("SOURce1:FREQuency:FIXed 100000Hz"));
    }

    [Fact(DisplayName = "Amplitude und Offset mit 50-Ohm-Hüllkurve")]
    public async Task ScpiCase3()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.SetAmplitudeAsync(1, 2); await c.SetOffsetAsync(1, 1);
        Equal(2d, await c.GetAmplitudeAsync(1)); Equal(1d, await c.GetOffsetAsync(1));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => c.SetOffsetAsync(1, 4.5));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => c.SetAmplitudeAsync(1, 20));
    }

    [Fact(DisplayName = "Phase nutzt DEG und Abfrage RAD")]
    public async Task ScpiCase4()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.SetPhaseAsync(2, 90);
        True(Math.Abs(await c.GetPhaseDegreesAsync(2) - 90) < 1e-9);
        True(s.Commands.Contains("SOURce2:PHASe:ADJust 90DEG"));
    }

    [Fact(DisplayName = "Ungültige Kanal-, Frequenz- und Phasenwerte")]
    public async Task ScpiCase5()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync();
        await ThrowsAsync<ArgumentOutOfRangeException>(() => c.SetFrequencyAsync(3, 100));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => c.SetFrequencyAsync(1, double.NaN));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => c.SetPhaseAsync(1, 361));
    }

    [Fact(DisplayName = "Signalformabhängige Frequenzgrenze")]
    public async Task ScpiCase6()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.SetWaveformAsync(1, WaveformType.Square);
        await ThrowsAsync<ArgumentOutOfRangeException>(() => c.SetFrequencyAsync(1, 16e6));
    }

    [Fact(DisplayName = "Sweep-Konfiguration und Zustand")]
    public async Task ScpiCase7()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.SetWaveformAsync(1, WaveformType.Square);
        await c.ConfigureSweepAsync(new SweepConfiguration(1, 1000, 100000, 2));
        await c.StartSweepAsync(1); await c.StopSweepAsync(1);
        True(s.Commands.Contains("SOURce1:SWEep:STATe ON"));
        True(s.Commands.Contains("SOURce1:SWEep:STATe OFF"));
    }

    [Fact(DisplayName = "Sweep und Rauschen abgewiesen")]
    public async Task ScpiCase8()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.SetWaveformAsync(1, WaveformType.Noise);
        await ThrowsAsync<NotSupportedException>(() => c.ConfigureSweepAsync(new SweepConfiguration(1, 1, 2, 1)));
        await ThrowsAsync<NotSupportedException>(() => c.SetPhaseAsync(1, 90));
    }

    [Fact(DisplayName = "Explizite Ausgangssteuerung mit Rücklesung")]
    public async Task ScpiCase9()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.EnableOutputAsync(2);
        Equal(OutputState.On, (await c.GetStatusAsync()).Channel2);
        await c.DisableOutputAsync(2); Equal(OutputState.Off, (await c.GetStatusAsync()).Channel2);
    }

    [Fact(DisplayName = "Fehlerwarteschlange")]
    public async Task ScpiCase10()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); s.InjectError("-201,\"Synthetic local mode\"");
        Equal("-201,\"Synthetic local mode\"", await c.GetNextErrorAsync());
    }

    [Fact(DisplayName = "Timeout und unbekannter Ausgangszustand")]
    public async Task ScpiCase11()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035, timeout: TimeSpan.FromMilliseconds(40));
        await c.ConnectAsync(); s.Delay = TimeSpan.FromMilliseconds(100);
        await ThrowsAsync<OperationCanceledException>(() => c.GetStatusAsync());
        True(!c.IsConnected);
    }

    [Fact(DisplayName = "Abbruch laufender Operation")]
    public async Task ScpiCase12()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); s.Delay = TimeSpan.FromSeconds(2);
        using var cts = new CancellationTokenSource(20);
        await ThrowsAsync<OperationCanceledException>(() => c.GetFrequencyAsync(1, cts.Token));
    }

    [Fact(DisplayName = "Gleichzeitige SCPI-Operationen sind serialisiert")]
    public async Task ScpiCase13()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => c.GetFrequencyAsync(1)));
        Equal(10, s.Commands.Count(x => x == "SOURce1:FREQuency:FIXed?"));
    }

    [Fact(DisplayName = "Falsches Gerätemodell wird geschlossen")]
    public async Task ScpiCase14()
    {
        var s = new OwonSimulatorTransport("DGE2070"); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await ThrowsAsync<InvalidOperationException>(() => c.ConnectAsync()); True(!s.IsOpen);
    }

    [Fact(DisplayName = "Reale Schreibbefehle bleiben standardmäßig gesperrt")]
    public async Task ScpiCase15()
    {
        var s = new ExternalTransport(new OwonSimulatorTransport()); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync();
        await ThrowsAsync<InvalidOperationException>(() => c.EnableOutputAsync(1));
        True(!s.Inner.Commands.Any(x => x.StartsWith("OUTPut1:STATe ", StringComparison.Ordinal)));
    }

    [Fact(DisplayName = "Fehlerhafte Zahlenantwort wird erkannt")]
    public async Task ScpiCase16()
    {
        var s = new CorruptNumericTransport(new OwonSimulatorTransport()); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await ThrowsAsync<FormatException>(() => c.GetFrequencyAsync(1));
    }

    [Fact(DisplayName = "Puls-Tastverhältnis und Frequenzgrenze")]
    public async Task ScpiCase17()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.SetWaveformAsync(1, WaveformType.Pulse); await c.SetFrequencyAsync(1, 100000);
        await c.SetPulseDutyCycleAsync(1, 50); Equal(50d, await c.GetPulseDutyCycleAsync(1));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => c.SetPulseDutyCycleAsync(1, 0.01));
    }

    [Fact(DisplayName = "Lastanzeige nur bei ausgeschaltetem Ausgang")]
    public async Task ScpiCase18()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.SetOutputLoadOhmsAsync(1, 100);
        Equal(100d, await c.GetOutputLoadOhmsAsync(1));
        await ThrowsAsync<InvalidOperationException>(() => c.EnableOutputAsync(1));
        await c.SetOutputLoadOhmsAsync(1, 50); await c.EnableOutputAsync(1);
        await ThrowsAsync<InvalidOperationException>(() => c.SetOutputLoadOhmsAsync(1, 100));
    }

    [Fact(DisplayName = "Pegelgrenze oberhalb 10 MHz")]
    public async Task ScpiCase19()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.SetFrequencyAsync(1, 11e6);
        await ThrowsAsync<ArgumentOutOfRangeException>(() => c.SetAmplitudeAsync(1, 5));
    }

    [Fact(DisplayName = "High-Z-Anzeige mit getrenntem Spannungsbereich")]
    public async Task ScpiCase20()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.SetOutputHighImpedanceAsync(1);
        True((await c.GetOutputLoadSettingAsync(1)).IsHighImpedance);
        await c.SetAmplitudeAsync(1, 12); await c.SetOffsetAsync(1, 2);
        await c.EnableOutputAsync(1); Equal(OutputState.On, (await c.GetStatusAsync()).Channel1);
        await ThrowsAsync<InvalidOperationException>(() => c.SetOutputHighImpedanceAsync(1));
        await c.DisableOutputAsync(1);
        await ThrowsAsync<ArgumentOutOfRangeException>(() => c.SetFrequencyAsync(1, 11e6));
    }

    [Fact(DisplayName = "Ungültiger Sweep-Enum führt zu keinem Schreibbefehl")]
    public async Task ScpiCase21()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); var count = s.Commands.Count;
        await ThrowsAsync<ArgumentOutOfRangeException>(() => c.ConfigureSweepAsync(new SweepConfiguration(1, 1, 2, 1, (SweepSpacing)99)));
        True(!s.Commands.Skip(count).Any(x => !x.EndsWith('?')));
        await ThrowsAsync<NotSupportedException>(() => c.ConfigureSweepAsync(new SweepConfiguration(1, 1, 2, 1, Trigger: SweepTrigger.External)));
        True(!s.Commands.Skip(count).Any(x => !x.EndsWith('?')));
    }

    [Fact(DisplayName = "Nach Timeout wird Sitzung geschlossen und frisch verbunden")]
    public async Task ScpiCase22()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035, timeout: TimeSpan.FromMilliseconds(40));
        await c.ConnectAsync(); s.Delay = TimeSpan.FromMilliseconds(100);
        await ThrowsAsync<OperationCanceledException>(() => c.GetFrequencyAsync(1));
        True(!s.IsOpen); s.Delay = TimeSpan.Zero;
        await c.ConnectAsync(); Equal(1000d, await c.GetFrequencyAsync(1));
        Equal(2, s.Commands.Count(x => x == "*IDN?"));
    }

    [Fact(DisplayName = "Aktiver Festfrequenzausgang erlaubt geprüfte Direktänderung")]
    public async Task ScpiCase23()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.EnableOutputAsync(1);
        await c.SetAmplitudeAsync(1, 2); await c.SetOffsetAsync(1, 1); await c.SetFrequencyAsync(1, 100000);
        Equal(2d, await c.GetAmplitudeAsync(1));
        await ThrowsAsync<ArgumentOutOfRangeException>(() => c.SetAmplitudeAsync(1, 10));
        await ThrowsAsync<InvalidOperationException>(() => c.SetWaveformAsync(1, WaveformType.Square));
        await c.DisableOutputAsync(1); await c.SetWaveformAsync(1, WaveformType.Square);
    }

    [Fact(DisplayName = "Aktiver Sweep blockiert direkte Frequenzänderung")]
    public async Task ScpiCase24()
    {
        var s = new OwonSimulatorTransport(); await using var c = new OwonDgeClient(s, ModelProfile.Dge2035);
        await c.ConnectAsync(); await c.ConfigureSweepAsync(new SweepConfiguration(1, 1000, 2000, 1));
        await c.EnableOutputAsync(1); await c.StartSweepAsync(1);
        await ThrowsAsync<InvalidOperationException>(() => c.SetFrequencyAsync(1, 1500));
        await c.StopSweepAsync(1); await c.SetFrequencyAsync(1, 1500);
    }

    static void True(bool condition) { if (!condition) throw new Exception("Assertion fehlgeschlagen"); }
    static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Erwartet {expected}, erhalten {actual}"); }
    static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Erwartete Ausnahme {typeof(T).Name} blieb aus");
    }
    sealed class ExternalTransport(OwonSimulatorTransport inner) : IScpiTransport
    {
        public OwonSimulatorTransport Inner { get; } = inner;
        public ConnectionKind Kind => ConnectionKind.ExternalScpi;
        public bool IsOpen => Inner.IsOpen;
        public Task OpenAsync(CancellationToken ct) => Inner.OpenAsync(ct);
        public Task CloseAsync(CancellationToken ct) => Inner.CloseAsync(ct);
        public Task WriteAsync(string command, CancellationToken ct) => Inner.WriteAsync(command, ct);
        public Task<string> QueryAsync(string command, CancellationToken ct) => Inner.QueryAsync(command, ct);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }
    sealed class CorruptNumericTransport(OwonSimulatorTransport inner) : IScpiTransport
    {
        public ConnectionKind Kind => ConnectionKind.Simulation;
        public bool IsOpen => inner.IsOpen;
        public Task OpenAsync(CancellationToken ct) => inner.OpenAsync(ct);
        public Task CloseAsync(CancellationToken ct) => inner.CloseAsync(ct);
        public Task WriteAsync(string command, CancellationToken ct) => inner.WriteAsync(command, ct);
        public Task<string> QueryAsync(string command, CancellationToken ct) => command == "SOURce1:FREQuency:FIXed?" ? Task.FromResult("invalid") : inner.QueryAsync(command, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
