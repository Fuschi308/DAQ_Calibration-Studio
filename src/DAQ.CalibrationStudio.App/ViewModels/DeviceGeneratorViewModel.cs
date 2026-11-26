using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.App.Controls;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;
using DAQ.CalibrationStudio.Instruments;
using DAQ.CalibrationStudio.Simulation;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed partial class GeneratorChannelViewModel(int channel, DeviceGeneratorViewModel generator) : ObservableObject
{
    internal int Channel { get; } = channel;
    public string Name => $"CH{Channel}";
    public string OutputText => ActualOutput switch { true => "Ausgang ausschalten", false => "Ausgang einschalten", _ => "AUS anfordern" };
    public string[] Waveforms => generator.Waveforms;
    public IReadOnlyList<string> Loads { get; } = ["High-Z", "50 Ω"];
    internal WaveformSettings? LastRead { get; private set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(OutputText))] private bool? actualOutput;
    [ObservableProperty] private string waveform = "Sinus";
    [ObservableProperty] private decimal? frequency = 1000;
    [ObservableProperty] private decimal? amplitude = 5;
    [ObservableProperty] private decimal? offset = 0;
    [ObservableProperty] private decimal? phase = channel == 1 ? 0 : 90;
    [ObservableProperty] private decimal? duty = 50;
    [ObservableProperty] private string load = "High-Z";

    internal void Accept(WaveformSettings? value, bool loadFields = false)
    {
        LastRead = value;
        ActualOutput = value?.Enabled;
        if (!loadFields || value is null) return;
        Waveform = DeviceGeneratorViewModel.ShapeName(value.Shape);
        Frequency = (decimal)value.Frequency;
        Amplitude = (decimal)value.Amplitude;
        Offset = (decimal)value.Offset;
        Phase = (decimal)value.Phase;
        Duty = (decimal)value.Duty;
        Load = value.LoadOhms is null ? "High-Z" : "50 Ω";
    }

    internal void NotifyOptions()
    {
        OnPropertyChanged(nameof(Waveforms));
        ApplyCommand.NotifyCanExecuteChanged();
        ToggleOutputCommand.NotifyCanExecuteChanged();
    }

    internal WaveformSettings Settings(bool enabled)
    {
        if (Frequency is not decimal hz || Amplitude is not decimal amp || Offset is not decimal dc
            || Phase is not decimal degrees || Duty is not decimal percent)
            throw new ArgumentException("Frequenz, Amplitude, Offset, Phase und Tastgrad eingeben.");
        return new(Channel, DeviceGeneratorViewModel.ParseShape(Waveform), (double)hz, (double)amp, (double)dc, (double)degrees,
            (double)percent, Load == "50 Ω" ? 50 : null, enabled);
    }

    private bool CanOperate() => generator.CanWaveform;
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ApplyAsync() => generator.ConfigureAsync(this, false);
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ToggleOutputAsync() => generator.ToggleChannelAsync(this);
}

public sealed partial class DeviceGeneratorViewModel : ObservableObject
{
    private readonly DevicesViewModel page;
    private readonly StudioSession session;
    private DeviceStatus? observed;
    private DateTimeOffset lastTick;
    private DateTimeOffset lastPoll;
    private bool polling;
    private bool encoderActive;

    public DeviceGeneratorViewModel(DevicesViewModel page, StudioSession session)
    {
        this.page = page;
        this.session = session;
        Channels = [new(1, this), new(2, this)];
    }

    public GeneratorChannelViewModel[] Channels { get; }
    public string[] Waveforms => page.Simulation ? ["Sinus", "Rechteck", "Puls", "Rampe", "Dreieck", "DC", "Rauschen"]
        : ["Sinus", "Rechteck", "Puls", "Rampe"];
    public IReadOnlyList<string> EncoderModes { get; } = ["Konstant", "Beschleunigen", "Verzögern", "Zyklus"];
    public IReadOnlyList<string> Directions { get; } = ["Vorwärts", "Rückwärts"];
    public IReadOnlyList<string> SweepChannels { get; } = ["CH1", "CH2"];
    public IReadOnlyList<string> SweepModes { get; } = ["Linear", "Logarithmisch"];
    public bool CanEncoder => page.Simulation && page.Can(DeviceKind.Owon, DeviceCapability.Quadrature);
    public bool CanWaveform => !encoderActive && page.Can(DeviceKind.Owon, DeviceCapability.Waveform);
    public bool CanSweep => !encoderActive && page.Can(DeviceKind.Owon, DeviceCapability.Sweep);
    public string EncoderText => encoderActive ? "Encoder stoppen" : "Encoder starten";

    [ObservableProperty] private string encoderMode = "Konstant";
    [ObservableProperty] private decimal? pulses = 1024;
    [ObservableProperty] private decimal? rpm = 1500;
    [ObservableProperty] private decimal? startRpm = 300;
    [ObservableProperty] private decimal? maxRpm = 1500;
    [ObservableProperty] private decimal? duration = 2;
    [ObservableProperty] private decimal? deceleration = 2;
    [ObservableProperty] private decimal? phase = 90;
    [ObservableProperty] private string direction = "Vorwärts";
    [ObservableProperty] private string sweepChannel = "CH1";
    [ObservableProperty] private decimal? sweepStart = 100;
    [ObservableProperty] private decimal? sweepStop = 1000;
    [ObservableProperty] private decimal? sweepDuration = 1;
    [ObservableProperty] private string sweepMode = "Linear";
    [ObservableProperty] private string sweepStatus = "—";
    [ObservableProperty] private Fact[] encoderMetrics = [];
    [ObservableProperty] private ChartSeries[] chart = [];
    [ObservableProperty] private ChartSeries[] encoderChart = [];

    internal void RefreshAvailability()
    {
        var next = session.Devices.Active.Generator.Status;
        if (!next.Connected || observed?.Identity != next.Identity)
        {
            foreach (var channel in Channels) channel.Accept(null);
            Chart = [];
            EncoderChart = [];
            encoderActive = false;
            SweepStatus = "—";
            lastPoll = default;
        }
        observed = next;
        OnPropertyChanged(nameof(CanEncoder));
        OnPropertyChanged(nameof(CanWaveform));
        OnPropertyChanged(nameof(CanSweep));
        OnPropertyChanged(nameof(EncoderText));
        EncoderCommand.NotifyCanExecuteChanged();
        SweepCommand.NotifyCanExecuteChanged();
        StopSweepCommand.NotifyCanExecuteChanged();
        IdentifyCommand.NotifyCanExecuteChanged();
        foreach (var channel in Channels) channel.NotifyOptions();
    }

    public void Tick()
    {
        if (DateTimeOffset.UtcNow - lastTick < TimeSpan.FromMilliseconds(280)) return;
        lastTick = DateTimeOffset.UtcNow;
        if (!session.Devices.Active.Generator.Status.Connected) return;
        if (!session.Busy && !polling && (lastPoll == default || lastTick - lastPoll > TimeSpan.FromSeconds(4)))
        {
            lastPoll = lastTick;
            _ = PollAsync(loadFields: Channels.All(channel => channel.LastRead is null));
        }
        if (page.Simulation && encoderActive)
            RenderEncoder();
    }

    internal async Task ConfigureAsync(GeneratorChannelViewModel channel, bool enabled)
    {
        WaveformSettings? actual = null;
        await session.PerformAsync(async () =>
        {
            var requested = channel.Settings(enabled);
            var simulated = page.Simulation;
            if (!simulated && enabled && !await session.Files.ConfirmAsync($"OWON {channel.Name} einschalten",
                $"Angeschlossene Last, Massebezug und zulässigen Pegel am Prüfaufbau prüfen. " +
                $"{Presentation.Number(requested.Frequency, "0.###")} Hz · {Presentation.Number(requested.Amplitude, "0.###")} Vpp " +
                $"· Offset {Presentation.Number(requested.Offset, "0.###")} V.", "Ausgang einschalten"))
                return;
            await session.Devices.UseAsync(async (equipment, ct) =>
            {
                if (equipment.Simulated != simulated) throw new InvalidOperationException("Gerätemodus hat sich geändert.");
                equipment.Require(DeviceKind.Owon, DeviceCapability.Waveform);
                actual = await equipment.Generator.ApplyWaveformAsync(requested, ct);
            }, session.LifetimeToken);
            session.LogDevice("OWON", $"{channel.Name}: Einstellungen und Ausgangszustand zurückgelesen.");
            session.Say(enabled ? $"{channel.Name} meldet Ausgang EIN." : $"{channel.Name} konfiguriert, Ausgang AUS.");
        }, exclusive: true);
        if (actual is not null)
        {
            channel.Accept(actual, loadFields: true);
            UpdateChart();
        }
        RefreshAvailability();
    }

    internal async Task ToggleChannelAsync(GeneratorChannelViewModel channel)
    {
        if (channel.ActualOutput == false)
        {
            await ConfigureAsync(channel, enabled: true);
            return;
        }
        await page.PerformAsync(async (equipment, ct) =>
        {
            equipment.Require(DeviceKind.Owon, DeviceCapability.Waveform);
            if (equipment.Generator is OwonDevice physical)
                await physical.DisableChannelAsync(channel.Channel, ct);
            else
            {
                var current = await equipment.Generator.ReadWaveformAsync(channel.Channel, ct);
                var result = await equipment.Generator.ApplyWaveformAsync(current with { Enabled = false }, ct);
                if (result.Enabled) throw new IOException($"{channel.Name} meldet den Ausgang weiterhin EIN.");
            }
        }, $"{channel.Name} meldet Ausgang AUS.");
        await PollAsync(loadFields: false);
    }

    private bool CanIdentify() => page.Can(DeviceKind.Owon, DeviceCapability.Diagnostics);

    [RelayCommand(CanExecute = nameof(CanIdentify))]
    private async Task IdentifyAsync()
    {
        await page.IdentifyDeviceAsync(DeviceKind.Owon);
        await PollAsync(loadFields: true);
    }

    partial void OnSweepChannelChanged(string value)
    {
        SweepStatus = "—";
        lastPoll = default;
    }

    [RelayCommand(CanExecute = nameof(CanSweep))]
    private Task SweepAsync() => SetSweepAsync(true);

    [RelayCommand(CanExecute = nameof(CanSweep))]
    private Task StopSweepAsync() => SetSweepAsync(false);

    private async Task SetSweepAsync(bool enabled)
    {
        var channel = SweepChannel == "CH2" ? 2 : 1;
        SweepReading? reading = null;
        await session.PerformAsync(async () =>
        {
            if (enabled && (SweepStart is null || SweepStop is null || SweepDuration is null))
                throw new ArgumentException("Start, Ende und Dauer des Sweeps eingeben.");
            var request = new SweepSettings(channel, (double)(SweepStart ?? 0), (double)(SweepStop ?? 0),
                (double)(SweepDuration ?? 0), SweepMode == "Logarithmisch");
            var simulated = page.Simulation;
            if (enabled && !simulated && !await session.Files.ConfirmAsync($"OWON CH{channel} Sweep starten",
                $"Der Ausgang wird aktiviert. Last, Massebezug, Signalpegel und Frequenzbereich " +
                $"{Presentation.Number(request.StartHz, "0.###")}–{Presentation.Number(request.StopHz, "0.###")} Hz am Aufbau prüfen.", "Sweep einschalten"))
                return;
            await session.Devices.UseAsync(async (eq, ct) =>
            {
                if (eq.Simulated != simulated) throw new InvalidOperationException("Gerätemodus hat sich geändert.");
                eq.Require(DeviceKind.Owon, DeviceCapability.Sweep);
                await eq.Generator.SetSweepAsync(request, enabled, ct);
                reading = await eq.Generator.ReadSweepAsync(channel, ct);
            }, session.LifetimeToken);
            session.LogDevice("OWON", $"CH{channel}: Sweep-Status zurückgelesen.");
        }, exclusive: true);
        if (reading is not null) SetSweepStatus(reading);
        RefreshAvailability();
        await PollAsync(loadFields: false);
    }

    private void SetSweepStatus(SweepReading reading)
    {
        var state = reading.Active switch { true => "Sweep aktiv", false => "Sweep aus", _ => "Unbekannt" };
        SweepStatus = reading.Settings is { } settings
            ? $"{state} · {Presentation.Number(settings.StartHz, "0.###")}–{Presentation.Number(settings.StopHz, "0.###")} Hz"
                + $" · {Presentation.Number(settings.Seconds, "0.###")} s · {(settings.Logarithmic ? "logarithmisch" : "linear")}"
            : state;
    }

    [RelayCommand(CanExecute = nameof(CanEncoder))]
    private async Task EncoderAsync()
    {
        var stop = encoderActive;
        var configured = false;
        await page.PerformAsync(async (equipment, ct) =>
        {
            if (!equipment.Simulated || equipment.Generator is not SimulatedGenerator generator)
                throw new NotSupportedException("Dynamische Quadratur ist für reale Geräte gesperrt.");
            if (stop)
            {
                if (await generator.DisableAsync(ct) != OutputState.Off)
                    throw new IOException("Ausgangszustand der Simulation ist unbekannt.");
            }
            else
            {
                var mode = EncoderMode switch
                {
                    "Beschleunigen" => Simulation.EncoderMode.Ramp, "Verzögern" => Simulation.EncoderMode.Coast,
                    "Zyklus" => Simulation.EncoderMode.Cycle, _ => Simulation.EncoderMode.Constant
                };
                if (Pulses is not decimal periods || periods != decimal.Truncate(periods)
                    || Phase is not decimal degrees || MaxRpm is not decimal end || StartRpm is not decimal start
                    || Rpm is not decimal rpmValue || Duration is not decimal ramp || Deceleration is not decimal decel)
                    throw new ArgumentException("Encoderparameter vollständig eingeben.");
                var endRpm = mode == Simulation.EncoderMode.Constant ? rpmValue : end;
                generator.StartEncoder(new EncoderMotion(mode, checked((int)periods), (double)start, (double)endRpm, (double)degrees,
                    Direction == "Rückwärts", (double)ramp, (double)decel), ct);
            }
            configured = true;
        });
        if (configured) encoderActive = !stop;
        RefreshAvailability();
        if (encoderActive) RenderEncoder();
        else { EncoderChart = []; EncoderMetrics = []; }
        await PollAsync(loadFields: false);
    }

    private async Task PollAsync(bool loadFields)
    {
        if (polling || session.Busy || !session.Devices.Active.Generator.Status.Connected) return;
        polling = true;
        try
        {
            var selected = session.Devices.Active;
            WaveformSettings? channel1 = null, channel2 = null;
            SweepReading? sweep = null;
            var index = SweepChannel == "CH2" ? 2 : 1;
            await session.Devices.TryPollAsync(async (equipment, ct) =>
            {
                if (!ReferenceEquals(selected, equipment)) return;
                channel1 = await equipment.Generator.ReadWaveformAsync(1, ct);
                channel2 = await equipment.Generator.ReadWaveformAsync(2, ct);
                if (equipment.Generator.Status.Has(DeviceCapability.Sweep))
                    sweep = await equipment.Generator.ReadSweepAsync(index, ct);
            }, session.LifetimeToken);
            if (!ReferenceEquals(selected, session.Devices.Active)) return;
            if (channel1 is not null) Channels[0].Accept(channel1, loadFields && !encoderActive);
            if (channel2 is not null) Channels[1].Accept(channel2, loadFields && !encoderActive);
            if (sweep is not null && index == (SweepChannel == "CH2" ? 2 : 1)) SetSweepStatus(sweep);
            if (channel1 is not null || channel2 is not null) UpdateChart();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            session.LogDevice("OWON", error.Message);
            SweepStatus = "Nicht verfügbar";
        }
        finally { polling = false; }
    }

    private void UpdateChart() => Chart = MakeChart(Channels.Select(channel => channel.LastRead).OfType<WaveformSettings>().ToArray());

    private void RenderEncoder()
    {
        var a = session.Simulator.Generator.Current(1);
        var b = session.Simulator.Generator.Current(2);
        var daq = session.Devices.Active.Daq.Status.Connected
            ? session.Measurements.Latest?.Frame.Samples.FirstOrDefault(sample => sample.ChannelId == "ENC1")?.Corrected : null;
        var periods = (double)(Pulses ?? 0);
        EncoderMetrics =
        [
            new("Drehzahl", $"{Presentation.Number(periods > 0 ? a.Frequency * 60 / periods : null, "0.0")} U/min"),
            new("Sollfrequenz", Presentation.Value(a.Frequency, Quantity.Frequency)),
            new("DAQ-Istwert", Presentation.Value(daq, Quantity.Frequency)),
            new("Abweichung", Presentation.Value(daq - a.Frequency, Quantity.Frequency)),
            new("Phasenlage A/B", $"{Presentation.Number(b.Phase - a.Phase, "0.0")}°"),
            new("Pegel", a.Enabled && b.Enabled ? "0–5 V · simuliert" : "Aus"),
            new("Zustand", a.Enabled ? "Aktiv · simuliert" : "Verlauf beendet", a.Enabled ? "sim" : "")
        ];
        EncoderChart = MakeChart([a, b], encoder: true);
    }

    private static ChartSeries[] MakeChart(WaveformSettings[] settings, bool encoder = false)
    {
        if (settings.Length == 0) return [];
        var frequency = settings.Where(item => item.Frequency > 0 && item.Enabled).Select(item => item.Frequency).DefaultIfEmpty(1000).Min();
        var seconds = Enumerable.Range(0, 420).Select(index => index * 2d / (419 * frequency)).ToArray();
        return settings.Select(item => new ChartSeries(encoder ? item.Channel == 1 ? "A" : "B" : $"CH{item.Channel}",
            "V", seconds, seconds.Select(time => SimulatedGenerator.ValueAt(item, time)).ToArray(), item.Channel - 1)
        {
            IsStep = item.Shape is SignalShape.Square or SignalShape.Pulse
        }).ToArray();
    }

    internal static string ShapeName(SignalShape shape) => shape switch
    {
        SignalShape.Square => "Rechteck", SignalShape.Pulse => "Puls", SignalShape.Ramp => "Rampe",
        SignalShape.Triangle => "Dreieck", SignalShape.Dc => "DC", SignalShape.Noise => "Rauschen", _ => "Sinus"
    };

    internal static SignalShape ParseShape(string value) => value switch
    {
        "Rechteck" => SignalShape.Square, "Puls" => SignalShape.Pulse, "Rampe" => SignalShape.Ramp,
        "Dreieck" => SignalShape.Triangle, "DC" => SignalShape.Dc, "Rauschen" => SignalShape.Noise, _ => SignalShape.Sine
    };
}
