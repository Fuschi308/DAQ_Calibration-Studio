using System.Globalization;
using System.Text;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.Infrastructure.Persistence;

/// <summary>Writes sampled DAQ frames as a separate, append-only CSV file.</summary>
public sealed class CsvMeasurementWriter : IAsyncDisposable
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-AT");
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    private CsvMeasurementWriter(StreamWriter writer)
    {
        _writer = writer;
    }

    public static async Task<CsvMeasurementWriter> CreateAsync(string path, bool simulated, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var recorder = new CsvMeasurementWriter(writer);
        await writer.WriteLineAsync($"# DAQ Calibration Studio; Herkunft={(simulated ? "Simulation – keine reale DAQ-Messung" : "DAQ-Messlauf")}".AsMemory(), ct).ConfigureAwait(false);
        await writer.WriteLineAsync("Sequenz;Zeitstempel;Kanal;Rohwert;Skalierter Wert;Korrigierter Wert;Minimum;Maximum;Sollwert;Standardabweichung;Stichproben".AsMemory(), ct).ConfigureAwait(false);
        await writer.FlushAsync(ct).ConfigureAwait(false);
        return recorder;
    }

    public async Task WriteFrameAsync(DaqFrame frame, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var sample in frame.Samples)
            {
                var values = new[]
                {
                    frame.Sequence.ToString(German),
                    frame.Time.ToLocalTime().ToString("O", German),
                    sample.ChannelId,
                    sample.Raw?.ToString(German) ?? "",
                    Number(sample.Uncorrected), Number(sample.Corrected), Number(sample.Min), Number(sample.Max),
                    Number(sample.Setpoint), Number(sample.StdDev), sample.Count.ToString(German)
                };
                await _writer.WriteLineAsync(string.Join(';', values.Select(Quote)).AsMemory(), ct).ConfigureAwait(false);
            }
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
            _writeLock.Dispose();
        }
    }

    private static string Number(double? value) => value?.ToString("0.########", German) ?? "";
    private static string Quote(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}
