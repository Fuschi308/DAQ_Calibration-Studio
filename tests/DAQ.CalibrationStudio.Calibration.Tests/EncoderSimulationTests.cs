using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Simulation;
using Xunit;

namespace DAQ.CalibrationStudio.Calibration.Tests;

public sealed class EncoderSimulationTests
{
    [Fact]
    public async Task DynamicEncoderPairUsesOneSimulationTimestampAsSpeedChanges()
    {
        var desk = new SimulatedDesk();
        desk.Speed = 10;
        await desk.Generator.ConnectAsync("Simulation", default);
        await desk.Daq.ConnectAsync("Simulation", default);
        try
        {
            desk.Generator.StartEncoder(new EncoderMotion(EncoderMode.Ramp, 60, 600, 1200,
                Phase: 90, Reverse: false, RampSeconds: 30, DecelerationSeconds: 0), default);
            var initial = desk.Generator.CurrentPair();
            Assert.Equal(initial.A.Frequency, initial.B.Frequency);
            Assert.True(initial.A.Enabled && initial.B.Enabled);
            Assert.Equal(90, initial.B.Phase - initial.A.Phase);

            await Task.Delay(40);
            var moving = desk.Generator.CurrentPair();
            Assert.Equal(moving.A.Frequency, moving.B.Frequency);
            Assert.True(moving.A.Frequency > initial.A.Frequency + 5,
                $"Ramp frequency should rise while the simulated clock advances ({initial.A.Frequency:G6} → {moving.A.Frequency:G6} Hz).");

            var coherent = desk.Generator.CurrentPair();
            var frame = await desk.Daq.ReadFrameAsync(default);
            var measured = frame.Samples.Single(sample => sample.ChannelId == "ENC1").Corrected;
            Assert.NotNull(measured);
            Assert.InRange(measured!.Value, coherent.A.Frequency - 2, coherent.A.Frequency + 2);
        }
        finally
        {
            await desk.Generator.DisableAsync(default);
            await desk.Daq.DisconnectAsync(default);
            await desk.Generator.DisconnectAsync(default);
        }
    }
}
