using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Persistence.Analysis;

public sealed class SqliteAnalysisStoreFactory(IAppPaths paths) : IAnalysisStoreFactory
{
    public IAnalysisStore Open(ProjectId projectId) =>
        new SqliteAnalysisStore(new SqliteProjectDatabase(paths.GetProjectDatabasePath(projectId)));
}
