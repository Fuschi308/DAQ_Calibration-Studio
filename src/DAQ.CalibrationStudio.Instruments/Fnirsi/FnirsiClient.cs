using System.Buffers.Binary;

namespace Fnirsi.SG004A;

public enum FnirsiOutputMode { Unknown, Current, Voltage, Other }
public enum FnirsiOutputState { Unknown }
public sealed record FnirsiDeviceInfo(string ConfiguredModel, ushort FirmwareRegisterRaw);
public sealed record FnirsiStatus(ushort OutputModeRaw, FnirsiOutputMode OutputMode,
    ushort OutputSwitchRaw, FnirsiOutputState OutputState, bool CommunicationUncertain);

/// <summary>Thrown when a command may have reached the device but acknowledgement was not trustworthy.</summary>
public sealed class AmbiguousDeviceWriteException(string message, Exception inner) : IOException(message, inner);

/// <summary>Conservative SG-004A client. No operation here activates or deactivates the output.</summary>
public sealed class FnirsiClient : IAsyncDisposable
{
    private readonly IByteTransport _transport;
    private readonly byte _address;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _log;
    private readonly bool _allowSetpointWrites;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _communicationUncertain = true;

    public FnirsiClient(IByteTransport transport, byte slaveAddress = 1, TimeSpan? timeout = null,
        bool allowSetpointWrites = false, Action<string>? log = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (slaveAddress is < 1 or > 247) throw new ArgumentOutOfRangeException(nameof(slaveAddress));
        _address = slaveAddress;
        _timeout = timeout ?? TimeSpan.FromSeconds(1);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _allowSetpointWrites = allowSetpointWrites;
        _log = log;
    }

    public bool IsConnected => _transport.IsConnected;
    public bool CommunicationUncertain => _communicationUncertain;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = Linked(cancellationToken);
            await _transport.ConnectAsync(timeout.Token);
            _communicationUncertain = true;
            _log?.Invoke("Serial transport opened. Output state remains unknown.");
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try { await _transport.DisconnectAsync(); _communicationUncertain = true; }
        finally { _gate.Release(); }
    }

    public async Task<FnirsiDeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken = default)
        => new("SG-004A (configured, not authenticated)", await ReadWordAsync(SgProtocol.Firmware, cancellationToken));

    public async Task<FnirsiStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ushort mode = await ReadWordCoreAsync(SgProtocol.OutputMode, cancellationToken);
            ushort rawSwitch = await ReadWordCoreAsync(SgProtocol.OutputSwitch, cancellationToken);
            _communicationUncertain = false;
            var decoded = mode switch
            {
                0x0100 => FnirsiOutputMode.Current,
                0x0200 => FnirsiOutputMode.Voltage,
                0 => FnirsiOutputMode.Unknown,
                _ => FnirsiOutputMode.Other
            };
            return new(mode, decoded, rawSwitch, FnirsiOutputState.Unknown, false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Read register 40004. This is measured input, not independent output feedback.</summary>
    public Task<float> GetMeasuredInputAsync(CancellationToken cancellationToken = default)
        => ReadFloatAsync(SgProtocol.InputValue, cancellationToken);

    /// <summary>Read register 40006. Unit depends on currently selected output mode.</summary>
    public Task<float> GetOutputSetpointAsync(CancellationToken cancellationToken = default)
        => ReadFloatAsync(SgProtocol.OutputValue, cancellationToken);

    /// <summary>Read register 40006 as volts only while the output mode remains exactly voltage.</summary>
    public Task<float> GetVoltageSetpointAsync(CancellationToken cancellationToken = default)
        => ReadTypedSetpointAsync(0x0200, cancellationToken);

    /// <summary>Read register 40006 as milliamps only while the output mode remains exactly active current.</summary>
    public Task<float> GetCurrentSetpointAsync(CancellationToken cancellationToken = default)
        => ReadTypedSetpointAsync(0x0100, cancellationToken);

    public Task SetVoltageAsync(double voltageV, CancellationToken cancellationToken = default)
        => SetpointAsync(voltageV, FnirsiOutputMode.Voltage, cancellationToken);

    public Task SetCurrentAsync(double currentMilliAmps, CancellationToken cancellationToken = default)
        => SetpointAsync(currentMilliAmps, FnirsiOutputMode.Current, cancellationToken);

    public Task SetOutputModeAsync(FnirsiOutputMode mode, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Output-mode writes are held until hardware side effects are verified."));

    public Task EnableOutputAsync(CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Register 40008 output-enable semantics are not established."));

    public Task DisableOutputAsync(CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("Register 40008 output-disable semantics are not established."));

    private async Task SetpointAsync(double value, FnirsiOutputMode requiredMode, CancellationToken cancellationToken)
    {
        if (!_allowSetpointWrites) throw new InvalidOperationException("Setpoint writes require explicit opt-in.");
        if (!double.IsFinite(value) || value < 0 || value > 24)
            throw new ArgumentOutOfRangeException(nameof(value), "SG-004A voltage and active-current ranges are 0 to 24 V/mA.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ushort rawMode = await ReadWordCoreAsync(SgProtocol.OutputMode, cancellationToken);
            if (rawMode != (requiredMode == FnirsiOutputMode.Voltage ? 0x0200 : 0x0100))
                throw new InvalidOperationException($"Output mode 0x{rawMode:X4} does not exactly match the required mode. No setpoint was written.");
            float encoded = (float)value;
            byte[] request = SgProtocol.WriteFloatRequest(_address, SgProtocol.OutputValue, encoded);
            await ExchangeAsync(request, 0, cancellationToken);
            try
            {
                float readback = SgProtocol.ParseFloat(await ExchangeAsync(
                    SgProtocol.ReadRequest(_address, SgProtocol.ReadFloat, SgProtocol.OutputValue, 2), 4, cancellationToken));
                if (BitConverter.SingleToInt32Bits(readback) != BitConverter.SingleToInt32Bits(encoded))
                    throw new InvalidDataException("Setpoint register readback did not match the acknowledged write.");
                ushort modeAfter = await ReadWordCoreAsync(SgProtocol.OutputMode, cancellationToken);
                if (modeAfter != rawMode)
                    throw new InvalidDataException("Output mode changed during setpoint write.");
            }
            catch (Exception ex)
            {
                _communicationUncertain = true;
                try { await _transport.DisconnectAsync(); } catch { /* preserve readback failure */ }
                throw new AmbiguousDeviceWriteException("Setpoint acknowledgement was received, but register readback failed. Physical output is unknown.", ex);
            }
            _communicationUncertain = false;
        }
        finally { _gate.Release(); }
    }

    private async Task<ushort> ReadWordAsync(ushort register, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadWordCoreAsync(register, cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task<float> ReadFloatAsync(ushort register, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            byte[] payload = await ExchangeAsync(SgProtocol.ReadRequest(_address, SgProtocol.ReadFloat, register, 2), 4, cancellationToken);
            float value = SgProtocol.ParseFloat(payload);
            if (!float.IsFinite(value))
            {
                _communicationUncertain = true;
                await _transport.DisconnectAsync();
                throw new InvalidDataException("Device returned non-finite float.");
            }
            return value;
        }
        finally { _gate.Release(); }
    }

    private async Task<float> ReadTypedSetpointAsync(ushort requiredMode, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ushort before = await ReadWordCoreAsync(SgProtocol.OutputMode, cancellationToken);
            if (before != requiredMode)
                throw new InvalidOperationException($"Output mode 0x{before:X4} does not match required mode 0x{requiredMode:X4}.");
            float value = SgProtocol.ParseFloat(await ExchangeAsync(
                SgProtocol.ReadRequest(_address, SgProtocol.ReadFloat, SgProtocol.OutputValue, 2), 4, cancellationToken));
            ushort after = await ReadWordCoreAsync(SgProtocol.OutputMode, cancellationToken);
            if (after != before || !float.IsFinite(value))
            {
                _communicationUncertain = true;
                await _transport.DisconnectAsync();
                throw new InvalidDataException("Output mode changed during setpoint read or value was non-finite.");
            }
            return value;
        }
        finally { _gate.Release(); }
    }

    private async Task<ushort> ReadWordCoreAsync(ushort register, CancellationToken cancellationToken)
        => SgProtocol.ParseWord(await ExchangeAsync(SgProtocol.ReadRequest(_address, SgProtocol.ReadWord, register, 1), 2, cancellationToken));

    private async Task<byte[]> ExchangeAsync(byte[] request, int expectedDataLength, CancellationToken cancellationToken)
    {
        if (!_transport.IsConnected) throw new IOException("Transport is not connected.");
        using var timeout = Linked(cancellationToken);
        bool writeAttempted = false;
        try
        {
            _log?.Invoke($"TX {Convert.ToHexString(request)}");
            timeout.Token.ThrowIfCancellationRequested();
            writeAttempted = true;
            await _transport.WriteAsync(request, timeout.Token);
            byte[] header = new byte[3];
            await ReadExactlyAsync(header, timeout.Token);
            if (header[0] != _address) throw new InvalidDataException("Wrong slave address.");
            if (header[1] == (byte)(request[1] | 0x80))
            {
                byte[] crc = new byte[2];
                await ReadExactlyAsync(crc, timeout.Token);
                byte[] errorFrame = [.. header, .. crc];
                if (!SgProtocol.HasValidCrc(errorFrame)) throw new InvalidDataException("Error frame CRC mismatch.");
                throw new InvalidDataException($"Device error code 0x{header[2]:X2} (meaning undocumented).");
            }
            if (header[1] != request[1]) throw new InvalidDataException("Wrong function code.");
            bool isRead = request[1] is SgProtocol.ReadWord or SgProtocol.ReadFloat;
            if (isRead && header[2] != expectedDataLength) throw new InvalidDataException("Wrong response byte count.");
            byte[] tail = new byte[isRead ? expectedDataLength + 2 : 5];
            await ReadExactlyAsync(tail, timeout.Token);
            byte[] response = [.. header, .. tail];
            _log?.Invoke($"RX {Convert.ToHexString(response)}");
            if (!SgProtocol.HasValidCrc(response)) throw new InvalidDataException("Response CRC mismatch.");
            if (isRead) return response.AsSpan(2, expectedDataLength + 1).ToArray();
            if (!response.AsSpan(2, 4).SequenceEqual(request.AsSpan(2, 4)))
                throw new InvalidDataException("Write acknowledgement does not match register and count.");
            return response.AsSpan(2, 4).ToArray();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _communicationUncertain = true;
            try { await _transport.DisconnectAsync(); } catch { /* preserve original failure */ }
            if (writeAttempted && request[1] is SgProtocol.WriteWord or SgProtocol.WriteFloat)
                throw new AmbiguousDeviceWriteException("Write acknowledgement failed. Physical output may have changed.", ex);
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException("SG-004A reply timed out. Transport was closed.", ex);
            throw;
        }
    }

    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken token)
    {
        while (!buffer.IsEmpty)
        {
            int count = await _transport.ReadAsync(buffer, token);
            if (count <= 0) throw new EndOfStreamException("Serial reply ended early.");
            buffer = buffer[count..];
        }
    }

    private CancellationTokenSource Linked(CancellationToken token)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(_timeout);
        return source;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        await _transport.DisposeAsync();
        _gate.Dispose();
    }
}
