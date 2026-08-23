using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Qa;

public sealed class ProjectQaManager(
    IQaStoreFactory storeFactory,
    QaOrchestrator qaOrchestrator)
{
    public async Task<int> ProcessEligibleQaJobsAsync(
        ProjectId projectId,
        string outputRootFolder,
        CancellationToken cancellationToken = default)
    {
        var store = storeFactory.Open(projectId);
        var processedCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var next = await store.GetNextEligibleQaJobAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (next is null)
                break;

            var processed = await qaOrchestrator.ProcessJobAsync(
                projectId,
                next.JobId,
                outputRootFolder,
                cancellationToken).ConfigureAwait(false);

            if (!processed)
                break;

            processedCount++;
        }

        return processedCount;
    }
}
