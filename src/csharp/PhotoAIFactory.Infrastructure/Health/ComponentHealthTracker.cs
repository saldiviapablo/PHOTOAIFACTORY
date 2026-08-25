using System.Collections.Concurrent;
using PhotoAIFactory.Application.Health;

namespace PhotoAIFactory.Infrastructure.Health;

public sealed class ComponentHealthTracker : IComponentHealthTracker
{
    private readonly int failureThreshold;
    private readonly int maxRestarts;
    private readonly ConcurrentDictionary<string, ComponentEntry> components = new(StringComparer.OrdinalIgnoreCase);

    public ComponentHealthTracker(int failureThreshold = 3, int maxRestarts = 2)
    {
        this.failureThreshold = Math.Max(1, failureThreshold);
        this.maxRestarts = Math.Max(1, maxRestarts);
    }

    public void RecordSuccess(string componentName)
    {
        var entry = GetOrAdd(componentName);
        lock (entry)
        {
            entry.ConsecutiveFailures = 0;
            entry.CircuitOpen = false;
            entry.State = ComponentHealthState.Healthy;
            entry.Reason = "Healthy operation confirmed by probe";
            entry.LastCheckedUtc = DateTimeOffset.UtcNow;
            entry.LastStateChangeUtc = DateTimeOffset.UtcNow;
        }
    }

    public void RecordFailure(string componentName, string reason)
    {
        var entry = GetOrAdd(componentName);
        lock (entry)
        {
            entry.ConsecutiveFailures++;
            entry.Reason = reason;
            entry.LastCheckedUtc = DateTimeOffset.UtcNow;

            if (entry.ConsecutiveFailures >= failureThreshold)
            {
                entry.CircuitOpen = true;
                entry.State = ComponentHealthState.Unhealthy;
                entry.LastStateChangeUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                entry.State = ComponentHealthState.Degraded;
            }
        }
    }

    public void MarkUnhealthy(string componentName, string reason)
    {
        var entry = GetOrAdd(componentName);
        lock (entry)
        {
            entry.ConsecutiveFailures = Math.Max(entry.ConsecutiveFailures + 1, failureThreshold);
            entry.CircuitOpen = true;
            entry.State = ComponentHealthState.Unhealthy;
            entry.Reason = reason;
            entry.LastCheckedUtc = DateTimeOffset.UtcNow;
            entry.LastStateChangeUtc = DateTimeOffset.UtcNow;
        }
    }

    public ComponentHealthStatus GetStatus(string componentName)
    {
        var entry = GetOrAdd(componentName);
        lock (entry)
        {
            return new ComponentHealthStatus(
                entry.ComponentName,
                entry.State,
                entry.Reason,
                entry.ConsecutiveFailures,
                entry.TotalRestarts,
                entry.CircuitOpen,
                entry.LastCheckedUtc,
                entry.LastStateChangeUtc);
        }
    }

    public IReadOnlyList<ComponentHealthStatus> GetAllStatuses() =>
        components.Values.Select(entry =>
        {
            lock (entry)
            {
                return new ComponentHealthStatus(
                    entry.ComponentName,
                    entry.State,
                    entry.Reason,
                    entry.ConsecutiveFailures,
                    entry.TotalRestarts,
                    entry.CircuitOpen,
                    entry.LastCheckedUtc,
                    entry.LastStateChangeUtc);
            }
        }).ToList();

    public bool IsStageBlocked(string componentName)
    {
        var entry = GetOrAdd(componentName);
        lock (entry)
        {
            return entry.CircuitOpen || entry.State == ComponentHealthState.Unhealthy;
        }
    }

    public bool TryRequestRestart(string componentName, out int restartAttempt)
    {
        var entry = GetOrAdd(componentName);
        lock (entry)
        {
            if (entry.TotalRestarts >= maxRestarts)
            {
                restartAttempt = entry.TotalRestarts;
                entry.CircuitOpen = true;
                entry.State = ComponentHealthState.Unhealthy;
                entry.Reason = $"Automatic restart budget exhausted ({maxRestarts}/{maxRestarts}). Circuit opened.";
                return false; // Exhausted restarts
            }

            entry.TotalRestarts++;
            entry.State = ComponentHealthState.Starting;
            entry.Reason = $"Restart attempt {entry.TotalRestarts} of {maxRestarts}";
            entry.LastStateChangeUtc = DateTimeOffset.UtcNow;
            restartAttempt = entry.TotalRestarts;
            return true;
        }
    }

    public void ResetCircuit(string componentName)
    {
        var entry = GetOrAdd(componentName);
        lock (entry)
        {
            entry.ConsecutiveFailures = 0;
            entry.CircuitOpen = false;
            entry.TotalRestarts = 0;
            entry.State = ComponentHealthState.Healthy;
            entry.Reason = "Circuit reset manually or on health recovery";
            entry.LastCheckedUtc = DateTimeOffset.UtcNow;
            entry.LastStateChangeUtc = DateTimeOffset.UtcNow;
        }
    }

    private ComponentEntry GetOrAdd(string name) =>
        components.GetOrAdd(name, n => new ComponentEntry(n));

    private sealed class ComponentEntry(string name)
    {
        public string ComponentName { get; } = name;
        public ComponentHealthState State { get; set; } = ComponentHealthState.Starting;
        public string? Reason { get; set; } = "Initialized";
        public int ConsecutiveFailures { get; set; }
        public int TotalRestarts { get; set; }
        public bool CircuitOpen { get; set; }
        public DateTimeOffset LastCheckedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastStateChangeUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}
