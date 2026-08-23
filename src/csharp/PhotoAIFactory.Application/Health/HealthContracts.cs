namespace PhotoAIFactory.Application.Health;

public enum ComponentHealthState
{
    Starting,
    Healthy,
    Degraded,
    Unhealthy,
    Stopped
}

public sealed record ComponentHealthStatus(
    string ComponentName,
    ComponentHealthState State,
    string? Reason,
    int ConsecutiveFailures,
    int TotalRestarts,
    bool CircuitBreakerOpen,
    DateTimeOffset LastCheckedUtc,
    DateTimeOffset? LastStateChangeUtc);

public interface IComponentHealthTracker
{
    void RecordSuccess(string componentName);
    void RecordFailure(string componentName, string reason);
    ComponentHealthStatus GetStatus(string componentName);
    IReadOnlyList<ComponentHealthStatus> GetAllStatuses();
    bool IsStageBlocked(string componentName);
    bool TryRequestRestart(string componentName, out int restartAttempt);
    void ResetCircuit(string componentName);
}

public interface IComponentHealthMonitor
{
    Task<ComponentHealthStatus> CheckComponentHealthAsync(string componentName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ComponentHealthStatus>> CheckAllAsync(CancellationToken cancellationToken = default);
}
