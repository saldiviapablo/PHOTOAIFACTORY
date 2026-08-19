namespace PhotoAIFactory.Domain.Projects;

public sealed class Project
{
    private Project(
        ProjectId id,
        string name,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        ProjectState state,
        long stateRevision,
        DateTimeOffset stateChangedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("Project ID is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Project name is required.", nameof(name));
        }

        EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        EnsureUtc(updatedAtUtc, nameof(updatedAtUtc));
        EnsureUtc(stateChangedAtUtc, nameof(stateChangedAtUtc));
        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("Updated timestamp cannot precede creation.", nameof(updatedAtUtc));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), "Unknown project state.");
        }

        if (stateRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stateRevision));
        }

        Id = id;
        Name = name.Trim();
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        State = state;
        StateRevision = stateRevision;
        StateChangedAtUtc = stateChangedAtUtc;
    }

    public ProjectId Id { get; }
    public string Name { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset UpdatedAtUtc { get; }
    public ProjectState State { get; }
    public long StateRevision { get; }
    public DateTimeOffset StateChangedAtUtc { get; }

    public static Project Create(string name, DateTimeOffset nowUtc) =>
        new(ProjectId.New(), name, nowUtc, nowUtc, ProjectState.Stopped, 0, nowUtc);

    public static Project Restore(
        ProjectId id,
        string name,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        ProjectState state,
        long stateRevision,
        DateTimeOffset stateChangedAtUtc) =>
        new(id, name, createdAtUtc, updatedAtUtc, state, stateRevision, stateChangedAtUtc);

    public Project TransitionTo(ProjectState nextState, DateTimeOffset changedAtUtc)
    {
        EnsureUtc(changedAtUtc, nameof(changedAtUtc));
        if (!ProjectStateMachine.CanTransition(State, nextState))
        {
            throw new InvalidProjectStateTransitionException(State, nextState);
        }

        return new Project(
            Id,
            Name,
            CreatedAtUtc,
            changedAtUtc,
            nextState,
            checked(StateRevision + 1),
            changedAtUtc);
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }
}
