using System.Diagnostics;
using System.IO.Ports;

namespace Daq.Service.Protocol;

public sealed class SerialLineTransport : ILineTransport
{
    private readonly SerialPort _port;

    public SerialLineTransport(string portName)
    {
        _port = new SerialPort(portName, 115200)
        {
            ReadTimeout = 50,
            WriteTimeout = 500,
            NewLine = "\n",
        };
        try
        {
            _port.Open();
            _port.DtrEnable = true;
        }
        catch
        {
            _port.Dispose();
            throw;
        }
    }

    public static string[] GetPortNames() => SerialPort.GetPortNames();

    public void WriteLine(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _port.WriteLine(line);
    }

    public string ReadLine(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        Span<char> buffer = stackalloc char[ServiceProtocol.MaxLineLength];
        var count = 0;

        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int value;
            try
            {
                value = _port.ReadByte();
            }
            catch (TimeoutException)
            {
                continue;
            }

            if (value == '\n')
                return new string(buffer[..count]);
            if (value < 0 || count == buffer.Length || value is < 0x20 or > 0x7E)
                throw new InvalidDataException("The device sent an invalid or overlong protocol line.");

            buffer[count++] = (char)value;
        }

        throw new TimeoutException("The device did not answer within 500 ms.");
    }

    public void Dispose() => _port.Dispose();
}
