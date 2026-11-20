using DAQ.CalibrationStudio.Domain;
using Microsoft.Extensions.Logging;

namespace DAQ.CalibrationStudio.Application;

public enum EngineState { Idle, Preparing, ConfiguringInstrument, Settling, Acquiring, Evaluating, Paused, Aborting, Completed, Failed, Aborted }

public sealed record RunProgress(EngineState State, string Profile, int Step, int Total, string Message, IReadOnlyList<int>? SkippedSteps = null)
{
    public double Percent => Total == 0 ? 0 : Math.Clamp((double)Step / Total * 100, 0, 100);
}

public sealed record RunRequest(TestProfile Profile, string Operator, string Conditions, IReadOnlyList<ReferenceInfo> References);

public sealed class CalibrationEngine(
    DeviceManager devices, MeasurementHistory history, IStudioStore store, ILogger<CalibrationEngine> log)
{
    private readonly Lock control = new();
    private CancellationTokenSource? running;
    private bool pause;
    private bool skip;
    private volatile RunProgress progress = new(EngineState.Idle, "", 0, 0, "Bereit");

    public RunProgress Progress => progress;
    public bool IsRunning
    {
        get { lock (control) return running is not null; }
    }

    public async Task<RunResult> RunAsync(RunRequest request, CancellationToken ct = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (control)
        {
            if (running is not null)
                throw new InvalidOperationException("Ein Prüfablauf läuft bereits.");
            running = cancellation;
            pause = skip = false;
            progress = new(EngineState.Preparing, request.Profile.Name, 0, request.Profile.Steps.Count,
                "Warte auf exklusiven Gerätezugriff.");
        }

        try
        {
            RunResult? result = null;
            var profile = request.Profile with { Steps = request.Profile.Steps.ToArray() };
            await devices.UseAsync(async (equipment, token) =>
            {
                Validate(profile, equipment);
                result = await ExecuteAsync(request with { Profile = profile }, equipment, token).ConfigureAwait(false);
            }, cancellation.Token).ConfigureAwait(false);
            return result!;
        }
        catch (Exception ex)
        {
            if (progress.State is not (EngineState.Aborted or EngineState.Failed) || ex is not OperationCanceledException)
                progress = new(EngineState.Failed, request.Profile.Name, 0, request.Profile.Steps.Count, ex.Message);
            log.LogError(ex, "Prüflauf konnte nicht abgeschlossen werden");
            throw;
        }
        finally
        {
            lock (control)
            {
                running = null;
                pause = skip = false;
            }
        }
    }

    public void Pause()
    {
        lock (control)
        {
            if (running is not null && progress.State is EngineState.ConfiguringInstrument or EngineState.Settling or EngineState.Acquiring)
                pause = true;
        }
    }

    public void Resume()
    {
        lock (control) pause = false;
    }

    public void Skip()
    {
        lock (control)
        {
            if (running is not null && progress.State is EngineState.ConfiguringInstrument or EngineState.Settling or EngineState.Acquiring or EngineState.Paused)
            {
                skip = true;
                pause = false;
            }
        }
    }

    public void Cancel()
    {
        lock (control)
        {
            if (running is not null && progress.State is not (EngineState.Completed or EngineState.Failed or EngineState.Aborted))
            {
                progress = progress with { State = EngineState.Aborting, Message = "Ausgänge werden kontrolliert abgeschaltet." };
                running.Cancel();
            }
        }
    }

    private async Task<RunResult> ExecuteAsync(RunRequest request, Equipment equipment, CancellationToken ct)
    {
        var profile = request.Profile;
        var id = Guid.NewGuid();
        var started = DateTimeOffset.Now;
        var referenceKinds = profile.Steps.Select(step => step.Kind).ToHashSet();
        var status = equipment.Daq.Status.Identity ?? new DeviceIdentity(equipment.Daq.Status.Name);
        var identity = new DaqIdentity(status.Model, status.Serial, status.Firmware, status.Revision);
        var references = ReferencesFor(request, equipment, referenceKinds);
        var points = new List<PointResult>();
        var adjustments = new List<Adjustment>();
        var coefficients = new Dictionary<string, Coefficient>();
        var skipped = new List<int>();
        var current = 0;
        var outcome = RunOutcome.Passed;
        string? failure = null;
        var outputs = new Stimuli(equipment, referenceKinds);
        Report(EngineState.Preparing, "Messgeräte und Prüfgrenzen geprüft.");
        log.LogInformation("Prüflauf {RunId} gestartet. Profil {Profile} Simulation {Simulation}", id, profile.Name, equipment.Simulated);

        try
        {
            await outputs.StopAsync().ConfigureAwait(false);
            foreach (var step in profile.Steps)
            {
                try
                {
                    await ExecuteStepWithPauseAsync(step).ConfigureAwait(false);
                }
                catch (SkipStep)
                {
                    skipped.Add(current);
                    log.LogWarning("Prüflauf {RunId} Schritt {Step} übersprungen", id, current + 1);
                    await outputs.StopAsync().ConfigureAwait(false);
                }
                current++;
            }

            ct.ThrowIfCancellationRequested();
            Report(EngineState.Evaluating, "Messpunkte werden ausgewertet.");
            if (skipped.Count == 0 && profile.Kind == RunKind.Calibration)
            {
                foreach (var group in points.Where(point => coefficients.ContainsKey(point.ChannelId)).GroupBy(point => point.ChannelId))
                {
                    var adjustment = CalibrationMath.Fit(group.Key, group.ToArray(), profile.Method, coefficients[group.Key]);
                    if (adjustment is not null)
                        adjustments.Add(adjustment);
                }
            }
            if (skipped.Count > 0 || points.Any(point => !point.Passed))
            {
                outcome = RunOutcome.Failed;
                failure = skipped.Count > 0 ? "Ein oder mehrere Prüfpunkte wurden übersprungen." : "Mindestens ein As-found-Messpunkt liegt außerhalb der Toleranz.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = RunOutcome.Aborted;
            failure = "Durch Bediener abgebrochen.";
            adjustments.Clear();
        }
        catch (Exception ex)
        {
            outcome = RunOutcome.Error;
            failure = ex.Message;
            adjustments.Clear();
            log.LogError(ex, "Prüflauf {RunId} bei Schritt {Step} fehlgeschlagen", id, current + 1);
        }
        finally
        {
            try
            {
                await outputs.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (outcome != RunOutcome.Aborted)
                    outcome = RunOutcome.Error;
                adjustments.Clear();
                failure = string.Join(" ", new[] { failure, ex.Message }.OfType<string>());
                log.LogError(ex, "Sichere Abschaltung für Prüflauf {RunId} nicht vollständig bestätigt", id);
            }
        }

        var result = new RunResult(id, started, DateTimeOffset.Now, profile, identity, references, request.Operator.Trim(),
            request.Conditions, equipment.Simulated, outcome, points.ToArray(), adjustments.ToArray(), failure,
            profile.Steps.Where(step => step.Kind != StepKind.Wait).Select(step => step.ChannelId).Distinct().Select(ChannelCatalog.Get).ToArray(),
            skipped.ToArray());
        await store.SaveRunAsync(result, CancellationToken.None).ConfigureAwait(false);
        Report(outcome switch
        {
            RunOutcome.Aborted => EngineState.Aborted,
            RunOutcome.Error => EngineState.Failed,
            _ => EngineState.Completed
        }, failure ?? "Abgeschlossen. Das Ergebnis wurde gespeichert.");
        log.LogInformation("Prüflauf {RunId} gespeichert. Ergebnis {Result}, Messpunkte {Points}", id, outcome, points.Count);
        return result;

        void Report(EngineState state, string message) => progress = new(state, profile.Name, current, profile.Steps.Count, message, skipped.Count == 0 ? null : skipped.ToArray());

        async Task ExecuteStepWithPauseAsync(TestStep step)
        {
            while (true)
            {
                try
                {
                    Checkpoint();
                    Report(EngineState.ConfiguringInstrument, Label(step));
                    if (step.Kind == StepKind.Wait)
                    {
                        for (var elapsed = 0d; elapsed < step.Duration; elapsed += 0.1)
                        {
                            Checkpoint();
                            await equipment.DelayAsync(Math.Min(0.1, step.Duration - elapsed), ct).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        var point = await MeasureAsync(step).ConfigureAwait(false);
                        Checkpoint();
                        points.Add(point);
                    }
                    return;
                }
                catch (PauseStep)
                {
                    await outputs.StopAsync().ConfigureAwait(false);
                    Report(EngineState.Paused, "Pausiert. Prüfstimuli ausgeschaltet. Der Prüfpunkt startet beim Fortsetzen neu.");
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        lock (control)
                            if (!pause)
                                break;
                        await Task.Delay(40, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        async Task<PointResult> MeasureAsync(TestStep step)
        {
            var channel = ChannelCatalog.Get(step.ChannelId);
            double? confirmed = null;
            double? independent = null;
            switch (step.Kind)
            {
                case StepKind.AnalogPoint:
                    if (await equipment.Source.DisableAsync(ct).ConfigureAwait(false) != OutputState.Off)
                        throw new IOException("Ausgang AUS vor Umschaltung nicht bestätigt.");
                    await equipment.Daq.SetRouteAsync(channel.Id, ct).ConfigureAwait(false);
                    if (profile.Kind == RunKind.Calibration && !coefficients.ContainsKey(channel.Id))
                        coefficients[channel.Id] = await equipment.Daq.ReadCoefficientAsync(channel.Id, ct).ConfigureAwait(false);
                    Checkpoint();
                    var source = await equipment.Source.ApplyAsync(channel.Quantity, step.Value, true, ct).ConfigureAwait(false);
                    if (source.Output != OutputState.On || source.Quantity != channel.Quantity || source.Confirmed is not double value ||
                        !double.IsFinite(value) || Math.Abs(value - step.Value) > 0.00050001)
                        throw new IOException("FNIRSI hat Betriebsart, Ausgang oder Sollwert nicht wie erwartet bestätigt.");
                    confirmed = value;
                    independent = source.IndependentReference;
                    log.LogInformation("{Channel} Sollwert gesetzt. Angefordert {Requested}, rückgelesen {Confirmed} {Unit}",
                        channel.Id, step.Value, confirmed, channel.Unit);
                    Report(EngineState.Settling, $"{channel.Id}: Stabilisierung bei {value:0.000} {channel.Unit}");
                    await WaitStableAsync(channel, step).ConfigureAwait(false);
                    break;
                case StepKind.FrequencyPoint:
                    var a = new WaveformSettings(1, SignalShape.Square, step.Value, 5, 2.5, 0, 50, null, true);
                    var b = a with { Channel = 2, Phase = 90 };
                    await equipment.Generator.ApplyWaveformAsync(a, ct).ConfigureAwait(false);
                    var actual = await equipment.Generator.ApplyWaveformAsync(b, ct).ConfigureAwait(false);
                    if (!actual.Enabled || actual.Frequency != step.Value)
                        throw new IOException("OWON hat die Prüffrequenz nicht bestätigt.");
                    confirmed = actual.Frequency;
                    await equipment.DelayAsync(0.5, ct).ConfigureAwait(false);
                    break;
                case StepKind.DigitalPoint:
                    await equipment.Daq.SetDigitalOutputAsync(channel.DigitalOutput!, step.Value == 1, ct).ConfigureAwait(false);
                    break;
            }

            Report(EngineState.Acquiring, $"{channel.Id}: Messdaten erfassen ({step.Duration:0.00} s)");
            var measured = new SampleAccumulator();
            var uncorrected = new SampleAccumulator();
            for (var elapsed = 0d; elapsed < step.Duration - 1e-9; elapsed += 0.1)
            {
                await equipment.DelayAsync(Math.Min(0.1, step.Duration - elapsed), ct).ConfigureAwait(false);
                var sample = await ReadAsync(channel).ConfigureAwait(false);
                if (sample.Corrected is not double corrected)
                    throw new InvalidDataException($"{channel.Id}: Kein skalierter Messwert.");
                var raw = sample.Uncorrected ?? corrected;
                var count = Math.Max(1, sample.Count);
                uncorrected.Add(raw, count, sample.StdDev ?? 0);
                var gain = coefficients.GetValueOrDefault(channel.Id)?.Gain ?? 1;
                measured.Add(corrected, count, (sample.StdDev ?? 0) * gain, sample.Min, sample.Max);
            }
            var stats = measured.Statistics;
            var reference = independent ?? confirmed ?? step.Value;
            var error = stats.Mean - reference;
            var tolerance = CalibrationMath.AbsoluteTolerance(channel, reference, step.Tolerance);
            var sourceInfo = referenceKinds.Contains(StepKind.AnalogPoint) && step.Kind == StepKind.AnalogPoint
                ? references.FirstOrDefault(info => info.Model == equipment.Source.Status.Name)
                : null;
            var expanded = sourceInfo?.Uncertainty is > 0 && independent.HasValue
                ? CalibrationMath.ExpandedUncertainty(sourceInfo.Uncertainty.Value, stats)
                : (double?)null;
            return new(channel.Id, channel.Quantity, step.Value, confirmed, independent, reference, stats.Mean,
                uncorrected.Statistics.Mean, stats.StandardDeviation, checked((int)stats.Count), error,
                CalibrationMath.PercentOfReading(error, reference), CalibrationMath.PercentOfSpan(error, channel.Span), tolerance,
                CalibrationMath.IsWithinTolerance(error, tolerance), expanded);
        }

        async Task WaitStableAsync(ChannelDefinition channel, TestStep step)
        {
            var windowCount = (int)Math.Ceiling(step.Window / 0.1);
            var recent = new Queue<ChannelSample>(windowCount);
            for (var elapsed = 0d; elapsed < Math.Max(15, step.Window * 3); elapsed += 0.1)
            {
                await equipment.DelayAsync(0.1, ct).ConfigureAwait(false);
                var sample = await ReadAsync(channel).ConfigureAwait(false);
                if (sample.Uncorrected is null)
                    throw new InvalidDataException($"{channel.Id}: Unkorrigierte Messwerte fehlen.");
                recent.Enqueue(sample);
                if (recent.Count > windowCount)
                    recent.Dequeue();
                if (recent.Count < windowCount)
                    continue;

                var values = new SampleAccumulator();
                foreach (var item in recent)
                    values.Add(item.Uncorrected!.Value, Math.Max(1, item.Count), item.StdDev ?? 0);
                if (CalibrationMath.IsStable(values.Statistics, step.Stability))
                    return;
            }
            throw new TimeoutException($"{channel.Id}: Stabilitätsgrenze {step.Stability:G4} {channel.Unit} nicht erreicht.");
        }

        async Task<ChannelSample> ReadAsync(ChannelDefinition channel)
        {
            Checkpoint();
            if (channel.Group == ChannelGroup.Analog)
                equipment.Require(DeviceKind.Fnirsi, DeviceCapability.AnalogSource);
            else if (channel.Group == ChannelGroup.Counter)
                equipment.Require(DeviceKind.Owon, DeviceCapability.Quadrature);
            equipment.Require(DeviceKind.Daq, DeviceCapability.Measurements);
            var frame = await equipment.Daq.ReadFrameAsync(ct).ConfigureAwait(false);
            history.Publish(frame, equipment.Simulated);
            return frame.Samples.SingleOrDefault(sample => sample.ChannelId == channel.Id)
                ?? throw new InvalidDataException($"{channel.Id}: Kanal fehlt im Messblock {frame.Sequence}.");
        }

        void Checkpoint()
        {
            ct.ThrowIfCancellationRequested();
            lock (control)
            {
                if (skip)
                {
                    skip = false;
                    throw new SkipStep();
                }
                if (pause)
                    throw new PauseStep();
            }
        }
    }

    private static string Label(TestStep step) => !string.IsNullOrWhiteSpace(step.Label) ? step.Label
        : step.Kind == StepKind.Wait ? "Wartezeit" : $"{step.ChannelId} · {step.Value:G6} {ChannelCatalog.Get(step.ChannelId).Unit}";

    private static IReadOnlyList<ReferenceInfo> ReferencesFor(RunRequest request, Equipment equipment, HashSet<StepKind> kinds)
    {
        var sourceKinds = new[] { (StepKind.AnalogPoint, DeviceKind.Fnirsi), (StepKind.FrequencyPoint, DeviceKind.Owon) };
        var result = new List<ReferenceInfo>();
        foreach (var (kind, device) in sourceKinds)
        {
            if (!kinds.Contains(kind)) continue;
            var status = equipment[device].Status;
            var reference = request.References.FirstOrDefault(reference => reference.Model == status.Name);
            result.Add(reference is null || equipment.Simulated ? new(status.Name, status.Identity?.Serial)
                : reference with { Serial = status.Identity?.Serial ?? reference.Serial });
        }
        return result;
    }

    private static void Validate(TestProfile profile, Equipment equipment)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Steps.Count is < 1 or > 400 ||
            profile.Steps.All(step => step.Kind == StepKind.Wait))
            throw new ArgumentException("Ein Prüfprofil braucht einen Namen und 1–400 Schritte mit mindestens einem Messpunkt.");

        equipment.Require(DeviceKind.Daq, DeviceCapability.Measurements);
        foreach (var step in profile.Steps)
        {
            if (!double.IsFinite(step.Duration) || step.Duration is < 0.1 or > 300)
                throw new ArgumentException("Mess- und Wartezeiten müssen zwischen 0,1 und 300 s liegen.");
            if (step.Kind == StepKind.Wait)
                continue;
            var channel = ChannelCatalog.Get(step.ChannelId);
            if (!double.IsFinite(step.Value) || !double.IsFinite(step.Tolerance) || step.Tolerance < 0 ||
                step.Value < channel.Minimum || step.Value > channel.Maximum || channel.Unwired)
                throw new ArgumentException($"{step.ChannelId}: Sollwert, Anschluss oder Toleranz außerhalb des zulässigen Prüfbereichs.");
            switch (step.Kind)
            {
                case StepKind.AnalogPoint when channel.Group == ChannelGroup.Analog:
                    equipment.Require(DeviceKind.Daq, DeviceCapability.Route);
                    equipment.Require(DeviceKind.Fnirsi, DeviceCapability.AnalogSource);
                    if (profile.Kind == RunKind.Calibration)
                        equipment.Require(DeviceKind.Daq, DeviceCapability.Coefficients);
                    if (!equipment.Source.Supports(channel.Quantity, step.Value) || !double.IsFinite(step.Stability) ||
                        step.Stability <= 0 || !double.IsFinite(step.Window) || step.Window is < 0.2 or > 30)
                        throw new ArgumentException("Sollwert oder Stabilitätsparameter außerhalb des Arbeitsbereichs.");
                    break;
                case StepKind.FrequencyPoint when channel.Id == "ENC1" && step.Value > 0:
                    equipment.Require(DeviceKind.Owon, DeviceCapability.Quadrature);
                    if (!equipment.Simulated)
                        throw new NotSupportedException("Der Encoder-Prüfablauf ist für synchronisierte reale A/B-Ausgänge noch nicht freigegeben.");
                    break;
                case StepKind.DigitalPoint when channel.DigitalOutput is not null && step.Value is (0 or 1) && step.Tolerance == 0:
                    equipment.Require(DeviceKind.Daq, DeviceCapability.DigitalOutput);
                    break;
                default:
                    throw new ArgumentException($"{step.ChannelId}: Kanal und Prüfschritt passen nicht zusammen.");
            }
        }

        if (profile.Kind == RunKind.Calibration)
        {
            var analog = profile.Steps.Where(step => step.Kind == StepKind.AnalogPoint).ToArray();
            if (analog.Length == 0)
                throw new ArgumentException("Ein Kalibrierprofil benötigt analoge Prüfpunkte.");
            if (profile.Method is FitMethod.TwoPoint or FitMethod.Regression &&
                analog.GroupBy(step => step.ChannelId).Any(group => group.Select(step => step.Value).Distinct().Count() < 2))
                throw new ArgumentException("Für die gewählte Methode sind mindestens zwei unterschiedliche Sollwerte pro Kanal nötig.");
        }
    }

    private sealed class Stimuli(Equipment equipment, HashSet<StepKind> kinds)
    {
        public async Task StopAsync()
        {
            var errors = new List<string>();
            if (kinds.Contains(StepKind.AnalogPoint))
                await Off("FNIRSI", equipment.Source.DisableAsync).ConfigureAwait(false);
            if (kinds.Contains(StepKind.FrequencyPoint))
                await Off("OWON", equipment.Generator.DisableAsync).ConfigureAwait(false);
            if (kinds.Contains(StepKind.DigitalPoint))
            {
                foreach (var output in ChannelCatalog.All.Select(channel => channel.DigitalOutput).OfType<string>())
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await equipment.Daq.SetDigitalOutputAsync(output, false, timeout.Token).ConfigureAwait(false); }
                    catch (Exception ex) { errors.Add($"{output}: {ex.Message}"); }
                }
            }
            if (errors.Count > 0)
                throw new IOException($"Ausgangszustand unbekannt. Prüfaufbau manuell kontrollieren. {string.Join(" ", errors)}");

            async Task Off(string device, Func<CancellationToken, Task<OutputState>> disable)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    if (await disable(timeout.Token).ConfigureAwait(false) != OutputState.Off)
                        errors.Add($"{device}: AUS nicht bestätigt.");
                }
                catch (Exception ex)
                {
                    errors.Add($"{device}: {ex.Message}");
                }
            }
        }
    }

    private sealed class PauseStep : Exception;
    private sealed class SkipStep : Exception;
}
