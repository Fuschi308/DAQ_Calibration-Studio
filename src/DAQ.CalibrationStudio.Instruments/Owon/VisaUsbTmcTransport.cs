using System.Runtime.InteropServices;
using System.Text;

namespace Owon.DGE;

/// <summary>
/// Windows VISA C binding for an explicitly supplied USB INSTR resource.
/// Requires an existing, compatible VISA runtime and USBTMC device driver.
/// Synchronous native I/O runs on a worker thread and is bounded by the VISA timeout.
/// </summary>
public sealed class VisaUsbTmcTransport : IScpiTransport
{
    private const uint TimeoutAttribute = 0x3FFF001A; // VI_ATTR_TMO_VALUE
    private const uint ExclusiveLock = 1;
    private const int ErrorResourceNotFound = unchecked((int)0xBFFF0011);
    private const int SuccessMaxCount = 0x3FFF0006;
    private const int SuccessDeviceNotPresent = 0x3FFF007D;
    private readonly string resource;
    private readonly uint ioTimeoutMs;
    private readonly SemaphoreSlim gate = new(1, 1);
    private uint rm, session;
    private bool disposed;

    public VisaUsbTmcTransport(string resource, TimeSpan? ioTimeout = null)
    {
        if (string.IsNullOrWhiteSpace(resource) || !resource.StartsWith("USB", StringComparison.OrdinalIgnoreCase)
            || !resource.EndsWith("::INSTR", StringComparison.OrdinalIgnoreCase)
            || resource.Any(char.IsControl))
            throw new ArgumentException("Ein expliziter VISA USB...::INSTR-Ressourcenname ist erforderlich.", nameof(resource));
        this.resource = resource;
        var timeout = ioTimeout ?? TimeSpan.FromSeconds(2);
        if (timeout < TimeSpan.FromMilliseconds(1) || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(ioTimeout));
        ioTimeoutMs = checked((uint)Math.Ceiling(timeout.TotalMilliseconds));
    }

    public ConnectionKind Kind => ConnectionKind.ExternalScpi;
    public TimeSpan IoTimeout => TimeSpan.FromMilliseconds(ioTimeoutMs);
    public bool IsOpen => session != 0;
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (IsOpen) return;
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    OpenResourceManager(out rm);
                    var openStatus = Visa.viOpen(rm, resource, ExclusiveLock, ioTimeoutMs, out session);
                    Check(openStatus, "viOpen");
                    if (openStatus == SuccessDeviceNotPresent) throw new IOException("VISA-Ressource geöffnet, Gerät antwortet aber nicht.");
                    Check(Visa.viSetAttribute(session, TimeoutAttribute, ioTimeoutMs), "viSetAttribute timeout");
                }
                catch { CloseNative(); throw; }
            });
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally { gate.Release(); }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await Task.Run(CloseNative); }
        finally { gate.Release(); }
    }

    public async Task WriteAsync(string command, CancellationToken cancellationToken)
    {
        ValidateCommand(command, false);
        await gate.WaitAsync(cancellationToken);
        try { await Task.Run(() => WriteNative(command, cancellationToken)); cancellationToken.ThrowIfCancellationRequested(); }
        finally { gate.Release(); }
    }

    public async Task<string> QueryAsync(string command, CancellationToken cancellationToken)
    {
        ValidateCommand(command, true);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var answer = await Task.Run(() =>
            {
                WriteNative(command, cancellationToken);
                using var result = new MemoryStream();
                var buffer = new byte[4096];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureOpen();
                    var status = Visa.viRead(session, buffer, (uint)buffer.Length, out var count);
                    Check(status, "viRead");
                    if (count > buffer.Length) throw new IOException("VISA meldete eine ungültige Antwortlänge.");
                    result.Write(buffer, 0, (int)count);
                    if (result.Length > 16384) throw new IOException("SCPI-Textantwort überschreitet 16 KiB.");
                    if (status != SuccessMaxCount) break;
                }
                return Encoding.ASCII.GetString(result.ToArray()).TrimEnd('\r', '\n');
            });
            cancellationToken.ThrowIfCancellationRequested();
            return answer;
        }
        finally { gate.Release(); }
    }

    private void WriteNative(string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureOpen();
        // USB INSTR carries message end through VISA. LF is sent as a conservative SCPI delimiter.
        var bytes = Encoding.ASCII.GetBytes(command + "\n");
        Check(Visa.viWrite(session, bytes, (uint)bytes.Length, out var written), "viWrite");
        if (written != bytes.Length) throw new IOException("VISA hat den SCPI-Befehl nur teilweise geschrieben.");
    }
    public static Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct) => Task.Run<IReadOnlyList<string>>(() =>
    {
        uint manager = 0;
        uint search = 0;
        try
        {
            OpenResourceManager(out manager);
            var name = new StringBuilder(256);
            var status = Visa.viFindRsrc(manager, "USB?*INSTR", out search, out var count, name);
            if (status == ErrorResourceNotFound) return [];
            Check(status, "viFindRsrc");
            var resources = new List<string>();
            for (uint index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (index > 0)
                {
                    name.Clear();
                    Check(Visa.viFindNext(search, name), "viFindNext");
                }
                resources.Add(name.ToString());
            }
            return resources;
        }
        finally
        {
            if (search != 0) Visa.viClose(search);
            if (manager != 0) Visa.viClose(manager);
        }
    }, ct);

    private static void OpenResourceManager(out uint manager)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("USB-VISA benötigt einen passenden Windows-Laufzeittreiber.");
        try
        {
            Check(Visa.viOpenDefaultRM(out manager), "viOpenDefaultRM");
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new IOException("Keine kompatible VISA-Laufzeit gefunden. OWON benötigt eine zur Prozessarchitektur passende installierte VISA-Laufzeit.", error);
        }
    }

    private void EnsureOpen()
    {
        if (!IsOpen) throw new IOException("VISA-Sitzung ist nicht geöffnet.");
    }
    private void CloseNative()
    {
        var firstError = 0;
        if (session != 0) { firstError = Visa.viClose(session); session = 0; }
        if (rm != 0)
        {
            var status = Visa.viClose(rm); rm = 0;
            if (firstError >= 0) firstError = status;
        }
        Check(firstError, "viClose");
    }
    private static void Check(int status, string operation)
    {
        if (status < 0) throw new IOException($"{operation} fehlgeschlagen. VISA-Status 0x{status:X8}.");
    }
    private static void ValidateCommand(string command, bool query)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Any(c => c is '\r' or '\n' or '\0' || c > 127))
            throw new ArgumentException("SCPI-Befehl muss einzeiliges ASCII sein.", nameof(command));
        if (query && !command.EndsWith('?')) throw new ArgumentException("Abfrage muss mit ? enden.", nameof(command));
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await CloseAsync(CancellationToken.None);
        disposed = true;
        gate.Dispose();
    }

    private static class Visa
    {
        private const string Library = "DAQ_CALIBRATION_VISA";

        static Visa()
        {
            NativeLibrary.SetDllImportResolver(typeof(VisaUsbTmcTransport).Assembly, (name, assembly, _) =>
            {
                if (name != Library || !OperatingSystem.IsWindows()) return IntPtr.Zero;
                var candidates = Environment.Is64BitProcess ? new[] { "visa64.dll", "visa32.dll" } : new[] { "visa32.dll" };
                foreach (var candidate in candidates)
                    if (NativeLibrary.TryLoad(candidate, assembly, DllImportSearchPath.System32, out var handle))
                        return handle;
                return IntPtr.Zero;
            });
        }

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Library, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int viFindRsrc(uint session, string expression, out uint search, out uint count, StringBuilder first);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Library, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int viFindNext(uint search, StringBuilder name);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int viOpenDefaultRM(out uint session);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Library, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int viOpen(uint rm, [MarshalAs(UnmanagedType.LPStr)] string resource, uint accessMode, uint openTimeout, out uint session);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int viSetAttribute(uint session, uint attribute, uint value);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int viWrite(uint session, byte[] buffer, uint count, out uint written);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int viRead(uint session, byte[] buffer, uint count, out uint read);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
        internal static extern int viClose(uint session);
    }
}
