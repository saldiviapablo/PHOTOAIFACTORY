using System.Collections.Concurrent;

namespace PhotoAIFactory.Simulation.Tests.Simulation;

internal enum SimulationFaultKind
{
    ProcessCrash,
    Timeout,
    InvalidOutput,
    AccessDenied,
    DiskFull,
    OutOfMemory,
    HashMismatch
}

internal sealed record SimulationFaultRule
{
    public SimulationFaultRule(
        string stage,
        string point,
        SimulationFaultKind kind,
        int occurrence = 1)
    {
        Stage = Normalize(stage, nameof(stage));
        Point = Normalize(point, nameof(point));
        Kind = kind;
        Occurrence = occurrence > 0
            ? occurrence
            : throw new ArgumentOutOfRangeException(nameof(occurrence));
    }

    public string Stage { get; }
    public string Point { get; }
    public SimulationFaultKind Kind { get; }
    public int Occurrence { get; }

    private static string Normalize(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Fault stage/point is required.", parameterName);
        }

        return value.Trim().ToUpperInvariant();
    }
}

internal sealed record TriggeredFault(
    string Stage,
    string Point,
    SimulationFaultKind Kind,
    int ObservedOccurrence);

internal sealed class SimulationFaultPlan
{
    private readonly IReadOnlyList<SimulationFaultRule> rules;
    private readonly ConcurrentDictionary<string, int> counters = new(StringComparer.Ordinal);

    public SimulationFaultPlan(IEnumerable<SimulationFaultRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        this.rules = rules.ToArray();
    }

    public TriggeredFault? Observe(string stage, string point)
    {
        var normalizedStage = Normalize(stage, nameof(stage));
        var normalizedPoint = Normalize(point, nameof(point));
        var key = $"{normalizedStage}\u001f{normalizedPoint}";
        var occurrence = counters.AddOrUpdate(key, 1, static (_, current) => checked(current + 1));

        var rule = rules.FirstOrDefault(item =>
            item.Stage == normalizedStage &&
            item.Point == normalizedPoint &&
            item.Occurrence == occurrence);

        return rule is null
            ? null
            : new TriggeredFault(normalizedStage, normalizedPoint, rule.Kind, occurrence);
    }

    public int ObservedCount(string stage, string point)
    {
        var key = $"{Normalize(stage, nameof(stage))}\u001f{Normalize(point, nameof(point))}";
        return counters.TryGetValue(key, out var value) ? value : 0;
    }

    private static string Normalize(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Stage/point is required.", parameterName);
        }

        return value.Trim().ToUpperInvariant();
    }
}
