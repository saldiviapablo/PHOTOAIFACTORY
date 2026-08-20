using System.Threading;

namespace PhotoAIFactory.Application.Processing;

/// <summary>
/// V1 application-wide serialization boundary for heavy reveal Jobs.
/// This is intentionally separate from the GPU lease: it owns Job-stage
/// concurrency, while the GPU coordinator owns VRAM/device ownership.
/// </summary>
public sealed class RevealExecutionCoordinator
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private SemaphoreSlim? heldSemaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref heldSemaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
