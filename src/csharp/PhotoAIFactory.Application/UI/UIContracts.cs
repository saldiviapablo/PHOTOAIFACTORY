using System.Text.Json;
using Microsoft.Extensions.Logging;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Domain.Qa;

namespace PhotoAIFactory.Application.UI;

public sealed record ProjectSummaryDto(
    ProjectId Id,
    string Name,
    ProjectState State,
    long StateRevision,
    string InputFolder,
    string OutputFolder,
    RevealMode RevealMode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastActivityUtc,
    int TotalPhotos,
    int CompletedJobs,
    int PendingReviews,
    int ActiveErrors);

public sealed record DashboardSummaryDto(
    ProjectId ProjectId,
    string ProjectName,
    ProjectState State,
    string InputFolder,
    string OutputFolder,
    int ReceivedCount,
    int QueuedCount,
    int ProcessingCount,
    int CompletedCount,
    int ReviewCount,
    int RejectedCount,
    int ErrorCount,
    TimeSpan? AverageProcessingTime,
    bool HasAverageTimeData,
    ActiveJobSummaryDto? ActiveJob,
    IReadOnlyList<ComponentHealthCardDto> ComponentHealth);

public sealed record ActiveJobSummaryDto(
    JobId JobId,
    PhotoId PhotoId,
    string PhotoName,
    JobState State,
    string CurrentStageName,
    bool IsIndeterminateProgress,
    int ProgressPercent,
    TimeSpan ElapsedTime,
    RevealMode RevealMode,
    int SchemaVersion,
    string? PreviewPath,
    int RetryCount,
    int ReprocessCount);

public sealed record QueueItemDto(
    long QueueSequence,
    JobId JobId,
    PhotoId PhotoId,
    string PhotoName,
    string Format,
    JobState State,
    DateTimeOffset QueuedAtUtc,
    string ConfigVersionId,
    RevealMode RevealMode,
    int RetryCount,
    bool IsBlocked);

public sealed record QueueOverviewDto(
    int TotalQueued,
    bool IsPaused,
    bool IsStorageBlocked,
    bool IsComponentUnhealthy,
    ActiveJobSummaryDto? ActiveJob,
    IReadOnlyList<QueueItemDto> Items);

public sealed record JobDetailDto(
    JobId JobId,
    ProjectId ProjectId,
    PhotoId PhotoId,
    string PhotoName,
    string InputPath,
    string InputSha256,
    string InputFormat,
    long InputSizeBytes,
    JobState State,
    string CurrentStage,
    string ConfigVersionId,
    RevealMode RevealMode,
    int TechnicalRetryCount,
    int QualityReprocessCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? PreviewPath,
    string? OutputPublishedPath,
    string? OutputPublishedSha256,
    IReadOnlyList<JobCheckpointDto> Checkpoints,
    IReadOnlyList<JobModelExecutionDto> ModelExecutions,
    QaResultSummaryDto? QaResult,
    string? ParentJobId,
    string? ErrorDetails);

public sealed record JobCheckpointDto(
    string StageName,
    string AttemptId,
    string InputFingerprint,
    string? ArtifactPath,
    string? ArtifactSha256,
    DateTimeOffset CreatedAtUtc);

public sealed record JobModelExecutionDto(
    string ModelId,
    string ModelVersion,
    string? ArtifactSha256,
    JsonElement Parameters,
    JsonElement Timings);

public sealed record QaResultSummaryDto(
    string QaResultId,
    QaDecision Decision,
    string SuggestedNextAction,
    int TechnicalScore,
    JsonElement Findings,
    DateTimeOffset CreatedAtUtc);

public sealed record ReviewItemDto(
    string ReviewItemId,
    ProjectId ProjectId,
    JobId JobId,
    PhotoId PhotoId,
    string PhotoName,
    JobState JobState,
    string ReviewStage,
    string? CandidatePath,
    string? PreviewPath,
    QaDecision? QaDecision,
    JsonElement Findings,
    string? ErrorMessage,
    int ReprocessCount,
    DateTimeOffset CreatedAtUtc);

public sealed record HistoryItemDto(
    JobId JobId,
    PhotoId PhotoId,
    string PhotoName,
    JobState State,
    RevealMode RevealMode,
    string ConfigVersionId,
    string? OutputPath,
    string? OutputSha256,
    long OutputSizeBytes,
    QaDecision? FinalQaDecision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset CompletedAtUtc,
    TimeSpan Duration,
    string? ParentJobId,
    bool HasReprocessChild);

public sealed record ComponentHealthCardDto(
    string ComponentName,
    string DisplayName,
    ComponentHealthState State,
    string StatusText,
    bool CircuitOpen,
    int TotalRestarts,
    DateTimeOffset LastCheckedUtc);

public sealed record ModelDescriptorDto(
    string ModelId,
    string DisplayName,
    string Version,
    string PolicyStatus,
    string Purpose,
    bool IsInstalled,
    string? Sha256,
    string LicenseNotice);

public sealed record ErrorLogEntryDto(
    string LogId,
    DateTimeOffset TimestampUtc,
    LogLevel Level,
    string Component,
    string Message,
    string? ProjectId,
    string? JobId,
    bool IsRetryable,
    string? TechnicalDetails);

public sealed record AppPreferencesDto(
    string Theme,
    bool ShowDiagnostics,
    int RefreshIntervalSeconds,
    bool AutoScrollQueue,
    bool EnableHardwareAccelerationPreview);

public interface IProjectQueryService
{
    Task<IReadOnlyList<ProjectSummaryDto>> ListProjectsAsync(CancellationToken cancellationToken = default);
    Task<ProjectSummaryDto?> GetProjectSummaryAsync(ProjectId projectId, CancellationToken cancellationToken = default);
}

public interface IDashboardQueryService
{
    Task<DashboardSummaryDto?> GetDashboardSummaryAsync(ProjectId projectId, CancellationToken cancellationToken = default);
}

public interface IQueueQueryService
{
    Task<QueueOverviewDto?> GetQueueOverviewAsync(ProjectId projectId, CancellationToken cancellationToken = default);
    Task<JobDetailDto?> GetJobDetailAsync(ProjectId projectId, JobId jobId, CancellationToken cancellationToken = default);
}

public interface IReviewQueryService
{
    Task<IReadOnlyList<ReviewItemDto>> GetPendingReviewsAsync(ProjectId projectId, CancellationToken cancellationToken = default);
}

public interface IHistoryQueryService
{
    Task<IReadOnlyList<HistoryItemDto>> GetHistoryAsync(ProjectId projectId, int limit = 200, CancellationToken cancellationToken = default);
}

public interface IModelStatusService
{
    Task<IReadOnlyList<ComponentHealthCardDto>> GetComponentStatusesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelDescriptorDto>> GetModelDescriptorsAsync(CancellationToken cancellationToken = default);
}

public interface IErrorLogQueryService
{
    Task<IReadOnlyList<ErrorLogEntryDto>> GetErrorLogsAsync(
        ProjectId? projectId = null,
        JobId? jobId = null,
        LogLevel? minLevel = null,
        int limit = 200,
        CancellationToken cancellationToken = default);
}

public interface IThumbnailService
{
    Task<byte[]?> GetThumbnailBytesAsync(
        string imagePath,
        int maxWidth = 256,
        int maxHeight = 256,
        CancellationToken cancellationToken = default);
}

public interface IAppPreferencesService
{
    Task<AppPreferencesDto> GetPreferencesAsync(CancellationToken cancellationToken = default);
    Task SavePreferencesAsync(AppPreferencesDto preferences, CancellationToken cancellationToken = default);
}
