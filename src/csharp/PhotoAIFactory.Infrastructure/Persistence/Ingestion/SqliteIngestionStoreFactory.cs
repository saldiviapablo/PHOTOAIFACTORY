using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Persistence.Ingestion;

public sealed class SqliteIngestionStoreFactory(IAppPaths appPaths) : IIngestionStoreFactory
{
    public IIngestionStore Open(ProjectId projectId) =>
        new SqliteIngestionStore(
            new SqliteProjectDatabase(appPaths.GetProjectDatabasePath(projectId)));
}
