using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed record HistoryItem(RunResult Run)
{
    public string Id => Run.DisplayId;
    public string Date => Run.Started.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
    public string Device => $"{Run.Daq.Model} · {Run.Daq.Serial ?? "SN offen"}";
    public string Profile => Run.Profile.Name;
    public string Tester => string.IsNullOrWhiteSpace(Run.Operator) ? "—" : Run.Operator;
    public string Kind => Presentation.RunKindLabel(Run.Profile.Kind);
    public string Mode => Run.Simulated ? "Simulation" : "Hardware";
    public string Result => $"{Presentation.Outcome(Run.Outcome).Label}{(Run.Simulated ? " · Sim." : "")}";
    public string Tone => Presentation.Outcome(Run.Outcome).Tone;
    public int Passed => Run.Points.Count(point => point.Passed);
    public int Failed => Run.Points.Count(point => !point.Passed);
}
public sealed record HistoryPoint(string Channel, string Requested, string Confirmed, string Independent, string Measured,
    string Error, string Tolerance, string Status, string Tone);

public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly StudioSession session;
    private IReadOnlyList<HistoryItem> all = [];
    private int refreshId;

    [ObservableProperty] private IReadOnlyList<HistoryItem> runs = [];
    [ObservableProperty] private HistoryItem? selectedRun;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string kind = "Alle";
    [ObservableProperty] private string outcome = "Alle";
    [ObservableProperty] private string device = "Alle";
    [ObservableProperty] private string profile = "Alle";
    [ObservableProperty] private IReadOnlyList<string> devices = ["Alle"];
    [ObservableProperty] private IReadOnlyList<string> profiles = ["Alle"];
    [ObservableProperty] private DateTimeOffset? from;
    [ObservableProperty] private DateTimeOffset? until;
    [ObservableProperty] private string title = "Berichtsvorschau";
    [ObservableProperty] private IReadOnlyList<Fact> facts = [];
    [ObservableProperty] private IReadOnlyList<HistoryPoint> points = [];
    [ObservableProperty] private IReadOnlyList<AdjustmentRow> adjustments = [];
    [ObservableProperty] private IReadOnlyList<Fact> approvals = [];
    [ObservableProperty] private string reportNotice = "";
    private bool loading;

    public HistoryViewModel(StudioSession session) => this.session = session;

    public string[] Kinds { get; } = ["Alle", .. Enum.GetValues<RunKind>().Select(Presentation.RunKindLabel)];
    public string[] Outcomes { get; } = ["Alle", .. Enum.GetValues<RunOutcome>().Select(value => Presentation.Outcome(value).Label)];
    public bool HasSelection => SelectedRun is not null;

    public Task LoadAsync(RunResult? focus) => session.PerformAsync(async () =>
    {
        var mine = ++refreshId;
        var result = await Task.Run(() => session.Store.ListRunsAsync());
        if (mine != refreshId) return;
        var selected = focus?.Id ?? SelectedRun?.Run.Id;
        all = result.Select(run => new HistoryItem(run)).ToArray();
        loading = true;
        try
        {
            Devices = ["Alle", .. all.Select(item => item.Device).Distinct().OrderBy(value => value)];
            Profiles = ["Alle", .. all.Select(item => item.Profile).Distinct().OrderBy(value => value)];
            if (!Devices.Contains(Device)) Device = "Alle";
            if (!Profiles.Contains(Profile)) Profile = "Alle";
            if (focus is not null)
            {
                Kind = Outcome = Device = Profile = "Alle";
                SearchText = "";
                From = Until = null;
            }
        }
        finally { loading = false; }
        Filter(selected);
        RefreshPreview();
    });

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(null);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ExportCsvAsync() => ExportAsync("csv");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task ExportPdfAsync() => ExportAsync("pdf");

    partial void OnSearchTextChanged(string value) => Filter();
    partial void OnKindChanged(string value) => Filter();
    partial void OnOutcomeChanged(string value) => Filter();
    partial void OnDeviceChanged(string value) => Filter();
    partial void OnProfileChanged(string value) => Filter();
    partial void OnFromChanged(DateTimeOffset? value) => Filter();
    partial void OnUntilChanged(DateTimeOffset? value) => Filter();

    partial void OnSelectedRunChanged(HistoryItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        ExportCsvCommand.NotifyCanExecuteChanged();
        ExportPdfCommand.NotifyCanExecuteChanged();
        RefreshPreview();
    }

    private void Filter(Guid? target = null)
    {
        if (loading) return;
        var selected = target ?? SelectedRun?.Run.Id;
        var text = SearchText?.Trim() ?? "";
        Runs = all.Where(item => (Kind == "Alle" || Kind == item.Kind) &&
            (Outcome == "Alle" || Outcome == Presentation.Outcome(item.Run.Outcome).Label) &&
            (Device == "Alle" || Device == item.Device) && (Profile == "Alle" || Profile == item.Profile) &&
            (From is null || item.Run.Started.LocalDateTime.Date >= From.Value.Date) &&
            (Until is null || item.Run.Started.LocalDateTime.Date <= Until.Value.Date) &&
            (text.Length == 0 || new[] { item.Id, item.Profile, item.Device, item.Tester, item.Mode }
                .Any(field => field.Contains(text, StringComparison.CurrentCultureIgnoreCase))))
            .ToArray();
        SelectedRun = Runs.FirstOrDefault(item => item.Run.Id == selected) ?? Runs.FirstOrDefault();
    }

    private void RefreshPreview()
    {
        if (SelectedRun is not { } item)
        {
            Title = "Berichtsvorschau";
            Facts = [];
            Points = [];
            Adjustments = [];
            Approvals = [];
            ReportNotice = "";
            return;
        }
        var run = item.Run;
        Title = $"{item.Kind} · {item.Id}";
        Facts =
        [
            new("Prüfobjekt", item.Device),
            new("Hardware / Firmware", $"{run.Daq.Revision ?? "—"} / {run.Daq.Firmware ?? "—"}"),
            new("Prüfprofil", $"{item.Profile} · Revision {run.Profile.Version}"),
            new("Datum · Prüfer", $"{item.Date} · {item.Tester}"),
            new("Ergebnis", $"{item.Result} · {item.Passed} bestanden · {item.Failed} außerhalb Toleranz", item.Tone),
            new("Prüfbedingungen", string.IsNullOrWhiteSpace(run.Conditions) ? "—" : run.Conditions),
            .. run.References.Select(reference => new Fact("Referenz", string.Join(" · ",
                new[] { reference.Model, reference.Serial, reference.Accuracy, reference.Certificate }.OfType<string>()))),
            .. run.Error is null ? Array.Empty<Fact>() : [new Fact("Hinweis", run.Error, item.Tone)]
        ];
        Points = run.Points.Select(point => new HistoryPoint(point.ChannelId, Presentation.Value(point.Requested, point.Quantity),
            Presentation.Value(point.Confirmed, point.Quantity), Presentation.Value(point.IndependentReference, point.Quantity),
            Presentation.Value(point.Measured, point.Quantity), Presentation.Value(point.Error, point.Quantity, "+0.0000;-0.0000;0.0000"),
            Presentation.Value(point.Tolerance, point.Quantity),
            point.Passed ? "OK" : "Außer Toleranz", point.Passed ? "ok" : "warn")).ToArray();
        Adjustments = Presentation.AdjustmentRows(run);
        Approvals = [];
        ReportNotice = run.Simulated
            ? "Simulation. Die Ergebnisse beziehen sich auf das simulierte Prüfobjekt und die simulierten Referenzgeräte."
            : "Aussagekraft siehe Referenzdaten und Messunsicherheit. Rückführung erfordert einen unabhängig bestimmten Referenzwert.";
        _ = LoadApprovalsAsync(run.Id);
    }

    private Task LoadApprovalsAsync(Guid id) => session.PerformAsync(async () =>
    {
        var entries = await Task.Run(() => session.Store.ListApprovalsAsync(id));
        if (SelectedRun?.Run.Id == id)
            Approvals = entries.Select(entry => new Fact(entry.Time.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss"),
                $"{entry.Operator} · {entry.Target} · {(entry.Verified ? "Rücklesen bestätigt" : entry.Error ?? "Offen")}",
                entry.Verified ? "ok" : "warn")).ToArray();
    });

    private Task ExportAsync(string extension) => session.PerformAsync(async () =>
    {
        if (SelectedRun is not { } item) return;
        var path = await session.Files.SaveAsync($"{item.Kind} exportieren", $"{item.Id}-{(item.Run.Simulated ? "SIM" : "DAQ")}", extension);
        if (path is null) return;
        var entries = await session.Store.ListApprovalsAsync(item.Run.Id);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await Task.Run(() =>
            {
                if (extension == "pdf") session.Export.WritePdf(temp, item.Run, entries);
                else session.Export.WriteCsv(temp, item.Run, entries);
                File.Move(temp, path, overwrite: true);
            });
            session.Say($"{extension.ToUpperInvariant()}-Bericht exportiert.");
        }
        finally { File.Delete(temp); }
    });
}
