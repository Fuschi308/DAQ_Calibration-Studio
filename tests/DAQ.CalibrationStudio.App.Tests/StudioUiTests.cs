using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DAQ.CalibrationStudio.App;
using DAQ.CalibrationStudio.App.Services;
using DAQ.CalibrationStudio.App.ViewModels;
using DAQ.CalibrationStudio.App.Views;
using DAQ.CalibrationStudio.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(DAQ.CalibrationStudio.App.Tests.TestApp))]
namespace DAQ.CalibrationStudio.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class StudioUiTests
{
    [AvaloniaFact]
    public async Task Simulation_navigation_bindings_and_rendering()
    {
        var directory = Path.Combine(Path.GetTempPath(), "daq-ui-test-" + Guid.NewGuid().ToString("N"));
        await using var services = App.CreateServices(new StudioPaths(["--data-dir", directory]));
        var shell = services.GetRequiredService<ShellViewModel>();
        var session = services.GetRequiredService<StudioSession>();
        var window = new MainWindow { DataContext = shell, Width = 1280, Height = 800 };
        try
        {
            window.Show();
            await shell.InitializeAsync(startTimer: false);
            shell.Refresh();
            Assert.False(window.FindControl<Control>("ApprovalOverlay")!.IsVisible);
            Assert.True(shell.IsSimulated);
            Assert.All(shell.Devices, d => Assert.NotEqual("Getrennt", d.StatusText));
            Assert.True(shell.CanTopAction);
            Assert.Equal(6, shell.Navigation.Count);
            for (var i = 0; i < 80 && session.Measurements.Latest is null; i++) await Task.Delay(100);
            Assert.NotNull(session.Measurements.Latest);
            shell.Refresh();
            var overview = services.GetRequiredService<OverviewViewModel>();
            Assert.Contains(overview.Rows, row => row.Measured != "—");

            foreach (var nav in shell.Navigation)
            {
                var button = window.GetVisualDescendants().OfType<Button>()
                    .Single(b => AutomationProperties.GetName(b) == nav.Label);
                Assert.NotNull(button.Command);
                Assert.True(button.Command.CanExecute(button.CommandParameter));
                button.Command.Execute(button.CommandParameter);
                shell.Refresh();
                Assert.Same(nav.Page, shell.Page);
                Assert.True(nav.IsSelected);
                Assert.Single(shell.Navigation, n => n.IsSelected);
            }
            shell.NavigateCommand.Execute(shell.Navigation[5]);
            await Task.Delay(700);
            var settingsView = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
            var settings = services.GetRequiredService<SettingsViewModel>();
            var labels = new[] { "Allgemein", "Kalibrierung und Geräte", "Grenzwerte", "Referenzgeräte", "Simulation", "Datenspeicherung", "Architektur" };
            for (var i = 0; i < settings.Sections.Count; i++)
            {
                settings.Section = settings.Sections[i];
                Assert.Contains(settingsView.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == labels[i] && text.IsEffectivelyVisible);
            }
            shell.NavigateCommand.Execute(shell.Navigation[1]);
            shell.Refresh();
            await Task.Delay(700);
            var devicesView = Assert.Single(window.GetVisualDescendants().OfType<DevicesView>());
            var daqView = Assert.Single(devicesView.GetVisualDescendants().OfType<DaqView>());
            Assert.True(daqView.IsEffectivelyVisible);
            var facts = daqView.GetVisualDescendants().OfType<FactList>().First();
            Assert.NotEmpty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(facts.ItemsSource).Cast<object>());
            var live = services.GetRequiredService<LiveViewModel>();
            shell.NavigateCommand.Execute(shell.Navigation[2]);
            shell.Refresh();
            Assert.NotEmpty(live.Statistics);
            Assert.Contains("Sequenz", live.Status);

            // Each frame exercises real Avalonia layout and Skia rendering at the requested sizes/themes.
            foreach (var light in new[] { false, true })
            foreach (var size in new[] { new PixelSize(1280, 800), new PixelSize(1920, 1080) })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
                window.Width = size.Width;
                window.Height = size.Height;
                await Task.Delay(150);
                Assert.Equal(size.Width, window.Bounds.Width);
                Assert.Equal(size.Height, window.Bounds.Height);
                foreach (var nav in shell.Navigation)
                {
                    shell.NavigateCommand.Execute(nav);
                    shell.Refresh();
                    await Task.Delay(600);
                    window.UpdateLayout();
                    Assert.True(window.GetVisualDescendants().Any(visual => visual.GetType().Name == nav.Page.GetType().Name.Replace("ViewModel", "View")), $"Missing view for {nav.Label}: {string.Join(", ", window.GetVisualDescendants().Where(v => v.GetType().Name.EndsWith("View")).Select(v => v.GetType().Name))}");
                    using var bitmap = new RenderTargetBitmap(size);
                    bitmap.Render(window);
                    Assert.Equal(size, bitmap.PixelSize);
                    if (Environment.GetEnvironmentVariable("DAQ_UI_QA_DIR") is { Length: > 0 } output)
                    {
                        Directory.CreateDirectory(output);
                        bitmap.Save(Path.Combine(output, $"{(light ? "light" : "dark")}-{size.Width}x{size.Height}-{Array.IndexOf(shell.Navigation.ToArray(), nav)}.png"), PngBitmapEncoderOptions.Default);
                    }
                }
            }
        }
        finally
        {
            await shell.StopAsync();
            window.Close();
        }
    }
    [AvaloniaFact]
    public async Task Simulated_calibration_approval_and_history_flow()
    {
        var directory = Path.Combine(Path.GetTempPath(), "daq-ui-test-" + Guid.NewGuid().ToString("N"));
        await using var services = App.CreateServices(new StudioPaths(["--data-dir", directory]));
        var shell = services.GetRequiredService<ShellViewModel>();
        var session = services.GetRequiredService<StudioSession>();
        var window = new MainWindow { DataContext = shell };
        try
        {
            window.Show();
            await shell.InitializeAsync(false);
            session.Simulator.Speed = 10;
            var overview = services.GetRequiredService<OverviewViewModel>();
            foreach (var row in overview.Rows) row.IsChecked = row.Id == "AI-V1";
            overview.SelectedChannel = overview.Rows.Single(row => row.Id == "AI-V1");
            overview.PointsText = "0 / 5 / 10";
            overview.Averaging = 0.2;
            overview.Window = 0.2;
            overview.Tolerance = 0.2;
            overview.Stability = 0.01;
            shell.Refresh();
            Assert.True(overview.StartCommand.CanExecute(null));
            await overview.StartCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(session.LastRun);
            Assert.Equal(RunOutcome.Passed, session.LastRun!.Outcome);
            Assert.NotEmpty(session.LastRun.Adjustments);
            shell.Refresh();
            Assert.True(overview.ApproveCommand.CanExecute(null));
            overview.ApproveCommand.Execute(null);
            Assert.True(shell.ShowApproval);
            Assert.True(window.FindControl<Control>("ApprovalOverlay")!.IsVisible);
            var approval = Assert.IsType<ApprovalViewModel>(shell.Approval);
            Assert.False(approval.CommitCommand.CanExecute(null));
            approval.OperatorId = "UI-Test";
            Assert.False(approval.CommitCommand.CanExecute(null));
            approval.Confirmed = true;
            approval.Target = "RAM";
            Assert.True(approval.CommitCommand.CanExecute(null));
            await approval.CommitCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(shell.ShowApproval);
            Assert.False(window.FindControl<Control>("ApprovalOverlay")!.IsVisible);
            Assert.True(session.LastRunApproved);
            var history = services.GetRequiredService<HistoryViewModel>();
            await history.LoadAsync(session.LastRun);
            Assert.Equal(session.LastRun.Id, history.SelectedRun?.Run.Id);
            Assert.NotEmpty(history.Points);
            Assert.True(history.ExportCsvCommand.CanExecute(null));
            shell.NavigateCommand.Execute(shell.Navigation[4]);
            await Task.Delay(700);
            await history.LoadAsync(session.LastRun);
            await Task.Delay(100);
            var historyView = Assert.Single(window.GetVisualDescendants().OfType<HistoryView>());
            var preview = Assert.Single(historyView.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("paper"));
            Assert.True(preview.IsEffectivelyVisible);
            Assert.Contains(preview.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == history.Title && text.IsEffectivelyVisible);

            // A long run must expose cancellation and persist its aborted outcome.
            overview.Averaging = 30;
            shell.Refresh();
            var pending = overview.StartCommand.ExecuteAsync(null);
            for (var i = 0; i < 100 && !session.Engine.IsRunning; i++) await Task.Delay(20);
            Assert.True(session.Engine.IsRunning);
            shell.Refresh();
            Assert.True(overview.CancelCommand.CanExecute(null));
            overview.CancelCommand.Execute(null);
            await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(RunOutcome.Aborted, session.LastRun!.Outcome);
            await history.LoadAsync(session.LastRun);
            Assert.Equal(RunOutcome.Aborted, history.SelectedRun!.Run.Outcome);
        }
        finally
        {
            await shell.StopAsync();
            window.Close();
        }
    }
}
