using System.Collections.Concurrent;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Simulation.Tests.Simulation;

internal sealed class ScriptedProjectWorkStatus : IProjectWorkStatus
{
    private readonly ConcurrentDictionary<string, bool> active =
        new(StringComparer.Ordinal);

    public void SetActive(ProjectId projectId, bool value)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        active[projectId.Value] = value;
    }

    public Task<bool> HasActiveJobAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(active.TryGetValue(projectId.Value, out var value) && value);
    }
}
