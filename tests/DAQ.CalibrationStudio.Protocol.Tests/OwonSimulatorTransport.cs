using System.Globalization;
using System.Text.RegularExpressions;
using Owon.DGE;

namespace Owon.DGE.Simulator;

/// <summary>Synthetic SCPI test double. Its replies are not measurements or hardware evidence.</summary>
public sealed class OwonSimulatorTransport : IScpiTransport
{
    private readonly object sync = new();
    private readonly Channel[] channels = [new(), new()];
    private readonly Queue<string> errors = new();
    private readonly List<string> commands = new();
    private bool open, disposed;
    public OwonSimulatorTransport(string model = "DGE2035") => Model = model;
    public string Model { get; }
    public ConnectionKind Kind => ConnectionKind.Simulation;
    public bool IsOpen => open;
    public TimeSpan Delay { get; set; }
    public bool FailNextOperation { get; set; }
    public IReadOnlyList<string> Commands { get { lock (sync) return commands.ToArray(); } }
    public void InjectDisconnect() { lock (sync) open = false; }
    public void InjectError(string text) { lock (sync) errors.Enqueue(text); }
    public Task OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); open = true; }
        return Task.CompletedTask;
    }
    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) open = false;
        return Task.CompletedTask;
    }
    public async Task WriteAsync(string command, CancellationToken cancellationToken)
    {
        await PrepareAsync(cancellationToken);
        lock (sync)
        {
            commands.Add(command);
            var m = Regex.Match(command, @"^SOURce([12]):(.+?) (.+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var c = channels[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1];
                var field = m.Groups[2].Value.ToUpperInvariant();
                var value = m.Groups[3].Value;
                switch (field)
                {
                    case "FREQUENCY:FIXED": c.Frequency = UnitNumber(value, "Hz"); return;
                    case "VOLTAGE:LEVEL:IMMEDIATE:AMPLITUDE": c.Amplitude = UnitNumber(value, "Vpp"); return;
                    case "VOLTAGE:LEVEL:IMMEDIATE:OFFSET": c.Offset = UnitNumber(value, "V"); return;
                    case "PHASE:ADJUST": c.PhaseRadians = UnitNumber(value, "DEG") * Math.PI / 180; return;
                    case "PULSE:DCYCLE": c.DutyCycle = UnitNumber(value, "PCT"); return;
                    case "FUNCTION:SHAPE": c.Shape = value.ToUpperInvariant() switch
                    {
                        "SINUSOID" => "SIN", "SQUARE" => "SQU", "PULSE" => "PUL", "RAMP" => "RAMP", "PRNOISE" => "PRN",
                        _ => throw new IOException("Synthetic unknown waveform")
                    }; return;
                    case "FREQUENCY:START": c.Start = UnitNumber(value, "Hz"); return;
                    case "FREQUENCY:STOP": c.Stop = UnitNumber(value, "Hz"); return;
                    case "SWEEP:TIME": c.SweepTime = UnitNumber(value, "s"); return;
                    case "SWEEP:SPACING": c.SweepSpacing = value; return;
                    case "SWEEP:SOURCE": c.SweepSource = value; return;
                    case "SWEEP:STATE": c.Sweep = value.Equals("ON", StringComparison.OrdinalIgnoreCase); return;
                }
            }
            m = Regex.Match(command, @"^OUTPut([12]):STATe (ON|OFF)$", RegexOptions.IgnoreCase);
            if (m.Success) { channels[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1].Output = m.Groups[2].Value.Equals("ON", StringComparison.OrdinalIgnoreCase); return; }
            m = Regex.Match(command, @"^OUTPut([12]):IMPedance ([0-9]+)OHM$", RegexOptions.IgnoreCase);
            if (m.Success) { channels[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1].LoadOhms = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); return; }
            m = Regex.Match(command, @"^OUTPut([12]):IMPedance INFinity$", RegexOptions.IgnoreCase);
            if (m.Success) { channels[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1].LoadOhms = 9.9e37; return; }
            errors.Enqueue("-102,\"Synthetic syntax error\"");
            throw new IOException("Synthetic unsupported SCPI write");
        }
    }
    public async Task<string> QueryAsync(string command, CancellationToken cancellationToken)
    {
        await PrepareAsync(cancellationToken);
        lock (sync)
        {
            commands.Add(command);
            if (command == "*IDN?") return $"OWON,{Model},SIMULATED,SCPI:99.0 FV:SIMULATED";
            if (command == "SYSTem:ERRor:NEXT?") return errors.Count > 0 ? errors.Dequeue() : "0,\"No error\"";
            var m = Regex.Match(command, @"^SOURce([12]):(.+)\?$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var c = channels[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1];
                return m.Groups[2].Value.ToUpperInvariant() switch
                {
                    "FREQUENCY:FIXED" => Format(c.Frequency), "VOLTAGE:LEVEL:IMMEDIATE:AMPLITUDE" => Format(c.Amplitude),
                    "VOLTAGE:LEVEL:IMMEDIATE:OFFSET" => Format(c.Offset), "PHASE:ADJUST" => Format(c.PhaseRadians),
                    "PULSE:DCYCLE" => Format(c.DutyCycle),
                    "FUNCTION:SHAPE" => c.Shape, "FREQUENCY:START" => Format(c.Start), "FREQUENCY:STOP" => Format(c.Stop),
                    "SWEEP:TIME" => Format(c.SweepTime), "SWEEP:SPACING" => c.SweepSpacing,
                    "SWEEP:SOURCE" => c.SweepSource, "SWEEP:STATE" => c.Sweep ? "1" : "0",
                    "AM:STATE" or "FM:STATE" or "PM:STATE" or "BURST:STATE" or "MODE:STATE" => "0",
                    _ => throw new IOException("Synthetic unsupported SCPI query")
                };
            }
            m = Regex.Match(command, @"^OUTPut([12]):STATe\?$", RegexOptions.IgnoreCase);
            if (m.Success) return channels[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1].Output ? "1" : "0";
            m = Regex.Match(command, @"^OUTPut([12]):IMPedance\?$", RegexOptions.IgnoreCase);
            if (m.Success) return Format(channels[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1].LoadOhms);
            errors.Enqueue("-102,\"Synthetic syntax error\"");
            throw new IOException("Synthetic unsupported SCPI query");
        }
    }
    private async Task PrepareAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        lock (sync)
        {
            if (!open) throw new IOException("Synthetic disconnect");
            if (FailNextOperation) { FailNextOperation = false; throw new IOException("Synthetic transport error"); }
        }
    }
    private static string Format(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static double UnitNumber(string value, string suffix)
    {
        if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            !double.TryParse(value[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new IOException("Synthetic invalid numeric value");
        return number;
    }
    public ValueTask DisposeAsync() { lock (sync) { open = false; disposed = true; } return ValueTask.CompletedTask; }
    private sealed class Channel
    {
        public double Frequency = 1000, Amplitude = 1, Offset, PhaseRadians, Start = 1000, Stop = 2000, SweepTime = 1;
        public double DutyCycle = 50, LoadOhms = 50;
        public string Shape = "SIN", SweepSpacing = "LIN", SweepSource = "TIM";
        public bool Output, Sweep;
    }
}
