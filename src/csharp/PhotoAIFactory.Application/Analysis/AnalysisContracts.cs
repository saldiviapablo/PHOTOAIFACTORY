using System.Text.Json;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Analysis;
using PhotoAIFactory.Domain.Ingestion;

namespace PhotoAIFactory.Application.Analysis;

public sealed record ResolvedAnalysisInput(
    AssetId SourceAssetId,
    string SourceSha256,
    AnalysisInputKind Kind,
    string RepresentationPath,
    bool IsRegenerablePreview);

public sealed record AnalysisModelExecution(
    string ModelId,
    string ModelVersion,
    string? ArtifactSetSha256,
    JsonElement Parameters,
    JsonElement Timings);

public interface IAnalysisPreviewProvider
{
    string GetPreviewPath(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        AssetSnapshot rawAsset);

    Task EnsurePreviewAsync(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        AssetSnapshot rawAsset,
        string destinationPath,
        CancellationToken cancellationToken = default);
}

public interface IAnalysisInputResolver
{
    Task<ResolvedAnalysisInput> ResolveAsync(
        ProjectId projectId,
        PhotoId photoId,
        JobId jobId,
        string attemptId,
        CancellationToken cancellationToken = default);

    Task EnsureRepresentationAsync(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        ResolvedAnalysisInput input,
        CancellationToken cancellationToken = default);
}

public interface IAnalysisStore
{
    Task<AnalysisJobSnapshot?> GetInitialJobByPhotoAsync(
        ProjectId projectId,
        PhotoId photoId,
        CancellationToken cancellationToken = default);

    Task<AnalysisJobSnapshot> GetOrCreateInitialJobAsync(
        JobId proposedJobId,
        ProjectId projectId,
        PhotoId photoId,
        string preselectionConfigId,
        string processingConfigId,
        ResolvedAnalysisInput input,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<AnalysisJobSnapshot?> GetJobAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);

    Task<AnalysisResultSnapshot?> GetAnalysisAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);

    Task<PreselectionResultSnapshot?> GetPreselectionAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);

    Task MarkAnalyzingAsync(
        JobId jobId,
        string operationId,
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

    Task IncrementTechnicalRetryAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task PersistAnalysisCompleteAsync(
        AnalysisJobSnapshot job,
        string attemptId,
        int schemaVersion,
        JsonElement result,
        IReadOnlyList<AnalysisModelExecution> modelExecutions,
        string inputFingerprint,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task PersistPreselectionCompleteAsync(
        AnalysisJobSnapshot job,
        string attemptId,
        PreselectionDecision decision,
        JsonElement findings,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<bool> HasCheckpointAsync(
        JobId jobId,
        string stageName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QueueEntrySnapshot>> ListQueueAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    Task RequestProcessNextAsync(
        ProjectId projectId,
        JobId jobId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

public interface IAnalysisStoreFactory
{
    IAnalysisStore Open(ProjectId projectId);
}

public sealed record AnalysisRunResult(
    AnalysisJobSnapshot Job,
    AnalysisResultSnapshot Analysis,
    PreselectionResultSnapshot Preselection,
    IReadOnlyList<QueueEntrySnapshot> Queue);
