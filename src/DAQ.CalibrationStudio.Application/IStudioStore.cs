using DAQ.CalibrationStudio.Domain;

namespace DAQ.CalibrationStudio.Application;

public interface IStudioStore
{
    Task SaveRunAsync(RunResult run, CancellationToken ct = default);
    Task<IReadOnlyList<RunResult>> ListRunsAsync(CancellationToken ct = default);
    Task SaveProfileAsync(TestProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<TestProfile>> ListProfilesAsync(CancellationToken ct = default);
    Task AddApprovalAsync(Approval approval, CancellationToken ct = default);
    Task<IReadOnlyList<Approval>> ListApprovalsAsync(Guid runId, CancellationToken ct = default);
    Task<StudioSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(StudioSettings settings, CancellationToken ct = default);
}
