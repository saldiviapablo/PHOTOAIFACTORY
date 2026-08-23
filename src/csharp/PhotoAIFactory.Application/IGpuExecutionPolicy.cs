namespace PhotoAIFactory.Application;

public class GpuOutOfMemoryException(string componentName, string message, Exception? innerException = null)
    : InvalidOperationException($"GPU OOM in {componentName}: {message}", innerException)
{
    public string ComponentName { get; } = componentName;
}

public interface IGpuExecutionPolicy
{
    Task<T> ExecuteWithGpuAsync<T>(
        string ownerName,
        Func<Task<T>> operation,
        Func<Task>? releaseMemory = null,
        CancellationToken cancellationToken = default);

    Task ExecuteWithGpuAsync(
        string ownerName,
        Func<Task> operation,
        Func<Task>? releaseMemory = null,
        CancellationToken cancellationToken = default);
}
