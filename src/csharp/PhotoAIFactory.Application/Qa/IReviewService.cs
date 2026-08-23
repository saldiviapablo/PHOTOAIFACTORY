using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Qa;

public interface IReviewService
{
    Task ApproveAsync(
        ProjectId projectId,
        JobId jobId,
        string operationId,
        string outputRootFolder,
        CancellationToken cancellationToken = default);

    Task RejectAsync(
        ProjectId projectId,
        JobId jobId,
        string operationId,
        CancellationToken cancellationToken = default);

    Task<JobId> ReprocessAsync(
        ProjectId projectId,
        JobId jobId,
        string operationId,
        CancellationToken cancellationToken = default);

    Task LeavePendingAsync(
        ProjectId projectId,
        JobId jobId,
        CancellationToken cancellationToken = default);
}
