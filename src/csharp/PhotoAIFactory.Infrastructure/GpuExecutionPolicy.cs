using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoAIFactory.Application;

namespace PhotoAIFactory.Infrastructure;

public sealed class GpuExecutionPolicy : IGpuExecutionPolicy
{
    private readonly IGpuResourceCoordinator coordinator;
    private readonly ILogger<GpuExecutionPolicy> logger;

    public GpuExecutionPolicy(
        IGpuResourceCoordinator coordinator,
        ILogger<GpuExecutionPolicy>? logger = null)
    {
        this.coordinator = coordinator;
        this.logger = logger ?? NullLogger<GpuExecutionPolicy>.Instance;
    }

    public async Task<T> ExecuteWithGpuAsync<T>(
        string ownerName,
        Func<Task<T>> operation,
        Func<Task>? releaseMemory = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Initial attempt
        await using (var lease = await coordinator.AcquireAsync(ownerName, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsGpuOom(ex))
            {
                logger.LogWarning(ex, "GPU OOM detected for {Owner}. Triggering single memory recovery retry.", ownerName);
            }
        } // Lease released before memory reclamation

        // 2. Perform memory recovery / model parking
        if (releaseMemory is not null)
        {
            try
            {
                await releaseMemory().ConfigureAwait(false);
            }
            catch (Exception releaseEx)
            {
                logger.LogWarning(releaseEx, "Memory release callback threw an exception for {Owner}.", ownerName);
            }
        }

        // Short stabilization pause
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);

        // 3. Single retry attempt under fresh lease
        await using (var retryLease = await coordinator.AcquireAsync(ownerName, cancellationToken).ConfigureAwait(false))
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception retryEx) when (IsGpuOom(retryEx))
            {
                logger.LogError(retryEx, "GPU OOM persisted on retry for {Owner}. Failing cleanly without silent quality degradation.", ownerName);
                throw new GpuOutOfMemoryException(ownerName, "GPU out of memory on single recovery retry.", retryEx);
            }
        }
    }

    public async Task ExecuteWithGpuAsync(
        string ownerName,
        Func<Task> operation,
        Func<Task>? releaseMemory = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithGpuAsync<bool>(
            ownerName,
            async () =>
            {
                await operation().ConfigureAwait(false);
                return true;
            },
            releaseMemory,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsGpuOom(Exception ex)
    {
        if (ex is OutOfMemoryException or GpuOutOfMemoryException)
        {
            return true;
        }

        var message = ex.Message.ToLowerInvariant();
        return message.Contains("out of memory") ||
               message.Contains("cuda out of memory") ||
               message.Contains("gpu out of memory") ||
               message.Contains("comfy_gpu_oom") ||
               message.Contains("allocation failed");
    }
}
