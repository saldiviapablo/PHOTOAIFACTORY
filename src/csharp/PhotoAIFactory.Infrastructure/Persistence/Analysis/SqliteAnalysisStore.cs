using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Analysis;
using PhotoAIFactory.Domain.Ingestion;

namespace PhotoAIFactory.Infrastructure.Persistence.Analysis;

public sealed class SqliteAnalysisStore(SqliteProjectDatabase database) : IAnalysisStore
{
    public async Task<AnalysisJobSnapshot?> GetInitialJobByPhotoAsync(
        ProjectId projectId,
        PhotoId photoId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReadJobByPhotoAsync(connection, null, projectId, photoId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AnalysisJobSnapshot> GetOrCreateInitialJobAsync(
        JobId proposedJobId,
        ProjectId projectId,
        PhotoId photoId,
        string preselectionConfigId,
        string processingConfigId,
        ResolvedAnalysisInput input,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await ReadJobByPhotoAsync(
            connection, transaction, projectId, photoId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO jobs(
                    job_id, project_id, photo_id, parent_job_id, state,
                    preselection_config_id, processing_config_id,
                    analysis_source_asset_id, analysis_source_sha256,
                    analysis_input_kind, analysis_representation_path,
                    technical_retry_count, quality_reprocess_count,
                    created_at_utc, updated_at_utc)
                VALUES(
                    $job, $project, $photo, NULL, 'RECEIVED',
                    $pre, $processing, $asset, $sha, $kind, $path,
                    0, 0, $now, $now);
                """;
            command.Parameters.AddWithValue("$job", proposedJobId.Value);
            command.Parameters.AddWithValue("$project", projectId.Value);
            command.Parameters.AddWithValue("$photo", photoId.Value);
            command.Parameters.AddWithValue("$pre", preselectionConfigId);
            command.Parameters.AddWithValue("$processing", processingConfigId);
            command.Parameters.AddWithValue("$asset", input.SourceAssetId.Value);
            command.Parameters.AddWithValue("$sha", input.SourceSha256);
            command.Parameters.AddWithValue("$kind", InputKindToDb(input.Kind));
            command.Parameters.AddWithValue("$path", input.RepresentationPath);
            command.Parameters.AddWithValue("$now", DbTime(nowUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertTransitionAsync(
            connection,
            transaction,
            proposedJobId,
            fromState: null,
            JobState.Received,
            "JOB_CREATED",
            $"job-create:{proposedJobId.Value}",
            nowUtc,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await ReadJobByIdAsync(connection, null, proposedJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Created Job could not be read.");
    }

    public async Task<AnalysisJobSnapshot?> GetJobAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs WHERE job_id=$job;";
        command.Parameters.AddWithValue("$job", jobId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapJob(reader) : null;
    }

    public async Task<AnalysisResultSnapshot?> GetAnalysisAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT analysis_id, job_id, schema_version, result_json, created_at_utc
            FROM analysis_results
            WHERE job_id=$job;
            """;
        command.Parameters.AddWithValue("$job", jobId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using var document = JsonDocument.Parse(reader.GetString(3));
        return new(
            reader.GetString(0),
            new JobId(reader.GetString(1)),
            reader.GetInt32(2),
            document.RootElement.Clone(),
            ParseTime(reader.GetString(4)));
    }

    public async Task<PreselectionResultSnapshot?> GetPreselectionAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT preselection_id, job_id, decision, findings_json, created_at_utc
            FROM preselection_results
            WHERE job_id=$job;
            """;
        command.Parameters.AddWithValue("$job", jobId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        using var document = JsonDocument.Parse(reader.GetString(3));
        return new(
            reader.GetString(0),
            new JobId(reader.GetString(1)),
            DecisionFromDb(reader.GetString(2)),
            document.RootElement.Clone(),
            ParseTime(reader.GetString(4)));
    }

    public Task MarkAnalyzingAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            jobId,
            [JobState.Received, JobState.Retrying, JobState.Interrupted],
            JobState.Analyzing,
            "ANALYSIS_STARTED_OR_RESUMED",
            operationId,
            nowUtc,
            cancellationToken);

    public Task MarkInterruptedAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            jobId,
            [JobState.Analyzing, JobState.Retrying],
            JobState.Interrupted,
            "ANALYSIS_INTERRUPTED",
            operationId,
            nowUtc,
            cancellationToken);

    public Task MarkErrorAsync(
        JobId jobId,
        string operationId,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(
            jobId,
            [JobState.Received, JobState.Analyzing, JobState.Retrying],
            JobState.Error,
            string.IsNullOrWhiteSpace(reason) ? "ANALYSIS_ERROR" : reason,
            operationId,
            nowUtc,
            cancellationToken);

    public async Task IncrementTechnicalRetryAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var existing = await ReadJobByIdAsync(connection, transaction, jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {jobId.Value} was not found.");

        if (existing.State != JobState.Analyzing)
        {
            throw new InvalidOperationException(
                $"Technical retry requires ANALYZING, got {existing.State}.");
        }

        if (existing.TechnicalRetryCount >= 2)
        {
            throw new InvalidOperationException("Technical retry limit reached.");
        }

        JobStateMachine.EnsureTransition(existing.State, JobState.Retrying);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE jobs
                SET technical_retry_count=technical_retry_count+1,
                    state='RETRYING',
                    updated_at_utc=$now
                WHERE job_id=$job AND state='ANALYZING' AND technical_retry_count < 2;
                """;
            update.Parameters.AddWithValue("$job", jobId.Value);
            update.Parameters.AddWithValue("$now", DbTime(nowUtc));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Technical retry update lost its state precondition.");
            }
        }

        await InsertTransitionAsync(
            connection,
            transaction,
            jobId,
            existing.State,
            JobState.Retrying,
            "TECHNICAL_RETRY",
            operationId,
            nowUtc,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PersistAnalysisCompleteAsync(
        AnalysisJobSnapshot job,
        string attemptId,
        int schemaVersion,
        JsonElement result,
        IReadOnlyList<AnalysisModelExecution> modelExecutions,
        string inputFingerprint,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFingerprint);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await HasCheckpointAsync(
                connection, transaction, job.Id, "ANALYSIS_COMPLETE", cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var current = await ReadJobByIdAsync(connection, transaction, job.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Job disappeared before ANALYSIS_COMPLETE.");
        if (current.State != JobState.Analyzing)
        {
            throw new InvalidOperationException(
                $"ANALYSIS_COMPLETE requires ANALYZING, got {current.State}.");
        }

        await using (var analysis = connection.CreateCommand())
        {
            analysis.Transaction = transaction;
            analysis.CommandText = """
                INSERT INTO analysis_results(
                    analysis_id, job_id, schema_version, result_json, created_at_utc)
                VALUES($id, $job, $schema, $json, $now);
                """;
            analysis.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            analysis.Parameters.AddWithValue("$job", job.Id.Value);
            analysis.Parameters.AddWithValue("$schema", schemaVersion);
            analysis.Parameters.AddWithValue("$json", result.GetRawText());
            analysis.Parameters.AddWithValue("$now", DbTime(nowUtc));
            await analysis.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var execution in modelExecutions)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO model_executions(
                    model_execution_id, job_id, stage, model_id, model_version,
                    artifact_set_sha256, parameters_json, timings_json, created_at_utc)
                VALUES($id, $job, 'ANALYSIS', $model, $version, $hash, $parameters, $timings, $now);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$job", job.Id.Value);
            command.Parameters.AddWithValue("$model", execution.ModelId);
            command.Parameters.AddWithValue("$version", execution.ModelVersion);
            command.Parameters.AddWithValue(
                "$hash", (object?)execution.ArtifactSetSha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$parameters", execution.Parameters.GetRawText());
            command.Parameters.AddWithValue("$timings", execution.Timings.GetRawText());
            command.Parameters.AddWithValue("$now", DbTime(nowUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertCheckpointAsync(
            connection,
            transaction,
            job.Id,
            "ANALYSIS_COMPLETE",
            attemptId,
            inputFingerprint,
            nowUtc,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PersistPreselectionCompleteAsync(
        AnalysisJobSnapshot job,
        string attemptId,
        PreselectionDecision decision,
        JsonElement findings,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await HasCheckpointAsync(
                connection, transaction, job.Id, "PRESELECTION_COMPLETE", cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await HasCheckpointAsync(
                connection, transaction, job.Id, "ANALYSIS_COMPLETE", cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "PRESELECTION_COMPLETE cannot be persisted before ANALYSIS_COMPLETE.");
        }

        var current = await ReadJobByIdAsync(connection, transaction, job.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Job disappeared before PRESELECTION_COMPLETE.");
        if (current.State != JobState.Analyzing)
        {
            throw new InvalidOperationException(
                $"Preselection transition requires ANALYZING, got {current.State}.");
        }

        await using (var resultCommand = connection.CreateCommand())
        {
            resultCommand.Transaction = transaction;
            resultCommand.CommandText = """
                INSERT INTO preselection_results(
                    preselection_id, job_id, decision, findings_json, created_at_utc)
                VALUES($id, $job, $decision, $findings, $now);
                """;
            resultCommand.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            resultCommand.Parameters.AddWithValue("$job", job.Id.Value);
            resultCommand.Parameters.AddWithValue("$decision", DecisionToDb(decision));
            resultCommand.Parameters.AddWithValue("$findings", findings.GetRawText());
            resultCommand.Parameters.AddWithValue("$now", DbTime(nowUtc));
            await resultCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var targetState = decision switch
        {
            PreselectionDecision.Approved => JobState.Queued,
            PreselectionDecision.ReviewPre => JobState.ReviewPre,
            PreselectionDecision.RejectedPre => JobState.RejectedPre,
            _ => throw new ArgumentOutOfRangeException(nameof(decision))
        };
        JobStateMachine.EnsureTransition(current.State, targetState);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE jobs
                SET state=$state, updated_at_utc=$now
                WHERE job_id=$job AND state='ANALYZING';
                """;
            update.Parameters.AddWithValue("$state", JobStateToDb(targetState));
            update.Parameters.AddWithValue("$now", DbTime(nowUtc));
            update.Parameters.AddWithValue("$job", job.Id.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "Preselection transition lost its ANALYZING precondition.");
            }
        }

        await InsertTransitionAsync(
            connection,
            transaction,
            job.Id,
            current.State,
            targetState,
            $"PRESELECTION_{DecisionToDb(decision)}",
            $"preselection:{attemptId}",
            nowUtc,
            cancellationToken).ConfigureAwait(false);

        if (decision == PreselectionDecision.Approved)
        {
            await using var enqueue = connection.CreateCommand();
            enqueue.Transaction = transaction;
            enqueue.CommandText = """
                INSERT INTO queue_entries(
                    queue_entry_id, project_id, job_id, sequence_number,
                    process_next, enqueued_at_utc, process_next_requested_at_utc)
                SELECT
                    $id, $project, $job,
                    COALESCE(MAX(sequence_number), 0) + 1,
                    0, $now, NULL
                FROM queue_entries
                WHERE project_id=$project;
                """;
            enqueue.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            enqueue.Parameters.AddWithValue("$project", job.ProjectId.Value);
            enqueue.Parameters.AddWithValue("$job", job.Id.Value);
            enqueue.Parameters.AddWithValue("$now", DbTime(nowUtc));
            await enqueue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertCheckpointAsync(
            connection,
            transaction,
            job.Id,
            "PRESELECTION_COMPLETE",
            attemptId,
            $"analysis:{job.AnalysisSourceSha256}",
            nowUtc,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasCheckpointAsync(
        JobId jobId,
        string stageName,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await HasCheckpointAsync(
            connection, null, jobId, stageName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QueueEntrySnapshot>> ListQueueAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT queue_entry_id, project_id, job_id, sequence_number, process_next,
                   enqueued_at_utc, process_next_requested_at_utc
            FROM queue_entries
            WHERE project_id=$project
            ORDER BY process_next DESC,
                     CASE WHEN process_next=1 THEN process_next_requested_at_utc END ASC,
                     sequence_number ASC;
            """;
        command.Parameters.AddWithValue("$project", projectId.Value);

        var rows = new List<QueueEntrySnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new(
                reader.GetString(0),
                new ProjectId(reader.GetString(1)),
                new JobId(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetInt64(4) == 1,
                ParseTime(reader.GetString(5)),
                reader.IsDBNull(6) ? null : ParseTime(reader.GetString(6))));
        }

        return rows;
    }

    public async Task RequestProcessNextAsync(
        ProjectId projectId,
        JobId jobId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = """
                UPDATE queue_entries
                SET process_next=0, process_next_requested_at_utc=NULL
                WHERE project_id=$project AND process_next=1;
                """;
            clear.Parameters.AddWithValue("$project", projectId.Value);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var set = connection.CreateCommand())
        {
            set.Transaction = transaction;
            set.CommandText = """
                UPDATE queue_entries
                SET process_next=1, process_next_requested_at_utc=$now
                WHERE project_id=$project AND job_id=$job;
                """;
            set.Parameters.AddWithValue("$project", projectId.Value);
            set.Parameters.AddWithValue("$job", jobId.Value);
            set.Parameters.AddWithValue("$now", DbTime(nowUtc));
            if (await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "PROCESS_NEXT target is not queued in this project.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TransitionAsync(
        JobId jobId,
        IReadOnlyCollection<JobState> allowedSources,
        JobState target,
        string reason,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await HasOperationAsync(
                connection, transaction, jobId, operationId, cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var existing = await ReadJobByIdAsync(connection, transaction, jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job {jobId.Value} was not found.");

        if (existing.State == target)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!allowedSources.Contains(existing.State))
        {
            throw new InvalidOperationException(
                $"Cannot transition Job {jobId.Value} from {existing.State} to {target}.");
        }

        JobStateMachine.EnsureTransition(existing.State, target);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE jobs
                SET state=$state, updated_at_utc=$now
                WHERE job_id=$job AND state=$from;
                """;
            update.Parameters.AddWithValue("$state", JobStateToDb(target));
            update.Parameters.AddWithValue("$now", DbTime(nowUtc));
            update.Parameters.AddWithValue("$job", jobId.Value);
            update.Parameters.AddWithValue("$from", JobStateToDb(existing.State));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Job transition lost its source-state precondition.");
            }
        }

        await InsertTransitionAsync(
            connection,
            transaction,
            jobId,
            existing.State,
            target,
            reason,
            operationId,
            nowUtc,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AnalysisJobSnapshot?> ReadJobByPhotoAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProjectId projectId,
        PhotoId photoId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT *
            FROM jobs
            WHERE project_id=$project AND photo_id=$photo AND parent_job_id IS NULL;
            """;
        command.Parameters.AddWithValue("$project", projectId.Value);
        command.Parameters.AddWithValue("$photo", photoId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapJob(reader) : null;
    }

    private static async Task<AnalysisJobSnapshot?> ReadJobByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        JobId jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM jobs WHERE job_id=$job;";
        command.Parameters.AddWithValue("$job", jobId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapJob(reader) : null;
    }

    private static AnalysisJobSnapshot MapJob(SqliteDataReader reader) =>
        new(
            new JobId(reader.GetString(reader.GetOrdinal("job_id"))),
            new ProjectId(reader.GetString(reader.GetOrdinal("project_id"))),
            new PhotoId(reader.GetString(reader.GetOrdinal("photo_id"))),
            JobStateFromDb(reader.GetString(reader.GetOrdinal("state"))),
            reader.GetString(reader.GetOrdinal("preselection_config_id")),
            reader.GetString(reader.GetOrdinal("processing_config_id")),
            new AssetId(reader.GetString(reader.GetOrdinal("analysis_source_asset_id"))),
            reader.GetString(reader.GetOrdinal("analysis_source_sha256")),
            InputKindFromDb(reader.GetString(reader.GetOrdinal("analysis_input_kind"))),
            reader.GetString(reader.GetOrdinal("analysis_representation_path")),
            reader.GetInt32(reader.GetOrdinal("technical_retry_count")),
            ParseTime(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            ParseTime(reader.GetString(reader.GetOrdinal("updated_at_utc"))));

    private static async Task<bool> HasCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        JobId jobId,
        string stageName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*)
            FROM job_checkpoints
            WHERE job_id=$job AND stage_name=$stage;
            """;
        command.Parameters.AddWithValue("$job", jobId.Value);
        command.Parameters.AddWithValue("$stage", stageName);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> HasOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobId jobId,
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*)
            FROM job_state_transitions
            WHERE job_id=$job AND operation_id=$operation;
            """;
        command.Parameters.AddWithValue("$job", jobId.Value);
        command.Parameters.AddWithValue("$operation", operationId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task InsertTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobId jobId,
        JobState? fromState,
        JobState toState,
        string reason,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO job_state_transitions(
                transition_id, job_id, from_state, to_state, reason, operation_id, occurred_at_utc)
            VALUES($id, $job, $from, $to, $reason, $operation, $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$job", jobId.Value);
        command.Parameters.AddWithValue(
            "$from", fromState is null ? DBNull.Value : JobStateToDb(fromState.Value));
        command.Parameters.AddWithValue("$to", JobStateToDb(toState));
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$operation", operationId);
        command.Parameters.AddWithValue("$now", DbTime(nowUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobId jobId,
        string stageName,
        string attemptId,
        string inputFingerprint,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO job_checkpoints(
                checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
            VALUES($id, $job, $stage, $attempt, $fingerprint, $now);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$job", jobId.Value);
        command.Parameters.AddWithValue("$stage", stageName);
        command.Parameters.AddWithValue("$attempt", attemptId);
        command.Parameters.AddWithValue("$fingerprint", inputFingerprint);
        command.Parameters.AddWithValue("$now", DbTime(nowUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string DbTime(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string InputKindToDb(AnalysisInputKind kind) => kind switch
    {
        AnalysisInputKind.JpegCamera => "JPEG_CAMERA",
        AnalysisInputKind.JpegMaster => "JPEG_MASTER",
        AnalysisInputKind.RawPreview => "RAW_PREVIEW",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static AnalysisInputKind InputKindFromDb(string value) => value switch
    {
        "JPEG_CAMERA" => AnalysisInputKind.JpegCamera,
        "JPEG_MASTER" => AnalysisInputKind.JpegMaster,
        "RAW_PREVIEW" => AnalysisInputKind.RawPreview,
        _ => throw new InvalidDataException($"Unknown analysis_input_kind '{value}'.")
    };

    private static string DecisionToDb(PreselectionDecision decision) => decision switch
    {
        PreselectionDecision.Approved => "APPROVED",
        PreselectionDecision.ReviewPre => "REVIEW_PRE",
        PreselectionDecision.RejectedPre => "REJECTED_PRE",
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };

    private static PreselectionDecision DecisionFromDb(string value) => value switch
    {
        "APPROVED" => PreselectionDecision.Approved,
        "REVIEW_PRE" => PreselectionDecision.ReviewPre,
        "REJECTED_PRE" => PreselectionDecision.RejectedPre,
        _ => throw new InvalidDataException($"Unknown preselection decision '{value}'.")
    };

    private static string JobStateToDb(JobState state) => state switch
    {
        JobState.Received => "RECEIVED",
        JobState.Analyzing => "ANALYZING",
        JobState.ReviewPre => "REVIEW_PRE",
        JobState.RejectedPre => "REJECTED_PRE",
        JobState.Queued => "QUEUED",
        JobState.Processing => "PROCESSING",
        JobState.Qa => "QA",
        JobState.ReviewFinal => "REVIEW_FINAL",
        JobState.RejectedFinal => "REJECTED_FINAL",
        JobState.Completed => "COMPLETED",
        JobState.Error => "ERROR",
        JobState.CancelRequested => "CANCEL_REQUESTED",
        JobState.Cancelled => "CANCELLED",
        JobState.Retrying => "RETRYING",
        JobState.Interrupted => "INTERRUPTED",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static JobState JobStateFromDb(string value) => value switch
    {
        "RECEIVED" => JobState.Received,
        "ANALYZING" => JobState.Analyzing,
        "REVIEW_PRE" => JobState.ReviewPre,
        "REJECTED_PRE" => JobState.RejectedPre,
        "QUEUED" => JobState.Queued,
        "PROCESSING" => JobState.Processing,
        "QA" => JobState.Qa,
        "REVIEW_FINAL" => JobState.ReviewFinal,
        "REJECTED_FINAL" => JobState.RejectedFinal,
        "COMPLETED" => JobState.Completed,
        "ERROR" => JobState.Error,
        "CANCEL_REQUESTED" => JobState.CancelRequested,
        "CANCELLED" => JobState.Cancelled,
        "RETRYING" => JobState.Retrying,
        "INTERRUPTED" => JobState.Interrupted,
        _ => throw new InvalidDataException($"Unknown Job state '{value}'.")
    };
}
