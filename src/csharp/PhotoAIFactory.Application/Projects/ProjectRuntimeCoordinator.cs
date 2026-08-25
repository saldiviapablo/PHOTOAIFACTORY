using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Projects;

public sealed class ProjectRuntimeCoordinationException : InvalidOperationException
{
    public LifecycleResultStatus Status { get; }
    public Project? Project { get; }
    public ProjectId? ProjectId { get; }
    public ProjectState? DurableState => Project?.State;
    public string? PrimaryFailure { get; }
    public IReadOnlyList<LifecycleResultStatus> CompensationStatuses { get; }

    public ProjectRuntimeCoordinationException(
        string message,
        LifecycleResultStatus status,
        Project? project = null,
        ProjectId? projectId = null,
        string? primaryFailure = null,
        IReadOnlyList<LifecycleResultStatus>? compensationStatuses = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        Project = project;
        ProjectId = projectId ?? project?.Id;
        PrimaryFailure = primaryFailure;
        CompensationStatuses = compensationStatuses ?? [];
    }
}

public sealed class ProjectRuntimeCoordinator
{
    private readonly ProjectLifecycleService lifecycleService;
    private readonly ProjectIngestionManager ingestionManager;
    private readonly IComponentHealthTracker? healthTracker;
    private readonly ILogger<ProjectRuntimeCoordinator> logger;

    public ProjectRuntimeCoordinator(
        ProjectLifecycleService lifecycleService,
        ProjectIngestionManager ingestionManager,
        IComponentHealthTracker? healthTracker = null,
        ILogger<ProjectRuntimeCoordinator>? logger = null)
    {
        this.lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
        this.ingestionManager = ingestionManager ?? throw new ArgumentNullException(nameof(ingestionManager));
        this.healthTracker = healthTracker;
        this.logger = logger ?? NullLogger<ProjectRuntimeCoordinator>.Instance;
    }

    public async Task<LifecycleResult> StartOrResumeProjectAsync(
        ProjectId projectId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        var result = await lifecycleService.StartOrResumeAsync(projectId, operationId, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Status != LifecycleResultStatus.Transitioned && result.Status != LifecycleResultStatus.AlreadyInDesiredState)
        {
            logger.LogWarning("Start/Resume lifecycle transition rejected for Project {ProjectId}; status={Status}", projectId.Value, result.Status);
            throw new ProjectRuntimeCoordinationException(
                $"Start or resume failed for Project {projectId.Value}: status={result.Status}",
                result.Status,
                project: result.Project,
                projectId: projectId);
        }

        if (result.Project?.State == ProjectState.Running || result.Status == LifecycleResultStatus.AlreadyInDesiredState)
        {
            try
            {
                var ingestResult = await ingestionManager.StartAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (ingestResult.Status != IngestionStartStatus.Started && ingestResult.Status != IngestionStartStatus.AlreadyStarted)
                {
                    throw new InvalidOperationException($"Ingestion session failed to start with status {ingestResult.Status}.");
                }

                healthTracker?.RecordSuccess("IngestionRuntime");
                logger.LogInformation("Project {ProjectId} runtime active; ingestion status={Status}", projectId.Value, ingestResult.Status);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to activate ingestion runtime for Project {ProjectId} in RUNNING state. Initiating fail-closed compensation.", projectId.Value);

                var compensationStatuses = new List<LifecycleResultStatus>();
                bool primaryCompensationSucceeded = false;
                Project? currentDurableProject = result.Project;

                // 1. Primary Fail-closed compensation: transition project to PAUSED via legal state machine protocol
                var compensationOpId = $"{operationId}:compensation-pause";
                try
                {
                    var pauseResult = await PauseProjectAsync(projectId, compensationOpId, CancellationToken.None).ConfigureAwait(false);
                    compensationStatuses.Add(pauseResult.Status);
                    if (pauseResult.Project != null) currentDurableProject = pauseResult.Project;

                    if (pauseResult.Status == LifecycleResultStatus.Transitioned ||
                        (pauseResult.Status == LifecycleResultStatus.AlreadyInDesiredState && pauseResult.Project?.State == ProjectState.Paused))
                    {
                        primaryCompensationSucceeded = true;
                        logger.LogWarning("Fail-closed compensation to PAUSED completed for Project {ProjectId}.", projectId.Value);
                    }
                    else
                    {
                        logger.LogError("Primary pause compensation returned non-success status {Status} for Project {ProjectId}.", pauseResult.Status, projectId.Value);
                    }
                }
                catch (ProjectRuntimeCoordinationException pce)
                {
                    compensationStatuses.Add(pce.Status);
                    if (pce.Project != null) currentDurableProject = pce.Project;
                    logger.LogError(pce, "Primary pause compensation coordination failed for Project {ProjectId}.", projectId.Value);
                }
                catch (Exception compEx)
                {
                    compensationStatuses.Add(LifecycleResultStatus.OperationConflict);
                    logger.LogError(compEx, "Primary pause compensation failed for Project {ProjectId}.", projectId.Value);
                }

                // 2. Secondary Compensation if primary did not achieve safe state
                if (!primaryCompensationSucceeded)
                {
                    logger.LogWarning("Attempting secondary compensation to ComponentUnhealthy for Project {ProjectId}.", projectId.Value);
                    var unhealthyOpId = $"{operationId}:compensation-unhealthy";

                    try
                    {
                        var unhealthyResult = await lifecycleService.EnterComponentUnhealthyAsync(
                            projectId, "IngestionRuntime", unhealthyOpId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        compensationStatuses.Add(unhealthyResult.Status);
                        if (unhealthyResult.Project != null) currentDurableProject = unhealthyResult.Project;

                        if (unhealthyResult.Status == LifecycleResultStatus.Transitioned ||
                            (unhealthyResult.Status == LifecycleResultStatus.AlreadyInDesiredState && IsSafeNonRunningState(unhealthyResult.Project?.State)))
                        {
                            logger.LogWarning("Secondary compensation to ComponentUnhealthy applied for Project {ProjectId}.", projectId.Value);
                        }
                        else if (unhealthyResult.Status == LifecycleResultStatus.ConcurrencyConflict)
                        {
                            // Bounded retry (max 1 additional attempt) with fresh durable revision
                            logger.LogWarning("Secondary compensation encountered ConcurrencyConflict. Performing bounded re-read and retry for Project {ProjectId}.", projectId.Value);
                            var freshProject = await lifecycleService.OpenAsync(projectId, CancellationToken.None).ConfigureAwait(false);
                            if (freshProject != null) currentDurableProject = freshProject;

                            if (freshProject != null && IsSafeNonRunningState(freshProject.State))
                            {
                                logger.LogInformation("Project {ProjectId} already in safe non-running state {State}.", projectId.Value, freshProject.State);
                            }
                            else if (freshProject != null)
                            {
                                var retryOpId = $"{operationId}:compensation-unhealthy-retry";
                                var retryResult = await lifecycleService.EnterComponentUnhealthyAsync(
                                    projectId, "IngestionRuntime", retryOpId, expectedRevision: freshProject.StateRevision, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                                compensationStatuses.Add(retryResult.Status);
                                if (retryResult.Project != null) currentDurableProject = retryResult.Project;

                                if (retryResult.Status == LifecycleResultStatus.Transitioned ||
                                    (retryResult.Status == LifecycleResultStatus.AlreadyInDesiredState && IsSafeNonRunningState(retryResult.Project?.State)))
                                {
                                    logger.LogWarning("Bounded retry of secondary compensation to ComponentUnhealthy succeeded for Project {ProjectId}.", projectId.Value);
                                }
                                else
                                {
                                    logger.LogCritical("Bounded retry of secondary compensation returned non-success status {Status} for Project {ProjectId}.", retryResult.Status, projectId.Value);
                                }
                            }
                        }
                        else
                        {
                            logger.LogCritical("Secondary compensation to ComponentUnhealthy returned non-success status {Status} for Project {ProjectId}.", unhealthyResult.Status, projectId.Value);
                        }
                    }
                    catch (Exception secEx)
                    {
                        logger.LogCritical(secEx, "Secondary compensation to ComponentUnhealthy threw critical exception for Project {ProjectId}.", projectId.Value);
                    }
                }

                // 3. Health Tracker Failure Recording & Dispatch Blocking
                // Mark ingestion component as unhealthy in tracker so IsStageBlocked returns true and ProjectDispatchGuard blocks dispatch
                if (healthTracker != null)
                {
                    healthTracker.MarkUnhealthy("IngestionRuntime", $"Ingestion runtime startup failed: {ex.Message}");
                }

                var finalDurableState = currentDurableProject?.State ?? ProjectState.Running;
                var finalStatus = compensationStatuses.LastOrDefault();

                throw new ProjectRuntimeCoordinationException(
                    $"Ingestion runtime failed to start for Project {projectId.Value}. Primary failure: {ex.Message}. " +
                    $"Durable state: {finalDurableState}. Compensation statuses: [{string.Join(", ", compensationStatuses)}].",
                    finalStatus != default ? finalStatus : LifecycleResultStatus.OperationConflict,
                    project: currentDurableProject,
                    projectId: projectId,
                    primaryFailure: ex.Message,
                    compensationStatuses: compensationStatuses,
                    innerException: ex);
            }
        }

        return result;
    }

    public async Task<LifecycleResult> PauseProjectAsync(
        ProjectId projectId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        // 1. Enter PAUSE_REQUESTED in SQLite
        var requestResult = await lifecycleService.EnterPauseRequestedAsync(projectId, operationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (requestResult.Status == LifecycleResultStatus.AlreadyInDesiredState && requestResult.Project?.State == ProjectState.Paused)
        {
            return requestResult;
        }

        if (requestResult.Status != LifecycleResultStatus.Transitioned && requestResult.Status != LifecycleResultStatus.AlreadyInDesiredState)
        {
            logger.LogWarning("EnterPauseRequested failed for Project {ProjectId}; status={Status}", projectId.Value, requestResult.Status);
            throw new ProjectRuntimeCoordinationException(
                $"Pause request failed for Project {projectId.Value}: status={requestResult.Status}",
                requestResult.Status,
                project: requestResult.Project,
                projectId: projectId);
        }

        // 2. Stop Ingestion Runtime while in PAUSE_REQUESTED
        try
        {
            await ingestionManager.StopAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stop ingestion runtime during pause for Project {ProjectId}. State remains PAUSE_REQUESTED.", projectId.Value);
            throw; // Leaves state honestly as PAUSE_REQUESTED; does NOT falsely report clean PAUSED
        }

        // 3. Finalize to PAUSED
        var completeResult = await lifecycleService.NotifySafeCompletionAsync(projectId, $"{operationId}:complete", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (completeResult.Status != LifecycleResultStatus.Transitioned &&
            !(completeResult.Status == LifecycleResultStatus.AlreadyInDesiredState && completeResult.Project?.State == ProjectState.Paused))
        {
            logger.LogError("Pause finalization failed for Project {ProjectId}; status={Status}", projectId.Value, completeResult.Status);
            throw new ProjectRuntimeCoordinationException(
                $"Pause finalization failed for Project {projectId.Value}: status={completeResult.Status}",
                completeResult.Status,
                project: completeResult.Project,
                projectId: projectId);
        }

        return completeResult;
    }

    public async Task<LifecycleResult> StopProjectAsync(
        ProjectId projectId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        // 1. Enter STOP_REQUESTED in SQLite
        var requestResult = await lifecycleService.EnterStopRequestedAsync(projectId, operationId, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (requestResult.Status == LifecycleResultStatus.AlreadyInDesiredState && requestResult.Project?.State == ProjectState.Stopped)
        {
            return requestResult;
        }

        if (requestResult.Status != LifecycleResultStatus.Transitioned && requestResult.Status != LifecycleResultStatus.AlreadyInDesiredState)
        {
            logger.LogWarning("EnterStopRequested failed for Project {ProjectId}; status={Status}", projectId.Value, requestResult.Status);
            throw new ProjectRuntimeCoordinationException(
                $"Stop request failed for Project {projectId.Value}: status={requestResult.Status}",
                requestResult.Status,
                project: requestResult.Project,
                projectId: projectId);
        }

        // 2. Stop Ingestion Runtime while in STOP_REQUESTED
        try
        {
            await ingestionManager.StopAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stop ingestion runtime during stop for Project {ProjectId}. State remains STOP_REQUESTED.", projectId.Value);
            throw; // Leaves state honestly as STOP_REQUESTED; does NOT falsely report clean STOPPED
        }

        // 3. Finalize to STOPPED
        var completeResult = await lifecycleService.NotifySafeCompletionAsync(projectId, $"{operationId}:complete", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (completeResult.Status != LifecycleResultStatus.Transitioned &&
            !(completeResult.Status == LifecycleResultStatus.AlreadyInDesiredState && completeResult.Project?.State == ProjectState.Stopped))
        {
            logger.LogError("Stop finalization failed for Project {ProjectId}; status={Status}", projectId.Value, completeResult.Status);
            throw new ProjectRuntimeCoordinationException(
                $"Stop finalization failed for Project {projectId.Value}: status={completeResult.Status}",
                completeResult.Status,
                project: completeResult.Project,
                projectId: projectId);
        }

        return completeResult;
    }

    private static bool IsSafeNonRunningState(ProjectState? state) =>
        state is ProjectState.Paused
              or ProjectState.PauseRequested
              or ProjectState.ComponentUnhealthy
              or ProjectState.StopRequested
              or ProjectState.Stopped
              or ProjectState.BlockedStorage;
}
