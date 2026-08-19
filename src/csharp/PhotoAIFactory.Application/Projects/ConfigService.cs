using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Projects;

public enum ConfigChangeStatus
{
    Created,
    Unchanged,
    RejectedProjectNotPaused,
    VersionConflict,
    OperationConflict,
    NotFound
}

public sealed record ConfigChangeResult(
    ConfigChangeStatus Status,
    ConfigVersion? ConfigVersion,
    ProjectState? ProjectState);

public sealed class ConfigService
{
    private static readonly EventId RejectedEvent = new(2100, "ConfigChangeRejected");
    private static readonly EventId CreatedEvent = new(2101, "ConfigVersionCreated");
    private static readonly EventId UnchangedEvent = new(2102, "ConfigUnchanged");
    private static readonly EventId ConflictEvent = new(2103, "LifecycleConflict");

    private readonly IProjectStoreFactory stores;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ConfigService> logger;

    public ConfigService(
        IProjectStoreFactory stores,
        TimeProvider timeProvider,
        ILogger<ConfigService>? logger = null)
    {
        this.stores = stores;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<ConfigService>.Instance;
    }

    public async Task<ConfigChangeResult> ApplyAsync(
        ProjectId projectId,
        ProjectConfigV1 config,
        string expectedConfigVersionId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(expectedConfigVersionId) || string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("Expected ConfigVersion ID and operation ID are required.");

        var store = stores.Open(projectId);
        var snapshot = await store.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return new(ConfigChangeStatus.NotFound, null, null);
        if (snapshot.Project.State != ProjectState.Paused)
        {
            Log(RejectedEvent, projectId, "Config change rejected in state {ProjectState}", snapshot.Project.State);
            return new(ConfigChangeStatus.RejectedProjectNotPaused, null, snapshot.Project.State);
        }

        var write = await store.ApplyWhenPausedAsync(
            projectId, config, expectedConfigVersionId, operationId,
            timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var result = write.Status switch
        {
            ConfigWriteStatus.Created or ConfigWriteStatus.Replayed =>
                new ConfigChangeResult(ConfigChangeStatus.Created, write.ConfigVersion, write.ProjectState),
            ConfigWriteStatus.Unchanged =>
                new ConfigChangeResult(ConfigChangeStatus.Unchanged, write.ConfigVersion, write.ProjectState),
            ConfigWriteStatus.ProjectNotPaused =>
                new ConfigChangeResult(ConfigChangeStatus.RejectedProjectNotPaused, null, write.ProjectState),
            ConfigWriteStatus.VersionConflict =>
                new ConfigChangeResult(ConfigChangeStatus.VersionConflict, write.ConfigVersion, write.ProjectState),
            ConfigWriteStatus.OperationConflict =>
                new ConfigChangeResult(ConfigChangeStatus.OperationConflict, write.ConfigVersion, write.ProjectState),
            ConfigWriteStatus.NotFound => new ConfigChangeResult(ConfigChangeStatus.NotFound, null, null),
            _ => throw new ArgumentOutOfRangeException()
        };

        var eventId = result.Status switch
        {
            ConfigChangeStatus.Created => CreatedEvent,
            ConfigChangeStatus.Unchanged => UnchangedEvent,
            ConfigChangeStatus.RejectedProjectNotPaused => RejectedEvent,
            _ => ConflictEvent
        };
        Log(eventId, projectId, "Config change result {ConfigChangeStatus}", result.Status);
        return result;
    }

    private void Log(EventId eventId, ProjectId projectId, string template, object value)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["project_id"] = projectId.Value });
        logger.LogInformation(eventId, template, value);
    }
}
