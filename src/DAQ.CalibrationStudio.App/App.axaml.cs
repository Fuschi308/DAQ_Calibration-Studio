using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DAQ.CalibrationStudio.App.Services;
using DAQ.CalibrationStudio.App.ViewModels;
using DAQ.CalibrationStudio.App.Views;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Daq;
using DAQ.CalibrationStudio.Infrastructure.Persistence;
using DAQ.CalibrationStudio.Infrastructure.Reporting;
using DAQ.CalibrationStudio.Instruments;
using DAQ.CalibrationStudio.Simulation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DAQ.CalibrationStudio.App;

public partial class App : Avalonia.Application
{
    private ServiceProvider? services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = new StudioPaths(desktop.Args ?? []);
            services = CreateServices(paths);
            var shell = services.GetRequiredService<ShellViewModel>();
            var main = new MainWindow { DataContext = shell };
            services.GetRequiredService<DesktopFiles>().Owner = main;
            var closed = false;
            var closing = false;
            main.Opened += async (_, _) => await shell.InitializeAsync();
            main.Closing += async (_, args) =>
            {
                if (closed) return;
                args.Cancel = true;
                if (closing) return;
                closing = true;
                await shell.StopAsync();
                await services.DisposeAsync();
                closed = true;
                main.Close();
            };
            desktop.MainWindow = main;
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static ServiceProvider CreateServices(StudioPaths paths)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(paths);
        collection.AddSingleton<StudioLog>();
        collection.AddSingleton<ILoggerProvider>(services => services.GetRequiredService<StudioLog>());
        collection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
        collection.AddSingleton<IStudioStore>(_ => new SqliteStudioStore(paths.Database));
        collection.AddSingleton<ReportExporter>();
        collection.AddSingleton<DesktopFiles>();
        collection.AddSingleton<SimulatedDesk>(services =>
        {
            try { return new SimulatedDesk(paths.SimulationFlash); }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            {
                services.GetRequiredService<ILogger<App>>().LogError(ex,
                    "Simulierter Flash ist fehlerhaft. Diese Sitzung nutzt Standardkoeffizienten ohne Flash-Datei.");
                return new SimulatedDesk();
            }
        });
        collection.AddSingleton(services => new DeviceManager(services.GetRequiredService<SimulatedDesk>().Equipment,
            new Equipment(false, [new UsbDaqDevice(), .. HardwareInstruments.Create()]),
            services.GetRequiredService<ILogger<DeviceManager>>()));
        collection.AddSingleton<MeasurementHistory>();
        collection.AddSingleton<MeasurementMonitor>();
        collection.AddSingleton<CalibrationEngine>();
        collection.AddSingleton<AdjustmentService>();
        collection.AddSingleton<LiveRecording>();
        collection.AddSingleton<StudioSession>();
        collection.AddSingleton<OverviewViewModel>();
        collection.AddSingleton<DevicesViewModel>();
        collection.AddSingleton<LiveViewModel>();
        collection.AddSingleton<ProfilesViewModel>();
        collection.AddSingleton<HistoryViewModel>();
        collection.AddSingleton<SettingsViewModel>();
        collection.AddSingleton<ShellViewModel>();
        return collection.BuildServiceProvider();
    }
}
