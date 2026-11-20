using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DAQ.CalibrationStudio.App.Services;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed partial class NavItem(string label, string icon, object page) : ObservableObject
{
    public string Label { get; } = label;
    public string Icon { get; } = icon;
    public object Page { get; } = page;
    [ObservableProperty] private bool isSelected;
}

public sealed partial class DeviceTile(DeviceKind kind, string name) : ObservableObject
{
    public DeviceKind Kind { get; } = kind;
    public string Name { get; } = name;
    [ObservableProperty] private string statusText = "Getrennt";
    [ObservableProperty] private string stateTone = "";
    [ObservableProperty] private bool isSelected;
}

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly StudioSession session;
    private readonly OverviewViewModel overview;
    private readonly DevicesViewModel devicePage;
    private readonly LiveViewModel live;
    private readonly ProfilesViewModel profiles;
    private readonly HistoryViewModel history;
    private readonly SettingsViewModel settings;
    private readonly DispatcherTimer timer;
    private DateTimeOffset noticeUntil;
    private int logRevision = -1;
    private bool initialized;

    [ObservableProperty] private object page;
    [ObservableProperty] private bool showLog;
    [ObservableProperty] private string logFilter = "Alle";
    [ObservableProperty] private IReadOnlyList<StudioLogEntry> logs = [];
    [ObservableProperty] private string modeLabel = "Simulation";
    [ObservableProperty] private bool isSimulated = true;
    [ObservableProperty] private string statusIdentity = "DAQ getrennt";
    [ObservableProperty] private string operatorLabel = "";
    [ObservableProperty] private string statusClock = "";
    [ObservableProperty] private string activeProfile = "";
    [ObservableProperty] private string runState = "Bereit";
    [ObservableProperty] private string runStateTone = "";
    [ObservableProperty] private bool canTopAction;
    [ObservableProperty] private string topActionText = "Kalibrierung starten";
    [ObservableProperty] private string notice = "";
    [ObservableProperty] private bool hasNotice;
    [ObservableProperty] private ApprovalViewModel? approval;
    [ObservableProperty] private bool showApproval;
    [ObservableProperty] private int speed = 1;

    public ShellViewModel(StudioSession session, OverviewViewModel overview, DevicesViewModel devicePage,
        LiveViewModel live, ProfilesViewModel profiles, HistoryViewModel history, SettingsViewModel settings)
    {
        this.session = session;
        this.overview = overview;
        this.devicePage = devicePage;
        this.live = live;
        this.profiles = profiles;
        this.history = history;
        this.settings = settings;
        page = overview;
        Navigation =
        [
            new NavItem("Übersicht", "grid", overview) { IsSelected = true },
            new NavItem("Geräte", "chip", devicePage),
            new NavItem("Messdaten", "live", live),
            new NavItem("Prüfabläufe", "flow", profiles),
            new NavItem("Berichte", "file", history),
            new NavItem("Einstellungen", "sliders", settings)
        ];
        Devices =
        [
            new(DeviceKind.Daq, "DAQ"),
            new(DeviceKind.Fnirsi, "FNIRSI"),
            new(DeviceKind.Owon, "OWON"),
            new(DeviceKind.PeakTech, "PeakTech")
        ];
        session.Notice += DisplayNotice;
        session.ReportRequested += result =>
        {
            Activate(history);
            _ = history.LoadAsync(result);
        };
        session.ApprovalRequested += result =>
        {
            Approval = new ApprovalViewModel(session, result, () =>
            {
                ShowApproval = false;
                Approval = null;
                overview.RefreshCoefficients();
            });
            ShowApproval = true;
        };
        session.ModeChanged += () =>
        {
            overview.RefreshCoefficients();
            Refresh();
        };
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => Refresh();
    }

    public IReadOnlyList<NavItem> Navigation { get; }
    public IReadOnlyList<DeviceTile> Devices { get; }
    public string Version { get; } = typeof(ShellViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
    public int[] SpeedOptions { get; } = [1, 2, 5, 10];
    public string[] LogFilters { get; } = ["Alle", "Info", "Warnung", "Fehler"];

    public async Task InitializeAsync(bool startTimer = true)
    {
        if (initialized) return;
        initialized = true;
        if (startTimer) timer.Start();
        await session.InitializeAsync();
        overview.RefreshCoefficients();
        Refresh();
    }

    public async Task StopAsync()
    {
        timer.Stop();
        await session.StopAsync();
    }

    [RelayCommand]
    private void Navigate(NavItem? item)
    {
        if (item is null) return;
        Activate(item.Page);
        if (item.Page == history)
            _ = history.LoadAsync(null);
    }

    [RelayCommand]
    private void OpenDevice(DeviceTile? tile)
    {
        if (tile is null) return;
        devicePage.Open(tile.Kind);
        Activate(devicePage);
    }

    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanTopAction))]
    private async Task TopActionAsync()
    {
        if (session.Engine.IsRunning)
        {
            if (session.Engine.Progress.State == EngineState.Paused)
                session.Engine.Resume();
            else
                session.Engine.Pause();
            Refresh();
            return;
        }
        Activate(overview);
        if (overview.StartCommand.CanExecute(null))
            await overview.StartCommand.ExecuteAsync(null);
        Refresh();
    }

    [RelayCommand]
    private void ToggleLog() => ShowLog = !ShowLog;

    [RelayCommand]
    private Task ToggleThemeAsync() => session.PerformAsync(() =>
        session.SaveSettingsAsync(session.Settings with { LightTheme = !session.Settings.LightTheme }));

    partial void OnCanTopActionChanged(bool value) => TopActionCommand.NotifyCanExecuteChanged();

    partial void OnSpeedChanged(int value)
    {
        if (SpeedOptions.Contains(value))
            session.Simulator.Speed = value;
    }

    partial void OnLogFilterChanged(string value)
    {
        logRevision = -1;
        RefreshLogs();
    }

    partial void OnShowLogChanged(bool value)
    {
        if (value) RefreshLogs();
    }

    private void Activate(object target)
    {
        Page = target;
        foreach (var nav in Navigation) nav.IsSelected = ReferenceEquals(nav.Page, target);
        Refresh();
    }

    private void DisplayNotice(string message)
    {
        Notice = message;
        HasNotice = true;
        noticeUntil = DateTimeOffset.Now.AddSeconds(6);
    }

    private void RefreshLogs()
    {
        if (session.Log.Revision == logRevision) return;
        logRevision = session.Log.Revision;
        Logs = session.Log.Snapshot().Where(entry => LogFilter == "Alle" || entry.Level == LogFilter).ToArray();
    }

    public void Refresh()
    {
        var eq = session.Devices.Active;
        IsSimulated = eq.Simulated;
        ModeLabel = eq.Simulated ? "Simulation" : "Hardware";
        if (Speed != session.Simulator.Speed)
            Speed = session.Simulator.Speed;
        foreach (var device in Devices)
        {
            var (label, tone) = Presentation.Connection(eq[device.Kind].Status, eq.Simulated);
            device.StatusText = label;
            device.StateTone = tone;
            device.IsSelected = Page == devicePage && devicePage.CurrentDevice == device.Kind;
        }
        var identity = eq.Daq.Status.Identity;
        StatusIdentity = identity is null ? $"DAQ getrennt · {ModeLabel}" : string.Join(" · ",
            new[] { identity.Model, identity.Revision is null ? null : $"Rev. {identity.Revision}", identity.Serial,
                identity.Firmware is null ? null : $"FW {identity.Firmware}" }.OfType<string>());
        OperatorLabel = string.IsNullOrEmpty(session.Settings.Operator) ? "Prüfer offen" : $"Prüfer {session.Settings.Operator}";
        StatusClock = DateTime.Now.ToString("HH:mm:ss");
        ActiveProfile = session.ActiveRunProfile?.Name ?? "";
        var state = session.Engine.Progress.State;
        var deviceFault = !session.Engine.IsRunning && eq.Sessions.Any(device => device.Status.State == DeviceState.Faulted);
        RunState = deviceFault ? "Gerätefehler" : Presentation.EngineLabel(state);
        RunStateTone = deviceFault ? "err" : state switch
        {
            EngineState.Failed => "err",
            EngineState.Paused or EngineState.Aborted => "warn",
            EngineState.Completed => "ok",
            _ => session.Engine.IsRunning ? "accent" : ""
        };
        overview.Tick(Page == overview);
        TopActionText = session.Engine.IsRunning
            ? state == EngineState.Paused ? "Fortsetzen" : "Pause"
            : overview.CanApprove ? "Ergebnis übernehmen" : "Kalibrierung starten";
        CanTopAction = overview.CanStart || overview.CanApprove || overview.CanPause || overview.CanResume;
        HasNotice = DateTimeOffset.Now < noticeUntil;
        if (ShowLog) RefreshLogs();
        profiles.Tick();
        if (Page == devicePage) devicePage.Tick();
        if (Page == live) live.Tick();
        if (Page == settings) settings.Tick();
    }
}
