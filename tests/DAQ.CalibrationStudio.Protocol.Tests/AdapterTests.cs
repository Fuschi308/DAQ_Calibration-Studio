using Daq.Service.Protocol;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Daq;
using DAQ.CalibrationStudio.Domain;
using DAQ.CalibrationStudio.Instruments;
using Fnirsi.SG004A;
using Fnirsi.SG004A.Simulator;
using Owon.DGE.Simulator;

namespace DAQ.CalibrationStudio.Protocol.Tests;

public sealed class AdapterTests
{
    [Fact]
    public async Task FnirsiDoesNotConfuseReadbackAndReference()
    {
        var transport = new SimulatedByteTransport { OutputModeRaw = 0x0100, MeasuredInput = 19.98f };
        await using var device = new FnirsiDevice(_ => transport);
        await device.ConnectAsync("synthetic", default);
        Assert.True(device.Status.Has(DeviceCapability.SetpointOnly));
        Assert.False(device.Status.Has(DeviceCapability.AnalogSource));
        var initial = await device.ReadAsync(default);
        Assert.Null(initial.Requested);
        Assert.Equal(0, initial.Confirmed);
        Assert.Null(initial.IndependentReference);
        Assert.Equal(OutputState.Unknown, initial.Output);
        await Assert.ThrowsAsync<NotSupportedException>(() => device.ApplyAsync(Quantity.Current, 20, true, default));

        var programmed = await device.WriteSetpointAsync(Quantity.Current, 20, default);
        Assert.Equal(20, programmed.Requested);
        Assert.Equal(20, programmed.Confirmed);
        Assert.Null(programmed.IndependentReference);
        Assert.Equal(OutputState.Unknown, programmed.Output);
        Assert.Equal(OutputState.Unknown, await device.DisableAsync(default));
        Assert.Single(transport.Requests, frame => frame[1] == SgProtocol.WriteFloat);
        Assert.DoesNotContain(transport.Requests, frame => frame[1] == SgProtocol.WriteWord);
    }

    [Fact]
    public async Task OwonConfiguresWhileOffAndReadsBackSweepAndShutdown()
    {
        var transport = new OwonSimulatorTransport("DGE2070");
        await using var device = new OwonDevice(_ => transport);
        await device.ConnectAsync("synthetic", default);
        Assert.Equal("DGE2070", device.Status.Identity!.Model);
        Assert.False(device.Status.Has(DeviceCapability.Quadrature));
        Assert.All(transport.Commands, command => Assert.EndsWith("?", command, StringComparison.Ordinal));

        var configure = new WaveformSettings(1, SignalShape.Square, 5000, 2.2, 0.7, 90, 50, null, true);
        var actual = await device.ApplyWaveformAsync(configure, default);
        Assert.Equal(configure, actual);
        var commands = transport.Commands.ToList();
        Assert.True(commands.FindIndex(command => command == "OUTPut1:STATe OFF") < commands.FindIndex(command => command == "OUTPut1:STATe ON"));
        Assert.True(commands.FindIndex(command => command == "SOURce1:FREQuency:FIXed?") < commands.FindIndex(command => command == "OUTPut1:STATe ON"));

        var requestedSweep = new SweepSettings(1, 2000, 7000, 5, true);
        await device.SetSweepAsync(requestedSweep, true, default);
        Assert.Equal(new SweepReading(requestedSweep, true), await device.ReadSweepAsync(1, default));
        await device.SetSweepAsync(requestedSweep, false, default);
        Assert.False((await device.ReadSweepAsync(1, default)).Active);
        Assert.Equal(OutputState.Off, await device.DisableAsync(default));
    }

    [Fact]
    public async Task DaqKeepsFirmwareCorrectedMeasurementsAndCountersDistinct()
    {
        var transport = new ScriptedService(
            ("*IDN?", "OK model=DAQ hw=B serial=LABTEST fw=0.1.0 build=test proto=1 readings=1 control=1"),
            ("MEAS:ANALOG?", "OK valid=1 flags=0x00000001 cal_gen=5 values=5.0000,-0.0120,10.0000,12.0000,4.0000,0.0000 codes=3000000,-7200,6000000,3600000,1200000,0"),
            ("MEAS:RTD?", "OK state=ACTIVE valid=0x7 ohm=1097.300,1124.900,1082.100,nan temp=25.000,32.100,21.100,nan timeouts=0"),
            ("MEAS:IO?", "OK di=0x81 di_valid=1 counters=1 enc=0xFFFFCF88 hsc4=100 hsc5=4294967296 hsc6=48210 overcapture=0 received=16001 produced=16000"));
        await using var device = new UsbDaqDevice(_ => transport);
        await device.ConnectAsync("synthetic", default);
        Assert.True(device.Status.Has(DeviceCapability.Measurements));
        Assert.True(device.Status.Has(DeviceCapability.AcquisitionControl));
        Assert.False(device.Status.Has(DeviceCapability.Coefficients));
        var frame = await device.ReadFrameAsync(default);
        Assert.False(frame.DeviceTimed);
        var channels = frame.Samples.ToDictionary(sample => sample.ChannelId);
        Assert.Equal(3_600_000, channels["AI-I1"].Raw);
        Assert.Null(channels["AI-I1"].Uncorrected);
        Assert.Equal(12, channels["AI-I1"].Corrected);
        Assert.Equal(1, channels["DI8"].Corrected);
        Assert.Null(channels["RTD4"].Corrected);
        Assert.Null(channels["ENC1"].Corrected);
        Assert.Equal(4_294_967_296UL, device.LastCounters!.Hsc5);
        Assert.Null(channels["HSC5"].Raw);
        Assert.Equal(0, transport.Remaining);
    }

    [Fact]
    public async Task DaqDoesNotReuseMalformedSession()
    {
        var transport = new ScriptedService(
            ("*IDN?", "OK model=DAQ hw=B serial=LABTEST fw=0.1.0 build=test proto=1 readings=1"),
            ("MEAS:ANALOG?", "OK valid=1 flags=0x1 values=1,2,3,4,5,6 codes=0,1,2,3,4,8388608"));
        await using var device = new UsbDaqDevice(_ => transport);
        await device.ConnectAsync("synthetic", default);
        await Assert.ThrowsAsync<InvalidDataException>(() => device.ReadFrameAsync(default));
        Assert.Equal(DeviceState.Faulted, device.Status.State);
        Assert.True(transport.Disposed);
    }

    private sealed class ScriptedService(params (string Command, string Reply)[] exchanges) : ILineTransport
    {
        private readonly Queue<(string Command, string Reply)> _exchanges = new(exchanges);
        private string? _pending;
        public bool Disposed { get; private set; }
        public int Remaining => _exchanges.Count;

        public void WriteLine(string line, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Null(_pending);
            var (command, reply) = _exchanges.Dequeue();
            Assert.Equal(command, line);
            _pending = reply;
        }

        public string ReadLine(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(_pending);
            var reply = _pending;
            _pending = null;
            return reply;
        }

        public void Dispose() => Disposed = true;
    }
}
