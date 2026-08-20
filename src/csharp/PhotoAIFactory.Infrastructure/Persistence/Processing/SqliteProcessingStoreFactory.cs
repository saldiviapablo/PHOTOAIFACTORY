using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Persistence.Processing;

public sealed class SqliteProcessingStoreFactory(IAppPaths paths) : IProcessingStoreFactory
{
    public IProcessingStore Open(ProjectId projectId) =>
        new SqliteProcessingStore(
            new SqliteProjectDatabase(paths.GetProjectDatabasePath(projectId)));
}
