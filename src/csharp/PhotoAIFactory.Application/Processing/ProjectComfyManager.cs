using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Processing;

public sealed class ProjectComfyManager(ComfyOrchestrator orchestrator)
{
    public Task<ComfyRunResult> ProcessNextAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default) =>
        orchestrator.ProcessNextAsync(projectId, cancellationToken);
}
