using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Processing;

public enum ProcessingDispatchStatus
{
    NoWork,
    BasicRevealCompleted,
    FeedbackCompleted
}

public sealed record ProcessingDispatchResult(
    ProcessingDispatchStatus Status,
    JobId? JobId);

/// <summary>
/// V1 reveal-mode router. It inspects the durable queue/active Job and delegates
/// to exactly one reveal orchestrator. The orchestrators share the same
/// RevealExecutionCoordinator, so this router does not create another
/// concurrency or GPU lock.
/// </summary>
public sealed class ProjectProcessingManager(
    IProcessingStoreFactory processingStores,
    IProjectStoreFactory projectStores,
    BasicRevealOrchestrator basicReveal,
    FeedbackOrchestrator feedback)
{
    public async Task<ProcessingDispatchResult> ProcessNextAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await projectStores.Open(projectId)
            .GetAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Project {projectId.Value} was not found.");

        var store = processingStores.Open(projectId);
        var candidate = await store.GetActiveAsync(
            projectId, cancellationToken).ConfigureAwait(false)
            ?? await store.PeekNextQueuedAsync(
                projectId, cancellationToken).ConfigureAwait(false);

        if (candidate is null)
            return new(ProcessingDispatchStatus.NoWork, null);

        var configVersion = project.ConfigVersions.SingleOrDefault(
            item => string.Equals(
                item.Id,
                candidate.ProcessingConfigId,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Processing ConfigVersion {candidate.ProcessingConfigId} was not found.");

        var config = configVersion.ReadConfig();
        if (config.RevealMode == RevealMode.Feedback)
        {
            var result = await feedback.ProcessNextAsync(
                projectId, cancellationToken).ConfigureAwait(false);
            return result.Status == FeedbackWorkStatus.Completed
                ? new(ProcessingDispatchStatus.FeedbackCompleted, result.JobId)
                : new(ProcessingDispatchStatus.NoWork, result.JobId);
        }

        var basic = await basicReveal.ProcessNextAsync(
            projectId, cancellationToken).ConfigureAwait(false);
        return basic.Status == RevealWorkStatus.Completed
            ? new(ProcessingDispatchStatus.BasicRevealCompleted, basic.JobId)
            : new(ProcessingDispatchStatus.NoWork, basic.JobId);
    }
}
