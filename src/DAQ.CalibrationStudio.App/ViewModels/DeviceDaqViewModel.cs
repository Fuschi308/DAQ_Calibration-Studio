using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.App.Controls;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Daq;
using DAQ.CalibrationStudio.Domain;
using DAQ.CalibrationStudio.Simulation;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed partial class DeviceOutputViewModel(string label, DeviceDaqViewModel daq) : ObservableObject
{
    public string Id { get; } = label;
    public string Label => $"{Id}  {(Active ? "EIN" : "AUS")}";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Label))] private bool active;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private Task ToggleAsync() => daq.ToggleDigitalAsync(this);
    internal bool CanToggle() => daq.CanDriveOutputs;
}

public sealed partial class DeviceDaqViewModel(DevicesViewModel page, StudioSession session) : ObservableObject
{
    private MeasurementTick? last;
    private string? acquisitionState;
    private DateTimeOffset lastDiagnostic;
    private bool pollingDiagnostics;
    private DeviceIdentity? identity;

    public IReadOnlyList<ChannelRow> Channels { get; } = ChannelCatalog.All
        .Select((channel, index) => new ChannelRow(channel, index)).ToArray();
    private DeviceOutputViewModel[]? outputs;
    public IReadOnlyList<DeviceOutputViewModel> Outputs => outputs ??= new[] { "DO1", "DO2", "DO3", "DO4" }.Select(id => new DeviceOutputViewModel(id, this)).ToArray();
    public bool CanStimulate => page.Simulation && page.Can(DeviceKind.Daq, DeviceCapability.Measurements)
        && SelectedChannel?.Definition.Group is ChannelGroup.Analog or ChannelGroup.Rtd;
    public bool CanRoute => page.Can(DeviceKind.Daq, DeviceCapability.Route) && SelectedChannel?.Definition.Group == ChannelGroup.Analog;
    internal bool CanDriveOutputs => page.Can(DeviceKind.Daq, DeviceCapability.DigitalOutput);
    public bool CanAcquire => page.Can(DeviceKind.Daq, DeviceCapability.AcquisitionControl);
    private bool IsAcquiring => acquisitionState is "RUNNING" or "CLAMP_SETTLE" or "DISCARD";
    public string AcquisitionText => IsAcquiring ? "Erfassung stoppen" : "Erfassung starten";

    [ObservableProperty] private ChannelRow? selectedChannel;
    [ObservableProperty] private decimal? input = 5;
    [ObservableProperty] private Fact[] statistics = [];
    [ObservableProperty] private string raw = "—";
    [ObservableProperty] private string correction = "—";
    [ObservableProperty] private string computed = "—";
    [ObservableProperty] private ChartSeries[] chart = [];

    partial void OnSelectedChannelChanged(ChannelRow? value)
    {
        RefreshAvailability();
        if (value is null) return;
        UpdateFocus(last?.Frame);
        if (page.Simulation && page.Can(DeviceKind.Daq, DeviceCapability.Coefficients))
            _ = ReadCoefficientAsync(value);
    }

    public void Tick()
    {
        if (!page.Simulation && page.Can(DeviceKind.Daq, DeviceCapability.Diagnostics) && !pollingDiagnostics
            && DateTimeOffset.UtcNow - lastDiagnostic > TimeSpan.FromSeconds(4))
        {
            lastDiagnostic = DateTimeOffset.UtcNow;
            _ = PollDiagnosticsAsync();
        }
        if (SelectedChannel is null)
            SelectedChannel = Channels.FirstOrDefault();
        var latest = session.Measurements.Latest;
        if (!session.Devices.Active.Daq.Status.Connected || latest?.Simulated != page.Simulation)
            latest = null;
        if (last == latest) return;
        if (latest is not null && last is null && page.Simulation && SelectedChannel is { } focus
            && page.Can(DeviceKind.Daq, DeviceCapability.Coefficients))
            _ = ReadCoefficientAsync(focus);
        last = latest;
        foreach (var row in Channels)
        {
            var sample = latest?.Frame.Samples.FirstOrDefault(sample => sample.ChannelId == row.Id);
            row.Update(sample, null, sample?.StdDev, session.Tolerance(row.Definition), "% v. Messbereich");
            if (sample?.Corrected is not null)
            {
                row.Status = "Messwert";
                row.StateTone = page.Simulation ? "sim" : "ok";
            }
        }
        foreach (var output in Outputs)
        {
            var matchingId = Channels.FirstOrDefault(row => row.Definition.DigitalOutput == output.Id)?.Id;
            var sample = latest?.Frame.Samples.FirstOrDefault(sample => sample.ChannelId == matchingId);
            output.Active = page.Simulation && sample?.Corrected == 1;
        }
        UpdateFocus(latest?.Frame);
    }

    internal void RefreshAvailability()
    {
        var status = session.Devices.Active.Daq.Status;
        if (identity != status.Identity || !status.Connected)
        {
            identity = status.Identity;
            acquisitionState = null;
            lastDiagnostic = default;
            foreach (var channel in Channels) channel.SetCoefficient(null);
        }
        OnPropertyChanged(nameof(AcquisitionText));
        OnPropertyChanged(nameof(CanAcquire));
        OnPropertyChanged(nameof(CanStimulate));
        OnPropertyChanged(nameof(CanRoute));
        AcquisitionCommand.NotifyCanExecuteChanged();
        DiagnosticsCommand.NotifyCanExecuteChanged();
        ApplyInputCommand.NotifyCanExecuteChanged();
        AssignCommand.NotifyCanExecuteChanged();
        foreach (var output in Outputs) output.ToggleCommand.NotifyCanExecuteChanged();
    }

    private void UpdateFocus(DaqFrame? frame)
    {
        if (SelectedChannel is not { } row) return;
        var sample = frame?.Samples.FirstOrDefault(sample => sample.ChannelId == row.Id);
        var latestTime = frame?.Time ?? DateTimeOffset.UtcNow;
        var ticks = frame is null ? [] : session.Measurements.Snapshot(latestTime.AddSeconds(-5));
        var values = ticks.SelectMany(tick => tick.Frame.Samples)
            .Where(item => item.ChannelId == row.Id && item.Corrected.HasValue)
            .Select(item => item.Corrected!.Value).ToArray();
        var count = values.Length;
        double? mean = count > 0 ? values.Average() : null;
        double? spread = count > 1 ? Math.Sqrt(values.Sum(value => Math.Pow(value - mean!.Value, 2)) / (count - 1)) : null;
        Statistics =
        [
            new("Mittelwert", Presentation.Value(mean, row.Definition)),
            new("Minimum", Presentation.Value(count > 0 ? values.Min() : null, row.Definition)),
            new("Maximum", Presentation.Value(count > 0 ? values.Max() : null, row.Definition)),
            new("Standardabweichung", Presentation.Value(spread, row.Definition, "0.000000")),
            new("Gültige Snapshots (5 s)", count.ToString(Presentation.German))
        ];
        Raw = sample?.Raw?.ToString("N0", Presentation.German) ?? "—";
        Correction = row.Coefficient is { } coefficient
            ? $"Gain {Presentation.Number(coefficient.Gain, "0.000000")} · Offset {Presentation.Value(coefficient.Offset, row.Definition)}"
            : "—";
        Computed = Presentation.Value(sample?.Corrected, row.Definition);
        Chart = Presentation.HistoryChart(ticks, row.Definition, row.ColorIndex, "Soll / Ist");
    }

    private async Task ReadCoefficientAsync(ChannelRow row)
    {
        Coefficient? coefficient = null;
        try
        {
            await session.Devices.TryPollAsync(async (equipment, ct) =>
            {
                if (equipment.Simulated && equipment.Daq.Status is { Connected: true } status && status.Has(DeviceCapability.Coefficients))
                    coefficient = await equipment.Daq.ReadCoefficientAsync(row.Id, ct);
            }, session.LifetimeToken);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error)
        {
            session.LogDevice("DAQ", error.Message);
            return;
        }
        if (coefficient is not null && page.Simulation)
        {
            row.SetCoefficient(coefficient);
            if (SelectedChannel == row) UpdateFocus(last?.Frame);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAcquire))]
    private async Task AcquisitionAsync()
    {
        var requestedStart = !IsAcquiring;
        bool completed = false;
        await page.PerformAsync(async (equipment, token) =>
        {
            equipment.Require(DeviceKind.Daq, DeviceCapability.AcquisitionControl);
            var device = (IDaqAcquisitionControl)equipment.Daq;
            var before = await ((UsbDaqDevice)device).ReadDiagnosticsAsync(token);
            var beforeRunning = before.AcquisitionState is "RUNNING" or "CLAMP_SETTLE" or "DISCARD";
            if (requestedStart != beforeRunning || requestedStart && before.AcquisitionState != "RUNNING")
                await device.SetAcquisitionAsync(requestedStart, token);
            completed = true;
        }, "Erfassungszustand bestätigt.");
        if (completed) await page.IdentifyDeviceAsync(DeviceKind.Daq);
    }

    private async Task PollDiagnosticsAsync()
    {
        pollingDiagnostics = true;
        try
        {
            UsbDaqDiagnostics? snapshot = null;
            UsbDaqCounters? counters = null;
            var selected = session.Devices.Active;
            await session.Devices.TryPollAsync(async (equipment, token) =>
            {
                if (ReferenceEquals(selected, equipment) && equipment.Daq is UsbDaqDevice daq && daq.Status.Connected)
                {
                    snapshot = await daq.ReadDiagnosticsAsync(token);
                    counters = daq.LastCounters;
                }
            }, session.LifetimeToken);
            if (snapshot is not null && ReferenceEquals(selected, session.Devices.Active))
                page.PublishDaqDiagnostics(ApplyDiagnostics(snapshot, counters));
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            session.LogDevice("DAQ", error.Message);
        }
        finally { pollingDiagnostics = false; }
    }

    private bool CanReadDiagnostics() => page.Can(DeviceKind.Daq, DeviceCapability.Diagnostics);

    [RelayCommand(CanExecute = nameof(CanReadDiagnostics))]
    private Task DiagnosticsAsync() => page.IdentifyDeviceAsync(DeviceKind.Daq);

    [RelayCommand(CanExecute = nameof(CanStimulate))]
    private Task ApplyInputAsync()
    {
        if (SelectedChannel is not { } channel || Input is not decimal input)
            return Task.CompletedTask;
        return page.PerformAsync((equipment, token) =>
        {
            if (!equipment.Simulated) throw new InvalidOperationException("Die Eingangsgröße ist eine Simulationsfunktion.");
            ((SimulatedDaq)equipment.Daq).SetStimulus(channel.Id, (double)input, token);
            return Task.CompletedTask;
        }, "Simulierter Eingangswert gesetzt.");
    }

    [RelayCommand(CanExecute = nameof(CanRoute))]
    private Task AssignAsync()
    {
        if (SelectedChannel is not { } channel)
            return Task.CompletedTask;
        return page.PerformAsync((equipment, token) =>
        {
            equipment.Require(DeviceKind.Daq, DeviceCapability.Route);
            return equipment.Daq.SetRouteAsync(channel.Id, token);
        }, $"Prüfadapter auf {channel.Id} geschaltet.");
    }

    internal Task ToggleDigitalAsync(DeviceOutputViewModel output) => page.PerformAsync(async (equipment, token) =>
    {
        equipment.Require(DeviceKind.Daq, DeviceCapability.DigitalOutput);
        var next = !output.Active;
        await equipment.Daq.SetDigitalOutputAsync(output.Id, next, token);
        output.Active = next;
    });

    internal Fact[] ApplyDiagnostics(UsbDaqDiagnostics diagnostics, UsbDaqCounters? counters)
    {
        acquisitionState = diagnostics.AcquisitionState;
        OnPropertyChanged(nameof(AcquisitionText));
        var values = new List<Fact>
        {
            new("Erfassung", diagnostics.AcquisitionState, diagnostics.AcquisitionState == "RUNNING" ? "ok" : ""),
            new("Start-Sperren", $"0x{diagnostics.StartBlocks:X4}", diagnostics.StartBlocks == 0 ? "" : "warn"),
            new("Fehlerflags", $"0x{diagnostics.Faults:X4}", diagnostics.Faults == 0 ? "" : "err"),
            new("Kalibriergeneration", diagnostics.CalibrationGeneration.ToString(Presentation.German)),
            new("Eingangsspannung", Presentation.Value(diagnostics.InputVolts, Quantity.Voltage)),
            new("Analoge Versorgung", Presentation.Value(diagnostics.Analog5Volts, Quantity.Voltage)),
            new("VDDA", Presentation.Value(diagnostics.VddaVolts, Quantity.Voltage)),
            new("CPU / Platine", $"{Presentation.Number(diagnostics.CpuCelsius, "0.0")} / {Presentation.Number(diagnostics.BoardCelsius, "0.0")} °C")
        };
        if (counters is { Initialized: true })
        {
            values.Add(new("ENC / HSC4 / HSC5 / HSC6", $"{counters.Encoder} / {counters.Hsc4} / {counters.Hsc5} / {counters.Hsc6}"));
            values.Add(new("Übererfassungen", counters.Overcapture.ToString(Presentation.German), counters.Overcapture == 0 ? "" : "warn"));
        }
        return values.ToArray();
    }
}
