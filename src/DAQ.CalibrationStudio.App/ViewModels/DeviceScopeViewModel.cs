using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.App.Controls;
using DAQ.CalibrationStudio.Application;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed record ScopeMetric(string Label, string Ch1, string Ch2);

public sealed partial class DeviceScopeViewModel(DevicesViewModel page, StudioSession session) : ObservableObject
{
    private DeviceStatus? observed;
    private ScopeCapture? capture;
    private bool capturedInSimulation;
    private bool? running;
    private bool polling;
    private bool settingsLoaded;
    private DateTimeOffset lastPoll;

    public IReadOnlyList<string> Couplings { get; } = ["DC", "AC", "GND"];
    public IReadOnlyList<string> TriggerChannels { get; } = ["CH1", "CH2"];
    public IReadOnlyList<string> TriggerEdges { get; } = ["Steigend", "Fallend"];
    public bool CanShow1 => page.Simulation && CanScope();
    public string RunText => running is true ? "Stop" : "Run";
    public string ScopeNotice => page.Simulation ? "Synthetische Messdaten · Cursorzeit in s" :
        "SCREEN-Min/Max · Zeit ab Fensteranfang · Phase zwischen Kanälen ungeprüft";

    [ObservableProperty] private bool show1 = true;
    [ObservableProperty] private bool show2 = true;
    [ObservableProperty] private decimal? timeDiv = 0.5m;
    [ObservableProperty] private decimal? voltsDiv1 = 1;
    [ObservableProperty] private decimal? voltsDiv2 = 1;
    [ObservableProperty] private string coupling1 = "DC";
    [ObservableProperty] private string coupling2 = "DC";
    [ObservableProperty] private string triggerChannel = "CH1";
    [ObservableProperty] private string triggerEdge = "Steigend";
    [ObservableProperty] private decimal? triggerLevel = 0;
    [ObservableProperty] private ChartSeries[] chart = [];
    [ObservableProperty] private ScopeMetric[] metrics = [];

    internal void RefreshAvailability()
    {
        var state = session.Devices.Active.Scope.Status;
        if (!state.Connected || state.Identity != observed?.Identity)
        {
            capture = null;
            Chart = [];
            Metrics = [];
            settingsLoaded = false;
            lastPoll = default;
            running = state.Connected && page.Simulation ? true : null;
        }
        observed = state;
        OnPropertyChanged(nameof(RunText));
        OnPropertyChanged(nameof(CanShow1));
        OnPropertyChanged(nameof(ScopeNotice));
        RunCommand.NotifyCanExecuteChanged();
        SingleCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    private bool CanScope() => page.Can(DeviceKind.PeakTech, DeviceCapability.Scope);
    private bool CanConfigure() => CanScope() && settingsLoaded;
    private bool CanExport() => capture is { Traces.Count: > 0 } && !session.Busy;

    public void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        if (!CanScope() || polling || running == false && settingsLoaded
            || now - lastPoll < TimeSpan.FromMilliseconds(page.Simulation ? 300 : 1800))
            return;
        lastPoll = now;
        _ = PollAsync();
    }

    private async Task PollAsync()
    {
        var selected = session.Devices.Active;
        polling = true;
        try
        {
            ScopeSettings? actual = null;
            ScopeCapture? measured = null;
            await session.Devices.TryPollAsync(async (equipment, token) =>
            {
                if (!ReferenceEquals(selected, equipment) || !equipment.Scope.Status.Connected) return;
                measured = await equipment.Scope.CaptureAsync(token);
                if (!settingsLoaded)
                {
                    try { actual = await equipment.Scope.ReadScopeAsync(token); }
                    catch (InvalidOperationException error) { session.LogDevice("PeakTech", error.Message); }
                }
            }, session.LifetimeToken);
            if (!ReferenceEquals(selected, session.Devices.Active)) return;
            if (actual is not null) UpdateSettings(actual);
            if (measured is not null) UpdateCapture(measured, selected.Simulated);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            session.LogDevice("PeakTech", error.Message);
        }
        finally { polling = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConfigure))]
    private async Task ApplyAsync()
    {
        ScopeSettings? actual = null;
        ScopeCapture? measured = null;
        var simulated = page.Simulation;
        await page.PerformAsync(async (equipment, token) =>
        {
            if (TimeDiv is not decimal time || VoltsDiv1 is not decimal scale1 ||
                VoltsDiv2 is not decimal scale2 || TriggerLevel is not decimal trigger)
                throw new ArgumentException("Zeitbasis, beide Vertikalskalen und den Triggerpegel eingeben.");
            equipment.Require(DeviceKind.PeakTech, DeviceCapability.Scope);
            var next = new ScopeSettings((double)time / 1000, (double)scale1, (double)scale2, TriggerChannel == "CH2" ? 2 : 1,
                (double)trigger, TriggerEdge == "Steigend", Coupling1, Coupling2, Show1, Show2);
            await equipment.Scope.ConfigureScopeAsync(next, token);
            actual = await equipment.Scope.ReadScopeAsync(token);
            measured = await equipment.Scope.CaptureAsync(token);
        });
        if (actual is not null) UpdateSettings(actual);
        if (measured is not null) UpdateCapture(measured, simulated);
    }

    [RelayCommand(CanExecute = nameof(CanScope))]
    private Task RunAsync() => ControlAsync(running is true ? ScopeAction.Stop : ScopeAction.Run);

    [RelayCommand(CanExecute = nameof(CanScope))]
    private Task SingleAsync() => ControlAsync(ScopeAction.Single);

    [RelayCommand(CanExecute = nameof(CanScope))]
    private Task RefreshAsync() => ReadAsync();

    private async Task ControlAsync(ScopeAction action)
    {
        ScopeCapture? result = null;
        var simulated = page.Simulation;
        var commanded = false;
        await page.PerformAsync(async (equipment, token) =>
        {
            equipment.Require(DeviceKind.PeakTech, DeviceCapability.Scope);
            await equipment.Scope.ControlScopeAsync(action, token);
            commanded = true;
            // In hardware, Single arms the edge trigger. The next refresh reads the displayed acquisition.
            if (action != ScopeAction.Single || equipment.Simulated)
                result = await equipment.Scope.CaptureAsync(token);
        });
        if (commanded)
        {
            running = action == ScopeAction.Run;
            OnPropertyChanged(nameof(RunText));
            if (action == ScopeAction.Single && !simulated)
                session.Say("Einzeltrigger aktiviert. Nach der Triggerung Aufnahme aktualisieren.");
        }
        if (result is not null) UpdateCapture(result, simulated);
    }

    private async Task ReadAsync()
    {
        ScopeSettings? actual = null;
        ScopeCapture? measured = null;
        var simulated = page.Simulation;
        await page.PerformAsync(async (equipment, token) =>
        {
            equipment.Require(DeviceKind.PeakTech, DeviceCapability.Scope);
            measured = await equipment.Scope.CaptureAsync(token);
            actual = await equipment.Scope.ReadScopeAsync(token);
        });
        if (actual is not null) UpdateSettings(actual);
        if (measured is not null) UpdateCapture(measured, simulated);
    }

    private void UpdateSettings(ScopeSettings actual)
    {
        TimeDiv = (decimal)(actual.TimePerDiv * 1000);
        VoltsDiv1 = (decimal)actual.VoltsPerDiv1;
        VoltsDiv2 = (decimal)actual.VoltsPerDiv2;
        TriggerChannel = $"CH{actual.TriggerChannel}";
        TriggerLevel = (decimal)actual.TriggerLevel;
        TriggerEdge = actual.Rising ? "Steigend" : "Fallend";
        Show1 = actual.Show1;
        Show2 = actual.Show2;
        Coupling1 = actual.Coupling1;
        Coupling2 = actual.Coupling2;
        settingsLoaded = true;
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private void UpdateCapture(ScopeCapture measured, bool simulated)
    {
        if (capture == measured && capturedInSimulation == simulated) return;
        capture = measured;
        capturedInSimulation = simulated;
        Chart = measured.Traces.Select(trace => new ChartSeries($"CH{trace.Channel}", "V", trace.Seconds, trace.Volts, trace.Channel - 1)
        {
            Minimum = trace.MinimumVolts,
            Maximum = trace.MaximumVolts
        }).ToArray();
        Stats? a = Stats.From(measured.Traces.FirstOrDefault(trace => trace.Channel == 1));
        Stats? b = Stats.From(measured.Traces.FirstOrDefault(trace => trace.Channel == 2));
        var approximate = !simulated;
        double? phase = null;
        if (simulated && a is { Period: > 0, FirstRise: { } first } &&
            b is { Period: > 0, FirstRise: { } second } && Math.Abs(a.Period.Value - b.Period.Value) <= a.Period * 0.02)
            phase = Math.IEEERemainder((first - second) / a.Period.Value * 360, 360);
        Metrics =
        [
            new("Frequenz", Measure(1 / a?.Period, "Hz", approximate), Measure(1 / b?.Period, "Hz", approximate)),
            new("Periode", Measure(a?.Period * 1000, "ms", approximate), Measure(b?.Period * 1000, "ms", approximate)),
            new("Spitze–Spitze", Measure(a?.Range, "V"), Measure(b?.Range, "V")),
            new("Minimum", Measure(a?.Min, "V"), Measure(b?.Min, "V")),
            new("Maximum", Measure(a?.Max, "V"), Measure(b?.Max, "V")),
            new("Mittelwert", Measure(a?.Mean, "V", approximate), Measure(b?.Mean, "V", approximate)),
            new("RMS", Measure(a?.Rms, "V", approximate), Measure(b?.Rms, "V", approximate)),
            new("Tastverhältnis", Measure(a?.Duty, "%", approximate), Measure(b?.Duty, "%", approximate)),
            new("Phase A → B", "—", Measure(phase, "°"))
        ];
        ExportCommand.NotifyCanExecuteChanged();
    }

    private static string Measure(double? value, string unit, bool approximate = false) => value is null ? "—" :
        $"{(approximate ? "≈ " : "")}{Presentation.Number(value, "0.####")} {unit}";

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportAsync()
    {
        if (capture is not { Traces.Count: > 0 } measured) return Task.CompletedTask;
        var mode = capturedInSimulation ? "SIMULATION" : "HARDWARE";
        return session.PerformAsync(async () =>
        {
            var file = await session.Files.SaveAsync("Oszilloskop-Aufnahme als CSV", $"scope-{mode.ToLowerInvariant()}-{measured.Time:yyyyMMdd-HHmmss}", "csv");
            if (file is null) return;
            await using var writer = new StreamWriter(file, false, new UTF8Encoding(true));
            await writer.WriteLineAsync("mode;received_at;channel;time_s;voltage_v;minimum_v;maximum_v");
            foreach (var trace in measured.Traces)
                for (var index = 0; index < trace.Seconds.Length; index++)
                {
                    session.LifetimeToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(FormattableString.Invariant($"{mode};{measured.Time:O};CH{trace.Channel};{trace.Seconds[index]:R};{trace.Volts[index]:R};{trace.MinimumVolts?[index]:R};{trace.MaximumVolts?[index]:R}"));
                }
            session.Say("Oszilloskop-Aufnahme als CSV exportiert.");
        }, exclusive: true);
    }

    private sealed record Stats(double Min, double Max, double Mean, double Rms, double? Period, double? Duty, double? FirstRise)
    {
        public double Range => Max - Min;

        public static Stats? From(ScopeTrace? trace)
        {
            if (trace is null || trace.Volts.Length == 0) return null;
            var values = trace.Volts;
            var minimum = trace.MinimumVolts?.Min() ?? values.Min();
            var maximum = trace.MaximumVolts?.Max() ?? values.Max();
            var mean = values.Average();
            var rms = Math.Sqrt(values.Average(value => value * value));
            var rising = new List<(double Time, int Index)>();
            var falling = new List<(double Time, int Index)>();
            if (maximum - minimum > 1e-9)
            {
                // Small hysteresis around mid-level prevents one noisy edge from becoming several periods.
                var middle = (minimum + maximum) / 2;
                var band = (maximum - minimum) * 0.1;
                var state = 0;
                for (var index = 0; index < values.Length; index++)
                {
                    var nextState = values[index] <= middle - band ? -1 : values[index] >= middle + band ? 1 : state;
                    if (state != 0 && state != nextState)
                        (nextState == 1 ? rising : falling).Add((trace.Seconds[index], index));
                    state = nextState;
                }
            }
            double? period = null, duty = null;
            if (rising.Count >= 2 && rising[^1].Index - rising[0].Index >= (rising.Count - 1) * 8)
            {
                period = (rising[^1].Time - rising[0].Time) / (rising.Count - 1);
                if (period <= 0) period = null;
                var fall = falling.FindIndex(edge => edge.Index > rising[0].Index && edge.Index < rising[1].Index);
                if (fall >= 0 && period > 0) duty = 100 * (falling[fall].Time - rising[0].Time) / period;
            }
            return new Stats(minimum, maximum, mean, rms, period, duty, rising.Count > 0 ? rising[0].Time : null);
        }
    }
}
