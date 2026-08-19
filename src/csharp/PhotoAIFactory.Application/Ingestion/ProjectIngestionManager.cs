using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Ingestion;

public enum IngestionStartStatus
{
    Started,
    AlreadyStarted,
    ProjectNotRunning,
    PendingAssociationsRequireResolution,
    ProjectNotFound
}

public sealed record IngestionStartResult(
    IngestionStartStatus Status,
    int PendingAssociationCount = 0);

public sealed class ProjectIngestionManager : IAsyncDisposable
{
    private static readonly EventId StartedEvent = new(3100, "IngestionStarted");
    private static readonly EventId StoppedEvent = new(3101, "IngestionStopped");
    private static readonly EventId PendingEvent = new(3102, "IngestionPendingAssociations");

    private readonly IProjectStoreFactory projectStores;
    private readonly IIngestionStoreFactory ingestionStores;
    private readonly IIngestionSessionFactory sessions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ProjectIngestionManager> logger;
    private readonly ConcurrentDictionary<string, IIngestionSession> active =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1, 1);

    public ProjectIngestionManager(
        IProjectStoreFactory projectStores,
        IIngestionStoreFactory ingestionStores,
        IIngestionSessionFactory sessions,
        TimeProvider timeProvider,
        ILogger<ProjectIngestionManager>? logger = null)
    {
        this.projectStores = projectStores;
        this.ingestionStores = ingestionStores;
        this.sessions = sessions;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<ProjectIngestionManager>.Instance;
    }

    public async Task<IngestionStartResult> StartAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active.ContainsKey(projectId.Value))
            {
                return new(IngestionStartStatus.AlreadyStarted);
            }

            var snapshot = await projectStores.Open(projectId)
                .GetAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                return new(IngestionStartStatus.ProjectNotFound);
            }

            if (snapshot.Project.State != ProjectState.Running)
            {
                return new(IngestionStartStatus.ProjectNotRunning);
            }

            var config = snapshot.LatestConfig;
            var projectConfig = config.ReadConfig();
            var store = ingestionStores.Open(projectId);
            var prepared = await store.PrepareSourceAsync(
                projectId,
                config.Id,
                projectConfig.InputFolder,
                projectConfig.IncludeSubfolders,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);

            if (prepared.Status == PrepareIngestionSourceStatus.PendingAssociationsRequireResolution)
            {
                logger.LogWarning(PendingEvent,
                    "Input source change is waiting for {PendingCount} pending RAW/JPEG associations",
                    prepared.PendingAssociationCount);
                return new(
                    IngestionStartStatus.PendingAssociationsRequireResolution,
                    prepared.PendingAssociationCount);
            }

            var session = sessions.Create(projectId, projectConfig, prepared.Source);
            try
            {
                await session.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            if (!active.TryAdd(projectId.Value, session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return new(IngestionStartStatus.AlreadyStarted);
            }

            logger.LogInformation(StartedEvent,
                "Ingestion started for source {SourceId}", prepared.Source.Id.Value);
            return new(IngestionStartStatus.Started);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StopAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        IIngestionSession? session;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            active.TryRemove(projectId.Value, out session);
        }
        finally
        {
            gate.Release();
        }

        if (session is null)
        {
            return;
        }

        await session.StopAsync(cancellationToken).ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);
        logger.LogInformation(StoppedEvent, "Ingestion stopped");
    }

    public Task ReconcileAsync(
        ProjectId projectId,
        string reason = "manual",
        CancellationToken cancellationToken = default)
    {
        if (!active.TryGetValue(projectId.Value, out var session))
        {
            throw new InvalidOperationException("Project ingestion is not active.");
        }

        return session.ReconcileAsync(reason, cancellationToken);
    }

    public Task WaitForIdleAsync(
        ProjectId projectId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!active.TryGetValue(projectId.Value, out var session))
        {
            throw new InvalidOperationException("Project ingestion is not active.");
        }

        return session.WaitForIdleAsync(timeout, cancellationToken);
    }

    public async Task<int> ResolvePendingAssociationsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        if (active.ContainsKey(projectId.Value))
        {
            throw new InvalidOperationException(
                "Pending source associations may only be force-resolved while ingestion is stopped.");
        }

        var store = ingestionStores.Open(projectId);
        var source = await store.GetLatestSourceAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return 0;
        }

        return await store.FinalizeAssociationsAsync(
            projectId,
            source.Id,
            timeProvider.GetUtcNow(),
            force: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        var sessionsToStop = active.ToArray();
        active.Clear();
        foreach (var item in sessionsToStop)
        {
            try
            {
                await item.Value.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await item.Value.DisposeAsync().ConfigureAwait(false);
            }
        }

        gate.Dispose();
    }
}
