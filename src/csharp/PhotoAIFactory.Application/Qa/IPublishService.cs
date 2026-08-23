using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Qa;

namespace PhotoAIFactory.Application.Qa;

public sealed record PublishCandidateRequest(
    JobId JobId,
    ProjectId ProjectId,
    PhotoId PhotoId,
    string AttemptId,
    string SourceCandidatePath,
    string ExpectedSourceSha256,
    string DestinationKind,
    QaResultSnapshot QaResult,
    string OutputRootFolder);

public sealed record PublishResult(
    string PublicationId,
    string DestinationPath,
    string Sha256,
    long SizeBytes,
    int Width,
    int Height,
    string HistoryPath,
    DateTimeOffset PublishedAtUtc);

public interface IPublishService
{
    Task<PublishResult> PublishAsync(
        PublishCandidateRequest request,
        CancellationToken cancellationToken = default);
}
