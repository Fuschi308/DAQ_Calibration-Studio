using System.Buffers.Binary;

namespace Fnirsi.SG004A;

/// <summary>SG-004A Modbus-like framing observed in the vendor manual and pinned Wirenboard source.</summary>
public static class SgProtocol
{
    public const ushort Firmware = 40001;
    public const ushort InputMode = 40002;
    public const ushort OutputMode = 40003;
    public const ushort InputValue = 40004;
    public const ushort OutputValue = 40006;
    public const ushort OutputSwitch = 40008;
    public const byte ReadWord = 0x64;
    public const byte WriteWord = 0x65;
    public const byte ReadFloat = 0x66;
    public const byte WriteFloat = 0x67;

    public static ushort Crc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xffff;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (ushort)((crc & 1) == 1 ? (crc >> 1) ^ 0xa001 : crc >> 1);
        }
        return crc;
    }

    public static byte[] Frame(byte address, byte function, ReadOnlySpan<byte> payload)
    {
        if (address == 0 || address > 247) throw new ArgumentOutOfRangeException(nameof(address));
        var frame = new byte[4 + payload.Length];
        frame[0] = address;
        frame[1] = function;
        payload.CopyTo(frame.AsSpan(2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), Crc(frame.AsSpan(0, frame.Length - 2)));
        return frame;
    }

    public static bool HasValidCrc(ReadOnlySpan<byte> frame) => frame.Length >= 4 &&
        BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]) == Crc(frame[..^2]);

    public static byte[] ReadRequest(byte address, byte function, ushort register, ushort count)
    {
        if (function != ReadWord && function != ReadFloat) throw new ArgumentOutOfRangeException(nameof(function));
        if (count == 0) throw new ArgumentOutOfRangeException(nameof(count));
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(payload, register);
        BinaryPrimitives.WriteUInt16BigEndian(payload[2..], count);
        return Frame(address, function, payload);
    }

    public static byte[] WriteFloatRequest(byte address, ushort register, float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        Span<byte> payload = stackalloc byte[9];
        BinaryPrimitives.WriteUInt16BigEndian(payload, register);
        BinaryPrimitives.WriteUInt16BigEndian(payload[2..], 2);
        payload[4] = 4;
        BinaryPrimitives.WriteInt32BigEndian(payload[5..], BitConverter.SingleToInt32Bits(value));
        return Frame(address, WriteFloat, payload);
    }

    public static ushort ParseWord(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 3 || payload[0] != 2) throw new InvalidDataException("Invalid uint16 payload length.");
        return BinaryPrimitives.ReadUInt16BigEndian(payload[1..]);
    }

    public static float ParseFloat(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 5 || payload[0] != 4) throw new InvalidDataException("Invalid float payload length.");
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(payload[1..]));
    }
}
