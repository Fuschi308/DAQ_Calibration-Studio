using Microsoft.Extensions.Logging;

namespace DAQ.CalibrationStudio.Application;

public sealed class Equipment
{
    private readonly Dictionary<DeviceKind, IDeviceSession> devices;
    private readonly Func<int> speed;

    public Equipment(bool simulated, IEnumerable<IDeviceSession> sessions, Func<int>? simulationSpeed = null)
    {
        Simulated = simulated;
        ScopeIsSimulated = simulated;
        devices = sessions.ToDictionary(device => device.Status.Kind);
        speed = simulationSpeed ?? (() => 1);
    }

    public bool Simulated { get; }
    public bool ScopeIsSimulated { get; private init; }
    public int Speed => Simulated ? speed() : 1;
    public IReadOnlyList<IDeviceSession> Sessions => devices.Values.ToArray();
    public IDaqDevice Daq => (IDaqDevice)devices[DeviceKind.Daq];
    public IAnalogSource Source => (IAnalogSource)devices[DeviceKind.Fnirsi];
    public IWaveformGenerator Generator => (IWaveformGenerator)devices[DeviceKind.Owon];
    public IScopeDevice Scope => (IScopeDevice)devices[DeviceKind.PeakTech];
    public IDeviceSession this[DeviceKind kind] => devices[kind];

    public bool IsDeviceSimulated(DeviceKind kind) => kind == DeviceKind.PeakTech ? ScopeIsSimulated : Simulated;

    internal Equipment WithHardwareScope(IScopeDevice scope) => new(
        Simulated, devices.Where(pair => pair.Key != DeviceKind.PeakTech).Select(pair => pair.Value).Append(scope), speed)
        { ScopeIsSimulated = false };

    public Task DelayAsync(double seconds, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromSeconds(seconds / Speed), ct);

    public void Require(DeviceKind kind, DeviceCapability capability)
    {
        var status = devices[kind].Status;
        if (!status.Connected)
            throw new InvalidOperationException($"{status.Name} ist nicht verbunden.");
        if (!status.Has(capability))
            throw new NotSupportedException($"{status.Name} unterstützt diese Funktion in diesem Betriebsmodus nicht.");
    }
}

public sealed class DeviceManager(Equipment simulated, Equipment physical, ILogger<DeviceManager> log) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim exclusive = new(1, 1);
    private readonly Equipment simulationEquipment = simulated;
    private volatile Equipment? selected;

    public Equipment Active => selected ?? simulationEquipment;

    public async Task UseAsync(Func<Equipment, CancellationToken, Task> operation, CancellationToken ct = default)
    {
        if (!await exclusive.WaitAsync(0, ct).ConfigureAwait(false))
            throw new InvalidOperationException("Die Geräte sind belegt. Warten Sie auf den laufenden Vorgang.");
        try
        {
            if (!await gate.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
                throw new TimeoutException("Laufende Geräteabfrage wurde nicht rechtzeitig beendet.");
            try
            {
                await operation(Active, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            exclusive.Release();
        }
    }

    public async Task TryPollAsync(Func<Equipment, CancellationToken, Task> poll, CancellationToken ct)
    {
        if (exclusive.CurrentCount == 0 || !await gate.WaitAsync(0, ct).ConfigureAwait(false))
            return;
        try
        {
            await poll(Active, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task SwitchAsync(bool simulation, CancellationToken ct = default) => UseAsync(async (current, token) =>
    {
        if (current.Simulated == simulation)
            return;
        var next = simulation ? simulationEquipment : physical;
        await DisconnectAllAsync(current, token).ConfigureAwait(false);
        selected = next;
        log.LogInformation("Betriebsmodus gewechselt. Simulation {Simulation}", simulation);
    }, ct);

    public Task SetSimulationScopeAsync(bool useHardware, CancellationToken ct = default) => UseAsync(async (current, token) =>
    {
        if (!current.Simulated)
            throw new InvalidOperationException("Die Oszilloskop-Auswahl steht im Simulationsbetrieb zur Verfügung.");
        if (useHardware == !current.ScopeIsSimulated)
            return;

        if (current.Scope.Status.State != DeviceState.Disconnected)
            await current.Scope.DisconnectAsync(token).ConfigureAwait(false);

        var next = useHardware ? simulationEquipment.WithHardwareScope(physical.Scope) : simulationEquipment;
        if (next.ScopeIsSimulated)
            await next.Scope.ConnectAsync("Simulation", token).ConfigureAwait(false);

        selected = next;
        log.LogInformation("Oszilloskop im Simulationsbetrieb gewechselt. Hardware {HardwareScope}", useHardware);
    }, ct);

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try
            {
                await DisconnectAllAsync(Active, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Beim Schließen ist ein oder sind mehrere Geräteausgänge unbekannt. Manuell kontrollieren.");
            }
            foreach (var session in simulationEquipment.Sessions.Concat(physical.Sessions))
            {
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Gerätefreigabe fehlgeschlagen für {Device}", session.Status.Name);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DisconnectAllAsync(Equipment equipment, CancellationToken ct)
    {
        var errors = new List<Exception>();
        foreach (var device in equipment.Sessions.OrderBy(device => device.Status.Kind is DeviceKind.Owon or DeviceKind.Fnirsi ? 0 : 1))
        {
            if (device.Status.State == DeviceState.Disconnected)
                continue;
            try
            {
                await device.DisconnectAsync(ct).ConfigureAwait(false);
                log.LogInformation("{Device} getrennt", device.Status.Name);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "{Device} konnte nicht kontrolliert getrennt werden", device.Status.Name);
                errors.Add(ex);
            }
        }
        if (errors.Count > 0)
            throw new AggregateException("Gerätefehler beim Trennen. Ausgangszustände am Prüfaufbau kontrollieren.", errors);
    }
}
