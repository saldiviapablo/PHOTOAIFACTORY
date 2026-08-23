using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Qa;

public sealed class ReviewService(
    IQaStoreFactory storeFactory,
    IPublishService publishService) : IReviewService
{
    public async Task ApproveAsync(
        ProjectId projectId,
        JobId jobId,
        string operationId,
        string outputRootFolder,
        CancellationToken cancellationToken = default)
    {
        var store = storeFactory.Open(projectId);
        var pendingReview = await store.GetPendingReviewItemAsync(jobId, "FINAL", cancellationToken).ConfigureAwait(false);
        var job = await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {jobId.Value} not found.");

        if (pendingReview is null)
        {
            if (job.State == JobState.Completed)
                return; // Already approved/completed idempotently

            throw new InvalidOperationException($"No pending FINAL review item found for job {jobId.Value}.");
        }

        if (job.State != JobState.ReviewFinal)
        {
            throw new InvalidOperationException($"Job {jobId.Value} is in state {job.State}, expected REVIEW_FINAL.");
        }

        var qaResult = await store.GetQaResultAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No QA result found for job {jobId.Value}.");

        var pubResult = await publishService.PublishAsync(
            new PublishCandidateRequest(
                job.JobId,
                job.ProjectId,
                job.PhotoId,
                "review-approve",
                job.CandidatePath,
                job.CandidateSha256,
                "FINAL",
                qaResult,
                outputRootFolder),
            cancellationToken).ConfigureAwait(false);

        await store.PersistPublicationAsync(
            new PersistPublicationRequest(
                pubResult.PublicationId,
                job.JobId,
                "review-approve",
                "FINAL",
                pubResult.DestinationPath,
                pubResult.Sha256,
                pubResult.SizeBytes,
                pubResult.Width,
                pubResult.Height,
                pubResult.HistoryPath,
                pubResult.PublishedAtUtc),
            cancellationToken).ConfigureAwait(false);

        await store.InsertCheckpointAsync(
            job.JobId,
            "OUTPUT_PUBLISHED",
            "review-approve",
            pubResult.Sha256,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        await store.TransitionJobStateAsync(
            job.JobId,
            JobState.ReviewFinal,
            JobState.Completed,
            "REVIEW_APPROVED",
            operationId,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        await store.ResolveReviewItemAsync(
            new ResolveReviewItemRequest(
                pendingReview.ReviewItemId,
                "APPROVED",
                operationId,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RejectAsync(
        ProjectId projectId,
        JobId jobId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var store = storeFactory.Open(projectId);
        var pendingReview = await store.GetPendingReviewItemAsync(jobId, "FINAL", cancellationToken).ConfigureAwait(false);
        var job = await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {jobId.Value} not found.");

        if (pendingReview is null)
        {
            if (job.State == JobState.RejectedFinal)
                return; // Idempotent

            throw new InvalidOperationException($"No pending FINAL review item found for job {jobId.Value}.");
        }

        if (job.State != JobState.ReviewFinal)
        {
            throw new InvalidOperationException($"Job {jobId.Value} is in state {job.State}, expected REVIEW_FINAL.");
        }

        await store.TransitionJobStateAsync(
            job.JobId,
            JobState.ReviewFinal,
            JobState.RejectedFinal,
            "REVIEW_REJECTED",
            operationId,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        await store.ResolveReviewItemAsync(
            new ResolveReviewItemRequest(
                pendingReview.ReviewItemId,
                "REJECTED",
                operationId,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobId> ReprocessAsync(
        ProjectId projectId,
        JobId jobId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var store = storeFactory.Open(projectId);
        var pendingReview = await store.GetPendingReviewItemAsync(jobId, "FINAL", cancellationToken).ConfigureAwait(false);
        var job = await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {jobId.Value} not found.");

        if (pendingReview is null)
        {
            throw new InvalidOperationException($"No pending FINAL review item found for job {jobId.Value}.");
        }

        if (job.State != JobState.ReviewFinal)
        {
            throw new InvalidOperationException($"Job {jobId.Value} is in state {job.State}, expected REVIEW_FINAL.");
        }

        if (job.QualityReprocessCount >= 1)
        {
            throw new InvalidOperationException($"Job {jobId.Value} has already reached maximum quality reprocess limit.");
        }

        var childJobId = JobId.New();
        var createdId = await store.CreateChildQualityReprocessJobAsync(
            job.JobId,
            childJobId,
            operationId,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        await store.ResolveReviewItemAsync(
            new ResolveReviewItemRequest(
                pendingReview.ReviewItemId,
                "REPROCESS",
                operationId,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        return createdId;
    }

    public async Task LeavePendingAsync(
        ProjectId projectId,
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        var store = storeFactory.Open(projectId);
        var job = await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {jobId.Value} not found.");

        if (job.State != JobState.ReviewFinal)
        {
            throw new InvalidOperationException($"Job {jobId.Value} is in state {job.State}, expected REVIEW_FINAL.");
        }

        // Left untouched as pending
    }
}
