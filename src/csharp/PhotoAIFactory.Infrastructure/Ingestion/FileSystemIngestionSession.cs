using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Ingestion;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.Ingestion;

public sealed class FileSystemIngestionSession : IIngestionSession
{
    private readonly ProjectConfigV1 config;
    private readonly IIngestionStore store;
    private readonly IngestionCoordinator coordinator;
    private readonly IngestionRuntimeOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<FileSystemIngestionSession> logger;
    private readonly Channel<string> queue;
    private readonly ConcurrentDictionary<string, byte> scheduled =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource lifetime = new();

    private FileSystemWatcher? watcher;
    private Task? consumer;
    private Task? periodic;
    private int pending;
    private int reconcileRequired;
    private int started;
    private int stopped;

    public FileSystemIngestionSession(
        ProjectId projectId,
        ProjectConfigV1 config,
        IngestionSourceSnapshot source,
        IIngestionStore store,
        IngestionCoordinator coordinator,
        IngestionRuntimeOptions options,
        TimeProvider timeProvider,
        ILogger<FileSystemIngestionSession> logger)
    {
        ProjectId = projectId;
        this.config = config;
        Source = source;
        this.store = store;
        this.coordinator = coordinator;
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
        queue = Channel.CreateBounded<string>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public ProjectId ProjectId { get; }
    public IngestionSourceSnapshot Source { get; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("Ingestion session already started.");
        }

        if (!Directory.Exists(config.InputFolder))
        {
            throw new DirectoryNotFoundException($"Input folder does not exist: {config.InputFolder}");
        }

        consumer = ConsumeAsync(lifetime.Token);
        periodic = PeriodicReconciliationAsync(lifetime.Token);

        if (options.EnableWatcher)
        {
            watcher = new FileSystemWatcher(config.InputFolder)
            {
                IncludeSubdirectories = config.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = options.WatcherInternalBufferKilobytes * 1024
            };
            watcher.Created += OnWatcherEvent;
            watcher.Changed += OnWatcherEvent;
            watcher.Renamed += OnWatcherEvent;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
        }

        await ReconcileAsync("startup", cancellationToken).ConfigureAwait(false);
    }

    public async Task ReconcileAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var search = config.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var count = 0;

        foreach (var path in Directory.EnumerateFiles(config.InputFolder, "*", search))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (coordinator.IsSupportedPath(path))
            {
                await ScheduleReconciledAsync(path, cancellationToken).ConfigureAwait(false);
                count++;
            }
        }

        Interlocked.Exchange(ref reconcileRequired, 0);
        logger.LogInformation(
            "Reconciliation {Reason} scheduled {FileCount} candidate files",
            reason, count);
        await store.FinalizeAssociationsAsync(
            ProjectId, Source.Id, timeProvider.GetUtcNow(), force: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WaitForIdleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startedAt = System.Diagnostics.Stopwatch.StartNew();
        while (startedAt.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref pending) == 0)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref pending) == 0)
                {
                    return;
                }
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Ingestion session did not become idle within {timeout}; pending={Volatile.Read(ref pending)}.");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
        {
            return;
        }

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnWatcherEvent;
            watcher.Changed -= OnWatcherEvent;
            watcher.Renamed -= OnWatcherEvent;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
            watcher = null;
        }

        lifetime.Cancel();
        queue.Writer.TryComplete();

        var tasks = new[] { consumer, periodic }.Where(item => item is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lifetime.Dispose();
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs args) =>
        Schedule(args.FullPath);

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        Interlocked.Exchange(ref reconcileRequired, 1);
        logger.LogWarning(args.GetException(),
            "FileSystemWatcher reported an error; reconciliation is required");
    }

    private void Schedule(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!coordinator.IsSupportedPath(fullPath) ||
            !scheduled.TryAdd(fullPath, 0))
        {
            return;
        }

        Interlocked.Increment(ref pending);
        if (queue.Writer.TryWrite(fullPath))
        {
            return;
        }

        Interlocked.Decrement(ref pending);
        scheduled.TryRemove(fullPath, out _);
        Interlocked.Exchange(ref reconcileRequired, 1);
        logger.LogWarning(
            "Ingestion event buffer is full; candidate will be recovered by reconciliation");
    }

    private async ValueTask ScheduleReconciledAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!coordinator.IsSupportedPath(fullPath) ||
            !scheduled.TryAdd(fullPath, 0))
        {
            return;
        }

        Interlocked.Increment(ref pending);
        try
        {
            await queue.Writer.WriteAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref pending);
            scheduled.TryRemove(fullPath, out _);
            throw;
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var path in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await coordinator.IngestPathAsync(
                        path,
                        options.StableFor,
                        options.StabilityTimeout,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Ingestion candidate failed: {Path}", path);
                    Interlocked.Exchange(ref reconcileRequired, 1);
                }
                finally
                {
                    scheduled.TryRemove(path, out _);
                    Interlocked.Decrement(ref pending);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PeriodicReconciliationAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(options.ReconciliationInterval, cancellationToken).ConfigureAwait(false);
                var reason = Volatile.Read(ref reconcileRequired) != 0
                    ? "watcher_or_buffer_recovery"
                    : "periodic";
                try
                {
                    await ReconcileAsync(reason, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref reconcileRequired, 1);
                    logger.LogError(ex, "Periodic reconciliation failed");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
