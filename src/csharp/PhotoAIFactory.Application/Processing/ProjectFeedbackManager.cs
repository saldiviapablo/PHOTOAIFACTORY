using Microsoft.Extensions.Logging;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Processing;

public sealed class ProjectFeedbackManager(
    FeedbackOrchestrator orchestrator,
    ILogger<ProjectFeedbackManager> logger)
{
    private static readonly EventId CompletedEvent = new(4400, "FeedbackQueueItemCompleted");

    public async Task<IReadOnlyList<FeedbackRunResult>> ProcessAvailableAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<FeedbackRunResult>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await orchestrator.ProcessNextAsync(
                projectId, cancellationToken).ConfigureAwait(false);

            if (result.Status == FeedbackWorkStatus.NoWork)
                return results;

            results.Add(result);
            logger.LogInformation(
                CompletedEvent,
                "FEEDBACK queue item completed for Job {JobId}",
                result.JobId?.Value);
        }
    }
}
