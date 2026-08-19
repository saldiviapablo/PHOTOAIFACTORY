using System.Collections.Concurrent;

namespace PhotoAIFactory.Simulation.Tests.Simulation;

internal sealed record ScenarioEvent(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string Category,
    string Name,
    IReadOnlyDictionary<string, string?> Data);

internal sealed class ScenarioEventRecorder(TimeProvider timeProvider)
{
    private readonly ConcurrentQueue<ScenarioEvent> events = new();
    private long sequence;

    public void Record(
        string category,
        string name,
        IReadOnlyDictionary<string, string?>? data = null)
    {
        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Scenario event category and name are required.");
        }

        var snapshot = data is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(data, StringComparer.Ordinal);

        events.Enqueue(new ScenarioEvent(
            Interlocked.Increment(ref sequence),
            timeProvider.GetUtcNow(),
            category.Trim().ToUpperInvariant(),
            name.Trim().ToUpperInvariant(),
            snapshot));
    }

    public IReadOnlyList<ScenarioEvent> Snapshot() =>
        events.OrderBy(item => item.Sequence).ToArray();
}
