using Avalonia.Styling;
using DAQ.CalibrationStudio.App.Services;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;
using DAQ.CalibrationStudio.Infrastructure.Reporting;
using DAQ.CalibrationStudio.Simulation;
using Microsoft.Extensions.Logging;

namespace DAQ.CalibrationStudio.App.ViewModels;

public sealed class StudioSession(DeviceManager devices, SimulatedDesk simulator, CalibrationEngine engine,
    MeasurementHistory measurements, MeasurementMonitor monitor, IStudioStore store, AdjustmentService adjustment,
    LiveRecording recording, ReportExporter export, DesktopFiles files, StudioPaths paths, StudioLog log,
    ILogger<StudioSession> logger)
{
    private readonly CancellationTokenSource lifetime = new();
    private Task? monitoring;
    private Task<RunResult>? running;
    private int operations;

    internal CancellationToken LifetimeToken => lifetime.Token;
    public DeviceManager Devices { get; } = devices;
    public SimulatedDesk Simulator { get; } = simulator;
    public CalibrationEngine Engine { get; } = engine;
    public MeasurementHistory Measurements { get; } = measurements;
    public IStudioStore Store { get; } = store;
    public AdjustmentService Adjustment { get; } = adjustment;
    public LiveRecording Recording { get; } = recording;
    public ReportExporter Export { get; } = export;
    public DesktopFiles Files { get; } = files;
    public StudioPaths Paths { get; } = paths;
    public StudioLog Log { get; } = log;
    public StudioSettings Settings { get; private set; } = new();
    public IReadOnlyList<TestProfile> Profiles { get; private set; } = StudioProfiles.Defaults;
    public RunResult? LastRun { get; private set; }
    public TestProfile? ActiveRunProfile { get; private set; }
    public bool LastRunApproved { get; set; }
    public bool Busy => operations > 0 || Engine.IsRunning;
    public event Action<string>? Notice;
    public event Action? SettingsChanged;
    public event Action? ModeChanged;
    public event Action? ProfilesChanged;
    public event Action<RunResult>? RunCompleted;
    public event Action<RunResult?>? ReportRequested;
    public event Action<RunResult>? ApprovalRequested;

    public async Task InitializeAsync()
    {
        logger.LogInformation("DAQ Calibration Studio gestartet. Datenordner {Root}", Paths.Root);
        await PerformAsync(async () =>
        {
            Settings = await Store.LoadSettingsAsync(lifetime.Token);
            ApplyTheme(Settings.LightTheme);
            SettingsChanged?.Invoke();
            await ReloadProfilesAsync();
        });
        if (Paths.StartInHardware)
            await PerformAsync(() => Devices.SwitchAsync(false, lifetime.Token));
        else
            await PerformAsync(ConnectSimulationAsync);
        monitoring = Task.Run(() => monitor.RunAsync(lifetime.Token));
    }

    public async Task StopAsync()
    {
        lifetime.Cancel();
        Engine.Cancel();
        try
        {
            if (running is not null)
                await running;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Prüflauf beim Schließen");
        }
        if (monitoring is not null)
        {
            try { await monitoring; }
            catch (OperationCanceledException) { }
        }
        await Recording.DisposeAsync();
    }

    public async Task RunAsync(TestProfile profile)
    {
        if (running is not null)
        {
            Say("Ein Prüfablauf läuft bereits.");
            return;
        }
        var request = new RunRequest(profile, Settings.Operator, Settings.Conditions, Settings.References ?? []);
        ActiveRunProfile = profile;
        running = Engine.RunAsync(request, lifetime.Token);
        try
        {
            var result = await running;
            LastRun = result;
            LastRunApproved = false;
            RunCompleted?.Invoke(result);
            Say(result.Outcome == RunOutcome.Passed ? "Prüfergebnis gespeichert." : result.Error ?? "Prüfergebnis gespeichert.");
        }
        catch (Exception ex)
        {
            Say(ex.Message);
        }
        finally
        {
            running = null;
        }
    }

    public async Task SwitchModeAsync()
    {
        if (Busy)
            throw new InvalidOperationException("Beenden Sie den laufenden Vorgang vor dem Moduswechsel.");
        var simulation = !Devices.Active.Simulated;
        if (!simulation && !await Files.ConfirmAsync("Hardwarebetrieb", "Geräte werden anschließend einzeln verbunden. " +
            "USB-Geräte können eine gemeinsame Masse haben. Verdrahtung und Pegel vor einer Ausgangsfreigabe prüfen.",
            "Hardwaremodus"))
            return;
        await Recording.StopAsync();
        await Devices.SwitchAsync(simulation, lifetime.Token);
        Measurements.Clear();
        ModeChanged?.Invoke();
        if (simulation)
            await ConnectSimulationAsync();
    }

    public async Task ReloadProfilesAsync()
    {
        var custom = await Store.ListProfilesAsync(lifetime.Token);
        Profiles = StudioProfiles.Defaults.Concat(custom.Where(profile => !profile.SnapshotOnly)).GroupBy(profile => profile.Id)
            .Select(group => group.Reverse().MaxBy(profile => profile.Version)!)
            .ToArray();
        ProfilesChanged?.Invoke();
    }

    public async Task SaveSettingsAsync(StudioSettings newSettings)
    {
        await Store.SaveSettingsAsync(newSettings, lifetime.Token);
        Settings = newSettings;
        ApplyTheme(newSettings.LightTheme);
        SettingsChanged?.Invoke();
    }

    public double Tolerance(ChannelDefinition channel) =>
        Settings.Tolerances?.GetValueOrDefault(channel.Id, channel.Tolerance) ?? channel.Tolerance;

    public void OpenReport(RunResult? result) => ReportRequested?.Invoke(result);

    public void OpenApproval(RunResult result) => ApprovalRequested?.Invoke(result);

    public void Say(string message) => Notice?.Invoke(message);

    public void LogDevice(string device, string action) => logger.LogInformation("{Device}: {Action}", device, action);

    public void LogDeviceFault(string device, string issue) => logger.LogError("{Device}: {Issue}", device, issue);

    public async Task PerformAsync(Func<Task> operation, string? success = null, bool exclusive = false)
    {
        if (exclusive && Busy)
        {
            Say("Ein Prüflauf oder eine Geräteaktion ist aktiv.");
            return;
        }
        if (exclusive) operations++;
        try
        {
            await operation();
            if (success is not null)
                Say(success);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bedienvorgang fehlgeschlagen");
            Say(ex.Message);
        }
        finally
        {
            if (exclusive) operations--;
        }
    }

    private Task ConnectSimulationAsync() => Devices.UseAsync(async (equipment, ct) =>
    {
        if (!equipment.Simulated)
            return;
        foreach (var device in equipment.Sessions)
        {
            await device.ConnectAsync("Simulation", ct).ConfigureAwait(false);
            logger.LogInformation("{Device} verbunden (Simulation)", device.Status.Name);
        }
    }, lifetime.Token);

    private static void ApplyTheme(bool light)
    {
        if (Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
    }
}
