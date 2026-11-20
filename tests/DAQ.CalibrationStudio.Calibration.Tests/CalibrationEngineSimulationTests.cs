using DAQ.CalibrationStudio.Application;
using DAQ.CalibrationStudio.Domain;
using DAQ.CalibrationStudio.Infrastructure.Persistence;
using DAQ.CalibrationStudio.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DAQ.CalibrationStudio.Calibration.Tests;

public sealed class CalibrationEngineSimulationTests
{
    [Fact]
    public async Task SimulatedVoltageCurrentFrequencyAndDigitalRunIsPersistedWithProvenance()
    {
        await using var rig = await TestRig.CreateAsync();
        rig.Simulation.Speed = 10;
        var profile = Profile(RunKind.Functional,
        [
            new(StepKind.AnalogPoint, "AI-V1", 5, Duration: 0.2, Tolerance: 0.2, Stability: 0.01, Window: 0.2),
            new(StepKind.AnalogPoint, "AI-I1", 12, Duration: 0.2, Tolerance: 0.2, Stability: 0.01, Window: 0.2),
            new(StepKind.FrequencyPoint, "ENC1", 1000, Duration: 0.2, Tolerance: 100),
            new(StepKind.DigitalPoint, "DI1", 1, Duration: 0.2)
        ]);

        var run = await rig.Engine.RunAsync(new RunRequest(profile, "MF", "23 °C", []))
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RunOutcome.Passed, run.Outcome);
        Assert.True(run.Simulated);
        Assert.Equal("DAQ-H7-SIM-0042", run.Daq.Serial);
        Assert.Equal(4, run.Points.Count);
        Assert.Contains(run.Points, point => point.ChannelId == "AI-V1" && point.Quantity == Quantity.Voltage && point.Passed);
        Assert.Contains(run.Points, point => point.ChannelId == "AI-I1" && point.Quantity == Quantity.Current && point.Passed);
        Assert.Contains(run.Points, point => point.ChannelId == "ENC1" && point.Quantity == Quantity.Frequency && point.Passed);
        Assert.Contains(run.Points, point => point.ChannelId == "DI1" && point.Quantity == Quantity.Digital && point.Passed);
        Assert.False(rig.Simulation.Source.Enabled);
        Assert.False(rig.Simulation.Generator.Current(1).Enabled);
        var digitalAfterRun = await rig.Simulation.Daq.ReadFrameAsync(default);
        Assert.Equal(0, digitalAfterRun.Samples.Single(sample => sample.ChannelId == "DI1").Corrected);

        var persisted = Assert.Single(await rig.Store.ListRunsAsync());
        Assert.Equal(run.Id, persisted.Id);
        Assert.True(persisted.Simulated);
        Assert.Equal(4, persisted.Points.Count);
    }

    [Fact]
    public async Task CalibrationDoesNotWriteWithoutApprovalAndRejectsStaleCoefficients()
    {
        await using var rig = await TestRig.CreateAsync();
        rig.Simulation.Speed = 10;
        var run = await rig.Engine.RunAsync(new RunRequest(CalibrationProfile(), "MF", "23 °C", []))
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunOutcome.Passed, run.Outcome);
        var adjustment = Assert.Single(run.Adjustments);
        Assert.NotEqual(adjustment.Original, adjustment.Proposed);
        Assert.Equal(adjustment.Original, await rig.Simulation.Daq.ReadCoefficientAsync("AI-V1", default));
        Assert.Empty(await rig.Store.ListApprovalsAsync(run.Id));

        var service = new AdjustmentService(rig.Devices, rig.Store, NullLogger<AdjustmentService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(run, "MF", explicitlyApproved: false, persistent: false));
        Assert.Equal(adjustment.Original, await rig.Simulation.Daq.ReadCoefficientAsync("AI-V1", default));

        var externallyChanged = adjustment.Original with
        {
            Gain = adjustment.Original.Gain + 0.001,
            Version = adjustment.Original.Version + 1
        };
        await rig.Simulation.Daq.WriteCoefficientsAsync(
            new Dictionary<string, Coefficient> { ["AI-V1"] = externallyChanged }, persistent: false, default);
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyAsync(run, "MF", explicitlyApproved: true, persistent: false));
        Assert.Contains("geändert", stale.Message);
        Assert.Equal(externallyChanged, await rig.Simulation.Daq.ReadCoefficientAsync("AI-V1", default));
        Assert.Empty(await rig.Store.ListApprovalsAsync(run.Id));
    }

    [Fact]
    public async Task ExplicitApprovalWritesThenVerifiesAndPersistsSeparateAuditEvents()
    {
        await using var rig = await TestRig.CreateAsync();
        rig.Simulation.Speed = 10;
        var run = await rig.Engine.RunAsync(new RunRequest(CalibrationProfile(), "MF", "23 °C", []))
            .WaitAsync(TimeSpan.FromSeconds(10));
        var service = new AdjustmentService(rig.Devices, rig.Store, NullLogger<AdjustmentService>.Instance);

        var approval = await service.ApplyAsync(run, "MF", explicitlyApproved: true, persistent: false)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(approval.Verified, approval.Error);
        Assert.Equal("RAM", approval.Target);
        var readback = await rig.Simulation.Daq.ReadCoefficientAsync("AI-V1", default);
        Assert.Equal(run.Adjustments[0].Proposed.Gain, readback.Gain, 10);
        Assert.Equal(run.Adjustments[0].Proposed.Offset, readback.Offset, 10);
        var audits = await rig.Store.ListApprovalsAsync(run.Id);
        Assert.Equal(2, audits.Count);
        Assert.Contains(audits, item => !item.Verified && item.Error?.Contains("steht aus") == true);
        Assert.Contains(audits, item => item.Verified && item.Target == "RAM");
    }

    [Fact]
    public async Task FactoryResetSimulationRequiresApprovalAndBacksUpBeforeVerifiedFlashWrite()
    {
        await using var rig = await TestRig.CreateAsync();
        var service = new AdjustmentService(rig.Devices, rig.Store, NullLogger<AdjustmentService>.Instance);
        var before = await rig.Simulation.Daq.ReadCoefficientAsync("AI-V1", default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResetSimulationAsync(["AI-V1"], "MF", explicitlyApproved: false));
        Assert.Empty(await rig.Store.ListRunsAsync());
        Assert.Equal(before, await rig.Simulation.Daq.ReadCoefficientAsync("AI-V1", default));

        var approval = await service.ResetSimulationAsync(["AI-V1"], "MF", explicitlyApproved: true)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(approval.Verified, approval.Error);
        Assert.Equal("Flash · Werkseinstellung", approval.Target);
        var backedUpRun = Assert.Single(await rig.Store.ListRunsAsync());
        Assert.True(backedUpRun.Simulated);
        Assert.Empty(backedUpRun.Points);
        var backup = Assert.Single(backedUpRun.Adjustments);
        Assert.Equal(before, backup.Original);
        var expectedFactory = Coefficient.Identity with { Version = before.Version + 1 };
        Assert.Equal(expectedFactory, backup.Proposed);
        Assert.Equal(expectedFactory, await rig.Simulation.Daq.ReadCoefficientAsync("AI-V1", default));
        var audits = await rig.Store.ListApprovalsAsync(backedUpRun.Id);
        Assert.Equal(2, audits.Count);
        Assert.Contains(audits, item => !item.Verified && item.Target == approval.Target);
        Assert.Contains(audits, item => item.Verified && item.Target == approval.Target);
    }

    [Fact]
    public async Task PausedRunCanResumeAndThenCancelWithAnAbortedPersistedResult()
    {
        await using var rig = await TestRig.CreateAsync();
        rig.Simulation.Speed = 10;
        var profile = Profile(RunKind.Functional,
        [
            new(StepKind.Wait, "", Duration: 30),
            new(StepKind.AnalogPoint, "AI-V1", 5, Duration: 0.2, Tolerance: 0.2, Stability: 0.01, Window: 0.2)
        ]);
        var task = rig.Engine.RunAsync(new RunRequest(profile, "MF", "Pause-Test", []));
        await WaitForAsync(() => rig.Engine.Progress.State == EngineState.ConfiguringInstrument, TimeSpan.FromSeconds(3));
        rig.Engine.Pause();
        await WaitForAsync(() => rig.Engine.Progress.State == EngineState.Paused, TimeSpan.FromSeconds(3));
        rig.Engine.Resume();
        await WaitForAsync(() => rig.Engine.Progress.State == EngineState.ConfiguringInstrument, TimeSpan.FromSeconds(3));
        rig.Engine.Cancel();

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RunOutcome.Aborted, result.Outcome);
        Assert.Contains("abgebrochen", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EngineState.Aborted, rig.Engine.Progress.State);
        Assert.Equal(result.Id, Assert.Single(await rig.Store.ListRunsAsync()).Id);
    }

    [Fact]
    public async Task SimulatedDaqDropoutProducesAndPersistsAnErrorResult()
    {
        await using var rig = await TestRig.CreateAsync();
        rig.Simulation.Speed = 10;
        var profile = Profile(RunKind.Functional,
        [
            new(StepKind.Wait, "", Duration: 15),
            new(StepKind.AnalogPoint, "AI-V1", 5, Duration: 0.2, Tolerance: 0.2, Stability: 0.01, Window: 0.2)
        ]);
        var task = rig.Engine.RunAsync(new RunRequest(profile, "MF", "Verbindungsabbruch", []));
        await WaitForAsync(() => rig.Engine.Progress.State == EngineState.ConfiguringInstrument, TimeSpan.FromSeconds(3));
        await Task.Delay(75);
        rig.Simulation.InjectFault(DeviceKind.Daq);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RunOutcome.Error, result.Outcome);
        Assert.Contains("Verbindungsverlust", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(result.Id, Assert.Single(await rig.Store.ListRunsAsync()).Id);
    }

    [Fact]
    public async Task DeviceManagerBlocksModeChangesAndSkipsPollingDuringManualOperation()
    {
        await using var rig = await TestRig.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = rig.Devices.UseAsync(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var polled = false;
        await rig.Devices.TryPollAsync((_, _) => { polled = true; return Task.CompletedTask; }, default);
        Assert.False(polled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Devices.SwitchAsync(simulation: false));

        release.SetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(2));
        await rig.Devices.SwitchAsync(simulation: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(rig.Simulation.Equipment, rig.Devices.Active);
    }

    private static TestProfile CalibrationProfile() => Profile(RunKind.Calibration,
    [
        new(StepKind.AnalogPoint, "AI-V1", 1, Duration: 0.2, Tolerance: 0.2, Stability: 0.01, Window: 0.2),
        new(StepKind.AnalogPoint, "AI-V1", 5, Duration: 0.2, Tolerance: 0.2, Stability: 0.01, Window: 0.2),
        new(StepKind.AnalogPoint, "AI-V1", 9, Duration: 0.2, Tolerance: 0.2, Stability: 0.01, Window: 0.2)
    ], FitMethod.Regression);

    private static TestProfile Profile(RunKind kind, IReadOnlyList<TestStep> steps, FitMethod method = FitMethod.SinglePoint) =>
        new(Guid.NewGuid(), 1, $"Simulation {kind}", kind, method, steps);

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed >= timeout)
                throw new TimeoutException("Erwarteter Engine-Zustand wurde nicht erreicht.");
            await Task.Delay(10);
        }
    }

    private sealed class TestRig : IAsyncDisposable
    {
        private readonly string root;
        private TestRig(string root, SimulatedDesk simulation, DeviceManager devices, SqliteStudioStore store,
            MeasurementHistory history, CalibrationEngine engine)
        {
            this.root = root;
            Simulation = simulation;
            Devices = devices;
            Store = store;
            History = history;
            Engine = engine;
        }

        public SimulatedDesk Simulation { get; }
        public DeviceManager Devices { get; }
        public SqliteStudioStore Store { get; }
        public MeasurementHistory History { get; }
        public CalibrationEngine Engine { get; }

        public static async Task<TestRig> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "daq-calibration-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var simulation = new SimulatedDesk(Path.Combine(root, "simulated-coefficients.json"));
            var physicalStandIn = new SimulatedDesk();
            foreach (var session in simulation.Sessions)
                await session.ConnectAsync("Simulation", default);
            var devices = new DeviceManager(simulation.Equipment, physicalStandIn.Equipment, NullLogger<DeviceManager>.Instance);
            var store = new SqliteStudioStore(Path.Combine(root, "history.db"));
            var history = new MeasurementHistory(NullLogger<MeasurementHistory>.Instance);
            var engine = new CalibrationEngine(devices, history, store, NullLogger<CalibrationEngine>.Instance);
            return new TestRig(root, simulation, devices, store, history, engine);
        }

        public async ValueTask DisposeAsync()
        {
            await Devices.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
