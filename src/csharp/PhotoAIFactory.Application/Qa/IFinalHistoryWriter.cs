using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Qa;

namespace PhotoAIFactory.Application.Qa;

public interface IFinalHistoryWriter
{
    Task<string> WriteFinalHistoryAsync(
        ProjectId projectId,
        PhotoId photoId,
        JobId jobId,
        string attemptId,
        string destinationPath,
        string destinationSha256,
        long destinationSizeBytes,
        int width,
        int height,
        QaResultSnapshot qaResult,
        string outputRootFolder,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default);
}
