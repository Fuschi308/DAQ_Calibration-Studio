using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.App.Controls;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed record SourceQuickPoint(string Label, decimal Value);

public sealed partial class DeviceSourceViewModel(DevicesViewModel page, StudioSession session) : ObservableObject
{
    private DateTimeOffset lastPoll;
    private long lastSequence = -1;
    private bool reading;
    private SourceReading? readback;
    private CancellationTokenSource? auto;

    public IReadOnlyList<string> Modes { get; } = ["Spannung", "Strom"];
    public Quantity SelectedQuantity => Mode == "Strom" ? Quantity.Current : Quantity.Voltage;
    public bool CanSelectMode => page.Simulation && CanProgram && readback?.Output != OutputState.On;
    public bool CanToggle => page.Can(DeviceKind.Fnirsi, DeviceCapability.AnalogSource);
    public bool CanProgram => CanToggle || page.Can(DeviceKind.Fnirsi, DeviceCapability.SetpointOnly) && readback is not null;
    public string AutoText => auto is null ? "Sequenz starten" : "Sequenz stoppen";
    public string OutputText => readback?.Output switch
    {
        OutputState.Off => "Ausgang einschalten",
        OutputState.On => "Ausgang ausschalten",
        _ => "Ausgang unbekannt"
    };
    public string RequestedText => Present(readback?.Requested);
    public string ConfirmedText => Present(readback?.Confirmed);
    public string IndependentText => Present(readback?.IndependentReference);
    private string Present(double? value) => Presentation.Value(value, readback?.Quantity ?? SelectedQuantity, "0.000");
    public string[] ChannelIds => ChannelCatalog.All.Where(item => item.Group == ChannelGroup.Analog && item.Quantity == SelectedQuantity)
        .Select(item => item.Id).ToArray();
    public SourceQuickPoint[] QuickPoints => (SelectedQuantity == Quantity.Current
            ? new decimal[] { 0, 4, 8, 12, 16, 20 } : new decimal[] { 0, 2, 4, 6, 8, 10 })
        .Select(value => new SourceQuickPoint(Presentation.Value((double)value, SelectedQuantity, "0"), value)).ToArray();

    [ObservableProperty] private string mode = "Spannung";
    [ObservableProperty] private string? channelId = "AI-V1";
    [ObservableProperty] private decimal? setpoint = 5;
    [ObservableProperty] private decimal? autoDwell = 1;
    [ObservableProperty] private bool autoRepeat;
    [ObservableProperty] private Fact[] channelReadings = [];
    [ObservableProperty] private ChartSeries[] chart = [];

    partial void OnModeChanged(string value)
    {
        var ids = ChannelIds;
        OnPropertyChanged(nameof(ChannelIds));
        OnPropertyChanged(nameof(QuickPoints));
        ChannelId = ids[0];
        if (page.Simulation && SelectedQuantity == Quantity.Voltage && Setpoint > 10)
            Setpoint = 10;
    }

    partial void OnChannelIdChanged(string? value) => RefreshChannel();

    internal void RefreshAvailability()
    {
        if (!session.Devices.Active.Source.Status.Connected)
            SetReading(null);
        RaiseAvailability();
    }

    private void RaiseAvailability()
    {
        OnPropertyChanged(nameof(CanSelectMode));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(CanProgram));
        ApplyCommand.NotifyCanExecuteChanged();
        QuickPointCommand.NotifyCanExecuteChanged();
        ToggleOutputCommand.NotifyCanExecuteChanged();
        AutoCommand.NotifyCanExecuteChanged();
    }

    private void SetReading(SourceReading? value)
    {
        if (readback == value) return;
        readback = value;
        if (!page.Simulation && value is not null)
            Mode = value.Quantity == Quantity.Current ? "Strom" : "Spannung";
        OnPropertyChanged(nameof(OutputText));
        OnPropertyChanged(nameof(RequestedText));
        OnPropertyChanged(nameof(ConfirmedText));
        OnPropertyChanged(nameof(IndependentText));
        RaiseAvailability();
    }

    public void Tick()
    {
        if (!session.Devices.Active.Source.Status.Connected)
            SetReading(null);
        else if (!session.Busy && !reading && DateTimeOffset.UtcNow - lastPoll > TimeSpan.FromSeconds(page.Simulation ? 0.5 : 3))
        {
            lastPoll = DateTimeOffset.UtcNow;
            _ = PollAsync();
        }

        var sequence = session.Measurements.Latest?.Frame.Sequence ?? -1;
        if (lastSequence != sequence)
        {
            lastSequence = sequence;
            RefreshChannel();
        }
    }

    private async Task PollAsync()
    {
        reading = true;
        try
        {
            SourceReading? current = null;
            await session.Devices.TryPollAsync(async (equipment, ct) =>
            {
                if (equipment.Source.Status.Connected)
                    current = await equipment.Source.ReadAsync(ct);
            }, session.LifetimeToken);
            if (current is not null) SetReading(current);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            SetReading(null);
            session.LogDevice("FNIRSI", error.Message);
        }
        finally { reading = false; }
    }

    private void RefreshChannel()
    {
        var id = ChannelId;
        var latest = session.Measurements.Latest;
        if (id is null || latest is not MeasurementTick tick || tick.Simulated != page.Simulation
            || !session.Devices.Active.Daq.Status.Connected || !ChannelIds.Contains(id))
        {
            ChannelReadings = [];
            Chart = [];
            return;
        }
        var channel = ChannelCatalog.Get(id);
        var sample = tick.Frame.Samples.FirstOrDefault(item => item.ChannelId == id);
        ChannelReadings =
        [
            new("DAQ-Istwert", Presentation.Value(sample?.Corrected, channel)),
            new("DAQ-Sollwert", Presentation.Value(sample?.Setpoint, channel)),
            new("Abweichung", Presentation.Value(sample?.Corrected - sample?.Setpoint, channel)),
            new("Prüfadapter", page.Simulation
                ? session.Simulator.Daq.SelectedRoute ?? "Nicht zugeordnet" : "Anschluss am Prüfaufbau")
        ];
        Chart = Presentation.HistoryChart(session.Measurements.Snapshot(tick.Frame.Time.AddSeconds(-12)), channel, 0, "Soll / Ist");
    }

    [RelayCommand(CanExecute = nameof(CanProgram))]
    private Task ApplyAsync() => Setpoint is decimal value ? ProgramAsync((double)value) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanProgram))]
    private Task QuickPointAsync(decimal value)
    {
        Setpoint = value;
        return ProgramAsync((double)value);
    }

    private async Task ProgramAsync(double value)
    {
        var quantity = SelectedQuantity;
        var channel = ChannelId;
        SourceReading? result = null;
        await session.PerformAsync(async () =>
        {
            var simulated = page.Simulation;
            if (!simulated && !await session.Files.ConfirmAsync("FNIRSI-Sollwert programmieren",
                $"{Presentation.Value(value, quantity, "0.000")}. Der Ausgangszustand ist unbekannt. " +
                "Wenn der Ausgang am Gerät aktiv ist, ändert sich das Signal sofort. Anschluss, Messbereich und aktiven Modus am Gerät prüfen.",
                "Sollwert senden"))
                return;
            await session.Devices.UseAsync(async (equipment, ct) =>
            {
                if (equipment.Simulated != simulated) throw new InvalidOperationException("Gerätemodus hat sich geändert.");
                if (equipment.Simulated)
                {
                    equipment.Require(DeviceKind.Fnirsi, DeviceCapability.AnalogSource);
                    var actual = await equipment.Source.ReadAsync(ct);
                    if (actual.Output == OutputState.Off && channel is not null && equipment.Daq.Status.Connected)
                        await equipment.Daq.SetRouteAsync(channel, ct);
                    result = await equipment.Source.ApplyAsync(quantity, value, actual.Output == OutputState.On, ct);
                }
                else
                {
                    equipment.Require(DeviceKind.Fnirsi, DeviceCapability.SetpointOnly);
                    var actual = await equipment.Source.ReadAsync(ct);
                    if (actual.Quantity != quantity)
                        throw new InvalidOperationException("Der Modus am Gerät hat sich geändert. Sollwert erneut prüfen.");
                    result = await ((IManualSetpointSource)equipment.Source).WriteSetpointAsync(quantity, value, ct);
                }
            }, session.LifetimeToken);
            session.LogDevice("FNIRSI", "Sollwert programmiert und aus dem Register zurückgelesen.");
            session.Say("Geräte-Sollwert zurückgelesen.");
        }, exclusive: true);
        if (result is not null) SetReading(result);
        RefreshAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleOutputAsync()
    {
        if (Setpoint is not decimal value) return;
        var quantity = SelectedQuantity;
        var channel = ChannelId;
        SourceReading? result = null;
        await page.PerformAsync(async (equipment, ct) =>
        {
            equipment.Require(DeviceKind.Fnirsi, DeviceCapability.AnalogSource);
            var current = await equipment.Source.ReadAsync(ct);
            if (current.Output == OutputState.On)
            {
                var off = await equipment.Source.DisableAsync(ct);
                if (off != OutputState.Off) throw new IOException("FNIRSI meldet den Ausgang nicht als AUS.");
                result = await equipment.Source.ReadAsync(ct);
            }
            else if (current.Output == OutputState.Off)
            {
                if (channel is not null && equipment.Simulated && equipment.Daq.Status.Connected)
                    await equipment.Daq.SetRouteAsync(channel, ct);
                result = await equipment.Source.ApplyAsync(quantity, (double)value, true, ct);
            }
            else
            {
                throw new InvalidOperationException("FNIRSI-Ausgangszustand ist unbekannt.");
            }
        });
        if (result is not null) SetReading(result);
    }

    private bool CanRunAuto() => auto is not null || page.Simulation &&
        page.Can(DeviceKind.Fnirsi, DeviceCapability.AnalogSource) && page.Can(DeviceKind.Daq, DeviceCapability.Route);

    [RelayCommand(CanExecute = nameof(CanRunAuto), AllowConcurrentExecutions = true)]
    private async Task AutoAsync()
    {
        if (auto is not null)
        {
            auto.Cancel();
            return;
        }
        if (ChannelId is not { } channel || AutoDwell is not decimal dwell || dwell is < 0.1m or > 600)
        {
            session.Say("Prüfkanal und Verweilzeit zwischen 0,1 und 600 Sekunden wählen.");
            return;
        }
        var quantity = SelectedQuantity;
        var points = QuickPoints.Select(point => (double)point.Value).ToArray();
        var repeat = AutoRepeat;
        using var control = CancellationTokenSource.CreateLinkedTokenSource(session.LifetimeToken);
        auto = control;
        OnPropertyChanged(nameof(AutoText));
        AutoCommand.NotifyCanExecuteChanged();
        try
        {
            await page.PerformAsync(async (equipment, ct) =>
            {
                if (!equipment.Simulated) throw new InvalidOperationException("Automatische FNIRSI-Punktfolge ist auf die Simulation begrenzt.");
                try
                {
                    if (await equipment.Source.DisableAsync(control.Token) != OutputState.Off)
                        throw new IOException("Ausgangszustand konnte nicht bestätigt werden.");
                    await equipment.Daq.SetRouteAsync(channel, control.Token);
                    do
                    {
                        foreach (var point in points)
                        {
                            var next = await equipment.Source.ApplyAsync(quantity, point, true, control.Token);
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => SetReading(next));
                            for (var remaining = (double)dwell; remaining > 1e-9;)
                            {
                                var interval = Math.Min(0.10, remaining);
                                await equipment.DelayAsync(interval, control.Token);
                                var frame = await equipment.Daq.ReadFrameAsync(control.Token);
                                session.Measurements.Publish(frame, true);
                                remaining -= interval;
                            }
                        }
                    } while (repeat);
                }
                catch (OperationCanceledException) when (control.IsCancellationRequested) { }
                finally
                {
                    if (await equipment.Source.DisableAsync(CancellationToken.None) != OutputState.Off)
                        throw new IOException("Der Ausgangszustand der Simulation ist unbekannt.");
                    var final = await equipment.Source.ReadAsync(CancellationToken.None);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => SetReading(final));
                }
            });
        }
        finally
        {
            auto = null;
            OnPropertyChanged(nameof(AutoText));
            RaiseAvailability();
        }
    }
}
