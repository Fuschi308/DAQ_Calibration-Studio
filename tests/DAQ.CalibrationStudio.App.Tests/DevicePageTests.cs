using Avalonia.Headless.XUnit;
using DAQ.CalibrationStudio.App;
using DAQ.CalibrationStudio.App.Services;
using DAQ.CalibrationStudio.App.ViewModels;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DAQ.CalibrationStudio.App.Tests;

public sealed class DevicePageTests
{
    [AvaloniaFact]
    public async Task DeviceCommandsUseTheSameSimulatedBench()
    {
        var path = Path.Combine(Path.GetTempPath(), "daq-device-test-" + Guid.NewGuid().ToString("N"));
        await using var services = App.CreateServices(new StudioPaths(["--data-dir", path]));
        var shell = services.GetRequiredService<ShellViewModel>();
        var session = services.GetRequiredService<StudioSession>();
        var page = services.GetRequiredService<DevicesViewModel>();
        await shell.InitializeAsync(startTimer: false);
        try
        {
            session.Simulator.Speed = 10;
            page.Open(DeviceKind.Daq);
            await page.DiscoverCommand.ExecuteAsync(null);
            Assert.Equal(["Simulation"], page.Endpoints);
            page.Daq.SelectedChannel = page.Daq.Channels.Single(row => row.Id == "AI-V2");
            page.Daq.Input = 6;
            Assert.True(page.Daq.ApplyInputCommand.CanExecute(null));
            await page.Daq.ApplyInputCommand.ExecuteAsync(null);
            await page.Daq.Outputs[0].ToggleCommand.ExecuteAsync(null);
            var afterInput = await session.Devices.Active.Daq.ReadFrameAsync(default);
            Assert.InRange(afterInput.Samples.Single(item => item.ChannelId == "AI-V2").Corrected!.Value, 5.9, 6.1);
            Assert.Equal(1, afterInput.Samples.Single(item => item.ChannelId == "DI1").Corrected);

            page.Open(DeviceKind.Fnirsi);
            page.Fnirsi.Mode = "Strom";
            await page.Fnirsi.QuickPointCommand.ExecuteAsync(12m);
            var programmed = await session.Devices.Active.Source.ReadAsync(default);
            Assert.Equal(Quantity.Current, programmed.Quantity);
            Assert.Equal(12, programmed.Confirmed);
            Assert.Equal(OutputState.Off, programmed.Output);
            Assert.Null(programmed.IndependentReference);
            await page.Fnirsi.ToggleOutputCommand.ExecuteAsync(null);
            Assert.Equal(OutputState.On, (await session.Devices.Active.Source.ReadAsync(default)).Output);
            page.Fnirsi.AutoDwell = 0.1m;
            await page.Fnirsi.AutoCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(OutputState.Off, (await session.Devices.Active.Source.ReadAsync(default)).Output);
            Assert.Equal("AI-I1", session.Simulator.Daq.SelectedRoute);

            page.Open(DeviceKind.Owon);
            var channel = page.Owon.Channels[0];
            channel.Frequency = 1000;
            channel.Amplitude = 2;
            await channel.ApplyCommand.ExecuteAsync(null);
            Assert.False((await session.Devices.Active.Generator.ReadWaveformAsync(1, default)).Enabled);
            await channel.ToggleOutputCommand.ExecuteAsync(null);
            Assert.True((await session.Devices.Active.Generator.ReadWaveformAsync(1, default)).Enabled);

            page.Open(DeviceKind.PeakTech);
            await page.Scope.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(2, page.Scope.Chart.Length);
            Assert.Contains(page.Scope.Chart[0].Y, voltage => Math.Abs(voltage) > 0.5);
            Assert.True(page.Scope.ExportCommand.CanExecute(null));

            page.Open(DeviceKind.Owon);
            page.Owon.SweepStart = 1000;
            page.Owon.SweepStop = 3000;
            await page.Owon.SweepCommand.ExecuteAsync(null);
            Assert.True((await session.Devices.Active.Generator.ReadSweepAsync(1, default)).Active);
            await page.Owon.StopSweepCommand.ExecuteAsync(null);
            Assert.False((await session.Devices.Active.Generator.ReadSweepAsync(1, default)).Active);
            await page.Owon.EncoderCommand.ExecuteAsync(null);
            Assert.Equal(2, page.Owon.EncoderChart.Length);
            Assert.False(page.Owon.CanWaveform);
            Assert.False(page.Owon.CanSweep);
            await page.Owon.EncoderCommand.ExecuteAsync(null);
            Assert.All(new[] { 1, 2 }, index => Assert.False(session.Simulator.Generator.Current(index).Enabled));
        }
        finally { await shell.StopAsync(); }
    }

    [AvaloniaFact]
    public async Task HardwareStartupRequiresExplicitConnectionAndDisablesOutputCommands()
    {
        var path = Path.Combine(Path.GetTempPath(), "daq-device-test-" + Guid.NewGuid().ToString("N"));
        await using var services = App.CreateServices(new StudioPaths(["--data-dir", path, "--hardware"]));
        var shell = services.GetRequiredService<ShellViewModel>();
        var session = services.GetRequiredService<StudioSession>();
        var page = services.GetRequiredService<DevicesViewModel>();
        await shell.InitializeAsync(startTimer: false);
        try
        {
            Assert.False(session.Devices.Active.Simulated);
            Assert.All(session.Devices.Active.Sessions, device => Assert.Equal(DeviceState.Disconnected, device.Status.State));
            page.Open(DeviceKind.Fnirsi);
            Assert.False(page.Fnirsi.ToggleOutputCommand.CanExecute(null));
            Assert.False(page.Fnirsi.ApplyCommand.CanExecute(null));
            page.Open(DeviceKind.Owon);
            Assert.False(page.Owon.Channels[0].ToggleOutputCommand.CanExecute(null));
            Assert.False(page.Owon.SweepCommand.CanExecute(null));
            Assert.False(page.Owon.EncoderCommand.CanExecute(null));
            Assert.False(page.ConnectCommand.CanExecute(null));
        }
        finally { await shell.StopAsync(); }
    }
}
