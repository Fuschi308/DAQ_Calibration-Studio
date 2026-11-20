using System.IO.Ports;

namespace Fnirsi.SG004A;

/// <summary>Explicit COM port transport. Discovery and driver installation are outside this library.</summary>
public sealed class SerialByteTransport(string portName, int baudRate = 9600) : IByteTransport
{
    private SerialPort? _port;

    public bool IsConnected => _port?.IsOpen == true;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsConnected) return;
        if (string.IsNullOrWhiteSpace(portName)) throw new ArgumentException("A COM port must be supplied.", nameof(portName));
        var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };
        // Native SerialPort.Open may block. Wait only for the transport's finite 1 s I/O deadline.
        // If the OS call finishes later, its worker disposes the abandoned port.
        Task openTask = Task.Run(port.Open);
        bool handedOff = false;
        try
        {
            await openTask.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _port, port, null) is not null)
            {
                await Task.Run(port.Dispose);
                return;
            }
            handedOff = true;
            if (cancellationToken.IsCancellationRequested)
            {
                await DisconnectAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            if (handedOff) throw;
            if (openTask.IsCompleted)
                await Task.Run(port.Dispose);
            else
                _ = openTask.ContinueWith(task =>
                {
                    _ = task.Exception;
                    try { port.Dispose(); } catch { /* abandoned native open */ }
                }, TaskScheduler.Default);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var port = Interlocked.Exchange(ref _port, null);
        if (port is not null) await Task.Run(port.Dispose);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var port = _port ?? throw new IOException("Serial port is closed.");
        using var cancellation = cancellationToken.Register(static state => ((SerialByteTransport)state!).Abort(), this);
        await port.BaseStream.WriteAsync(bytes, cancellationToken);
        await port.BaseStream.FlushAsync(cancellationToken);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var port = _port ?? throw new IOException("Serial port is closed.");
        using var cancellation = cancellationToken.Register(static state => ((SerialByteTransport)state!).Abort(), this);
        return await port.BaseStream.ReadAsync(buffer, cancellationToken);
    }

    private void Abort()
    {
        var port = Interlocked.Exchange(ref _port, null);
        try { port?.Dispose(); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            // Unplugging the device may make the cancellation callback's close fail.
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
