using System.Buffers.Binary;
using Fnirsi.SG004A;

namespace Fnirsi.SG004A.Simulator;

/// <summary>Synthetic byte-level peer. It models source-backed framing, not physical output.</summary>
public sealed class SimulatedByteTransport : IByteTransport
{
    private readonly Queue<byte> _reply = new();
    private readonly SemaphoreSlim _available = new(0);
    private bool _connected;
    private float _outputSetpoint;

    public enum Fault { None, Timeout, DisconnectAfterRequest, CorruptCrc, WrongAddress, WrongFunction, WrongWriteAck, BadByteCount, Truncated, DeviceError }
    public Fault NextFault { get; set; }
    public int? FaultOnRequestNumber { get; set; }
    public Fault ScheduledFault { get; set; }
    public bool IsConnected => _connected;
    public int MaxReadChunk { get; set; } = int.MaxValue;
    public ushort FirmwareRaw { get; set; } = 0x1234;
    public ushort InputModeRaw { get; set; } = 0x0100;
    public ushort OutputModeRaw { get; set; } = 0x0200;
    public ushort OutputSwitchRaw { get; set; } = 0x0101;
    public float MeasuredInput { get; set; }
    public float OutputSetpoint => _outputSetpoint;
    public List<byte[]> Requests { get; } = [];
    public Action<int>? AfterResponseQueued { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        _reply.Clear();
        while (_available.Wait(0)) { }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connected) throw new IOException("Synthetic peer is disconnected.");
        byte[] request = bytes.ToArray();
        Requests.Add(request);
        if (!SgProtocol.HasValidCrc(request)) throw new InvalidDataException("Request CRC mismatch.");
        if (request[0] != 1) throw new InvalidDataException("Synthetic peer only has slave address 1.");
        Fault fault = FaultOnRequestNumber == Requests.Count ? ScheduledFault : NextFault;
        if (FaultOnRequestNumber == Requests.Count) FaultOnRequestNumber = null;
        NextFault = Fault.None;
        if (fault == Fault.DisconnectAfterRequest) { _connected = false; return ValueTask.CompletedTask; }
        if (fault == Fault.Timeout) return ValueTask.CompletedTask;
        if (fault == Fault.DeviceError) { Enqueue(SgProtocol.Frame(1, (byte)(request[1] | 0x80), [2])); return ValueTask.CompletedTask; }

        ushort register = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(2, 2));
        byte[] response;
        if (request[1] == SgProtocol.ReadWord && request.Length == 8)
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4, 2)) != 1) throw new InvalidDataException("Synthetic word count must be 1.");
            ushort value = register switch
            {
                SgProtocol.Firmware => FirmwareRaw,
                SgProtocol.InputMode => InputModeRaw,
                SgProtocol.OutputMode => OutputModeRaw,
                SgProtocol.OutputSwitch => OutputSwitchRaw,
                _ => throw new InvalidDataException("Unsupported synthetic word register.")
            };
            byte[] payload = [2, 0, 0];
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), value);
            response = SgProtocol.Frame(1, request[1], payload);
        }
        else if (request[1] == SgProtocol.ReadFloat && request.Length == 8)
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(4, 2)) != 2) throw new InvalidDataException("Synthetic float count must be 2.");
            float value = register switch
            {
                SgProtocol.InputValue => MeasuredInput,
                SgProtocol.OutputValue => _outputSetpoint,
                _ => throw new InvalidDataException("Unsupported synthetic float register.")
            };
            byte[] payload = new byte[5];
            payload[0] = 4;
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1), BitConverter.SingleToInt32Bits(value));
            response = SgProtocol.Frame(1, request[1], payload);
        }
        else if (request[1] == SgProtocol.WriteFloat && request.Length == 13 && register == SgProtocol.OutputValue)
        {
            if (request[4] != 0 || request[5] != 2 || request[6] != 4) throw new InvalidDataException("Invalid synthetic float write layout.");
            float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(7, 4)));
            if (!float.IsFinite(value) || value < 0 || value > 24) throw new InvalidDataException("Synthetic setpoint out of range.");
            _outputSetpoint = value;
            response = SgProtocol.Frame(1, request[1], request.AsSpan(2, 4));
        }
        else throw new InvalidDataException("Unsupported synthetic operation. Mode and switch writes have no verified semantics.");

        if (fault == Fault.WrongWriteAck && request[1] == SgProtocol.WriteFloat)
        {
            response[5] ^= 1;
            response = SgProtocol.Frame(1, response[1], response.AsSpan(2, 4));
        }
        if (fault == Fault.WrongAddress) response[0] = 2;
        if (fault == Fault.WrongFunction) response[1] = request[1] == SgProtocol.ReadWord ? SgProtocol.ReadFloat : SgProtocol.ReadWord;
        if (fault == Fault.BadByteCount && request[1] is SgProtocol.ReadWord or SgProtocol.ReadFloat) response[2] ^= 1;
        if (fault == Fault.Truncated) response = response[..^3];
        if (fault == Fault.CorruptCrc) response[^1] ^= 1;
        Enqueue(response);
        AfterResponseQueued?.Invoke(Requests.Count);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!_connected) throw new IOException("Synthetic peer disconnected.");
        if (buffer.IsEmpty) return 0;
        await _available.WaitAsync(cancellationToken);
        if (!_connected) throw new IOException("Synthetic peer disconnected.");
        int count = Math.Min(Math.Min(buffer.Length, MaxReadChunk), _reply.Count);
        if (count < 1) throw new InvalidOperationException("Synthetic reply semaphore was inconsistent.");
        buffer.Span[0] = _reply.Dequeue();
        for (int i = 1; i < count; i++)
        {
            _available.Wait(0);
            buffer.Span[i] = _reply.Dequeue();
        }
        return count;
    }

    private void Enqueue(byte[] frame)
    {
        foreach (byte b in frame) { _reply.Enqueue(b); _available.Release(); }
    }
}
