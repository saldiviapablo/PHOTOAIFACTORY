using System.Collections.Concurrent;

namespace PhotoAIFactory.Infrastructure.Persistence;

public sealed class SqliteWriteCoordinator
{
    private static readonly ConcurrentDictionary<string, WriterState> States =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly WriterState state;

    public SqliteWriteCoordinator(string databasePath)
    {
        var normalized = Path.GetFullPath(databasePath);
        state = States.GetOrAdd(normalized, _ => new WriterState());
    }

    public int MaxObservedConcurrentWriters => Volatile.Read(ref state.MaxObservedWriters);
    public int OverlapViolationCount => Volatile.Read(ref state.OverlapViolations);

    public bool SharesBoundaryWith(SqliteWriteCoordinator other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(state, other.state);
    }

    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken = default)
    {
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var active = Interlocked.Increment(ref state.ActiveWriters);
        UpdateMaximum(active);
        if (active > 1)
        {
            Interlocked.Increment(ref state.OverlapViolations);
        }

        return new Lease(this);
    }

    private void UpdateMaximum(int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref state.MaxObservedWriters);
            if (candidate <= current || Interlocked.CompareExchange(ref state.MaxObservedWriters, candidate, current) == current)
            {
                return;
            }
        }
    }

    private sealed class Lease(SqliteWriteCoordinator owner) : IAsyncDisposable
    {
        private int disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner.state.ActiveWriters);
                owner.state.Gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class WriterState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ActiveWriters;
        public int MaxObservedWriters;
        public int OverlapViolations;
    }
}
