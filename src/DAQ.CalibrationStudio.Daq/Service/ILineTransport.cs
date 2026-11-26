namespace Daq.Service.Protocol;

/// <summary>Exchanges complete ASCII lines without their terminators.</summary>
public interface ILineTransport : IDisposable
{
    void WriteLine(string line, CancellationToken cancellationToken);
    string ReadLine(TimeSpan timeout, CancellationToken cancellationToken);
}
