using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;

namespace PhotoAIFactory.Infrastructure.Persistence.Processing;

public sealed class SqliteProcessingStore(SqliteProjectDatabase database) : IProcessingStore
{
    public async Task<BasicRevealJobSnapshot?> GetActiveAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCandidateAsync(
            """
            WHERE j.project_id=$projectId AND q.job_id IS NOT NULL AND j.state IN ('PROCESSING','RETRYING','INTERRUPTED')
            ORDER BY CASE j.state WHEN 'PROCESSING' THEN 0 WHEN 'RETRYING' THEN 1 ELSE 2 END,
                     j.updated_at_utc, j.job_id
            LIMIT 1
            """,
            projectId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BasicRevealJobSnapshot?> PeekNextQueuedAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCandidateAsync(
            """
            WHERE j.project_id=$projectId AND q.job_id IS NOT NULL AND j.state='QUEUED'
            ORDER BY q.process_next DESC,
                     q.process_next_requested_at_utc,
                     q.sequence_number
            LIMIT 1
            """,
            projectId,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> TryClaimAsync(
        JobId jobId, string operationId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionExactAsync(
            jobId, "QUEUED", "PROCESSING", "BASIC_REVEAL_CLAIMED",
            operationId, nowUtc, cancellationToken);

    public Task<bool> ResumeRetryAsync(
        JobId jobId, string operationId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionExactAsync(
            jobId, "RETRYING", "PROCESSING", "BASIC_REVEAL_RETRY_RESUMED",
            operationId, nowUtc, cancellationToken);

    public async Task<bool> ResumeInterruptedAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateOperation(operationId);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        JobStateMachine.EnsureTransition(JobState.Interrupted, JobState.Processing);
        var changed = await UpdateStateAsync(
            connection, transaction, jobId, "INTERRUPTED", "PROCESSING", nowUtc, cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await InsertTransitionAsync(
            connection, transaction, jobId, "INTERRUPTED", "PROCESSING",
            "BASIC_REVEAL_RECOVERY_RESUMED", operationId, nowUtc, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<JsonElement?> GetAnalysisResultAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_json FROM analysis_results WHERE job_id=$jobId;";
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
            return null;

        using var document = JsonDocument.Parse(
            Convert.ToString(value, CultureInfo.InvariantCulture)!);
        return document.RootElement.Clone();
    }

    public async Task<BasicRevealPassSnapshot?> GetBasicRevealPassAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.processing_pass_id, p.job_id, p.attempt_id, p.reveal_mode,
                   p.input_asset_id, p.input_sha256, p.recipe_id, p.darktable_version,
                   p.control_plan_json, o.output_id, o.path, o.sha256, o.size_bytes,
                   o.width, o.height, p.history_path, p.xmp_history_path,
                   p.completed_at_utc
            FROM processing_passes p
            JOIN outputs o ON o.output_id=p.output_id
            WHERE p.job_id=$jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadPass(reader)
            : null;
    }

    public async Task<bool> HasBasicRevealCheckpointAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM job_checkpoints
            WHERE job_id=$jobId AND stage_name='BASIC_REVEAL_COMPLETE';
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    public async Task<int> ScheduleRevealRetryAsync(
        JobId jobId,
        string operationId,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        ValidateOperation(operationId);
        await using var lease = await database.Writer.EnterAsync(
            cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        JobStateMachine.EnsureTransition(JobState.Processing, JobState.Retrying);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs
            SET state='RETRYING',
                reveal_retry_count=reveal_retry_count+1,
                updated_at_utc=$now
            WHERE job_id=$jobId
              AND state='PROCESSING'
              AND reveal_retry_count < 2;
            """;
        command.Parameters.AddWithValue("$now", Utc(nowUtc));
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        var changed = await command.ExecuteNonQueryAsync(
            cancellationToken).ConfigureAwait(false);
        if (changed == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return -1;
        }

        await InsertTransitionAsync(
            connection, transaction, jobId, "PROCESSING", "RETRYING",
            reason, operationId, nowUtc, cancellationToken).ConfigureAwait(false);

        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT reveal_retry_count FROM jobs WHERE job_id=$jobId;";
        count.Parameters.AddWithValue("$jobId", jobId.Value);
        var retryCount = Convert.ToInt32(
            await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return retryCount;
    }

    public async Task PersistBasicRevealCompleteAsync(
        BasicRevealPersistRequest request,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(
            cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT count(*) FROM job_checkpoints
                WHERE job_id=$jobId AND stage_name='BASIC_REVEAL_COMPLETE';
                """;
            existing.Parameters.AddWithValue("$jobId", request.Job.Id.Value);
            if (Convert.ToInt64(
                    await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) == 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        string? recipeId = null;
        if (request.Recipe is JsonElement recipe &&
            request.RecipeSchemaVersion is int recipeSchema &&
            request.RecipeSha256 is string recipeHash)
        {
            recipeId = Guid.NewGuid().ToString("N");
            await using var recipeCommand = connection.CreateCommand();
            recipeCommand.Transaction = transaction;
            recipeCommand.CommandText = """
                INSERT INTO processing_recipes(
                    recipe_id, job_id, schema_version, reveal_mode,
                    recipe_json, recipe_sha256, created_at_utc)
                VALUES(
                    $recipeId, $jobId, $schema, $mode,
                    $json, $sha, $created);
                """;
            recipeCommand.Parameters.AddWithValue("$recipeId", recipeId);
            recipeCommand.Parameters.AddWithValue("$jobId", request.Job.Id.Value);
            recipeCommand.Parameters.AddWithValue("$schema", recipeSchema);
            recipeCommand.Parameters.AddWithValue("$mode", Mode(request.RevealMode));
            recipeCommand.Parameters.AddWithValue("$json", recipe.GetRawText());
            recipeCommand.Parameters.AddWithValue("$sha", recipeHash);
            recipeCommand.Parameters.AddWithValue("$created", Utc(request.CompletedAtUtc));
            await recipeCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var outputId = Guid.NewGuid().ToString("N");
        await using (var output = connection.CreateCommand())
        {
            output.Transaction = transaction;
            output.CommandText = """
                INSERT INTO outputs(
                    output_id, job_id, attempt_id, stage, role,
                    path, sha256, size_bytes, width, height,
                    validated, permanent, created_at_utc)
                VALUES(
                    $outputId, $jobId, $attemptId, 'BASIC_REVEAL', 'BASIC_REVEAL_STAGING',
                    $path, $sha, $size, $width, $height,
                    1, 0, $created);
                """;
            output.Parameters.AddWithValue("$outputId", outputId);
            output.Parameters.AddWithValue("$jobId", request.Job.Id.Value);
            output.Parameters.AddWithValue("$attemptId", request.AttemptId);
            output.Parameters.AddWithValue("$path", request.Artifact.Path);
            output.Parameters.AddWithValue("$sha", request.Artifact.Sha256);
            output.Parameters.AddWithValue("$size", request.Artifact.SizeBytes);
            output.Parameters.AddWithValue("$width", request.Artifact.Width);
            output.Parameters.AddWithValue("$height", request.Artifact.Height);
            output.Parameters.AddWithValue("$created", Utc(request.CompletedAtUtc));
            await output.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var passId = Guid.NewGuid().ToString("N");
        await using (var pass = connection.CreateCommand())
        {
            pass.Transaction = transaction;
            pass.CommandText = """
                INSERT INTO processing_passes(
                    processing_pass_id, job_id, attempt_id, reveal_mode,
                    input_asset_id, input_sha256, recipe_id, darktable_version,
                    control_plan_json, output_id, history_path, xmp_history_path,
                    completed_at_utc)
                VALUES(
                    $passId, $jobId, $attemptId, $mode,
                    $inputAssetId, $inputSha, $recipeId, $darktableVersion,
                    $controlPlan, $outputId, $historyPath, $xmpPath,
                    $completed);
                """;
            pass.Parameters.AddWithValue("$passId", passId);
            pass.Parameters.AddWithValue("$jobId", request.Job.Id.Value);
            pass.Parameters.AddWithValue("$attemptId", request.AttemptId);
            pass.Parameters.AddWithValue("$mode", Mode(request.RevealMode));
            pass.Parameters.AddWithValue("$inputAssetId", request.Job.InputAssetId);
            pass.Parameters.AddWithValue("$inputSha", request.Job.InputSha256);
            pass.Parameters.AddWithValue("$recipeId", (object?)recipeId ?? DBNull.Value);
            pass.Parameters.AddWithValue("$darktableVersion", request.Artifact.DarktableVersion);
            pass.Parameters.AddWithValue("$controlPlan", request.ControlPlan.Details.GetRawText());
            pass.Parameters.AddWithValue("$outputId", outputId);
            pass.Parameters.AddWithValue("$historyPath", request.HistoryPath);
            pass.Parameters.AddWithValue("$xmpPath", (object?)request.XmpHistoryPath ?? DBNull.Value);
            pass.Parameters.AddWithValue("$completed", Utc(request.CompletedAtUtc));
            await pass.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.Transaction = transaction;
            checkpoint.CommandText = """
                INSERT INTO job_checkpoints(
                    checkpoint_id, job_id, stage_name, attempt_id,
                    input_fingerprint, created_at_utc)
                VALUES(
                    $checkpointId, $jobId, 'BASIC_REVEAL_COMPLETE', $attemptId,
                    $fingerprint, $created);
                """;
            checkpoint.Parameters.AddWithValue("$checkpointId", Guid.NewGuid().ToString("N"));
            checkpoint.Parameters.AddWithValue("$jobId", request.Job.Id.Value);
            checkpoint.Parameters.AddWithValue("$attemptId", request.AttemptId);
            checkpoint.Parameters.AddWithValue(
                "$fingerprint",
                $"{request.Job.InputSha256}:{request.Artifact.Sha256}:{request.RevealMode}");
            checkpoint.Parameters.AddWithValue("$created", Utc(request.CompletedAtUtc));
            await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteQueue = connection.CreateCommand())
        {
            deleteQueue.Transaction = transaction;
            deleteQueue.CommandText = "DELETE FROM queue_entries WHERE job_id=$jobId;";
            deleteQueue.Parameters.AddWithValue("$jobId", request.Job.Id.Value);
            await deleteQueue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        JobStateMachine.EnsureTransition(JobState.Processing, JobState.Qa);
        var moved = await UpdateStateAsync(
            connection, transaction, request.Job.Id, "PROCESSING", "QA",
            request.CompletedAtUtc, cancellationToken).ConfigureAwait(false);
        if (moved != 1)
            throw new InvalidOperationException(
                "BASIC_REVEAL_COMPLETE cannot advance a Job that is not PROCESSING.");

        await InsertTransitionAsync(
            connection, transaction, request.Job.Id, "PROCESSING", "QA",
            "BASIC_REVEAL_COMPLETE",
            $"basic-reveal-complete:{request.AttemptId}",
            request.CompletedAtUtc,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task MarkInterruptedAsync(
        JobId jobId, string operationId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionAnyAsync(
            jobId, ["PROCESSING", "RETRYING"], "INTERRUPTED",
            "BASIC_REVEAL_INTERRUPTED", operationId, nowUtc,
            removeQueueEntry: false, cancellationToken: cancellationToken);

    public Task MarkErrorAsync(
        JobId jobId, string operationId, string reason, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionAnyAsync(
            jobId, ["PROCESSING", "RETRYING"], "ERROR",
            reason, operationId, nowUtc,
            removeQueueEntry: true, cancellationToken: cancellationToken);

    private async Task<BasicRevealJobSnapshot?> ReadCandidateAsync(
        string whereAndOrder,
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT j.job_id, j.project_id, j.photo_id, j.state,
                   j.processing_config_id,
                   a.asset_id, a.managed_path, a.sha256, a.format,
                   j.reveal_retry_count,
                   COALESCE(q.sequence_number, 0),
                   COALESCE(q.process_next, 0)
            FROM jobs j
            JOIN photos p ON p.photo_id=j.photo_id
            JOIN assets a ON a.asset_id=p.master_asset_id
            LEFT JOIN queue_entries q ON q.job_id=j.job_id
            {whereAndOrder};
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new(
            new JobId(reader.GetString(0)),
            new ProjectId(reader.GetString(1)),
            new PhotoId(reader.GetString(2)),
            ParseState(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.GetInt64(10),
            reader.GetInt32(11) != 0);
    }

    private async Task<bool> TransitionExactAsync(
        JobId jobId, string from, string to, string reason,
        string operationId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ValidateOperation(operationId);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        JobStateMachine.EnsureTransition(ParseState(from), ParseState(to));
        await using var lease = await database.Writer.EnterAsync(
            cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        var changed = await UpdateStateAsync(
            connection, transaction, jobId, from, to, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await InsertTransitionAsync(
            connection, transaction, jobId, from, to,
            reason, operationId, nowUtc, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task TransitionAnyAsync(
        JobId jobId, IReadOnlyList<string> allowedFrom, string toState,
        string reason, string operationId, DateTimeOffset nowUtc,
        bool removeQueueEntry,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        ValidateOperation(operationId);
        await using var lease = await database.Writer.EnterAsync(
            cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT state FROM jobs WHERE job_id=$jobId;";
        read.Parameters.AddWithValue("$jobId", jobId.Value);
        var current = Convert.ToString(
            await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (current is null || !allowedFrom.Contains(current, StringComparer.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        JobStateMachine.EnsureTransition(ParseState(current), ParseState(toState));
        var changed = await UpdateStateAsync(
            connection, transaction, jobId, current, toState, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        if (changed == 1)
        {
            await InsertTransitionAsync(
                connection, transaction, jobId, current, toState,
                reason, operationId, nowUtc, cancellationToken).ConfigureAwait(false);

            if (removeQueueEntry)
            {
                await using var deleteQueue = connection.CreateCommand();
                deleteQueue.Transaction = transaction;
                deleteQueue.CommandText = "DELETE FROM queue_entries WHERE job_id=$jobId;";
                deleteQueue.Parameters.AddWithValue("$jobId", jobId.Value);
                await deleteQueue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> UpdateStateAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        JobId jobId, string expected, string next, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs
            SET state=$next, updated_at_utc=$now
            WHERE job_id=$jobId AND state=$expected;
            """;
        command.Parameters.AddWithValue("$next", next);
        command.Parameters.AddWithValue("$now", Utc(nowUtc));
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        command.Parameters.AddWithValue("$expected", expected);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertTransitionAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        JobId jobId, string from, string to, string reason,
        string operationId, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO job_state_transitions(
                transition_id, job_id, from_state, to_state,
                reason, operation_id, occurred_at_utc)
            VALUES(
                $id, $jobId, $from, $to,
                $reason, $operationId, $occurred);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$occurred", Utc(nowUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static BasicRevealPassSnapshot ReadPass(SqliteDataReader reader)
    {
        using var document = JsonDocument.Parse(reader.GetString(8));
        return new(
            reader.GetString(0),
            new JobId(reader.GetString(1)),
            reader.GetString(2),
            ParseMode(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            document.RootElement.Clone(),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetInt64(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            DateTimeOffset.Parse(reader.GetString(17), CultureInfo.InvariantCulture));
    }

    private static JobState ParseState(string value) =>
        Enum.Parse<JobState>(
            string.Concat(value.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant())),
            true);

    private static RevealMode ParseMode(string value) => value switch
    {
        "PRE_AI" => RevealMode.PreAi,
        "DT_AUTO" => RevealMode.DtAuto,
        _ => throw new InvalidDataException($"Unknown reveal mode {value}.")
    };

    private static string Mode(RevealMode value) => value switch
    {
        RevealMode.PreAi => "PRE_AI",
        RevealMode.DtAuto => "DT_AUTO",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateOperation(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A durable operation ID is required.", nameof(value));
    }
}
