using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Infrastructure.Qa;

namespace PhotoAIFactory.Infrastructure.Processing;

/// <summary>
/// Autonomous production queue dispatcher service.
/// It continuously advances eligible RUNNING projects through the approved pipeline:
/// 1. READY_FOR_ANALYSIS -> Job creation, AI analysis, Preselection, Queue
/// 2. QUEUED -> Reveal / Feedback / Comfy
/// 3. QA -> Evaluation and publication
/// </summary>
public sealed class QueueDispatcherService(
    IServiceScopeFactory scopeFactory,
    IComponentHealthTracker healthTracker,
    ILogger<QueueDispatcherService> logger) : BackgroundService
{
    private static readonly EventId StartedEvent = new(4900, "QueueDispatcherStarted");
    private static readonly EventId StoppingEvent = new(4901, "QueueDispatcherStopping");
    private static readonly EventId CycleEvent = new(4902, "ProjectDispatchCycle");
    private static readonly EventId AnalysisEvent = new(4903, "AnalysisDispatched");
    private static readonly EventId ProcessingEvent = new(4904, "ProcessingDispatched");
    private static readonly EventId ErrorEvent = new(4999, "QueueDispatcherError");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(StartedEvent, "QueueDispatcherService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var workDone = false;
            try
            {
                workDone = await DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ErrorEvent, ex, "Unexpected error in QueueDispatcherService dispatch cycle.");
                healthTracker.RecordFailure("QueueDispatcher", ex.Message);
            }

            if (workDone)
            {
                // Brief pause to allow cooperative cancellation and prevent CPU burn
                await Task.Delay(25, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                // Bounded sleep when idle
                await Task.Delay(250, stoppingToken).ConfigureAwait(false);
            }
        }

        logger.LogInformation(StoppingEvent, "QueueDispatcherService stopping.");
    }

    public async Task<bool> DispatchOnceAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var queryService = scope.ServiceProvider.GetRequiredService<IProjectQueryService>();
        var analysisManager = scope.ServiceProvider.GetRequiredService<ProjectAnalysisManager>();
        var processingManager = scope.ServiceProvider.GetRequiredService<ProjectProcessingManager>();
        var qaManager = scope.ServiceProvider.GetRequiredService<ProjectQaManager>();

        var projects = await queryService.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        var runningProjects = projects.Where(p => p.State == ProjectState.Running).ToList();

        if (runningProjects.Count == 0)
            return false;

        var workDone = false;

        foreach (var project in runningProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ProjectDispatchGuard.CanDispatchNextJob(project.State, healthTracker))
                continue;

            // 1. Advance READY_FOR_ANALYSIS photos -> Job creation, AI Analysis, Preselection, Queue
            try
            {
                var analysisResult = await analysisManager.ProcessReadyAsync(project.Id, cancellationToken).ConfigureAwait(false);
                if (analysisResult.Status == AnalysisDispatchStatus.AnalysisCompleted && analysisResult.NewlyDispatchedCount > 0)
                {
                    logger.LogInformation(
                        AnalysisEvent,
                        "Dispatched analysis for {Count} photos in Project {ProjectId}",
                        analysisResult.NewlyDispatchedCount,
                        project.Id.Value);
                    workDone = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ErrorEvent, ex, "Error processing ready photos for Project {ProjectId}", project.Id.Value);
            }

            // 2. Advance next queued job -> Reveal / Feedback / Comfy
            try
            {
                var procResult = await processingManager.ProcessNextAsync(project.Id, cancellationToken).ConfigureAwait(false);
                if (procResult.Status != ProcessingDispatchStatus.NoWork)
                {
                    logger.LogInformation(ProcessingEvent, "Dispatched processing for Job {JobId} in Project {ProjectId}: {Status}", procResult.JobId?.Value, project.Id.Value, procResult.Status);
                    workDone = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ErrorEvent, ex, "Error processing next queued job for Project {ProjectId}", project.Id.Value);
            }

            // 3. Advance QA & Publication for jobs that completed reveal
            try
            {
                var qaProcessed = await qaManager.ProcessEligibleQaJobsAsync(project.Id, project.OutputFolder, cancellationToken).ConfigureAwait(false);
                if (qaProcessed > 0)
                {
                    workDone = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ErrorEvent, ex, "Error processing QA jobs for Project {ProjectId}", project.Id.Value);
            }
        }

        return workDone;
    }
}
