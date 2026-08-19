using System.Globalization;
using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.Persistence.Repositories;

public sealed class SqliteProjectStore : IProjectStore
{
    private readonly SqliteProjectDatabase database;

    public SqliteProjectStore(SqliteProjectDatabase database)
    {
        this.database = database;
    }

    public SqliteProjectDatabase Database => database;

    public async Task<ProjectSnapshot> CreateAsync(
        Project project,
        ConfigVersion initialConfig,
        string creationOperationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(initialConfig);
        if (initialConfig.ProjectId != project.Id || initialConfig.VersionNumber != 1)
        {
            throw new ArgumentException("Initial ConfigVersion must belong to the project and be version 1.", nameof(initialConfig));
        }

        if (string.IsNullOrWhiteSpace(creationOperationKey))
        {
            throw new ArgumentException("Creation operation key is required.", nameof(creationOperationKey));
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writerLease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prior = await FindProjectByCreationKeyAsync(connection, transaction, creationOperationKey, cancellationToken)
                .ConfigureAwait(false);
            if (prior is not null)
            {
                var priorSnapshot = await LoadSnapshotAsync(connection, transaction, prior, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(prior.Name, project.Name, StringComparison.Ordinal) ||
                    !string.Equals(priorSnapshot.LatestConfig.Sha256, initialConfig.Sha256, StringComparison.Ordinal))
                {
                    throw new IdempotencyConflictException("Project creation operation key was already used with different content.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return priorSnapshot;
            }

            await InsertProjectAsync(connection, transaction, project, creationOperationKey, cancellationToken).ConfigureAwait(false);
            await InsertConfigAsync(connection, transaction, initialConfig, cancellationToken).ConfigureAwait(false);
            var createdAudit = ProjectStateTransition.Create(
                project.Id, ProjectState.Stopped, ProjectState.Stopped, "PROJECT_CREATED",
                project.StateChangedAtUtc, project.StateRevision, creationOperationKey);
            await InsertTransitionAsync(connection, transaction, createdAudit, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ProjectSnapshot(project, [initialConfig]);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ProjectSnapshot?> GetAsync(ProjectId projectId, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var project = await FindProjectByIdAsync(connection, null, projectId, cancellationToken).ConfigureAwait(false);
        return project is null
            ? null
            : await LoadSnapshotAsync(connection, null, project, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConfigVersion> AppendAsync(
        ProjectId projectId,
        ProjectConfigV1 config,
        string operationKey,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            throw new ArgumentException("Operation key is required.", nameof(operationKey));
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writerLease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var project = await FindProjectByIdAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Project {projectId.Value} was not found.");

            var expectedJson = ProjectConfigCanonicalizer.Serialize(config);
            var expectedHash = ProjectConfigCanonicalizer.ComputeSha256(expectedJson);
            var prior = await FindConfigByOperationKeyAsync(connection, transaction, projectId, operationKey, cancellationToken)
                .ConfigureAwait(false);
            if (prior is not null)
            {
                if (!string.Equals(prior.Sha256, expectedHash, StringComparison.Ordinal))
                {
                    throw new IdempotencyConflictException("Config operation key was already used with different content.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return prior;
            }

            var nextVersion = await NextVersionAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
            var configVersion = ConfigVersion.Create(project.Id, nextVersion, config, operationKey, createdAtUtc);
            await InsertConfigAsync(connection, transaction, configVersion, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return configVersion;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<ConfigVersion>> ListAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return await LoadConfigsAsync(connection, null, projectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransitionWriteResult> TryTransitionAsync(
        ProjectId projectId,
        ProjectState expectedState,
        long expectedRevision,
        ProjectState nextState,
        string reason,
        string operationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (!ProjectStateMachine.CanTransition(expectedState, nextState))
            throw new InvalidProjectStateTransitionException(expectedState, nextState);
        if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("Transition reason and operation ID are required.");

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writerLease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await FindTransitionByOperationAsync(
                connection, transaction, projectId, operationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var replayProject = await FindProjectByIdAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
                var sameOperation = existing.FromState == expectedState && existing.ToState == nextState &&
                    string.Equals(existing.Reason, reason, StringComparison.Ordinal);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(
                    sameOperation ? TransitionWriteStatus.Replayed : TransitionWriteStatus.OperationConflict,
                    replayProject,
                    existing);
            }

            var current = await FindProjectByIdAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(TransitionWriteStatus.NotFound, null, null);
            }
            if (current.State != expectedState || current.StateRevision != expectedRevision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(TransitionWriteStatus.ConcurrencyConflict, current, null);
            }

            var updated = current.TransitionTo(nextState, occurredAtUtc);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE projects
                SET project_state=$nextState, state_revision=$nextRevision,
                    state_changed_at_utc=$changedAtUtc, updated_at_utc=$changedAtUtc
                WHERE project_id=$projectId AND project_state=$expectedState AND state_revision=$expectedRevision;
                """;
            update.Parameters.AddWithValue("$nextState", StateToken(nextState));
            update.Parameters.AddWithValue("$nextRevision", updated.StateRevision);
            update.Parameters.AddWithValue("$changedAtUtc", FormatUtc(occurredAtUtc));
            update.Parameters.AddWithValue("$projectId", projectId.Value);
            update.Parameters.AddWithValue("$expectedState", StateToken(expectedState));
            update.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return new(TransitionWriteStatus.ConcurrencyConflict, current, null);
            }

            var transition = ProjectStateTransition.Create(
                projectId, expectedState, nextState, reason, occurredAtUtc,
                updated.StateRevision, operationId);
            await InsertTransitionAsync(connection, transaction, transition, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(TransitionWriteStatus.Applied, updated, transition);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<ProjectStateTransition>> ListTransitionsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<ProjectStateTransition>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transition_id, project_id, from_state, to_state, reason,
                   occurred_at_utc, state_revision, operation_id
            FROM project_state_transitions
            WHERE project_id=$projectId
            ORDER BY state_revision;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(RestoreTransition(reader));
        return results;
    }

    public async Task<ConfigWriteResult> ApplyWhenPausedAsync(
        ProjectId projectId,
        ProjectConfigV1 config,
        string expectedConfigVersionId,
        string operationId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedConfigVersionId) || string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("Expected ConfigVersion ID and operation ID are required.");

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writerLease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var project = await FindProjectByIdAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(ConfigWriteStatus.NotFound, null, null);
            }

            var canonical = ProjectConfigCanonicalizer.Serialize(config);
            var hash = ProjectConfigCanonicalizer.ComputeSha256(canonical);
            var priorOperation = await FindConfigByOperationKeyAsync(
                connection, transaction, projectId, operationId, cancellationToken).ConfigureAwait(false);
            if (priorOperation is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(
                    string.Equals(priorOperation.Sha256, hash, StringComparison.Ordinal)
                        ? ConfigWriteStatus.Replayed
                        : ConfigWriteStatus.OperationConflict,
                    priorOperation,
                    project.State);
            }

            if (project.State != ProjectState.Paused)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(ConfigWriteStatus.ProjectNotPaused, null, project.State);
            }

            var latest = await FindLatestConfigAsync(connection, transaction, projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new ConfigIntegrityException("Project has no ConfigVersion.");
            if (!string.Equals(latest.Id, expectedConfigVersionId, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(ConfigWriteStatus.VersionConflict, latest, project.State);
            }
            if (string.Equals(latest.Sha256, hash, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(ConfigWriteStatus.Unchanged, latest, project.State);
            }

            var created = ConfigVersion.Create(
                projectId, latest.VersionNumber + 1, config, operationId, createdAtUtc);
            await InsertConfigAsync(connection, transaction, created, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ConfigWriteStatus.Created, created, project.State);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task InsertProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Project project,
        string creationOperationKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO projects(
                project_id, name, creation_operation_key, created_at_utc, updated_at_utc,
                project_state, state_revision, state_changed_at_utc)
            VALUES (
                $projectId, $name, $operationKey, $createdAtUtc, $updatedAtUtc,
                $projectState, $stateRevision, $stateChangedAtUtc);
            """;
        command.Parameters.AddWithValue("$projectId", project.Id.Value);
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$operationKey", creationOperationKey);
        command.Parameters.AddWithValue("$createdAtUtc", FormatUtc(project.CreatedAtUtc));
        command.Parameters.AddWithValue("$updatedAtUtc", FormatUtc(project.UpdatedAtUtc));
        command.Parameters.AddWithValue("$projectState", StateToken(project.State));
        command.Parameters.AddWithValue("$stateRevision", project.StateRevision);
        command.Parameters.AddWithValue("$stateChangedAtUtc", FormatUtc(project.StateChangedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectStateTransition transition,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO project_state_transitions(
                transition_id, project_id, from_state, to_state, reason,
                occurred_at_utc, state_revision, operation_id)
            VALUES ($id, $projectId, $fromState, $toState, $reason,
                    $occurredAtUtc, $stateRevision, $operationId);
            """;
        command.Parameters.AddWithValue("$id", transition.Id);
        command.Parameters.AddWithValue("$projectId", transition.ProjectId.Value);
        command.Parameters.AddWithValue("$fromState", StateToken(transition.FromState));
        command.Parameters.AddWithValue("$toState", StateToken(transition.ToState));
        command.Parameters.AddWithValue("$reason", transition.Reason);
        command.Parameters.AddWithValue("$occurredAtUtc", FormatUtc(transition.OccurredAtUtc));
        command.Parameters.AddWithValue("$stateRevision", transition.StateRevision);
        command.Parameters.AddWithValue("$operationId", transition.OperationId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertConfigAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConfigVersion config,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO project_config_versions(
                config_version_id, project_id, version_number, schema_version,
                config_json, config_sha256, operation_key, created_at_utc)
            VALUES (
                $id, $projectId, $versionNumber, $schemaVersion,
                $json, $sha256, $operationKey, $createdAtUtc);
            """;
        command.Parameters.AddWithValue("$id", config.Id);
        command.Parameters.AddWithValue("$projectId", config.ProjectId.Value);
        command.Parameters.AddWithValue("$versionNumber", config.VersionNumber);
        command.Parameters.AddWithValue("$schemaVersion", config.SchemaVersion);
        command.Parameters.AddWithValue("$json", config.CanonicalJson);
        command.Parameters.AddWithValue("$sha256", config.Sha256);
        command.Parameters.AddWithValue("$operationKey", config.OperationKey);
        command.Parameters.AddWithValue("$createdAtUtc", FormatUtc(config.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Project?> FindProjectByCreationKeyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string operationKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT project_id, name, created_at_utc, updated_at_utc,
                   project_state, state_revision, state_changed_at_utc
            FROM projects WHERE creation_operation_key=$operationKey;
            """;
        command.Parameters.AddWithValue("$operationKey", operationKey);
        return await ReadProjectAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Project?> FindProjectByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT project_id, name, created_at_utc, updated_at_utc,
                   project_state, state_revision, state_changed_at_utc
            FROM projects WHERE project_id=$projectId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        return await ReadProjectAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Project?> ReadProjectAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Project.Restore(
            new ProjectId(reader.GetString(0)),
            reader.GetString(1),
            ParseUtc(reader.GetString(2)),
            ParseUtc(reader.GetString(3)),
            ParseState(reader.GetString(4)),
            reader.GetInt64(5),
            ParseUtc(reader.GetString(6)));
    }

    private static async Task<ProjectSnapshot> LoadSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Project project,
        CancellationToken cancellationToken) =>
        new(project, await LoadConfigsAsync(connection, transaction, project.Id, cancellationToken).ConfigureAwait(false));

    private static async Task<IReadOnlyList<ConfigVersion>> LoadConfigsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        var results = new List<ConfigVersion>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT config_version_id, project_id, version_number, schema_version,
                   config_json, config_sha256, operation_key, created_at_utc
            FROM project_config_versions
            WHERE project_id=$projectId
            ORDER BY version_number;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(RestoreConfig(reader));
        }

        return results;
    }

    private static async Task<ConfigVersion?> FindConfigByOperationKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectId projectId,
        string operationKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT config_version_id, project_id, version_number, schema_version,
                   config_json, config_sha256, operation_key, created_at_utc
            FROM project_config_versions
            WHERE project_id=$projectId AND operation_key=$operationKey;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        command.Parameters.AddWithValue("$operationKey", operationKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? RestoreConfig(reader) : null;
    }

    private static async Task<ConfigVersion?> FindLatestConfigAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT config_version_id, project_id, version_number, schema_version,
                   config_json, config_sha256, operation_key, created_at_utc
            FROM project_config_versions
            WHERE project_id=$projectId
            ORDER BY version_number DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? RestoreConfig(reader) : null;
    }

    private static async Task<ProjectStateTransition?> FindTransitionByOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectId projectId,
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT transition_id, project_id, from_state, to_state, reason,
                   occurred_at_utc, state_revision, operation_id
            FROM project_state_transitions
            WHERE project_id=$projectId AND operation_id=$operationId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        command.Parameters.AddWithValue("$operationId", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? RestoreTransition(reader) : null;
    }

    private static ConfigVersion RestoreConfig(SqliteDataReader reader) => ConfigVersion.Restore(
        reader.GetString(0),
        new ProjectId(reader.GetString(1)),
        reader.GetInt32(2),
        reader.GetInt32(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        ParseUtc(reader.GetString(7)));

    private static ProjectStateTransition RestoreTransition(SqliteDataReader reader) =>
        ProjectStateTransition.Restore(
            reader.GetString(0),
            new ProjectId(reader.GetString(1)),
            ParseState(reader.GetString(2)),
            ParseState(reader.GetString(3)),
            reader.GetString(4),
            ParseUtc(reader.GetString(5)),
            reader.GetInt64(6),
            reader.GetString(7));

    private static async Task<int> NextVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version_number), 0) + 1 FROM project_config_versions WHERE project_id=$projectId;";
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static string StateToken(ProjectState state) => state switch
    {
        ProjectState.Running => "RUNNING",
        ProjectState.PauseRequested => "PAUSE_REQUESTED",
        ProjectState.Paused => "PAUSED",
        ProjectState.StopRequested => "STOP_REQUESTED",
        ProjectState.Stopped => "STOPPED",
        ProjectState.BlockedStorage => "BLOCKED_STORAGE",
        ProjectState.ComponentUnhealthy => "COMPONENT_UNHEALTHY",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static ProjectState ParseState(string value) => value switch
    {
        "RUNNING" => ProjectState.Running,
        "PAUSE_REQUESTED" => ProjectState.PauseRequested,
        "PAUSED" => ProjectState.Paused,
        "STOP_REQUESTED" => ProjectState.StopRequested,
        "STOPPED" => ProjectState.Stopped,
        "BLOCKED_STORAGE" => ProjectState.BlockedStorage,
        "COMPONENT_UNHEALTHY" => ProjectState.ComponentUnhealthy,
        _ => throw new InvalidDataException($"Unknown persisted project state '{value}'.")
    };
}
