using System.Text.Json;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Processing;

public enum ComfyWorkStatus
{
    NoWork,
    Skipped,
    Completed
}

public sealed record ComfyRunResult(
    ComfyWorkStatus Status,
    JobId? JobId,
    ComfyExecutionSnapshot? Execution);

public sealed record ComfyTaskDecision(
    string TaskId,
    string Action,
    string Reason);

public sealed record ComfyValidatedPlan(
    int SchemaVersion,
    string PlanVersion,
    ComfyUiMode Mode,
    string BenchmarkStatus,
    IReadOnlyList<ComfyTaskDecision> Decisions,
    IReadOnlyList<string> ExecutionOrder,
    JsonElement Raw);

public sealed record ComfyTaskDescriptor(
    string TaskId,
    bool ProductionApproved,
    string ApprovalStatus,
    string? WorkflowId,
    string Reason);

public sealed record ComfyExecutionArtifact(
    string Path,
    string Sha256,
    long SizeBytes,
    JsonElement WorkflowManifest,
    JsonElement PromptIds);

public sealed record ComfyHistoryRecovery(
    string AttemptId,
    ComfyExecutionArtifact Artifact);

public sealed record ComfyPersistPlanRequest(
    JobId JobId,
    int SchemaVersion,
    string Mode,
    JsonElement Plan,
    string PlanSha256,
    DateTimeOffset CreatedAtUtc);

public sealed record ComfyPersistCompleteRequest(
    ComfyJobSnapshot Job,
    string AttemptId,
    string Status,
    ComfyExecutionArtifact Artifact,
    JsonElement TaskManifest,
    string HistoryPath,
    DateTimeOffset CompletedAtUtc);

public interface IComfyStore
{
    Task<ComfyJobSnapshot?> GetNextEligibleAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);
    Task<ComfyPlanSnapshot?> GetPlanAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);
    Task<ComfyExecutionSnapshot?> GetExecutionAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);
    Task<bool> HasCheckpointAsync(
        JobId jobId,
        string stageName,
        CancellationToken cancellationToken = default);
    Task PersistPlanAsync(
        ComfyPersistPlanRequest request,
        CancellationToken cancellationToken = default);
    Task<bool> ClaimFromQaAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
    Task<bool> ResumeRetryAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
    Task<bool> ResumeInterruptedAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
    Task<int> ScheduleRetryAsync(
        JobId jobId,
        string operationId,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
    Task MarkInterruptedAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
    Task MarkErrorAsync(
        JobId jobId,
        string operationId,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
    Task PersistCompleteAsync(
        ComfyPersistCompleteRequest request,
        CancellationToken cancellationToken = default);
}

public interface IComfyStoreFactory
{
    IComfyStore Open(ProjectId projectId);
}

public interface IComfyWorkflowCatalog
{
    IReadOnlyCollection<ComfyTaskDescriptor> Tasks { get; }
    ComfyTaskDescriptor Require(string taskId);
    string ValidationWorkflowJson { get; }
    string ValidationWorkflowId { get; }
}

public interface IComfyUiRuntime
{
    string InputDirectory { get; }
    string OutputDirectory { get; }
    int? ProcessId { get; }
    IComfyUiClient Client { get; }
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IComfyWorkflowExecutor
{
    Task<ComfyExecutionArtifact> ValidateCoreRoundTripAsync(
        CancellationToken cancellationToken = default);
    Task<ComfyExecutionArtifact> ExecuteApprovedAsync(
        ComfyJobSnapshot job,
        IReadOnlyList<ComfyTaskDescriptor> tasks,
        string attemptId,
        CancellationToken cancellationToken = default);
}

public interface IComfyHistoryWriter
{
    string GetHistoryPath(ProjectConfigV1 config, PhotoId photoId, JobId jobId);
    Task WriteAsync(
        ProjectConfigV1 config,
        ComfyJobSnapshot job,
        string configSha256,
        ComfyPlanSnapshot plan,
        string attemptId,
        string status,
        ComfyExecutionArtifact artifact,
        JsonElement taskManifest,
        string historyPath,
        CancellationToken cancellationToken = default);
    Task<ComfyHistoryRecovery?> TryReadRecoveryAsync(
        ComfyJobSnapshot job,
        ComfyPlanSnapshot plan,
        string historyPath,
        CancellationToken cancellationToken = default);
}

public sealed class ComfyStageException(
    string code,
    string category,
    string message,
    bool retryable,
    Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public string Category { get; } = category;
    public bool Retryable { get; } = retryable;
}
