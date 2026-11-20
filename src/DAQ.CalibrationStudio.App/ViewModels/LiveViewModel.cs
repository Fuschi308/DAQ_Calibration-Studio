using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.App.Controls;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;
using DAQ.CalibrationStudio.Infrastructure.Persistence;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed partial class PlotPanel(string label) : ObservableObject
{
    public string Label { get; } = label;
    [ObservableProperty] private IReadOnlyList<ChartSeries> series = [];
}
public sealed record LiveStats(string Channel, string Raw, string Scaled, string Corrected,
    string Mean, string Min, string Max, string StdDev, string Unit);

public sealed partial class LiveViewModel : ObservableObject
{
    private readonly StudioSession session;
    private readonly Dictionary<Quantity, PlotPanel> plotByQuantity = [];
    private long lastSequence = -1;
    private DateTimeOffset clearTime = DateTimeOffset.MinValue;

    [ObservableProperty] private string window = "60 s";
    [ObservableProperty] private bool isHeld;
    [ObservableProperty] private IReadOnlyList<PlotPanel> plots = [];
    [ObservableProperty] private IReadOnlyList<LiveStats> statistics = [];
    [ObservableProperty] private string recordText = "Aufzeichnung starten";
    [ObservableProperty] private string status = "DAQ-Messblöcke";

    public LiveViewModel(StudioSession session)
    {
        this.session = session;
        Channels = ChannelCatalog.All.Select((definition, index) => new ChannelRow(definition, index % 8)
            { IsChecked = index is 0 or 3 or 6 }).ToArray();
        foreach (var channel in Channels)
            channel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChannelRow.IsChecked))
                    lastSequence = -1;
            };
        session.ModeChanged += () =>
        {
            clearTime = DateTimeOffset.MinValue;
            lastSequence = -1;
            IsHeld = false;
            Tick();
        };
    }

    public IReadOnlyList<ChannelRow> Channels { get; }
    public string[] Windows { get; } = ["10 s", "30 s", "60 s", "5 min"];

    public void Tick()
    {
        RecordText = session.Recording.Active ? "Aufzeichnung stoppen" : "Aufzeichnung starten";
        if (IsHeld) return;
        var latest = session.Measurements.Latest;
        var seq = latest?.Frame.Sequence ?? 0;
        if (lastSequence == seq) return;
        lastSequence = seq;
        var selected = Channels.Where(row => row.IsChecked).ToArray();
        var frames = CaptureWindow();
        foreach (var row in Channels)
        {
            var sample = latest?.Frame.Samples.FirstOrDefault(item => item.ChannelId == row.Id);
            row.Update(sample, null, sample?.StdDev, session.Tolerance(row.Definition), "% v. Messbereich");
        }
        var activePlots = new List<PlotPanel>();
        foreach (var group in selected.GroupBy(row => row.Definition.Quantity))
        {
            if (!plotByQuantity.TryGetValue(group.Key, out var plot))
            {
                var unit = group.Key == Quantity.Digital ? "" : $" ({ChannelCatalog.Unit(group.Key)})";
                plot = new PlotPanel(ChannelCatalog.Name(group.Key) + unit);
                plotByQuantity.Add(group.Key, plot);
            }
            plot.Series = group.SelectMany(row => Presentation.HistoryChart(frames, row.Definition, row.ColorIndex, "Soll / Ist")).ToArray();
            activePlots.Add(plot);
        }
        if (!Plots.SequenceEqual(activePlots)) Plots = activePlots;
        Statistics = selected.Select(Stats).ToArray();
        Status = latest is null ? "Warte auf Datenblöcke der DAQ." : $"{(latest.Value.Simulated ? "Simulation" : "DAQ")} · " +
            $"{frames.Length} Blöcke im Fenster · Sequenz {seq} · {(latest.Value.Frame.DeviceTimed ? "Gerätezeit" : "Hostzeit / Simulationszeit")}";

        LiveStats Stats(ChannelRow row)
        {
            var samples = frames.SelectMany(tick => tick.Frame.Samples).Where(sample => sample.ChannelId == row.Id && sample.Corrected.HasValue).ToArray();
            if (samples.Length == 0)
                return new(row.Id, "—", "—", "—", "—", "—", "—", "—", row.Definition.Unit);
            var values = new SampleAccumulator();
            foreach (var sample in samples)
                values.Add(sample.Corrected!.Value, Math.Max(1, sample.Count), sample.StdDev ?? 0, sample.Min, sample.Max);
            var statistics = values.Statistics;
            var last = samples[^1];
            return new(row.Id, last.Raw?.ToString(Presentation.German) ?? "—", Presentation.Value(last.Uncorrected, row.Definition),
                Presentation.Value(last.Corrected, row.Definition), Presentation.Value(statistics.Mean, row.Definition),
                Presentation.Value(statistics.Min, row.Definition), Presentation.Value(statistics.Max, row.Definition),
                Presentation.Number(statistics.StandardDeviation, "0.000000"), row.Definition.Unit);
        }
    }

    [RelayCommand]
    private void Hold()
    {
        IsHeld = !IsHeld;
        if (!IsHeld) Tick();
    }

    [RelayCommand]
    private void Clear()
    {
        clearTime = session.Measurements.Latest?.Frame.Time.AddTicks(1) ?? DateTimeOffset.MinValue;
        lastSequence = -1;
        Plots = [];
        Statistics = [];
        if (!IsHeld) Tick();
    }

    [RelayCommand]
    private Task RecordAsync() => session.PerformAsync(async () =>
    {
        if (session.Recording.Active)
        {
            await session.Recording.StopAsync();
            session.Say($"Messaufzeichnung gespeichert. {session.Recording.LastPath}");
            return;
        }
        await session.Recording.StopAsync();
        var state = session.Devices.Active;
        if (!state.Daq.Status.Connected)
            throw new InvalidOperationException("Verbinden Sie die DAQ für die Aufzeichnung.");
        var path = Path.Combine(session.Paths.Recordings, $"{(state.Simulated ? "simulation" : "daq")}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.csv");
        session.Recording.Start(path, state.Simulated);
        session.Say("Messaufzeichnung gestartet.");
        Tick();
    });

    [RelayCommand]
    private Task ExportAsync() => session.PerformAsync(async () =>
    {
        var captured = CaptureWindow();
        if (captured.Length == 0) throw new InvalidOperationException("Im gewählten Fenster sind keine Messdaten verfügbar.");
        var path = await session.Files.SaveAsync("Messdatenfenster exportieren", $"daq-messdaten-{DateTime.Now:yyyyMMdd-HHmmss}", "csv");
        if (path is null) return;
        var ids = Channels.Where(row => row.IsChecked).Select(row => row.Id).ToHashSet();
        if (ids.Count == 0)
            throw new InvalidOperationException("Wählen Sie mindestens einen Messkanal für den Export.");
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await Task.Run(async () =>
            {
                await using var writer = await CsvMeasurementWriter.CreateAsync(temp, captured[0].Simulated);
                foreach (var item in captured)
                    await writer.WriteFrameAsync(item.Frame with { Samples = item.Frame.Samples.Where(sample => ids.Contains(sample.ChannelId)).ToArray() });
            });
            File.Move(temp, path, overwrite: true);
            session.Say("Messdatenfenster exportiert.");
        }
        finally { File.Delete(temp); }
    });

    partial void OnWindowChanged(string value)
    {
        lastSequence = -1;
        Tick();
    }

    private MeasurementTick[] CaptureWindow()
    {
        if (session.Measurements.Latest is not { } latest)
            return [];
        var seconds = Window switch { "10 s" => 10, "30 s" => 30, "5 min" => 300, _ => 60 };
        var since = latest.Frame.Time.AddSeconds(-seconds);
        return session.Measurements.Snapshot(clearTime > since ? clearTime : since);
    }
}
