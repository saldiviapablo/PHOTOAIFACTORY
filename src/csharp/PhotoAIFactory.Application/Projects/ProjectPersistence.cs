using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Projects;

public sealed record ProjectSnapshot(Project Project, IReadOnlyList<ConfigVersion> ConfigVersions)
{
    public ConfigVersion LatestConfig => ConfigVersions.OrderByDescending(item => item.VersionNumber).First();
}

public interface IProjectRepository
{
    Task<ProjectSnapshot> CreateAsync(
        Project project,
        ConfigVersion initialConfig,
        string creationOperationKey,
        CancellationToken cancellationToken = default);

    Task<ProjectSnapshot?> GetAsync(ProjectId projectId, CancellationToken cancellationToken = default);
}

public interface IConfigVersionRepository
{
    // Low-level persistence port retained for Slice 1 compatibility and storage tests.
    // Production changes to an existing Project must go through ConfigService.
    Task<ConfigVersion> AppendAsync(
        ProjectId projectId,
        ProjectConfigV1 config,
        string operationKey,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConfigVersion>> ListAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);
}

public enum TransitionWriteStatus
{
    Applied,
    Replayed,
    ConcurrencyConflict,
    OperationConflict,
    NotFound
}

public sealed record TransitionWriteResult(
    TransitionWriteStatus Status,
    Project? Project,
    ProjectStateTransition? Transition);

public interface IProjectLifecycleRepository
{
    Task<TransitionWriteResult> TryTransitionAsync(
        ProjectId projectId,
        ProjectState expectedState,
        long expectedRevision,
        ProjectState nextState,
        string reason,
        string operationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProjectStateTransition>> ListTransitionsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);
}

public enum ConfigWriteStatus
{
    Created,
    Replayed,
    Unchanged,
    ProjectNotPaused,
    VersionConflict,
    OperationConflict,
    NotFound
}

public sealed record ConfigWriteResult(
    ConfigWriteStatus Status,
    ConfigVersion? ConfigVersion,
    ProjectState? ProjectState);

public interface IProjectConfigChangeRepository
{
    Task<ConfigWriteResult> ApplyWhenPausedAsync(
        ProjectId projectId,
        ProjectConfigV1 config,
        string expectedConfigVersionId,
        string operationId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IProjectStore :
    IProjectRepository,
    IConfigVersionRepository,
    IProjectLifecycleRepository,
    IProjectConfigChangeRepository;

public interface IProjectStoreFactory
{
    IProjectStore Open(ProjectId projectId);
}

public sealed class IdempotencyConflictException(string message) : InvalidOperationException(message);

public sealed class ProjectService
{
    private static readonly EventId CreatedEvent = new(1900, "ProjectCreated");
    private readonly IProjectRepository? projects;
    private readonly IProjectStoreFactory? storeFactory;
    private readonly ILogger<ProjectService> logger;

    public ProjectService(IProjectRepository projects)
    {
        this.projects = projects;
        logger = NullLogger<ProjectService>.Instance;
    }

    public ProjectService(IProjectStoreFactory storeFactory)
        : this(storeFactory, NullLogger<ProjectService>.Instance)
    {
    }

    public ProjectService(IProjectStoreFactory storeFactory, ILogger<ProjectService> logger)
    {
        this.storeFactory = storeFactory;
        this.logger = logger;
    }

    public async Task<ProjectSnapshot> CreateProjectAsync(
        string name,
        ProjectConfigV1 config,
        string operationKey,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationKey(operationKey);
        var project = Project.Create(name, nowUtc);
        var initialConfig = ConfigVersion.Create(project.Id, 1, config, operationKey, nowUtc);
        var store = storeFactory?.Open(project.Id);
        var snapshot = await (store ?? projects!).CreateAsync(
            project, initialConfig, operationKey, cancellationToken).ConfigureAwait(false);
        using var scope = logger.BeginScope(
            new Dictionary<string, object?> { ["project_id"] = project.Id.Value });
        logger.LogInformation(CreatedEvent, "Project created in state {ProjectState}", project.State);
        return snapshot;
    }

    public Task<ProjectSnapshot?> OpenProjectAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default) =>
        (storeFactory?.Open(projectId) ?? projects!).GetAsync(projectId, cancellationToken);

    private static void ValidateOperationKey(string operationKey)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            throw new ArgumentException("A durable operation key is required.", nameof(operationKey));
        }
    }
}
