using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Recovery;

public enum JobRecoveryAction
{
    None,
    NormalizedToInterrupted,
    ResumedToAnalyzing,
    ResumedToPreselection,
    ResumedToQueued,
    ResumedToProcessing,
    ResumedToQa,
    ResumedToReviewFinal,
    CompletedFromDurablePublication,
    CompletedFromOutputCheckpoint,
    RolledBackCorruptCheckpoint,
    FailedUnrecoverable
}

public sealed record JobRecoveryResult(
    JobId JobId,
    JobState InitialState,
    JobState FinalState,
    JobRecoveryAction Action,
    string? LatestValidCheckpoint,
    string? Reason);

public sealed record ProjectRecoveryReport(
    ProjectId ProjectId,
    int TotalJobsScanned,
    int InterruptedJobsNormalized,
    int JobsResumed,
    int JobsCompleted,
    int JobsRolledBack,
    IReadOnlyList<JobRecoveryResult> JobResults);

public interface IRecoveryCoordinator
{
    Task<ProjectRecoveryReport> ReconcileAndRecoverProjectAsync(
        ProjectId projectId,
        string outputRootFolder,
        CancellationToken cancellationToken = default);

    Task<JobRecoveryResult> ReconcileJobAsync(
        ProjectId projectId,
        JobId jobId,
        string outputRootFolder,
        CancellationToken cancellationToken = default);
}
