using System.Globalization;
using System.Text;

namespace Daq.Service.Protocol;

public sealed class ProtocolFields
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    internal ProtocolFields(ReadOnlySpan<char> fields)
    {
        while (!(fields = fields.TrimStart(' ')).IsEmpty)
        {
            var separator = fields.IndexOf('=');
            if (separator <= 0 || !IsKey(fields[..separator]))
                throw new InvalidDataException("Invalid response field name.");

            var key = fields[..separator].ToString();
            fields = fields[(separator + 1)..];
            string value;
            if (!fields.IsEmpty && fields[0] == '"')
            {
                value = ReadQuoted(ref fields);
            }
            else
            {
                var end = fields.IndexOf(' ');
                end = end < 0 ? fields.Length : end;
                value = fields[..end].ToString();
                fields = fields[end..];
            }

            if (!_values.TryAdd(key, value))
                throw new InvalidDataException($"Response field '{key}' is duplicated.");
        }
    }

    public string Text(string key) => Get(key) is { Length: > 0 } value ? value : throw InvalidField(key);

    public bool Flag(string key) => Get(key) switch
    {
        "0" => false,
        "1" => true,
        _ => throw InvalidField(key),
    };

    public bool OptionalFlag(string key) => _values.ContainsKey(key) && Flag(key);

    public uint UInt32(string key) =>
        uint.TryParse(Get(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw InvalidField(key);

    public ulong UInt64(string key) =>
        ulong.TryParse(Get(key), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw InvalidField(key);

    public uint HexMask(string key)
    {
        var text = Get(key).AsSpan();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            return value;
        throw InvalidField(key);
    }

    public double? Measurement(string key) => ParseMeasurement(Get(key));

    public static double? ParseMeasurement(string text)
    {
        if (text == "nan")
            return null;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
            return value;
        throw new InvalidDataException($"Invalid measurement '{text}'.");
    }

    private string Get(string key) => _values.TryGetValue(key, out var value) ? value : throw InvalidField(key);

    private static InvalidDataException InvalidField(string key) => new($"Missing or invalid response field '{key}'.");

    private static bool IsKey(ReadOnlySpan<char> key)
    {
        if (key[0] is < 'a' or > 'z')
            return false;
        foreach (var character in key[1..])
        {
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
                return false;
        }
        return true;
    }

    private static string ReadQuoted(ref ReadOnlySpan<char> fields)
    {
        var value = new StringBuilder();
        for (var index = 1; index < fields.Length; index++)
        {
            var character = fields[index];
            if (character == '"')
            {
                fields = fields[(index + 1)..];
                if (!fields.IsEmpty && fields[0] != ' ')
                    break;
                return value.ToString();
            }
            if (character == '\\')
            {
                index++;
                if (index == fields.Length || fields[index] is not ('"' or '\\'))
                    break;
                character = fields[index];
            }
            value.Append(character);
        }
        throw new InvalidDataException("Invalid quoted response field.");
    }
}
