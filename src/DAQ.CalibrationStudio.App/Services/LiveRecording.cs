using System.Threading.Channels;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace DAQ.CalibrationStudio.App.Services;

public sealed class LiveRecording(MeasurementHistory history, ILogger<LiveRecording> log) : IAsyncDisposable
{
    private Recording? recording;
    public bool Active => Volatile.Read(ref recording) is { Running.IsCompleted: false };
    public string? LastPath { get; private set; }

    public void Start(string path, bool simulated)
    {
        if (recording is not null)
            throw new InvalidOperationException("Beenden Sie die vorherige Aufzeichnung.");
        var queue = Channel.CreateBounded<MeasurementTick>(new BoundedChannelOptions(512) { SingleReader = true });
        var current = new Recording(queue, DrainAsync(), OnTick);
        recording = current;
        LastPath = path;
        history.Published += current.OnTick;
        log.LogInformation("Messaufzeichnung gestartet. Datei {Path}, Simulation {Simulation}", path, simulated);

        void OnTick(MeasurementTick tick)
        {
            if (tick.Simulated != simulated || !queue.Writer.TryWrite(tick))
                queue.Writer.TryComplete(new IOException("Die Aufzeichnung wurde bei Datenrückstau oder Moduswechsel beendet."));
        }

        async Task DrainAsync()
        {
            try
            {
                await using var writer = await CsvMeasurementWriter.CreateAsync(path, simulated).ConfigureAwait(false);
                await foreach (var tick in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    await writer.WriteFrameAsync(tick.Frame).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                queue.Writer.TryComplete();
                log.LogError(ex, "Messaufzeichnung fehlgeschlagen. Datei {Path}", path);
                throw;
            }
        }
    }

    public async Task StopAsync()
    {
        var current = Interlocked.Exchange(ref recording, null);
        if (current is null)
            return;
        history.Published -= current.OnTick;
        current.Queue.Writer.TryComplete();
        await current.Running.ConfigureAwait(false);
        log.LogInformation("Messaufzeichnung abgeschlossen. Datei {Path}", LastPath);
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception) { /* DrainAsync already logged the write error. */ }
    }

    private sealed record Recording(Channel<MeasurementTick> Queue, Task Running, Action<MeasurementTick> OnTick);
}
