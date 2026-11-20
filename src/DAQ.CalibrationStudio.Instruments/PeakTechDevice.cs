using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DAQ.CalibrationStudio.Application;

namespace DAQ.CalibrationStudio.Instruments;

public sealed class PeakTechDevice()
    : InstrumentSession(DeviceKind.PeakTech, "PeakTech P 1400", "USB / libusb-win32",
        "SCREEN CH1/CH2 über Studio an Firmware V1.2.1 geprüft"),
        IScopeDevice
{
    private Process? _process;
    private Task<string>? _stderr;
    private FileStream? _exclusiveUse;
    private DeviceIdentity? _initialIdentity;

    protected override bool IsOpen => _process is { HasExited: false };
    protected override DeviceCapability ConnectedCapabilities => DeviceCapability.Diagnostics | DeviceCapability.Scope;

    public override async Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct)
    {
        using var process = StartBridge(await FindPythonAsync(ct), "discover");
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            return await ReplyAsync<string[]>(process, stderr, ct);
        }
        finally
        {
            await StopBridgeAsync(process);
            await stderr;
        }
    }

    protected override async Task OpenAsync(string endpoint, CancellationToken ct)
    {
        if (endpoint.Length > 128 || endpoint.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("USB-Seriennummer des P 1400 eingeben.", nameof(endpoint));
        var python = await FindPythonAsync(ct);
        var lockPath = Path.Combine(Path.GetTempPath(), "DAQ.CalibrationStudio.PeakTech.lock");
        try
        {
            _exclusiveUse = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException("Das P 1400 wird bereits von einer Studio-Sitzung verwendet.", error);
        }
        _process = StartBridge(python, "session", endpoint);
        _stderr = _process.StandardError.ReadToEndAsync();
        _initialIdentity = await ReplyAsync<DeviceIdentity>(_process, _stderr, ct);
    }

    protected override Task<DeviceIdentity> IdentifyCoreAsync(CancellationToken ct)
    {
        if (_initialIdentity is null)
            return RequestAsync<DeviceIdentity>("identify", null, ct);
        var identity = _initialIdentity;
        _initialIdentity = null;
        return Task.FromResult(identity);
    }

    public Task<ScopeSettings> ReadScopeAsync(CancellationToken ct) =>
        ExecuteAsync(token => RequestAsync<ScopeSettings>("settings", null, token), ct);

    public Task ConfigureScopeAsync(ScopeSettings settings, CancellationToken ct) =>
        ExecuteAsync(token => RequestAsync<JsonElement>("configure", settings, token), ct);

    public Task ControlScopeAsync(ScopeAction action, CancellationToken ct) =>
        ExecuteAsync(token => RequestAsync<JsonElement>("control", action.ToString(), token), ct);

    public Task<ScopeCapture> CaptureAsync(CancellationToken ct) => ExecuteAsync(async token =>
    {
        var traces = await RequestAsync<ScopeTrace[]>("capture", null, token);
        return new ScopeCapture(DateTimeOffset.UtcNow, traces);
    }, ct);

    private async Task<T> RequestAsync<T>(string operation, object? argument, CancellationToken ct)
    {
        var process = _process ?? throw new IOException("P 1400 ist getrennt.");
        try
        {
            var message = JsonSerializer.Serialize(new { operation, argument });
            await process.StandardInput.WriteLineAsync(message.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            return await ReplyAsync<T>(process, _stderr!, ct);
        }
        catch (OperationCanceledException)
        {
            await CloseAsync();
            throw;
        }
    }

    private static async Task<T> ReplyAsync<T>(Process process, Task<string> stderr, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null)
                throw new IOException($"PeakTech-Hilfsprozess beendet. {(await stderr.WaitAsync(timeout.Token)).Trim()}");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var error = root.GetProperty("error");
            if (error.ValueKind != JsonValueKind.Null)
            {
                var message = error.GetProperty("message").GetString();
                if (error.GetProperty("fatal").GetBoolean()) throw new IOException(message);
                throw new InvalidOperationException(message);
            }
            return root.GetProperty("result").Deserialize<T>()!;
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Der PeakTech-Hilfsprozess hat nicht rechtzeitig geantwortet.", error);
        }
        catch (JsonException error)
        {
            throw new IOException("Ungültige Antwort aus dem PeakTech-Hilfsprozess.", error);
        }
    }

    private static Process StartBridge(string python, params string[] args)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "PeakTech", "studio_bridge.py");
        if (!File.Exists(script)) throw new FileNotFoundException("Die PeakTech-Brücke fehlt im Anwendungsordner.", script);
        var info = new ProcessStartInfo(python)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var parameter in new[] { "-E", "-s", "-B", "-u", "-X", "utf8", script }.Concat(args))
            info.ArgumentList.Add(parameter);
        return Process.Start(info) ?? throw new IOException("Der PeakTech-Hilfsprozess konnte nicht gestartet werden.");
    }

    private static async Task<string> FindPythonAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("P 1400 benötigt Windows mit libusb-win32.");
        var configured = Environment.GetEnvironmentVariable("DAQ_CALIBRATION_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured) || !File.Exists(configured))
                throw new FileNotFoundException("DAQ_CALIBRATION_PYTHON muss auf eine installierte python.exe zeigen.", configured);
            return configured;
        }
        string? launcher = null;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!Path.IsPathFullyQualified(folder)) continue;
            var candidate = Path.Combine(folder, "python.exe");
            if (!folder.EndsWith("WindowsApps", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                return candidate;
            var py = Path.Combine(folder, "py.exe");
            if (launcher is null && File.Exists(py)) launcher = py;
        }
        if (launcher is not null)
        {
            // -0p only lists installed interpreters. Starting the found executable avoids launcher auto-install behavior.
            var info = new ProcessStartInfo(launcher, "-0p")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var process = Process.Start(info);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEndAsync(ct);
                var errors = process.StandardError.ReadToEndAsync(ct);
                try
                {
                    await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
                    var lines = (await output).Split('\n');
                    _ = await errors;
                    foreach (var line in lines)
                    {
                        var start = line.IndexOf(":\\", StringComparison.Ordinal);
                        if (start < 1) continue;
                        var candidate = line[(start - 1)..].Trim();
                        if (File.Exists(candidate) && candidate.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase))
                            return candidate;
                    }
                }
                finally
                {
                    await StopBridgeAsync(process);
                }
            }
        }
        throw new FileNotFoundException("P 1400 benötigt eine vorhandene Python-3-Installation. DAQ_CALIBRATION_PYTHON kann den Interpreter angeben.");
    }

    protected override async Task CloseAsync()
    {
        var process = _process;
        var stderr = _stderr;
        _process = null;
        _stderr = null;
        _initialIdentity = null;
        try
        {
            if (process is not null)
            {
                using (process)
                {
                    await StopBridgeAsync(process);
                    if (stderr is not null) await stderr;
                }
            }
        }
        finally
        {
            _exclusiveUse?.Dispose();
            _exclusiveUse = null;
        }
    }

    private static async Task StopBridgeAsync(Process process)
    {
        if (process.HasExited) return;
        try
        {
            if (process.StartInfo.RedirectStandardInput) process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            try { process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }
}
