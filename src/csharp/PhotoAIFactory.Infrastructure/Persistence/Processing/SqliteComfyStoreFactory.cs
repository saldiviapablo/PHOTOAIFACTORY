using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Persistence.Processing;

public sealed class SqliteComfyStoreFactory(IAppPaths paths) : IComfyStoreFactory
{
    public IComfyStore Open(ProjectId projectId) =>
        new SqliteComfyStore(
            new SqliteProjectDatabase(paths.GetProjectDatabasePath(projectId)));
}
