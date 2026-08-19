using PhotoAIFactory.Application;

namespace PhotoAIFactory.Infrastructure;

public sealed class GpuResourceCoordinator : IGpuResourceCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _currentOwner;
    public string? CurrentOwner => Volatile.Read(ref _currentOwner);

    public async Task<IAsyncDisposable> AcquireAsync(string owner, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("GPU owner is required", nameof(owner));
        await _gate.WaitAsync(cancellationToken);
        Volatile.Write(ref _currentOwner, owner);
        return new Lease(this, owner);
    }

    private void Release(string owner)
    {
        if (!string.Equals(CurrentOwner, owner, StringComparison.Ordinal))
            throw new InvalidOperationException($"GPU lease owner mismatch. Current={CurrentOwner}, releasing={owner}");
        Volatile.Write(ref _currentOwner, null);
        _gate.Release();
    }

    private sealed class Lease(GpuResourceCoordinator parent, string owner) : IAsyncDisposable
    {
        private GpuResourceCoordinator? _parent = parent;
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _parent, null)?.Release(owner);
            return ValueTask.CompletedTask;
        }
    }
}
