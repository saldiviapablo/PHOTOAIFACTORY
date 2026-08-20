using Microsoft.Extensions.Logging;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Processing;

/// <summary>
/// Sequential Phase 4 queue entry point. It never skips a FEEDBACK FIFO head
/// and never starts a second heavy Job.
/// </summary>
public sealed class ProjectRevealManager(
    BasicRevealOrchestrator orchestrator,
    ILogger<ProjectRevealManager> logger)
{
    private static readonly EventId DeferredEvent =
        new(4200, "FeedbackDeferred");

    public async Task<IReadOnlyList<RevealRunResult>> ProcessAvailableAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<RevealRunResult>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await orchestrator.ProcessNextAsync(
                projectId, cancellationToken).ConfigureAwait(false);

            if (result.Status == RevealWorkStatus.NoWork)
            {
                return results;
            }

            results.Add(result);

            if (result.Status == RevealWorkStatus.DeferredFeedback)
            {
                logger.LogInformation(
                    DeferredEvent,
                    "Phase 4 stopped at FEEDBACK FIFO head Job {JobId}; Phase 5 owns it",
                    result.JobId?.Value);
                return results;
            }
        }
    }
}
