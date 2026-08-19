using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Hosting;

/// <summary>
/// Phase 1 production-neutral work-status source. Replace this registration with the
/// real Job subsystem adapter when Job execution is introduced.
/// </summary>
public sealed class NoActiveProjectWorkStatus : IProjectWorkStatus
{
    public Task<bool> HasActiveJobAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}
