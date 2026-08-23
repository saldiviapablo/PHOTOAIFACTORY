using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Persistence.Qa;

public sealed class SqliteQaStoreFactory(IAppPaths paths) : IQaStoreFactory
{
    public IQaStore Open(ProjectId projectId) =>
        new SqliteQaStore(
            new SqliteProjectDatabase(paths.GetProjectDatabasePath(projectId)));
}
