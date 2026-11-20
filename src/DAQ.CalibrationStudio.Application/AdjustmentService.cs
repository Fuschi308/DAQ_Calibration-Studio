using DAQ.CalibrationStudio.Domain;
using Microsoft.Extensions.Logging;

namespace DAQ.CalibrationStudio.Application;

public sealed class AdjustmentService(DeviceManager devices, IStudioStore store, ILogger<AdjustmentService> log)
{
    public async Task<Approval> ApplyAsync(RunResult run, string operatorId, bool explicitlyApproved,
        bool persistent, CancellationToken ct = default)
    {
        RequireApproval(operatorId, explicitlyApproved);
        if (run.Profile.Kind != RunKind.Calibration || run.Adjustments.Count == 0 ||
            run.Outcome is not (RunOutcome.Passed or RunOutcome.Failed))
            throw new InvalidOperationException("Nur abgeschlossene Kalibrierungen mit vollständigen Koeffizienten sind justierbar.");

        Approval? result = null;
        await devices.UseAsync(async (equipment, token) =>
        {
            equipment.Require(DeviceKind.Daq, DeviceCapability.Coefficients);
            var currentIdentity = equipment.Daq.Status.Identity;
            if (equipment.Simulated != run.Simulated || string.IsNullOrWhiteSpace(run.Daq.Serial) || currentIdentity?.Serial != run.Daq.Serial)
                throw new InvalidOperationException("Der Prüfdatensatz gehört zu einer anderen DAQ oder zu einem anderen Betriebsmodus.");

            var past = await store.ListApprovalsAsync(run.Id, token).ConfigureAwait(false);
            if (past.Any(entry => entry.Target == "Verworfen"))
                throw new InvalidOperationException("Die Koeffizienten dieses Laufs wurden verworfen.");

            var date = persistent ? DateOnly.FromDateTime(DateTime.Today) : (DateOnly?)null;
            var next = new List<Adjustment>();
            foreach (var adjustment in run.Adjustments)
            {
                var channel = ChannelCatalog.Get(adjustment.ChannelId);
                var proposed = adjustment.Proposed;
                if (!double.IsFinite(proposed.Gain) || proposed.Gain is < 0.5 or > 1.5 ||
                    !double.IsFinite(proposed.Offset) || Math.Abs(proposed.Offset) > channel.Span / 5)
                    throw new InvalidOperationException($"{channel.Id}: Koeffizienten sind unplausibel.");
                var actual = await equipment.Daq.ReadCoefficientAsync(channel.Id, token).ConfigureAwait(false);
                if (actual != adjustment.Original)
                    throw new InvalidOperationException($"{channel.Id}: Aktive Koeffizienten haben sich seit der Messung geändert.");
                next.Add(adjustment with { Proposed = proposed with { CalibratedOn = date } });
            }
            result = await WriteAndVerifyAsync(equipment, run, operatorId, persistent ? "Flash" : "RAM", persistent, next, token)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return result!;
    }

    public async Task<Approval> DiscardAsync(RunResult run, string operatorId, CancellationToken ct = default)
    {
        RequireApproval(operatorId, true);
        var approval = new Approval(Guid.NewGuid(), run.Id, DateTimeOffset.Now, operatorId.Trim(), "Verworfen",
            run.Adjustments, false, "Verwerfen bestätigt.");
        await devices.UseAsync(async (_, token) =>
        {
            var past = await store.ListApprovalsAsync(run.Id, token).ConfigureAwait(false);
            if (past.Any(entry => entry.Verified || entry.Target == "Verworfen"))
                throw new InvalidOperationException("Für diesen Lauf liegt bereits eine abschließende Entscheidung vor.");
            await store.AddApprovalAsync(approval, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return approval;
    }

    public async Task<Approval> ResetSimulationAsync(IEnumerable<string> channelIds, string operatorId,
        bool explicitlyApproved, CancellationToken ct = default)
    {
        RequireApproval(operatorId, explicitlyApproved);
        var ids = channelIds.Distinct().ToArray();
        if (ids.Length == 0 || ids.Any(id => ChannelCatalog.Get(id).Group != ChannelGroup.Analog))
            throw new ArgumentException("Wählen Sie analog kalibrierbare Kanäle aus.");

        Approval? result = null;
        await devices.UseAsync(async (equipment, token) =>
        {
            equipment.Require(DeviceKind.Daq, DeviceCapability.Coefficients);
            if (!equipment.Simulated)
                throw new NotSupportedException("Diese Rücksetzung ist im Simulationsbetrieb verfügbar.");
            var identity = equipment.Daq.Status.Identity!;
            var backup = new List<Adjustment>();
            foreach (var id in ids)
            {
                var coefficient = await equipment.Daq.ReadCoefficientAsync(id, token).ConfigureAwait(false);
                var factory = Coefficient.Identity with { Version = checked(coefficient.Version + 1) };
                backup.Add(new Adjustment(id, coefficient, factory, 0));
            }
            var now = DateTimeOffset.Now;
            var run = new RunResult(Guid.NewGuid(), now, now,
                new TestProfile(Guid.NewGuid(), 1, "Koeffizientensicherung vor Rücksetzung", RunKind.Functional, FitMethod.SinglePoint, [], SnapshotOnly: true),
                new DaqIdentity(identity.Model, identity.Serial, identity.Firmware, identity.Revision),
                [], operatorId.Trim(), "Sicherung der aktiven Koeffizienten vor simulierten Werkseinstellungen.",
                true, RunOutcome.Passed, [], backup, "Verwaltungsvorgang ohne Prüfpunkte.");
            await store.SaveRunAsync(run, token).ConfigureAwait(false);
            result = await WriteAndVerifyAsync(equipment, run, operatorId, "Flash · Werkseinstellung", true, backup, token)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return result!;
    }

    private async Task<Approval> WriteAndVerifyAsync(Equipment equipment, RunResult run, string operatorId,
        string target, bool persistent, IReadOnlyList<Adjustment> adjustments, CancellationToken ct)
    {
        var intent = new Approval(Guid.NewGuid(), run.Id, DateTimeOffset.Now, operatorId.Trim(), target,
            adjustments, false, "Schreibabsicht protokolliert. Ergebnis steht aus.");
        await store.AddApprovalAsync(intent, ct).ConfigureAwait(false);
        Approval result;
        var coefficients = adjustments.ToDictionary(item => item.ChannelId, item => item.Proposed);
        try
        {
            await equipment.Daq.WriteCoefficientsAsync(coefficients, persistent, ct).ConfigureAwait(false);
            foreach (var (id, expected) in coefficients)
            {
                if (await equipment.Daq.ReadCoefficientAsync(id, ct).ConfigureAwait(false) != expected)
                    throw new IOException($"{id}: Rücklesewert stimmt nicht überein.");
            }
            result = new(Guid.NewGuid(), run.Id, DateTimeOffset.Now, operatorId.Trim(), target, adjustments, true);
            log.LogInformation("Justierung {RunId} in {Target} geschrieben und verifiziert", run.Id, target);
        }
        catch (Exception ex)
        {
            result = new(Guid.NewGuid(), run.Id, DateTimeOffset.Now, operatorId.Trim(), target, adjustments, false,
                $"{ex.Message} Gespeicherten Zustand kontrollieren.");
            log.LogError(ex, "Justierung {RunId} nicht bestätigt", run.Id);
        }
        await store.AddApprovalAsync(result, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private static void RequireApproval(string operatorId, bool explicitlyApproved)
    {
        if (!explicitlyApproved || string.IsNullOrWhiteSpace(operatorId))
            throw new InvalidOperationException("Ausdrückliche Freigabe und Prüfer-Kennung erforderlich.");
    }
}
