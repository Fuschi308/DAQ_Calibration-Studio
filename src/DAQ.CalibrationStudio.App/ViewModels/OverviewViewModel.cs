using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.App.Controls;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed partial class OverviewViewModel : ObservableObject
{
    private readonly StudioSession session;
    private readonly ChannelRow[] channels;
    private long lastSequence = -1;
    private bool coefficientsPending;
    private RunProgress? priorProgress;

    [ObservableProperty] private IReadOnlyList<ChannelRow> rows = [];
    [ObservableProperty] private ChannelRow? selectedChannel;
    [ObservableProperty] private string typeFilter = "Alle";
    [ObservableProperty] private string statusFilter = "Alle";
    [ObservableProperty] private string groupFilter = "Alle Gruppen";
    [ObservableProperty] private string relativeMode = "% v. Messbereich";
    [ObservableProperty] private IReadOnlyList<Fact> kpis = [];
    [ObservableProperty] private IReadOnlyList<ChartSeries> chart = [];
    [ObservableProperty] private string chartMode = "Soll / Ist";
    [ObservableProperty] private string chartWindow = "60 s";
    [ObservableProperty] private string pointsText = "";
    [ObservableProperty] private string method = Presentation.MethodLabel(FitMethod.Regression);
    [ObservableProperty] private double tolerance = 0.008;
    [ObservableProperty] private double stability = 0.001;
    [ObservableProperty] private double window = 1;
    [ObservableProperty] private double averaging = 2;
    [ObservableProperty] private string selectedProfile = "Benutzerdefiniert";
    [ObservableProperty] private IReadOnlyList<string> profileNames = ["Benutzerdefiniert"];
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool canStart;
    [ObservableProperty] private bool canPause;
    [ObservableProperty] private bool canResume;
    [ObservableProperty] private bool canCancel;
    [ObservableProperty] private bool canApprove;
    [ObservableProperty] private bool canReset;
    [ObservableProperty] private string runTitle = "Bereit";
    [ObservableProperty] private string runMessage = "Kanäle auswählen und Kalibrierung starten.";
    [ObservableProperty] private string runStateTone = "";
    [ObservableProperty] private IReadOnlyList<RunStepRow> runSteps = [];

    public OverviewViewModel(StudioSession session)
    {
        this.session = session;
        var palette = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 2, 5, 6, 1, 3, 0, 1, 2, 3, 4 };
        channels = ChannelCatalog.All.Select((channel, index) => new ChannelRow(channel, palette[index])).ToArray();
        foreach (var row in channels)
        {
            row.SetCoefficient(null);
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ChannelRow.IsChecked))
                {
                    OnPropertyChanged(nameof(ChannelCountText));
                    UpdateCommandState();
                }
            };
        }
        Rows = channels;
        SelectedChannel = channels[2];
        channels[2].IsChecked = true;
        session.ProfilesChanged += UpdateProfiles;
        session.RunCompleted += _ => InvalidateMeasurements();
        session.ModeChanged += InvalidateMeasurements;
        session.SettingsChanged += InvalidateMeasurements;
        UpdateProfiles();
    }

    public string[] TypeFilters { get; } = ["Alle", "Spannung", "Strom", "RTD", "Frequenz", "Digital"];
    public string[] StatusFilters { get; } = ["Alle", "OK", "Prüfen", "Fehler", "n. a."];
    public string[] GroupFilters { get; } = ["Alle Gruppen", .. Enum.GetValues<ChannelGroup>().Select(ChannelCatalog.GroupName)];
    public string[] RelativeModes { get; } = ["% v. Messbereich", "% v. Messwert", "aus"];
    public string[] Methods { get; } = Enum.GetValues<FitMethod>().Select(Presentation.MethodLabel).ToArray();
    public string[] ChartModes { get; } = ["Soll / Ist", "Abweichung", "Stabilität"];
    public string[] Windows { get; } = ["30 s", "60 s", "5 min"];
    public double ChartWindowSeconds => ChartWindow switch { "30 s" => 30, "5 min" => 300, _ => 60 };
    public string ChannelLabel => SelectedChannel is null ? "—" : $"{SelectedChannel.Id} · {SelectedChannel.Type} · {SelectedChannel.Definition.Source}";
    public string RangeLabel => SelectedChannel?.Range ?? "—";
    public string CorrectionLabel => SelectedChannel?.Coefficient is { } coefficient
        ? $"G {Presentation.Number(coefficient.Gain, "0.000000")} · O {Presentation.Value(coefficient.Offset, SelectedChannel.Definition)}"
        : "—";
    public string MeasuredText => SelectedChannel?.Measured ?? "—";
    public string SetpointText => SelectedChannel?.Requested ?? "—";
    public string ErrorText => SelectedChannel?.Error ?? "—";
    public string StabilityText => SelectedChannel?.Stability ?? "—";
    public string ReferenceName => session.Devices.Active.Source.Status.Name;
    public string ReferenceNotice => session.Devices.Active.Simulated
        ? "Simulation. Der bestätigte Sollwert dient als Vergleichswert. Eine unabhängig gemessene Referenz und rückführbare Unsicherheit fehlen."
        : "Automatische analoge Kalibrierung benötigt einen sicheren Ausgangsschaltpfad und einen bestätigten Prüfadapter.";
    public string ChannelCountText => $"{channels.Count(row => row.IsChecked)} ausgewählt";
    public bool RunDetailsVisible => priorProgress is { State: not EngineState.Idle };

    public void Tick(bool visible)
    {
        UpdateCommandState();
        var engine = session.Engine.Progress;
        if (engine != priorProgress)
        {
            priorProgress = engine;
            Progress = engine.Percent;
            RunTitle = string.IsNullOrWhiteSpace(engine.Profile) ? Presentation.EngineLabel(engine.State) : engine.Profile;
            RunMessage = engine.Message;
            RunStateTone = engine.State switch { EngineState.Completed => "ok", EngineState.Failed => "err", EngineState.Aborted => "warn", _ => "accent" };
            RunSteps = RunRows(session.ActiveRunProfile, engine);
            OnPropertyChanged(nameof(RunDetailsVisible));
        }
        if (!visible) return;
        var latest = session.Measurements.Latest;
        var seq = latest?.Frame.Sequence ?? 0;
        if (lastSequence == seq) return;
        lastSequence = seq;
        var frames = latest is { } tick
            ? session.Measurements.Snapshot(tick.Frame.Time.AddSeconds(-1))
            : [];
        foreach (var row in channels)
        {
            var samples = frames.SelectMany(frame => frame.Frame.Samples.Where(sample => sample.ChannelId == row.Id)).ToArray();
            var latestSample = latest?.Frame.Samples.FirstOrDefault(sample => sample.ChannelId == row.Id);
            var comparable = samples.Where(sample => sample.Setpoint == latestSample?.Setpoint &&
                sample.Corrected.HasValue).ToArray();
            if (comparable.Length == 0)
            {
                row.Update(latestSample, null, null, session.Tolerance(row.Definition), RelativeMode);
            }
            else
            {
                var accumulator = new SampleAccumulator();
                foreach (var sample in comparable)
                    accumulator.Add(sample.Corrected!.Value, Math.Max(1, sample.Count), (sample.StdDev ?? 0) * (row.Coefficient?.Gain ?? 1));
                var statistics = accumulator.Statistics;
                row.Update(latestSample, statistics.Mean, statistics.StandardDeviation, session.Tolerance(row.Definition), RelativeMode);
            }
        }
        var figures = new[]
        {
            new Fact("Kanäle", channels.Length.ToString()),
            new Fact("OK", channels.Count(row => row.Status == "OK").ToString(), "ok"),
            new Fact("Prüfen", channels.Count(row => row.Status == "Prüfen").ToString(), "warn"),
            new Fact("Fehler", channels.Count(row => row.Status == "Fehler").ToString(), "err"),
            new Fact("Nicht bewertet", channels.Count(row => row.Status == "n. a.").ToString()),
            new Fact("Kal. fällig/fehlt", channels.Count(row => row.Calibration is "Abgelaufen" or "Unkalibriert").ToString(), "warn"),
            new Fact("Letzte Prüfung", session.LastRun?.Finished.LocalDateTime.ToString("dd.MM.yyyy HH:mm") ?? "—"),
            new Fact("Prüfbedingungen", string.IsNullOrWhiteSpace(session.Settings.Conditions) ? "—" : session.Settings.Conditions)
        };
        if (!Kpis.SequenceEqual(figures)) Kpis = figures;
        FilterRows();
        RefreshChart();
        RaiseChannel();
    }

    public async void RefreshCoefficients()
    {
        if (coefficientsPending) return;
        coefficientsPending = true;
        try
        {
            var equipment = session.Devices.Active;
            if (!equipment.Daq.Status.Connected || !equipment.Daq.Status.Has(DeviceCapability.Coefficients))
            {
                foreach (var row in channels) row.SetCoefficient(null);
                return;
            }
            await session.Devices.TryPollAsync(async (eq, token) =>
            {
                if (!ReferenceEquals(eq, equipment)) return;
                foreach (var row in channels.Where(row => row.Definition.Group is ChannelGroup.Analog or ChannelGroup.Rtd))
                    row.SetCoefficient(await equipment.Daq.ReadCoefficientAsync(row.Id, token));
            }, CancellationToken.None);
        }
        catch (Exception ex) { session.Say(ex.Message); }
        finally
        {
            coefficientsPending = false;
            InvalidateMeasurements();
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        var check = Rows.Any(row => !row.IsChecked);
        foreach (var row in Rows) row.IsChecked = check;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => StartInternalAsync(null, RunKind.Calibration);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task CalibrateAllAsync() => StartInternalAsync(channels.Where(row => row.Definition.Group == ChannelGroup.Analog), RunKind.Calibration);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task VerifyAsync()
    {
        if (session.LastRun is { Profile.Kind: RunKind.Calibration } last &&
            last.Simulated == session.Devices.Active.Simulated &&
            last.Daq.Serial == session.Devices.Active.Daq.Status.Identity?.Serial)
            return session.RunAsync(last.Profile with { Id = Guid.NewGuid(), Version = 1, Kind = RunKind.Verification,
                Name = $"Verifikation · {last.Profile.Name}", Method = FitMethod.SinglePoint, SnapshotOnly = true });
        return StartInternalAsync(null, RunKind.Verification);
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => session.Engine.Pause();

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => session.Engine.Resume();

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => session.Engine.Cancel();

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private void Approve()
    {
        if (session.LastRun is { } result)
            session.OpenApproval(result);
    }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private Task ResetAsync() => session.PerformAsync(async () =>
    {
        var affected = channels.Where(row => row.IsChecked && row.Definition.Group == ChannelGroup.Analog).Select(row => row.Id).ToArray();
        if (affected.Length == 0 && SelectedChannel?.Definition.Group == ChannelGroup.Analog)
            affected = [SelectedChannel.Id];
        if (affected.Length == 0)
            throw new InvalidOperationException("Wählen Sie einen analogen Kanal.");
        if (string.IsNullOrWhiteSpace(session.Settings.Operator))
            throw new InvalidOperationException("Hinterlegen Sie zuerst eine Prüfer-Kennung in Einstellungen.");
        if (!await session.Files.ConfirmAsync("Kalibrierkoeffizienten zurücksetzen",
            $"Simulation · {string.Join(", ", affected)} · Prüfer {session.Settings.Operator}. Die Kanäle gelten anschließend als unkalibriert. Die aktuellen Werte werden gesichert.",
            "Zurücksetzen", "Ich gebe das Zurücksetzen auf Werkseinstellungen frei."))
            return;
        var result = await session.Adjustment.ResetSimulationAsync(affected, session.Settings.Operator, true, session.LifetimeToken);
        if (!result.Verified)
            throw new IOException(result.Error ?? "Rücksetzung konnte nicht bestätigt werden.");
        session.Say("Simulierte Werkseinstellung geschrieben und zurückgelesen.");
        RefreshCoefficients();
    }, exclusive: true);

    [RelayCommand]
    private void OpenReport() => session.OpenReport(session.LastRun);

    [RelayCommand]
    private void InspectChannel(ChannelRow? row)
    {
        if (row is not null) SelectedChannel = row;
    }

    [RelayCommand]
    private Task SaveProfileAsync() => session.PerformAsync(async () =>
    {
        var profile = BuildProfile(null, RunKind.Calibration);
        await session.Store.SaveProfileAsync(profile);
        await session.ReloadProfilesAsync();
        SelectedProfile = profile.Name;
    }, "Prüfprofil gespeichert.");

    partial void OnTypeFilterChanged(string value) => FilterRows();
    partial void OnStatusFilterChanged(string value) => FilterRows();
    partial void OnGroupFilterChanged(string value) => FilterRows();
    partial void OnRelativeModeChanged(string value) => InvalidateMeasurements();
    partial void OnChartModeChanged(string value) => RefreshChart();

    partial void OnChartWindowChanged(string value)
    {
        OnPropertyChanged(nameof(ChartWindowSeconds));
        RefreshChart();
    }

    partial void OnSelectedChannelChanged(ChannelRow? value)
    {
        if (value is not null)
        {
            PointsText = string.Join(" / ", StudioProfiles.SuggestedPoints(value.Definition).Select(point => Presentation.Number(point, "0.###")));
            Tolerance = session.Tolerance(value.Definition);
            Stability = value.Definition.Quantity == Quantity.Current ? 0.002 : 0.0007;
        }
        RefreshChart();
        RaiseChannel();
        UpdateCommandState();
    }

    private async Task StartInternalAsync(IEnumerable<ChannelRow>? overrideRows, RunKind kind)
    {
        await session.PerformAsync(async () =>
        {
            var predefined = session.Profiles.FirstOrDefault(profile => profile.Name == SelectedProfile);
            var profile = predefined is not null && overrideRows is null
                ? kind == RunKind.Verification ? predefined with { Id = Guid.NewGuid(), Version = 1,
                    Name = $"Verifikation · {predefined.Name}", Kind = kind, Method = FitMethod.SinglePoint, SnapshotOnly = true } : predefined
                : BuildProfile(overrideRows, kind) with { SnapshotOnly = true };
            await session.RunAsync(profile);
        });
    }

    private TestProfile BuildProfile(IEnumerable<ChannelRow>? overrideRows, RunKind kind)
    {
        var chosen = overrideRows?.ToArray() ?? channels.Where(row => row.IsChecked).ToArray();
        if (chosen.Length == 0 && SelectedChannel is { } active) chosen = [active];
        if (chosen.Length == 0 || chosen.Any(row => row.Definition.Group != ChannelGroup.Analog))
            throw new InvalidOperationException("Bitte einen oder mehrere Spannung-/Stromkanäle wählen.");

        var selected = SelectedChannel?.Definition;
        var steps = chosen.SelectMany(row =>
        {
            var definition = row.Definition;
            var fromForm = selected is not null && definition.Quantity == selected.Quantity &&
                definition.Minimum == selected.Minimum && definition.Maximum == selected.Maximum && overrideRows is null;
            var points = fromForm ? ParsePoints(PointsText) : StudioProfiles.SuggestedPoints(definition);
            return StudioProfiles.AnalogPoints(definition, points, Averaging, fromForm ? Tolerance : session.Tolerance(definition),
                fromForm ? Stability : null, Window);
        }).ToArray();
        var selectedMethod = Enum.GetValues<FitMethod>().FirstOrDefault(value => Presentation.MethodLabel(value) == Method);
        return new(Guid.NewGuid(), 1, $"{(kind == RunKind.Calibration ? "Kalibrierung" : "Verifikation")} · {string.Join(", ", chosen.Select(row => row.Id))}",
            kind, kind == RunKind.Calibration ? selectedMethod : FitMethod.SinglePoint, steps);
    }

    private static double[] ParsePoints(string text)
    {
        var parts = text.Split(['/', ';', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 50)
            throw new ArgumentException("1–50 Prüfpunkte eingeben, mit Leerzeichen, / oder ; getrennt.");
        return parts.Select(part => double.TryParse(part.Replace('−', '-'), System.Globalization.NumberStyles.Float,
            Presentation.German, out var number) ? number : throw new ArgumentException($"Ungültiger Prüfpunkt: {part}")).ToArray();
    }

    private void UpdateCommandState()
    {
        var equipment = session.Devices.Active;
        CanStart = !session.Busy && equipment.Daq.Status.Connected && equipment.Daq.Status.Has(DeviceCapability.Route) &&
            equipment.Source.Status.Connected && equipment.Source.Status.Has(DeviceCapability.AnalogSource);
        CanCancel = session.Engine.IsRunning;
        CanResume = CanCancel && session.Engine.Progress.State == EngineState.Paused;
        CanPause = CanCancel && session.Engine.Progress.State is EngineState.ConfiguringInstrument or EngineState.Settling or EngineState.Acquiring;
        CanReset = !session.Busy && equipment.Simulated && equipment.Daq.Status.Connected;
        CanApprove = !session.Busy && !session.LastRunApproved && equipment.Daq.Status.Connected && equipment.Daq.Status.Has(DeviceCapability.Coefficients) &&
            session.LastRun is { Adjustments.Count: > 0 } result && result.Simulated == equipment.Simulated;
    }

    partial void OnCanStartChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged();
        CalibrateAllCommand.NotifyCanExecuteChanged();
    }
    partial void OnCanCancelChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();
    partial void OnCanResumeChanged(bool value) => ResumeCommand.NotifyCanExecuteChanged();
    partial void OnCanPauseChanged(bool value) => PauseCommand.NotifyCanExecuteChanged();
    partial void OnCanApproveChanged(bool value) => ApproveCommand.NotifyCanExecuteChanged();
    partial void OnCanResetChanged(bool value) => ResetCommand.NotifyCanExecuteChanged();

    private void UpdateProfiles() => ProfileNames = ["Benutzerdefiniert", .. session.Profiles.Select(profile => profile.Name).Distinct()];

    private void FilterRows()
    {
        var filtered = channels.Where(row => (TypeFilter == "Alle" || row.Type == TypeFilter) &&
            (StatusFilter == "Alle" || row.Status == StatusFilter) &&
            (GroupFilter == "Alle Gruppen" || row.Group == GroupFilter)).ToArray();
        if (!Rows.SequenceEqual(filtered)) Rows = filtered;
    }

    private void InvalidateMeasurements() => lastSequence = -1;

    private void RefreshChart()
    {
        if (SelectedChannel is { } row && session.Measurements.Latest is { } latest)
            Chart = Presentation.HistoryChart(session.Measurements.Snapshot(latest.Frame.Time.AddSeconds(-ChartWindowSeconds)),
                row.Definition, row.ColorIndex, ChartMode);
        else Chart = [];
    }

    private void RaiseChannel()
    {
        foreach (var property in new[] { nameof(ChannelLabel), nameof(RangeLabel), nameof(CorrectionLabel), nameof(MeasuredText),
            nameof(SetpointText), nameof(ErrorText), nameof(StabilityText), nameof(ReferenceName), nameof(ReferenceNotice) })
            OnPropertyChanged(property);
    }

    internal static RunStepRow[] RunRows(TestProfile? profile, RunProgress engine) => profile?.Steps.Select((step, index) =>
    {
        var (label, tone) = engine.SkippedSteps?.Contains(index) == true ? ("Übersprungen", "warn")
            : index < engine.Step ? ("Erfasst", "ok")
            : index == engine.Step && engine.State == EngineState.Paused ? ("Pausiert", "warn")
            : index == engine.Step && engine.State == EngineState.Failed ? ("Fehler", "err")
            : index == engine.Step && engine.State == EngineState.Aborted ? ("Abgebrochen", "warn")
            : index == engine.Step ? (Presentation.EngineLabel(engine.State), "accent") : ("Offen", "");
        return new RunStepRow(Presentation.StepLabel(step), index == engine.Step ? engine.Message : "", label, tone);
    }).ToArray() ?? [];
}
