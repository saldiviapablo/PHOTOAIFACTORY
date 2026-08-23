using System.Text.Json;

namespace PhotoAIFactory.Domain.Qa;

public sealed record QaJobCandidateSnapshot(
    JobId JobId,
    ProjectId ProjectId,
    PhotoId PhotoId,
    JobState State,
    string ProcessingConfigId,
    string CandidatePath,
    string CandidateSha256,
    long CandidateSizeBytes,
    int TechnicalRetryCount,
    int QualityReprocessCount,
    string? ParentJobId);

public sealed record QaResultSnapshot(
    string QaResultId,
    JobId JobId,
    string AttemptId,
    string Decision,
    JsonElement ResultJson,
    string InputPath,
    string InputSha256,
    DateTimeOffset CreatedAtUtc);

public sealed record ReviewItemSnapshot(
    string ReviewItemId,
    JobId JobId,
    string ReviewKind,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    string? Resolution,
    string? ResolutionOperationId);

public sealed record PublicationSnapshot(
    string PublicationId,
    JobId JobId,
    string AttemptId,
    string DestinationKind,
    string DestinationPath,
    string Sha256,
    long SizeBytes,
    int Width,
    int Height,
    string HistoryPath,
    DateTimeOffset PublishedAtUtc);
