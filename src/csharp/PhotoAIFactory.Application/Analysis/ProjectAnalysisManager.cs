using Microsoft.Extensions.Logging;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Ingestion;

namespace PhotoAIFactory.Application.Analysis;

/// <summary>
/// Application-level reconciliation entry point for Phase 3.
/// It processes READY_FOR_ANALYSIS Photos sequentially and deliberately does not
/// become a second queue/orchestration authority.
/// </summary>
public sealed class ProjectAnalysisManager(
    IProjectStoreFactory projectStores,
    IIngestionStoreFactory ingestionStores,
    AnalysisOrchestrator orchestrator,
    ILogger<ProjectAnalysisManager> logger)
{
    private static readonly EventId PhotoFailureEvent = new(3201, "AnalysisPhotoFailed");

    public async Task<IReadOnlyList<AnalysisRunResult>> ProcessReadyAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        var projectStore = projectStores.Open(projectId);
        var snapshot = await projectStore.GetAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId.Value} was not found.");

        if (snapshot.Project.State != ProjectState.Running)
        {
            return [];
        }

        var configVersion = snapshot.LatestConfig;
        var config = configVersion.ReadConfig();
        var ingestionStore = ingestionStores.Open(projectId);
        var ready = (await ingestionStore.ListPhotosAsync(projectId, cancellationToken).ConfigureAwait(false))
            .Where(photo => photo.State == IngestionPhotoState.ReadyForAnalysis)
            .OrderBy(photo => photo.CreatedAtUtc)
            .ThenBy(photo => photo.Id.Value, StringComparer.Ordinal)
            .ToArray();

        var completed = new List<AnalysisRunResult>(ready.Length);
        foreach (var photo in ready)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                completed.Add(await orchestrator.ProcessPhotoAsync(
                    projectId,
                    photo.Id,
                    configVersion.Id,
                    configVersion.Id,
                    config.SemanticMode,
                    config.PreselectionEnabled,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The failed Job has already been durably classified by the
                // orchestrator. Continue so one bad photo never stalls the factory.
                logger.LogError(
                    PhotoFailureEvent,
                    ex,
                    "Phase 3 failed for Project {ProjectId}, Photo {PhotoId}; continuing reconciliation",
                    projectId.Value,
                    photo.Id.Value);
            }
        }

        return completed;
    }
}
