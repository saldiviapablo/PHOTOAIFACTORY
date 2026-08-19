using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Ingestion;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.Ingestion;

public sealed class IngestionSessionFactory(
    IIngestionStoreFactory stores,
    IFileStabilityProbe stability,
    IManagedOriginalArchive archive,
    IRawSupportClassifier rawClassifier,
    IOptions<IngestionRuntimeOptions> options,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : IIngestionSessionFactory
{
    public IIngestionSession Create(
        ProjectId projectId,
        ProjectConfigV1 config,
        IngestionSourceSnapshot source)
    {
        var store = stores.Open(projectId);
        var coordinator = new IngestionCoordinator(
            config,
            source,
            store,
            stability,
            archive,
            rawClassifier,
            timeProvider,
            loggerFactory.CreateLogger<IngestionCoordinator>());

        return new FileSystemIngestionSession(
            projectId,
            config,
            source,
            store,
            coordinator,
            options.Value,
            timeProvider,
            loggerFactory.CreateLogger<FileSystemIngestionSession>());
    }
}
