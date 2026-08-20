using System.Text.Json;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Processing;

public sealed record DarktableControlPlan(
    string PolicyId,
    string? XmpPath,
    string? XmpSha256,
    string? Style,
    bool ApplyCustomPresets,
    JsonElement Details);

public sealed record BasicRevealArtifact(
    string Path,
    string Sha256,
    long SizeBytes,
    int Width,
    int Height,
    string DarktableVersion,
    TimeSpan Duration);

public sealed record BasicRevealRecovery(
    string AttemptId,
    BasicRevealArtifact Artifact);

public sealed record BasicRevealPersistRequest(
    BasicRevealJobSnapshot Job,
    string AttemptId,
    RevealMode RevealMode,
    JsonElement? Recipe,
    int? RecipeSchemaVersion,
    string? RecipeSha256,
    DarktableControlPlan ControlPlan,
    BasicRevealArtifact Artifact,
    string HistoryPath,
    string? XmpHistoryPath,
    DateTimeOffset CompletedAtUtc);

public enum RevealWorkStatus { NoWork, Completed, DeferredFeedback }

public sealed record RevealRunResult(
    RevealWorkStatus Status,
    BasicRevealPassSnapshot? Pass,
    JobId? JobId);

public interface IProcessingStore
{
    Task<BasicRevealJobSnapshot?> GetActiveAsync(ProjectId projectId, CancellationToken cancellationToken = default);
    Task<BasicRevealJobSnapshot?> PeekNextQueuedAsync(ProjectId projectId, CancellationToken cancellationToken = default);
    Task<bool> TryClaimAsync(JobId jobId, string operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<bool> ResumeRetryAsync(JobId jobId, string operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<bool> ResumeInterruptedAsync(JobId jobId, string operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task<JsonElement?> GetAnalysisResultAsync(JobId jobId, CancellationToken cancellationToken = default);
    Task<BasicRevealPassSnapshot?> GetBasicRevealPassAsync(JobId jobId, CancellationToken cancellationToken = default);
    Task<bool> HasBasicRevealCheckpointAsync(JobId jobId, CancellationToken cancellationToken = default);
    Task<int> ScheduleRevealRetryAsync(JobId jobId, string operationId, string reason, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task PersistBasicRevealCompleteAsync(BasicRevealPersistRequest request, CancellationToken cancellationToken = default);
    Task MarkInterruptedAsync(JobId jobId, string operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
    Task MarkErrorAsync(JobId jobId, string operationId, string reason, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}

public interface IProcessingStoreFactory
{
    IProcessingStore Open(ProjectId projectId);
}

public interface IDarktableRecipeCompiler
{
    DarktableControlPlan Compile(RevealMode revealMode, JsonElement? recipe, ProjectConfigV1 config);
}

public interface IBasicRevealExecutor
{
    string GetOutputPath(ProjectId projectId, JobId jobId, string attemptId);
    Task<BasicRevealArtifact> ExportAsync(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        BasicRevealJobSnapshot job,
        DarktableControlPlan plan,
        int jpegQuality,
        CancellationToken cancellationToken = default);
    Task<BasicRevealArtifact> RecoverAsync(
        ProjectId projectId,
        JobId jobId,
        BasicRevealJobSnapshot job,
        BasicRevealRecovery recovery,
        CancellationToken cancellationToken = default);
}

public interface IProcessingHistoryWriter
{
    string GetHistoryPath(ProjectConfigV1 config, PhotoId photoId, JobId jobId);
    Task<string> WriteAsync(
        ProjectConfigV1 config,
        BasicRevealJobSnapshot job,
        RevealMode revealMode,
        JsonElement? recipe,
        string processingConfigSha256,
        DarktableControlPlan plan,
        BasicRevealArtifact artifact,
        string attemptId,
        string historyPath,
        CancellationToken cancellationToken = default);
    Task<BasicRevealRecovery?> TryReadRecoveryAsync(
        ProjectConfigV1 config,
        BasicRevealJobSnapshot job,
        RevealMode revealMode,
        JsonElement? recipe,
        string processingConfigSha256,
        DarktableControlPlan plan,
        string historyPath,
        CancellationToken cancellationToken = default);
}

public sealed class RevealStageException(
    string code,
    string category,
    string message,
    bool retryable,
    Exception? innerException = null)
    : Exception($"{code} [{category}]: {message}", innerException)
{
    public string Code { get; } = code;
    public string Category { get; } = category;
    public bool Retryable { get; } = retryable;
}

public sealed class RevealHistoryCollisionException(string message) : IOException(message);
