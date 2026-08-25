using Microsoft.Extensions.Logging;
using PhotoAIFactory.Application.Health;
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
    IAnalysisStoreFactory analysisStores,
    AnalysisOrchestrator orchestrator,
    ILogger<ProjectAnalysisManager> logger,
    IComponentHealthTracker? healthTracker = null,
    TimeProvider? timeProvider = null)
{
    private static readonly EventId PhotoFailureEvent = new(3201, "AnalysisPhotoFailed");

    public async Task<AnalysisDispatchResult> ProcessReadyAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        var projectStore = projectStores.Open(projectId);
        var snapshot = await projectStore.GetAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId.Value} was not found.");

        if (!ProjectDispatchGuard.CanDispatchNextJob(snapshot.Project.State, healthTracker))
        {
            return new AnalysisDispatchResult(AnalysisDispatchStatus.NoWork, 0, []);
        }

        var configVersion = snapshot.LatestConfig;
        var config = configVersion.ReadConfig();
        var ingestionStore = ingestionStores.Open(projectId);
        var analysisStore = analysisStores.Open(projectId);

        var latestSource = await ingestionStore.GetLatestSourceAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (latestSource is not null)
        {
            await ingestionStore.FinalizeAssociationsAsync(
                projectId,
                latestSource.Id,
                (timeProvider ?? TimeProvider.System).GetUtcNow(),
                force: false,
                cancellationToken).ConfigureAwait(false);
        }

        var ready = (await ingestionStore.ListPhotosAsync(projectId, cancellationToken).ConfigureAwait(false))
            .Where(photo => photo.State == IngestionPhotoState.ReadyForAnalysis)
            .OrderBy(photo => photo.CreatedAtUtc)
            .ThenBy(photo => photo.Id.Value, StringComparer.Ordinal)
            .ToArray();

        var newlyDispatched = new List<AnalysisRunResult>();
        var hasSuppressed = false;

        foreach (var photo in ready)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existingJob = await analysisStore.GetInitialJobByPhotoAsync(projectId, photo.Id, cancellationToken).ConfigureAwait(false);
            var hasAnalysisComplete = existingJob is not null && await analysisStore.HasCheckpointAsync(existingJob.Id, "ANALYSIS_COMPLETE", cancellationToken).ConfigureAwait(false);

            if (!AnalysisEligibilityRule.IsEligibleForAnalysis(photo, existingJob, hasAnalysisComplete))
            {
                hasSuppressed = true;
                continue;
            }

            try
            {
                var runResult = await orchestrator.ProcessPhotoAsync(
                    projectId,
                    photo.Id,
                    configVersion.Id,
                    configVersion.Id,
                    config.SemanticMode,
                    config.PreselectionEnabled,
                    cancellationToken).ConfigureAwait(false);

                newlyDispatched.Add(runResult);
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

        if (newlyDispatched.Count > 0)
        {
            return new AnalysisDispatchResult(AnalysisDispatchStatus.AnalysisCompleted, newlyDispatched.Count, newlyDispatched);
        }

        return new AnalysisDispatchResult(
            hasSuppressed ? AnalysisDispatchStatus.Suppressed : AnalysisDispatchStatus.NoWork,
            0,
            []);
    }
}
