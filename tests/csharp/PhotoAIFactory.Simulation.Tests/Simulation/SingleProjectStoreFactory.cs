using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Simulation.Tests.Simulation;

internal sealed class SingleProjectStoreFactory(IProjectStore store) : IProjectStoreFactory
{
    public IProjectStore Open(ProjectId projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        return store;
    }
}
