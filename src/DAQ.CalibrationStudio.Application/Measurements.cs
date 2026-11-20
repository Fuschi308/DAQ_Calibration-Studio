using DAQ.CalibrationStudio.Domain;
using Microsoft.Extensions.Logging;

namespace DAQ.CalibrationStudio.Application;

public readonly record struct MeasurementTick(DaqFrame Frame, bool Simulated);

public sealed class MeasurementHistory(ILogger<MeasurementHistory> log)
{
    private const int Capacity = 3_200;
    private readonly Lock sync = new();
    private readonly Queue<MeasurementTick> frames = new(Capacity);
    private MeasurementTick? latest;

    public event Action<MeasurementTick>? Published;

    public MeasurementTick? Latest
    {
        get
        {
            lock (sync) return latest;
        }
    }

    public MeasurementTick[] Snapshot(DateTimeOffset since)
    {
        lock (sync)
            return frames.Where(tick => tick.Frame.Time >= since).ToArray();
    }

    public void Clear()
    {
        lock (sync)
        {
            latest = null;
            frames.Clear();
        }
    }

    public void Publish(DaqFrame frame, bool simulated)
    {
        var next = new MeasurementTick(frame, simulated);
        lock (sync)
        {
            if (latest is { } previous && (previous.Simulated != simulated || frame.Sequence <= previous.Frame.Sequence))
            {
                frames.Clear();
            }
            else if (latest is { } last && (frame.Sequence > last.Frame.Sequence + 1 || frame.Time < last.Frame.Time))
            {
                log.LogWarning("DAQ-Blocklücke oder Zeitrücksprung. Vorher {Previous}, jetzt {Current}", last.Frame.Sequence, frame.Sequence);
            }
            latest = next;
            if (frames.Count == Capacity)
                frames.Dequeue();
            frames.Enqueue(next);
        }

        foreach (var handler in Published?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<MeasurementTick>)handler)(next);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Fehler beim Verteilen eines DAQ-Messblocks");
            }
        }
    }
}

public sealed class MeasurementMonitor(DeviceManager manager, MeasurementHistory history, ILogger<MeasurementMonitor> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        string? lastError = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await manager.TryPollAsync(async (equipment, token) =>
                {
                    if (!equipment.Daq.Status.Connected || !equipment.Daq.Status.Has(DeviceCapability.Measurements))
                    {
                        if (history.Latest.HasValue)
                            history.Clear();
                        return;
                    }
                    var frame = await equipment.Daq.ReadFrameAsync(token).ConfigureAwait(false);
                    history.Publish(frame, equipment.Simulated);
                    lastError = null;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (ex.Message != lastError)
                    log.LogError(ex, "DAQ-Messdatenerfassung fehlgeschlagen");
                lastError = ex.Message;
            }
        }
    }
}
