using System.Text.Json;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Qa;

namespace PhotoAIFactory.Application.Qa;

public sealed record PersistQaResultRequest(
    JobId JobId,
    string AttemptId,
    string Decision,
    JsonElement ResultJson,
    string InputPath,
    string InputSha256,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateReviewItemRequest(
    string ReviewItemId,
    JobId JobId,
    string ReviewKind,
    DateTimeOffset CreatedAtUtc);

public sealed record ResolveReviewItemRequest(
    string ReviewItemId,
    string Resolution,
    string ResolutionOperationId,
    DateTimeOffset ResolvedAtUtc);

public sealed record PersistPublicationRequest(
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

public interface IQaStore
{
    Task<QaJobCandidateSnapshot?> GetNextEligibleQaJobAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    Task<QaJobCandidateSnapshot?> GetJobAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);

    Task<QaResultSnapshot?> GetQaResultAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);

    Task<bool> HasQaResultAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);

    Task<bool> HasCheckpointAsync(
        JobId jobId,
        string stageName,
        CancellationToken cancellationToken = default);

    Task PersistQaResultAsync(
        PersistQaResultRequest request,
        CancellationToken cancellationToken = default);

    Task<ReviewItemSnapshot?> GetPendingReviewItemAsync(
        JobId jobId,
        string reviewKind,
        CancellationToken cancellationToken = default);

    Task<ReviewItemSnapshot?> GetReviewItemByIdAsync(
        string reviewItemId,
        CancellationToken cancellationToken = default);

    Task CreateReviewItemAsync(
        CreateReviewItemRequest request,
        CancellationToken cancellationToken = default);

    Task ResolveReviewItemAsync(
        ResolveReviewItemRequest request,
        CancellationToken cancellationToken = default);

    Task<PublicationSnapshot?> GetPublicationAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);

    Task<bool> HasPublicationAsync(
        JobId jobId,
        CancellationToken cancellationToken = default);

    Task PersistPublicationAsync(
        PersistPublicationRequest request,
        CancellationToken cancellationToken = default);

    Task InsertCheckpointAsync(
        JobId jobId,
        string stageName,
        string attemptId,
        string inputFingerprint,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<bool> ClaimJobForQaAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task TransitionJobStateAsync(
        JobId jobId,
        JobState fromState,
        JobState toState,
        string reason,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<int> ScheduleTechnicalRetryAsync(
        JobId jobId,
        string operationId,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<JobId> CreateChildQualityReprocessJobAsync(
        JobId parentJobId,
        JobId childJobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
}

public interface IQaStoreFactory
{
    IQaStore Open(ProjectId projectId);
}
