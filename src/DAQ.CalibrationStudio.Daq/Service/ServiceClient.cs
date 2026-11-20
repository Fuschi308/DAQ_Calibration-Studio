using System.Diagnostics;
using System.Globalization;

namespace Daq.Service.Protocol;

public sealed class ServiceCommandException(uint code, string message) : IOException($"ERR {code}: {message}")
{
    public uint Code { get; } = code;
}

/// <summary>Owns one connection. A failed exchange closes it because replies have no request IDs.</summary>
public sealed class ServiceClient(ILineTransport transport) : IDisposable
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMilliseconds(500);
    private readonly object _gate = new();
    private bool _closed;

    public ProtocolFields Query(string command, CancellationToken cancellationToken = default)
    {
        if (command.Length == 0 || !IsValidLine(command))
            throw new ArgumentException("Invalid protocol command.", nameof(command));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                transport.WriteLine(command, cancellationToken);
                var started = Stopwatch.GetTimestamp();
                var remaining = ResponseTimeout;
                while (remaining > TimeSpan.Zero)
                {
                    var line = transport.ReadLine(remaining, cancellationToken);
                    remaining = ResponseTimeout - Stopwatch.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero)
                        break;
                    if (!IsValidLine(line))
                        throw new InvalidDataException("The device sent an invalid protocol line.");
                    if (line.StartsWith('!'))
                        continue;
                    if (line == "OK")
                        return new ProtocolFields([]);
                    if (line.StartsWith("OK ", StringComparison.Ordinal))
                        return new ProtocolFields(line.AsSpan(3));
                    if (line.StartsWith("ERR ", StringComparison.Ordinal))
                        throw ParseError(line);
                    throw new InvalidDataException("The device did not send an OK or ERR response.");
                }
                throw new TimeoutException("The device did not answer within 500 ms.");
            }
            catch (Exception exception) when (exception is not ServiceCommandException)
            {
                try
                {
                    Close();
                }
                catch (IOException)
                {
                    // A removed serial device can also fail during close.
                }
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
            Close();
    }

    private void Close()
    {
        if (_closed)
            return;
        _closed = true;
        transport.Dispose();
    }

    private static Exception ParseError(string line)
    {
        var separator = line.IndexOf(' ', 4);
        if (separator < 0 || separator == line.Length - 1 ||
            !uint.TryParse(line.AsSpan(4, separator - 4), NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            return new InvalidDataException("The device sent an invalid ERR response.");
        return new ServiceCommandException(code, line[(separator + 1)..]);
    }

    private static bool IsValidLine(string line) =>
        line.Length <= ServiceProtocol.MaxLineLength && line.All(character => character is >= ' ' and <= '~');
}
