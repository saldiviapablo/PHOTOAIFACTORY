namespace PhotoAIFactory.Domain.Projects;

public static class ProjectStateMachine
{
    private static readonly HashSet<(ProjectState From, ProjectState To)> AllowedTransitions =
    [
        (ProjectState.Stopped, ProjectState.Running),
        (ProjectState.Running, ProjectState.PauseRequested),
        (ProjectState.PauseRequested, ProjectState.Paused),
        (ProjectState.Paused, ProjectState.Running),
        (ProjectState.Running, ProjectState.StopRequested),
        (ProjectState.PauseRequested, ProjectState.StopRequested),
        (ProjectState.Paused, ProjectState.StopRequested),
        (ProjectState.StopRequested, ProjectState.Stopped),
        (ProjectState.Running, ProjectState.BlockedStorage),
        (ProjectState.BlockedStorage, ProjectState.Running),
        (ProjectState.BlockedStorage, ProjectState.PauseRequested),
        (ProjectState.BlockedStorage, ProjectState.Paused),
        (ProjectState.BlockedStorage, ProjectState.StopRequested),
        (ProjectState.BlockedStorage, ProjectState.Stopped),
        (ProjectState.Running, ProjectState.ComponentUnhealthy),
        (ProjectState.ComponentUnhealthy, ProjectState.Running),
        (ProjectState.ComponentUnhealthy, ProjectState.PauseRequested),
        (ProjectState.ComponentUnhealthy, ProjectState.Paused),
        (ProjectState.ComponentUnhealthy, ProjectState.StopRequested),
        (ProjectState.ComponentUnhealthy, ProjectState.Stopped)
    ];

    public static bool CanTransition(ProjectState from, ProjectState to) =>
        Enum.IsDefined(from) && Enum.IsDefined(to) && AllowedTransitions.Contains((from, to));
}

public sealed class InvalidProjectStateTransitionException(ProjectState from, ProjectState to)
    : InvalidOperationException($"Project transition {from} -> {to} is not allowed.")
{
    public ProjectState From { get; } = from;
    public ProjectState To { get; } = to;
}

public sealed class ProjectStateTransition
{
    private ProjectStateTransition(
        string id,
        ProjectId projectId,
        ProjectState fromState,
        ProjectState toState,
        string reason,
        DateTimeOffset occurredAtUtc,
        long stateRevision,
        string operationId)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(projectId.Value) ||
            string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException("Transition identity, reason and operation ID are required.");
        }
        if (!Enum.IsDefined(fromState) || !Enum.IsDefined(toState) || stateRevision < 0)
        {
            throw new ArgumentException("Transition state or revision is invalid.");
        }
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Transition timestamp must be UTC.", nameof(occurredAtUtc));
        }

        Id = id;
        ProjectId = projectId;
        FromState = fromState;
        ToState = toState;
        Reason = reason;
        OccurredAtUtc = occurredAtUtc;
        StateRevision = stateRevision;
        OperationId = operationId;
    }

    public string Id { get; }
    public ProjectId ProjectId { get; }
    public ProjectState FromState { get; }
    public ProjectState ToState { get; }
    public string Reason { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public long StateRevision { get; }
    public string OperationId { get; }

    public static ProjectStateTransition Create(
        ProjectId projectId,
        ProjectState fromState,
        ProjectState toState,
        string reason,
        DateTimeOffset occurredAtUtc,
        long stateRevision,
        string operationId) =>
        new(Guid.NewGuid().ToString("N"), projectId, fromState, toState, reason,
            occurredAtUtc, stateRevision, operationId);

    public static ProjectStateTransition Restore(
        string id,
        ProjectId projectId,
        ProjectState fromState,
        ProjectState toState,
        string reason,
        DateTimeOffset occurredAtUtc,
        long stateRevision,
        string operationId) =>
        new(id, projectId, fromState, toState, reason, occurredAtUtc, stateRevision, operationId);
}
