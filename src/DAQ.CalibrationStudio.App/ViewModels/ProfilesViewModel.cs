using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.App.Controls;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed record ProfileItem(TestProfile Profile)
{
    public string Name => Profile.Name;
    public string Kind => Profile.Kind switch { RunKind.Calibration => "Kalibrierung", RunKind.Verification => "Verifikation", _ => "Funktionsprüfung" };
    public int Version => Profile.Version;
    public IReadOnlyList<TestStep> Steps => Profile.Steps;
}

public sealed class ProfileStepItem(TestStep step)
{
    public TestStep Step { get; } = step;
    public string ChannelId => Step.ChannelId;
    public string Kind => ProfilesViewModel.StepName(Step.Kind);
    public string Label => string.IsNullOrWhiteSpace(Step.Label) ? Kind : Step.Label;
    public double Value => Step.Value;
    public double Duration => Step.Duration;
    public double Tolerance => Step.Tolerance;
}

public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly StudioSession session;
    private RunProgress? previous;
    private Guid draftId = Guid.NewGuid();

    [ObservableProperty] private IReadOnlyList<ProfileItem> profiles = [];
    [ObservableProperty] private ProfileItem? selectedProfile;
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string kind = "Kalibrierung";
    [ObservableProperty] private string method = Presentation.MethodLabel(FitMethod.Regression);
    [ObservableProperty] private ProfileStepItem? selectedStep;
    [ObservableProperty] private string stepKind = "Analogmesspunkt";
    [ObservableProperty] private string channelId = "AI-V1";
    [ObservableProperty] private IReadOnlyList<string> channelIds = [];
    [ObservableProperty] private double stepValue;
    [ObservableProperty] private double stepDuration = 2;
    [ObservableProperty] private double stepTolerance = 0.008;
    [ObservableProperty] private double stepStability = 0.0007;
    [ObservableProperty] private double stepWindow = 1;
    [ObservableProperty] private string stepLabel = "";
    [ObservableProperty] private string runTitle = "Bereit";
    [ObservableProperty] private string runMessage = "Profil auswählen und Prüfablauf starten.";
    [ObservableProperty] private double progress;
    [ObservableProperty] private IReadOnlyList<RunStepRow> runSteps = [];
    [ObservableProperty] private IReadOnlyList<ChartSeries> chart = [];
    [ObservableProperty] private bool canStart;
    [ObservableProperty] private bool canPause;
    [ObservableProperty] private bool canResume;
    [ObservableProperty] private bool canCancel;

    public ProfilesViewModel(StudioSession session)
    {
        this.session = session;
        session.ProfilesChanged += () => LoadProfiles(SelectedProfile?.Profile.Id);
        session.RunCompleted += result =>
        {
            Chart = result.Points.GroupBy(point => point.Quantity).Take(1)
                .SelectMany(group => group.GroupBy(point => point.ChannelId, (id, values) =>
                    new ChartSeries(id, ChannelCatalog.Unit(group.Key), values.Select(point => point.Requested).ToArray(),
                        values.Select(point => point.Error).ToArray(), group.Key.GetHashCode())))
                .ToArray();
        };
        Steps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(VersionText));
        LoadProfiles(null);
    }

    public ObservableCollection<ProfileStepItem> Steps { get; } = [];
    public string[] Kinds { get; } = ["Funktionsprüfung", "Kalibrierung", "Verifikation"];
    public string[] Methods { get; } = Enum.GetValues<FitMethod>().Select(Presentation.MethodLabel).ToArray();
    public string[] StepKinds { get; } = Enum.GetValues<StepKind>().Select(StepName).ToArray();
    public string VersionText => SelectedProfile is null ? "Neuer Entwurf"
        : $"Revision {SelectedProfile.Version}{(IsDirty ? " · geändert" : "")}";
    private bool IsDirty => SelectedProfile is null || SelectedProfile.Name != Name || SelectedProfile.Kind != Kind ||
        Presentation.MethodLabel(SelectedProfile.Profile.Method) != Method ||
        !SelectedProfile.Steps.SequenceEqual(Steps.Select(item => item.Step));

    public void Tick()
    {
        var current = session.Engine.Progress;
        var equipment = session.Devices.Active;
        CanStart = !session.Busy && equipment.Daq.Status.Connected && equipment.Daq.Status.Has(DeviceCapability.Measurements) &&
            Steps.Any(item => item.Step.Kind != Domain.StepKind.Wait) && Steps.All(item => item.Step.Kind switch
            {
                Domain.StepKind.AnalogPoint => equipment.Daq.Status.Has(DeviceCapability.Route) &&
                    (Kind != "Kalibrierung" || equipment.Daq.Status.Has(DeviceCapability.Coefficients)) &&
                    equipment.Source.Status.Connected && equipment.Source.Status.Has(DeviceCapability.AnalogSource),
                Domain.StepKind.FrequencyPoint => equipment.Simulated && equipment.Generator.Status.Connected &&
                    equipment.Generator.Status.Has(DeviceCapability.Quadrature),
                Domain.StepKind.DigitalPoint => equipment.Daq.Status.Has(DeviceCapability.DigitalOutput),
                _ => true
            });
        CanCancel = session.Engine.IsRunning;
        CanResume = CanCancel && current.State == EngineState.Paused;
        CanPause = CanCancel && current.State is EngineState.ConfiguringInstrument or EngineState.Settling or EngineState.Acquiring;
        if (previous == current) return;
        previous = current;
        RunTitle = string.IsNullOrEmpty(current.Profile) ? Presentation.EngineLabel(current.State) : current.Profile;
        RunMessage = current.Message;
        Progress = current.Percent;
        RunSteps = OverviewViewModel.RunRows(session.ActiveRunProfile, current);
    }

    [RelayCommand]
    private void New()
    {
        SelectedProfile = null;
        draftId = Guid.NewGuid();
        Name = "Neues Prüfprofil";
        Kind = "Kalibrierung";
        Method = Presentation.MethodLabel(FitMethod.Regression);
        Steps.Clear();
        SelectedStep = null;
        StepKind = StepName(Domain.StepKind.AnalogPoint);
        PopulateChannels();
    }

    [RelayCommand]
    private void Duplicate()
    {
        var copy = Steps.Select(item => item.Step).ToArray();
        var copyName = Name + " (Kopie)";
        SelectedProfile = null;
        draftId = Guid.NewGuid();
        Name = copyName;
        LoadSteps(copy);
    }

    [RelayCommand]
    private Task SaveAsync() => session.PerformAsync(async () =>
    {
        if (!IsDirty) return;
        var snapshot = BuildProfile();
        await session.Store.SaveProfileAsync(snapshot);
        await session.ReloadProfilesAsync();
        LoadProfiles(snapshot.Id);
    }, "Prüfprofil gespeichert.");

    [RelayCommand]
    private Task AddStepAsync() => session.PerformAsync(() =>
    {
        var next = new ProfileStepItem(EditStep());
        Steps.Add(next);
        SelectedStep = next;
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task UpdateStepAsync() => session.PerformAsync(() =>
    {
        if (SelectedStep is not { } selected) return Task.CompletedTask;
        var next = new ProfileStepItem(EditStep());
        Steps[Steps.IndexOf(selected)] = next;
        SelectedStep = next;
        return Task.CompletedTask;
    });

    [RelayCommand]
    private void RemoveStep()
    {
        if (SelectedStep is not { } selected) return;
        var position = Steps.IndexOf(selected);
        Steps.RemoveAt(position);
        SelectedStep = Steps.ElementAtOrDefault(Math.Min(position, Steps.Count - 1));
    }

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => session.PerformAsync(async () =>
    {
        if (IsDirty)
        {
            session.Say("Speichern Sie den Profilentwurf vor dem Start.");
            return;
        }
        Chart = [];
        await session.RunAsync(SelectedProfile!.Profile);
    });

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => session.Engine.Pause();

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume() => session.Engine.Resume();

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => session.Engine.Cancel();

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Skip() => session.Engine.Skip();

    [RelayCommand]
    private void OpenReport() => session.OpenReport(session.LastRun);

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        if (value is null)
        {
            OnPropertyChanged(nameof(VersionText));
            return;
        }
        Name = value.Name;
        Kind = value.Kind;
        Method = Presentation.MethodLabel(value.Profile.Method);
        LoadSteps(value.Steps);
        OnPropertyChanged(nameof(VersionText));
    }

    partial void OnSelectedStepChanged(ProfileStepItem? value)
    {
        if (value is null) return;
        var step = value.Step;
        StepKind = StepName(step.Kind);
        ChannelId = step.ChannelId;
        StepValue = step.Value;
        StepDuration = step.Duration;
        StepTolerance = step.Tolerance;
        StepStability = step.Stability;
        StepWindow = step.Window;
        StepLabel = step.Label;
    }

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(VersionText));
    partial void OnKindChanged(string value) => OnPropertyChanged(nameof(VersionText));
    partial void OnMethodChanged(string value) => OnPropertyChanged(nameof(VersionText));
    partial void OnStepKindChanged(string value) => PopulateChannels();
    partial void OnCanStartChanged(bool value) => StartCommand.NotifyCanExecuteChanged();
    partial void OnCanPauseChanged(bool value) => PauseCommand.NotifyCanExecuteChanged();
    partial void OnCanResumeChanged(bool value) => ResumeCommand.NotifyCanExecuteChanged();
    partial void OnCanCancelChanged(bool value)
    {
        CancelCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
    }

    public static string StepName(StepKind kind) => kind switch
    {
        Domain.StepKind.AnalogPoint => "Analogmesspunkt",
        Domain.StepKind.FrequencyPoint => "Frequenzmesspunkt",
        Domain.StepKind.DigitalPoint => "Digitalpegel prüfen",
        _ => "Wartezeit"
    };

    private void LoadProfiles(Guid? id)
    {
        Profiles = session.Profiles.Select(profile => new ProfileItem(profile)).ToArray();
        SelectedProfile = Profiles.FirstOrDefault(item => item.Profile.Id == id) ?? Profiles.FirstOrDefault();
    }

    private void LoadSteps(IEnumerable<TestStep> steps)
    {
        Steps.Clear();
        foreach (var step in steps)
            Steps.Add(new ProfileStepItem(step));
        SelectedStep = Steps.FirstOrDefault();
        PopulateChannels();
    }

    private void PopulateChannels()
    {
        var kindValue = Enum.GetValues<StepKind>().First(value => StepName(value) == StepKind);
        ChannelIds = kindValue switch
        {
            Domain.StepKind.AnalogPoint => ChannelCatalog.All.Where(channel => channel.Group == ChannelGroup.Analog).Select(channel => channel.Id).ToArray(),
            Domain.StepKind.FrequencyPoint => ["ENC1"],
            Domain.StepKind.DigitalPoint => ChannelCatalog.All.Where(channel => channel.DigitalOutput is not null).Select(channel => channel.Id).ToArray(),
            _ => [""]
        };
        if (!ChannelIds.Contains(ChannelId))
            ChannelId = ChannelIds[0];
    }

    private TestStep EditStep()
    {
        var kindValue = Enum.GetValues<StepKind>().First(value => StepName(value) == StepKind);
        if (!double.IsFinite(StepDuration) || StepDuration is < 0.1 or > 300)
            throw new ArgumentException("Geben Sie eine Mess- oder Wartezeit zwischen 0,1 und 300 s ein.");
        if (kindValue == Domain.StepKind.Wait)
            return new(kindValue, "", Duration: StepDuration, Label: StepLabel.Trim());
        var definition = ChannelCatalog.Get(ChannelId);
        if (!double.IsFinite(StepValue) || StepValue < definition.Minimum || StepValue > definition.Maximum ||
            !double.IsFinite(StepTolerance) || StepTolerance < 0)
            throw new ArgumentException($"{definition.Id}: Sollwert oder Toleranz außerhalb des zulässigen Bereichs.");
        if (kindValue == Domain.StepKind.AnalogPoint &&
            (!double.IsFinite(StepStability) || StepStability <= 0 || !double.IsFinite(StepWindow) || StepWindow is < 0.2 or > 30))
            throw new ArgumentException("Das Stabilitätsfenster muss 0,2 bis 30 s betragen und σ größer als null sein.");
        if (kindValue == Domain.StepKind.DigitalPoint && StepValue is not (0 or 1))
            throw new ArgumentException("Ein Digitalpegel muss 0 oder 1 sein.");
        if (kindValue == Domain.StepKind.FrequencyPoint && StepValue <= 0)
            throw new ArgumentException("Die Prüffrequenz muss größer als null sein.");
        return new(kindValue, ChannelId, StepValue, StepDuration, kindValue == Domain.StepKind.DigitalPoint ? 0 : StepTolerance,
            StepStability, StepWindow, StepLabel.Trim());
    }

    private TestProfile BuildProfile()
    {
        if (string.IsNullOrWhiteSpace(Name) || Steps.Count is < 1 or > 400 ||
            Steps.All(item => item.Step.Kind == Domain.StepKind.Wait))
            throw new InvalidOperationException("Geben Sie einen Profilnamen und 1–400 Schritte mit mindestens einem Messpunkt ein.");
        var runKind = Kind switch { "Kalibrierung" => RunKind.Calibration, "Verifikation" => RunKind.Verification, _ => RunKind.Functional };
        var fit = Enum.GetValues<FitMethod>().First(value => Presentation.MethodLabel(value) == Method);
        return new(SelectedProfile?.Profile.Id ?? draftId, (SelectedProfile?.Version ?? 0) + 1, Name.Trim(),
            runKind, fit, Steps.Select(item => item.Step).ToArray());
    }

    private void Move(int shift)
    {
        if (SelectedStep is not { } selection) return;
        var position = Steps.IndexOf(selection);
        var destination = position + shift;
        if (destination >= 0 && destination < Steps.Count)
            Steps.Move(position, destination);
    }
}
