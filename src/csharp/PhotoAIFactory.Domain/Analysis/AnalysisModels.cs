using System.Text.Json;
using PhotoAIFactory.Domain.Ingestion;

namespace PhotoAIFactory.Domain.Analysis;

public enum AnalysisInputKind
{
    JpegCamera,
    JpegMaster,
    RawPreview
}

public sealed record AnalysisJobSnapshot(
    JobId Id,
    ProjectId ProjectId,
    PhotoId PhotoId,
    JobState State,
    string PreselectionConfigId,
    string ProcessingConfigId,
    AssetId AnalysisSourceAssetId,
    string AnalysisSourceSha256,
    AnalysisInputKind AnalysisInputKind,
    string AnalysisRepresentationPath,
    int TechnicalRetryCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record AnalysisResultSnapshot(
    string AnalysisId,
    JobId JobId,
    int SchemaVersion,
    JsonElement Result,
    DateTimeOffset CreatedAtUtc);

public sealed record ModelExecutionSnapshot(
    string ModelExecutionId,
    JobId JobId,
    string Stage,
    string ModelId,
    string ModelVersion,
    string? ArtifactSetSha256,
    JsonElement Parameters,
    JsonElement Timings,
    DateTimeOffset CreatedAtUtc);

public sealed record PreselectionResultSnapshot(
    string PreselectionId,
    JobId JobId,
    PreselectionDecision Decision,
    JsonElement Findings,
    DateTimeOffset CreatedAtUtc);

public sealed record QueueEntrySnapshot(
    string QueueEntryId,
    ProjectId ProjectId,
    JobId JobId,
    long SequenceNumber,
    bool ProcessNext,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset? ProcessNextRequestedAtUtc);
