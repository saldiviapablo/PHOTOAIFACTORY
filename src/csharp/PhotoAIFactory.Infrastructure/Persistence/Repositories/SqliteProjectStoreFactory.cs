using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Persistence.Repositories;

public sealed class SqliteProjectStoreFactory(IAppPaths appPaths) : IProjectStoreFactory
{
    public IProjectStore Open(ProjectId projectId) =>
        new SqliteProjectStore(new SqliteProjectDatabase(appPaths.GetProjectDatabasePath(projectId)));
}
