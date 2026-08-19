using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Projects;

public interface IProjectWorkStatus
{
    Task<bool> HasActiveJobAsync(ProjectId projectId, CancellationToken cancellationToken = default);
}

public static class ProjectDispatchGuard
{
    public static bool CanDispatchNextJob(ProjectState state) => state == ProjectState.Running;
}

public enum LifecycleResultStatus
{
    Transitioned,
    AlreadyInDesiredState,
    InvalidTransition,
    ConcurrencyConflict,
    OperationConflict,
    NotFound
}

public sealed record LifecycleResult(
    LifecycleResultStatus Status,
    Project? Project,
    ProjectStateTransition? Transition = null);

public sealed class ProjectLifecycleService
{
    private static readonly EventId StartedEvent = new(2000, "ProjectStarted");
    private static readonly EventId PauseRequestedEvent = new(2001, "PauseRequested");
    private static readonly EventId PausedEvent = new(2002, "ProjectPaused");
    private static readonly EventId StopRequestedEvent = new(2003, "StopRequested");
    private static readonly EventId StoppedEvent = new(2004, "ProjectStopped");
    private static readonly EventId ResumedEvent = new(2005, "ProjectResumed");
    private static readonly EventId ConflictEvent = new(2099, "LifecycleConflict");

    private readonly IProjectStoreFactory stores;
    private readonly IProjectWorkStatus workStatus;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ProjectLifecycleService> logger;

    public ProjectLifecycleService(
        IProjectStoreFactory stores,
        IProjectWorkStatus workStatus,
        TimeProvider timeProvider,
        ILogger<ProjectLifecycleService>? logger = null)
    {
        this.stores = stores;
        this.workStatus = workStatus;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<ProjectLifecycleService>.Instance;
    }

    public async Task<LifecycleResult> StartOrResumeAsync(
        ProjectId projectId,
        string operationId,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        var current = await OpenAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (current is null) return new(LifecycleResultStatus.NotFound, null);
        if (expectedRevision is not null && current.StateRevision != expectedRevision)
            return Conflict(current);
        if (current.State == ProjectState.Running)
            return new(LifecycleResultStatus.AlreadyInDesiredState, current);
        if (current.State is not (ProjectState.Stopped or ProjectState.Paused))
            return new(LifecycleResultStatus.InvalidTransition, current);

        var reason = current.State == ProjectState.Stopped ? "PROJECT_STARTED" : "PROJECT_RESUMED";
        var result = await TransitionAsync(current, ProjectState.Running, reason, operationId, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == LifecycleResultStatus.Transitioned)
        {
            Log(current.State == ProjectState.Stopped ? StartedEvent : ResumedEvent,
                result.Project!, "Project transitioned to RUNNING");
        }
        return result;
    }

    public async Task<LifecycleResult> RequestPauseAsync(
        ProjectId projectId,
        string operationId,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        var current = await OpenAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (current is null) return new(LifecycleResultStatus.NotFound, null);
        if (expectedRevision is not null && current.StateRevision != expectedRevision)
            return Conflict(current);
        if (current.State is ProjectState.Paused or ProjectState.PauseRequested)
            return new(LifecycleResultStatus.AlreadyInDesiredState, current);
        if (current.State != ProjectState.Running)
            return new(LifecycleResultStatus.InvalidTransition, current);

        var requested = await TransitionAsync(
            current, ProjectState.PauseRequested, "PAUSE_REQUESTED", operationId, cancellationToken).ConfigureAwait(false);
        if (requested.Status != LifecycleResultStatus.Transitioned) return requested;
        Log(PauseRequestedEvent, requested.Project!, "Project pause requested");

        if (await workStatus.HasActiveJobAsync(projectId, cancellationToken).ConfigureAwait(false))
            return requested;

        var paused = await TransitionAsync(
            requested.Project!, ProjectState.Paused, "NO_ACTIVE_JOB_SAFE_PAUSE",
            operationId + ":safe-complete", cancellationToken).ConfigureAwait(false);
        if (paused.Status == LifecycleResultStatus.Transitioned)
            Log(PausedEvent, paused.Project!, "Project reached PAUSED without an active Job");
        return paused;
    }

    public async Task<LifecycleResult> RequestStopAsync(
        ProjectId projectId,
        string operationId,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        var current = await OpenAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (current is null) return new(LifecycleResultStatus.NotFound, null);
        if (expectedRevision is not null && current.StateRevision != expectedRevision)
            return Conflict(current);
        if (current.State is ProjectState.Stopped or ProjectState.StopRequested)
            return new(LifecycleResultStatus.AlreadyInDesiredState, current);
        if (current.State is not (ProjectState.Running or ProjectState.PauseRequested or ProjectState.Paused))
            return new(LifecycleResultStatus.InvalidTransition, current);

        var requested = await TransitionAsync(
            current, ProjectState.StopRequested, "STOP_REQUESTED", operationId, cancellationToken).ConfigureAwait(false);
        if (requested.Status != LifecycleResultStatus.Transitioned) return requested;
        Log(StopRequestedEvent, requested.Project!, "Project stop requested");

        if (await workStatus.HasActiveJobAsync(projectId, cancellationToken).ConfigureAwait(false))
            return requested;

        var stopped = await TransitionAsync(
            requested.Project!, ProjectState.Stopped, "NO_ACTIVE_JOB_SAFE_STOP",
            operationId + ":safe-complete", cancellationToken).ConfigureAwait(false);
        if (stopped.Status == LifecycleResultStatus.Transitioned)
            Log(StoppedEvent, stopped.Project!, "Project reached STOPPED without an active Job");
        return stopped;
    }

    public async Task<LifecycleResult> NotifySafeCompletionAsync(
        ProjectId projectId,
        string operationId,
        long? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId);
        var current = await OpenAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (current is null) return new(LifecycleResultStatus.NotFound, null);
        if (expectedRevision is not null && current.StateRevision != expectedRevision)
            return Conflict(current);

        var next = current.State switch
        {
            ProjectState.PauseRequested => ProjectState.Paused,
            ProjectState.StopRequested => ProjectState.Stopped,
            _ => (ProjectState?)null
        };
        if (next is null) return new(LifecycleResultStatus.InvalidTransition, current);

        var reason = next == ProjectState.Paused ? "ACTIVE_JOB_SAFE_PAUSE" : "ACTIVE_JOB_SAFE_STOP";
        var result = await TransitionAsync(current, next.Value, reason, operationId, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == LifecycleResultStatus.Transitioned)
            Log(next == ProjectState.Paused ? PausedEvent : StoppedEvent, result.Project!,
                next == ProjectState.Paused ? "Project reached PAUSED" : "Project reached STOPPED");
        return result;
    }

    private async Task<Project?> OpenAsync(ProjectId projectId, CancellationToken cancellationToken) =>
        (await stores.Open(projectId).GetAsync(projectId, cancellationToken).ConfigureAwait(false))?.Project;

    private async Task<LifecycleResult> TransitionAsync(
        Project current,
        ProjectState next,
        string reason,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (!ProjectStateMachine.CanTransition(current.State, next))
            return new(LifecycleResultStatus.InvalidTransition, current);

        var write = await stores.Open(current.Id).TryTransitionAsync(
            current.Id, current.State, current.StateRevision, next, reason, operationId,
            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return write.Status switch
        {
            TransitionWriteStatus.Applied => new(LifecycleResultStatus.Transitioned, write.Project, write.Transition),
            TransitionWriteStatus.Replayed => new(LifecycleResultStatus.AlreadyInDesiredState, write.Project, write.Transition),
            TransitionWriteStatus.ConcurrencyConflict => Conflict(write.Project ?? current),
            TransitionWriteStatus.OperationConflict => new(LifecycleResultStatus.OperationConflict, write.Project),
            TransitionWriteStatus.NotFound => new(LifecycleResultStatus.NotFound, null),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private LifecycleResult Conflict(Project project)
    {
        Log(ConflictEvent, project, "Project lifecycle concurrency conflict");
        return new(LifecycleResultStatus.ConcurrencyConflict, project);
    }

    private void Log(EventId eventId, Project project, string message)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["project_id"] = project.Id.Value });
        logger.LogInformation(eventId, "{LifecycleMessage}; state={ProjectState}; revision={StateRevision}",
            message, project.State, project.StateRevision);
    }

    private static void ValidateOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("A durable operation ID is required.", nameof(operationId));
    }
}
