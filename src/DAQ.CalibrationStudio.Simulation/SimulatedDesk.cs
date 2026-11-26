using System.Diagnostics;
using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.Simulation;

public sealed class SimulatedDesk
{
    private readonly Stopwatch wall = Stopwatch.StartNew();
    private readonly DateTimeOffset origin = DateTimeOffset.UtcNow;
    private readonly Lock clockLock = new();
    private double time;
    private double lastWall;
    private int speed = 1;

    public SimulatedDesk(string? coefficientPath = null)
    {
        Source = new SimulatedSource(this);
        Generator = new SimulatedGenerator(this);
        Daq = new SimulatedDaq(this, coefficientPath);
        Scope = new SimulatedScope(this);
        Sessions = [Daq, Source, Generator, Scope];
        Equipment = new Equipment(true, Sessions, () => Speed);
    }

    public SimulatedDaq Daq { get; }
    public SimulatedSource Source { get; }
    public SimulatedGenerator Generator { get; }
    public SimulatedScope Scope { get; }
    public Equipment Equipment { get; }
    public IReadOnlyList<SimulatedSession> Sessions { get; }
    public DateTimeOffset Timestamp => origin.AddSeconds(Seconds);

    public int Speed
    {
        get { lock (clockLock) return speed; }
        set
        {
            if (value is not (1 or 2 or 5 or 10))
                throw new ArgumentOutOfRangeException(nameof(value));
            lock (clockLock)
            {
                AdvanceClock();
                speed = value;
            }
        }
    }

    public double Seconds
    {
        get
        {
            lock (clockLock)
            {
                AdvanceClock();
                return time;
            }
        }
    }

    public void InjectFault(DeviceKind kind) => Sessions.Single(session => session.Status.Kind == kind).InjectFault();

    private void AdvanceClock()
    {
        var now = wall.Elapsed.TotalSeconds;
        time += (now - lastWall) * speed;
        lastWall = now;
    }
}

public abstract class SimulatedSession(
    DeviceKind kind, string name, string transport,
    DeviceCapability capabilities, DeviceIdentity identity) : IDeviceSession
{
    private readonly Lock stateLock = new();
    private DeviceState state;
    private string? error;

    public DeviceStatus Status
    {
        get
        {
            lock (stateLock)
                return new(kind, name, transport, state, capabilities, "Simulation", state == DeviceState.Connected ? identity : null, error);
        }
    }

    public Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(["Simulation"]);
    }

    public async Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        if (Status.Connected)
            return;
        lock (stateLock)
        {
            state = DeviceState.Connecting;
            error = null;
        }
        try
        {
            await Task.Delay(160, ct).ConfigureAwait(false);
            ResetOutputs();
            lock (stateLock) state = DeviceState.Connected;
        }
        catch
        {
            lock (stateLock) state = DeviceState.Disconnected;
            throw;
        }
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ResetOutputs();
        lock (stateLock)
        {
            state = DeviceState.Disconnected;
            error = null;
        }
        return Task.CompletedTask;
    }

    public Task<DeviceIdentity> IdentifyAsync(CancellationToken ct) => Ready(ct, identity);

    public void InjectFault()
    {
        lock (stateLock)
        {
            if (state != DeviceState.Connected)
                return;
            state = DeviceState.Faulted;
            error = "Simulierter Verbindungsverlust.";
        }
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync(CancellationToken.None));

    protected virtual void ResetOutputs() { }

    protected void Check(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Status.Connected)
            throw new IOException($"{name}: {Status.Error ?? "keine Verbindung"}");
    }

    protected Task<T> Ready<T>(CancellationToken ct, T result)
    {
        Check(ct);
        return Task.FromResult(result);
    }
}
