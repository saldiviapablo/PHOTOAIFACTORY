using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Persistence.Processing;

public sealed class SqliteFeedbackStoreFactory(IAppPaths paths) : IFeedbackStoreFactory
{
    public IFeedbackStore Open(ProjectId projectId) =>
        new SqliteFeedbackStore(
            new SqliteProjectDatabase(paths.GetProjectDatabasePath(projectId)));
}
