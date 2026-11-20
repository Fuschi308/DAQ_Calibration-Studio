using Fnirsi.SG004A;
using Fnirsi.SG004A.Simulator;

namespace Fnirsi.SG004A.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void ManualExampleCrcAndByteOrder()
    {
        // FNIRSI manual V0.2, appendix example. The caption calls CRC high/low,
        // but the numeric bytes 40 AF are Modbus CRC low/high for numeric 0xAF40.
        byte[] frame = SgProtocol.Frame(1, SgProtocol.WriteWord, [0x9c, 0x42, 0x04, 0x62]);
        Assert.Equal("01659C42046240AF", Convert.ToHexString(frame));
        Assert.True(SgProtocol.HasValidCrc(frame));
        frame[^1] ^= 1;
        Assert.False(SgProtocol.HasValidCrc(frame));
    }

    [Fact]
    public void FloatWriteUsesBigEndianIeeeAndRegisterCountTwo()
    {
        byte[] frame = SgProtocol.WriteFloatRequest(1, SgProtocol.OutputValue, 5f);
        Assert.Equal("01679C4600020440A00000", Convert.ToHexString(frame[..^2]));
        Assert.True(SgProtocol.HasValidCrc(frame));
        Assert.Equal(5f, SgProtocol.ParseFloat([4, 0x40, 0xa0, 0, 0]));
        Assert.Throws<InvalidDataException>(() => SgProtocol.ParseFloat([2, 0x40, 0xa0, 0, 0]));
    }

    [Fact]
    public async Task ConnectDoesNotTransmitOrInferOutput()
    {
        await using var sim = new SimulatedByteTransport();
        await using var client = new FnirsiClient(sim);
        await client.ConnectAsync();
        Assert.True(client.IsConnected);
        Assert.Empty(sim.Requests);
        Assert.True(client.CommunicationUncertain);
        var status = await client.GetStatusAsync();
        Assert.Equal(FnirsiOutputMode.Voltage, status.OutputMode);
        Assert.Equal(FnirsiOutputState.Unknown, status.OutputState);
        Assert.Equal(0x0101, status.OutputSwitchRaw);
        Assert.Equal(2, sim.Requests.Count);
        Assert.All(sim.Requests, x => Assert.Equal(SgProtocol.ReadWord, x[1]));
    }

    [Fact]
    public async Task FirmwareIsRawAndInputMeasurementIsDistinctFromSetpoint()
    {
        await using var sim = new SimulatedByteTransport { FirmwareRaw = 0x7654, MeasuredInput = 3.25f, MaxReadChunk = 1 };
        await using var client = new FnirsiClient(sim);
        await client.ConnectAsync();
        Assert.Equal((ushort)0x7654, (await client.GetDeviceInfoAsync()).FirmwareRegisterRaw);
        Assert.Equal(3.25f, await client.GetMeasuredInputAsync());
        Assert.Equal(0f, await client.GetOutputSetpointAsync());
        Assert.Equal("01669C440002", Convert.ToHexString(sim.Requests[1][..6]));
        Assert.Equal("01669C460002", Convert.ToHexString(sim.Requests[2][..6]));
    }

    [Fact]
    public async Task TypedSetpointReadChecksModeBeforeAndAfter()
    {
        await using var sim = new SimulatedByteTransport { OutputModeRaw = 0x0200 };
        await using var client = new FnirsiClient(sim);
        await client.ConnectAsync();
        Assert.Equal(0f, await client.GetVoltageSetpointAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetCurrentSetpointAsync());
        Assert.Equal(4, sim.Requests.Count);
        sim.AfterResponseQueued = n => { if (n == 6) sim.OutputModeRaw = 0x0100; };
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetVoltageSetpointAsync());
        Assert.False(client.IsConnected);
    }

    [Theory]
    [InlineData(5, 0x0200, true)]
    [InlineData(12, 0x0100, false)]
    public async Task ExplicitSetpointWriteChecksModeAndReadsBack(double value, ushort mode, bool voltage)
    {
        await using var sim = new SimulatedByteTransport { OutputModeRaw = mode, MaxReadChunk = 2 };
        await using var client = new FnirsiClient(sim, allowSetpointWrites: true);
        await client.ConnectAsync();
        if (voltage) await client.SetVoltageAsync(value);
        else await client.SetCurrentAsync(value);
        Assert.Equal((float)value, sim.OutputSetpoint);
        Assert.Equal(new byte[] { 0x64, 0x67, 0x66, 0x64 }, sim.Requests.Select(x => x[1]));
        Assert.DoesNotContain(sim.Requests, x => x[1] == SgProtocol.WriteWord);
    }

    [Fact]
    public async Task SetpointRequiresOptInMatchingModeAndRange()
    {
        await using var sim = new SimulatedByteTransport();
        await using var client = new FnirsiClient(sim);
        await client.ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetVoltageAsync(5));
        Assert.Empty(sim.Requests);
        await using var writable = new FnirsiClient(new SimulatedByteTransport(), allowSetpointWrites: true);
        await writable.ConnectAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writable.SetVoltageAsync(double.NaN));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writable.SetCurrentAsync(25));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writable.SetCurrentAsync(5));
    }

    [Fact]
    public async Task ModeAndOutputSwitchOperationsAreIsolated()
    {
        await using var sim = new SimulatedByteTransport();
        await using var client = new FnirsiClient(sim);
        await client.ConnectAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => client.SetOutputModeAsync(FnirsiOutputMode.Current));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.EnableOutputAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => client.DisableOutputAsync());
        Assert.Empty(sim.Requests);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(20)]
    public async Task CurrentSetpointUsesMilliamps(double milliAmps)
    {
        await using var sim = new SimulatedByteTransport { OutputModeRaw = 0x0100, OutputSwitchRaw = 0x0102 };
        await using var client = new FnirsiClient(sim, allowSetpointWrites: true);
        await client.ConnectAsync();
        await client.SetCurrentAsync(milliAmps);
        Assert.Equal((float)milliAmps, sim.OutputSetpoint);
        Assert.Equal(FnirsiOutputState.Unknown, (await client.GetStatusAsync()).OutputState);
    }

    [Theory]
    [InlineData(SimulatedByteTransport.Fault.CorruptCrc)]
    [InlineData(SimulatedByteTransport.Fault.WrongAddress)]
    [InlineData(SimulatedByteTransport.Fault.WrongFunction)]
    [InlineData(SimulatedByteTransport.Fault.BadByteCount)]
    [InlineData(SimulatedByteTransport.Fault.DeviceError)]
    public async Task InvalidReplyClosesTransport(SimulatedByteTransport.Fault fault)
    {
        await using var sim = new SimulatedByteTransport();
        await using var client = new FnirsiClient(sim);
        await client.ConnectAsync();
        sim.NextFault = fault;
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetDeviceInfoAsync());
        Assert.False(client.IsConnected);
        Assert.True(client.CommunicationUncertain);
    }

    [Fact]
    public async Task TimeoutClosesTransportSoLateBytesCannotBecomeNextReply()
    {
        await using var sim = new SimulatedByteTransport();
        await using var client = new FnirsiClient(sim, timeout: TimeSpan.FromMilliseconds(25));
        await client.ConnectAsync();
        sim.NextFault = SimulatedByteTransport.Fault.Timeout;
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetDeviceInfoAsync());
        Assert.False(client.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => client.GetDeviceInfoAsync());
    }

    [Fact]
    public async Task FailedWriteIsAmbiguousEvenWhenSyntheticDeviceAppliedIt()
    {
        await using var sim = new SimulatedByteTransport();
        await using var client = new FnirsiClient(sim, allowSetpointWrites: true);
        await client.ConnectAsync();
        sim.FaultOnRequestNumber = 2;
        sim.ScheduledFault = SimulatedByteTransport.Fault.CorruptCrc;
        await Assert.ThrowsAsync<AmbiguousDeviceWriteException>(() => client.SetVoltageAsync(5));
        Assert.Equal(5f, sim.OutputSetpoint);
        Assert.False(client.IsConnected);
        Assert.True(client.CommunicationUncertain);
    }

    [Fact]
    public async Task MismatchedWriteEchoIsAmbiguous()
    {
        await using var sim = new SimulatedByteTransport();
        await using var client = new FnirsiClient(sim, allowSetpointWrites: true);
        await client.ConnectAsync();
        sim.FaultOnRequestNumber = 2;
        sim.ScheduledFault = SimulatedByteTransport.Fault.WrongWriteAck;
        await Assert.ThrowsAsync<AmbiguousDeviceWriteException>(() => client.SetVoltageAsync(5));
        Assert.Equal(5f, sim.OutputSetpoint);
        Assert.True(client.CommunicationUncertain);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ModeDriftAfterSetpointWriteIsAmbiguous()
    {
        await using var sim = new SimulatedByteTransport();
        sim.AfterResponseQueued = n => { if (n == 3) sim.OutputModeRaw = 0x0100; };
        await using var client = new FnirsiClient(sim, allowSetpointWrites: true);
        await client.ConnectAsync();
        await Assert.ThrowsAsync<AmbiguousDeviceWriteException>(() => client.SetVoltageAsync(5));
        Assert.Equal(5f, sim.OutputSetpoint);
        Assert.True(client.CommunicationUncertain);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task InFlightCancellationDropsPartialStateAndReconnectStartsClean()
    {
        await using var sim = new SimulatedByteTransport();
        await using var client = new FnirsiClient(sim, timeout: TimeSpan.FromSeconds(2));
        await client.ConnectAsync();
        sim.NextFault = SimulatedByteTransport.Fault.Truncated;
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetDeviceInfoAsync(cancelled.Token));
        Assert.False(client.IsConnected);
        Assert.True(client.CommunicationUncertain);
        await client.ConnectAsync();
        Assert.Equal((ushort)0x1234, (await client.GetDeviceInfoAsync()).FirmwareRegisterRaw);
    }

    [Fact]
    public async Task NonFiniteMeasurementIsRejected()
    {
        await using var sim = new SimulatedByteTransport { MeasuredInput = float.NaN };
        await using var client = new FnirsiClient(sim);
        await client.ConnectAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetMeasuredInputAsync());
    }

    [Fact]
    public async Task ParallelReadsAreSerializedAndCancellationIsHonored()
    {
        await using var sim = new SimulatedByteTransport { MaxReadChunk = 1 };
        await using var client = new FnirsiClient(sim);
        await client.ConnectAsync();
        var reads = Enumerable.Range(0, 20).Select(_ => client.GetDeviceInfoAsync()).ToArray();
        await Task.WhenAll(reads);
        Assert.Equal(20, sim.Requests.Count);
        Assert.All(sim.Requests, x => Assert.True(SgProtocol.HasValidCrc(x)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetDeviceInfoAsync(cancelled.Token));
        Assert.Equal(20, sim.Requests.Count);
    }
}
