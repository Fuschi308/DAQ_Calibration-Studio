using DAQ.CalibrationStudio.Application;

namespace DAQ.CalibrationStudio.Instruments;

public abstract class InstrumentSession(DeviceKind kind, string name, string connection, string verification)
    : IDeviceSession
{
    private readonly SemaphoreSlim _session = new(1, 1);
    private bool _disposed;

    public DeviceStatus Status { get; private set; } = new(kind, name, connection, DeviceState.Disconnected,
        DeviceCapability.None, verification);

    protected abstract bool IsOpen { get; }
    protected abstract DeviceCapability ConnectedCapabilities { get; }

    public abstract Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct);

    public async Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        await _session.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsOpen) throw new InvalidOperationException("Gerät zuerst trennen.");
            Status = Status with { State = DeviceState.Connecting, Error = null };
            try
            {
                await OpenAsync(endpoint.Trim(), ct);
                var identity = await IdentifyCoreAsync(ct);
                Status = Status with
                {
                    Identity = identity,
                    Capabilities = ConnectedCapabilities,
                    State = DeviceState.Connected
                };
            }
            catch (Exception error)
            {
                await ReportFaultAsync(error);
                throw;
            }
        }
        finally
        {
            _session.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await _session.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                await CloseAsync();
                Status = Status with
                {
                    Identity = null, Capabilities = DeviceCapability.None, State = DeviceState.Disconnected, Error = null
                };
            }
            catch (Exception error)
            {
                await ReportFaultAsync(error);
                throw;
            }
        }
        finally
        {
            _session.Release();
        }
    }

    public Task<DeviceIdentity> IdentifyAsync(CancellationToken ct) => ExecuteAsync(async token =>
    {
        var identity = await IdentifyCoreAsync(token);
        Status = Status with { Identity = identity };
        return identity;
    }, ct);

    protected abstract Task OpenAsync(string endpoint, CancellationToken ct);
    protected abstract Task<DeviceIdentity> IdentifyCoreAsync(CancellationToken ct);
    protected abstract Task CloseAsync();

    protected Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct) =>
        ExecuteAsync(async token =>
        {
            await action(token);
            return true;
        }, ct);

    protected async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _session.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsOpen) throw new InvalidOperationException("Das Gerät ist getrennt.");
            try
            {
                return await action(ct);
            }
            catch (Exception error)
            {
                if (!IsOpen || IsConnectionFault(error))
                    await ReportFaultAsync(error);
                throw;
            }
        }
        finally
        {
            _session.Release();
        }
    }

    protected virtual bool IsConnectionFault(Exception error) => error is IOException or InvalidDataException or TimeoutException;

    private async Task ReportFaultAsync(Exception error)
    {
        string message = error.Message;
        try
        {
            await CloseAsync();
        }
        catch (Exception closeError)
        {
            message += $" Trennen fehlgeschlagen. {closeError.Message}";
        }
        Status = Status with
        {
            Identity = null, Capabilities = DeviceCapability.None, State = DeviceState.Faulted, Error = message
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _session.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await CloseAsync();
            Status = Status with
            {
                Identity = null, Capabilities = DeviceCapability.None, State = DeviceState.Disconnected
            };
        }
        finally
        {
            _session.Release();
        }
    }
}
