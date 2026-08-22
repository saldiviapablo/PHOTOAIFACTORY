using System.Text.Json;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Processing;

public sealed record FeedbackImageArtifact(
    string Path,
    string Sha256,
    long SizeBytes,
    int Width,
    int Height,
    int BitsPerSample,
    int Channels,
    string DarktableVersion,
    TimeSpan Duration,
    byte[] AuthenticXmp);

public sealed record FeedbackPersistPass1Request(
    FeedbackJobSnapshot Job,
    string AttemptId,
    FeedbackImageArtifact Artifact,
    string XmpPath,
    string XmpSha256,
    JsonElement ControlPlan,
    DateTimeOffset CompletedAtUtc);

public sealed record FeedbackPersistInspectionRequest(
    FeedbackJobSnapshot Job,
    int SchemaVersion,
    JsonElement Recipe,
    string RecipeSha256,
    JsonElement Inspection,
    DateTimeOffset CompletedAtUtc);

public sealed record FeedbackPersistPass2Request(
    FeedbackJobSnapshot Job,
    string AttemptId,
    FeedbackImageArtifact Artifact,
    string XmpPath,
    string XmpSha256,
    string HistoryPath,
    JsonElement ControlPlan,
    DateTimeOffset CompletedAtUtc);

public sealed record FeedbackPass2Recovery(
    string AttemptId,
    FeedbackImageArtifact Artifact,
    string XmpPath,
    string XmpSha256);

public enum FeedbackWorkStatus
{
    NoWork,
    Completed
}

public sealed record FeedbackRunResult(
    FeedbackWorkStatus Status,
    FeedbackPassSnapshot? Pass2,
    JobId? JobId);

public interface IFeedbackStore
{
    Task<FeedbackJobSnapshot?> GetActiveAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    Task<FeedbackJobSnapshot?> PeekNextQueuedAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    Task<bool> TryClaimAsync(
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

    Task<JsonElement?> GetAnalysisResultAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);

    Task<FeedbackPassSnapshot?> GetPassAsync(
        JobId jobId,
        int passNumber,
        CancellationToken cancellationToken = default);

    Task<FeedbackInspectionSnapshot?> GetInspectionAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);

    Task<bool> HasCheckpointAsync(
        JobId jobId,
        string stageName,
        CancellationToken cancellationToken = default);

    Task PersistPass1CompleteAsync(
        FeedbackPersistPass1Request request,
        CancellationToken cancellationToken = default);

    Task PersistInspectionCompleteAsync(
        FeedbackPersistInspectionRequest request,
        CancellationToken cancellationToken = default);

    Task PersistPass2CompleteAsync(
        FeedbackPersistPass2Request request,
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
}

public interface IFeedbackStoreFactory
{
    IFeedbackStore Open(ProjectId projectId);
}

public interface IDarktableFeedbackExecutor
{
    Task<FeedbackImageArtifact> ExportPass1Async(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        FeedbackJobSnapshot job,
        CancellationToken cancellationToken = default);

    Task<FeedbackImageArtifact> ValidatePersistedPass1Async(
        FeedbackJobSnapshot job,
        FeedbackPassSnapshot pass,
        CancellationToken cancellationToken = default);

    Task<FeedbackImageArtifact> ExportPass2Async(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        FeedbackJobSnapshot job,
        FeedbackPassSnapshot pass1,
        int jpegQuality,
        CancellationToken cancellationToken = default);

    Task<FeedbackImageArtifact> RecoverPass2Async(
        FeedbackJobSnapshot job,
        FeedbackPass2Recovery recovery,
        CancellationToken cancellationToken = default);

    Task CleanupPass1TemporaryAsync(
        FeedbackPassSnapshot pass1,
        CancellationToken cancellationToken = default);
}

public interface IFeedbackHistoryWriter
{
    string GetHistoryPath(ProjectConfigV1 config, PhotoId photoId, JobId jobId);
    string GetXmpPath(ProjectConfigV1 config, PhotoId photoId, JobId jobId, int passNumber);

    Task<string> WriteXmpImmutableAsync(
        ProjectConfigV1 config,
        PhotoId photoId,
        JobId jobId,
        int passNumber,
        byte[] xmp,
        CancellationToken cancellationToken = default);

    Task WriteFinalAsync(
        ProjectConfigV1 config,
        FeedbackJobSnapshot job,
        string processingConfigSha256,
        FeedbackPassSnapshot pass1,
        FeedbackInspectionSnapshot inspection,
        FeedbackImageArtifact pass2,
        string pass2AttemptId,
        string pass2XmpPath,
        string historyPath,
        CancellationToken cancellationToken = default);

    Task<FeedbackPass2Recovery?> TryReadPass2RecoveryAsync(
        ProjectConfigV1 config,
        FeedbackJobSnapshot job,
        string processingConfigSha256,
        FeedbackPassSnapshot pass1,
        FeedbackInspectionSnapshot inspection,
        string historyPath,
        CancellationToken cancellationToken = default);
}
