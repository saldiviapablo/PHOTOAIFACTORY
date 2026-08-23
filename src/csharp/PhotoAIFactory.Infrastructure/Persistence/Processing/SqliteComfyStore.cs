using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;

namespace PhotoAIFactory.Infrastructure.Persistence.Processing;

public sealed class SqliteComfyStore(SqliteProjectDatabase database) : IComfyStore
{
    public async Task<ComfyJobSnapshot?> GetNextEligibleAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                j.job_id,
                j.project_id,
                j.photo_id,
                j.state,
                j.processing_config_id,
                CASE
                    WHEN fp.feedback_pass_id IS NOT NULL
                        THEN 'DARKTABLE_PASS2_COMPLETE'
                    ELSE 'BASIC_REVEAL_COMPLETE'
                END,
                COALESCE(fp.image_path, o.path),
                COALESCE(fp.image_sha256, o.sha256),
                COALESCE(fp.image_size_bytes, o.size_bytes),
                j.comfy_retry_count
            FROM jobs j
            LEFT JOIN feedback_passes fp
                ON fp.job_id=j.job_id AND fp.pass_number=2
            LEFT JOIN processing_passes pp
                ON pp.job_id=j.job_id
            LEFT JOIN outputs o
                ON o.output_id=pp.output_id
            WHERE j.project_id=$projectId
              AND j.state IN ('QA','PROCESSING','RETRYING','INTERRUPTED')
              AND NOT EXISTS (
                  SELECT 1
                  FROM job_checkpoints c
                  WHERE c.job_id=j.job_id
                    AND c.stage_name='COMFYUI_COMPLETE')
              AND (
                  EXISTS (
                      SELECT 1
                      FROM job_checkpoints c
                      WHERE c.job_id=j.job_id
                        AND c.stage_name='DARKTABLE_PASS2_COMPLETE')
                  OR EXISTS (
                      SELECT 1
                      FROM job_checkpoints c
                      WHERE c.job_id=j.job_id
                        AND c.stage_name='BASIC_REVEAL_COMPLETE')
              )
              AND COALESCE(fp.image_path, o.path) IS NOT NULL
            ORDER BY
                CASE j.state
                    WHEN 'PROCESSING' THEN 0
                    WHEN 'RETRYING' THEN 1
                    WHEN 'INTERRUPTED' THEN 2
                    ELSE 3
                END,
                j.updated_at_utc,
                j.job_id
            LIMIT 1;
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
            reader.GetInt64(8),
            reader.GetInt32(9));
    }

    public async Task<ComfyPlanSnapshot?> GetPlanAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                comfy_plan_id,
                job_id,
                schema_version,
                mode,
                plan_sha256,
                plan_json,
                created_at_utc
            FROM comfy_plans
            WHERE job_id=$jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        using var plan = JsonDocument.Parse(reader.GetString(5));
        return new(
            reader.GetString(0),
            new JobId(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            plan.RootElement.Clone(),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture));
    }

    public async Task<ComfyExecutionSnapshot?> GetExecutionAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                comfy_execution_id,
                job_id,
                attempt_id,
                status,
                input_path,
                input_sha256,
                output_path,
                output_sha256,
                output_size_bytes,
                task_manifest_json,
                workflow_manifest_json,
                prompt_ids_json,
                history_path,
                completed_at_utc
            FROM comfy_executions
            WHERE job_id=$jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        using var tasks = JsonDocument.Parse(reader.GetString(9));
        using var workflow = JsonDocument.Parse(reader.GetString(10));
        using var prompts = JsonDocument.Parse(reader.GetString(11));
        return new(
            reader.GetString(0),
            new JobId(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            tasks.RootElement.Clone(),
            workflow.RootElement.Clone(),
            prompts.RootElement.Clone(),
            reader.GetString(12),
            DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture));
    }

    public async Task<bool> HasCheckpointAsync(
        JobId jobId,
        string stageName,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*)
            FROM job_checkpoints
            WHERE job_id=$jobId AND stage_name=$stage;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        command.Parameters.AddWithValue("$stage", stageName);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    public async Task PersistPlanAsync(
        ComfyPersistPlanRequest request,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(
            cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT plan_sha256 FROM comfy_plans WHERE job_id=$jobId;";
        read.Parameters.AddWithValue("$jobId", request.JobId.Value);
        var scalar = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var existing = scalar is null or DBNull ? null : Convert.ToString(scalar, CultureInfo.InvariantCulture);
        if (existing is not null)
        {
            if (!string.Equals(existing, request.PlanSha256, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "A different immutable ComfyPlan already exists for this Job.");
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO comfy_plans(
                comfy_plan_id,
                job_id,
                schema_version,
                mode,
                plan_json,
                plan_sha256,
                created_at_utc)
            VALUES($id,$jobId,$schema,$mode,$json,$sha,$created);
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("$jobId", request.JobId.Value);
        insert.Parameters.AddWithValue("$schema", request.SchemaVersion);
        insert.Parameters.AddWithValue("$mode", request.Mode);
        insert.Parameters.AddWithValue("$json", request.Plan.GetRawText());
        insert.Parameters.AddWithValue("$sha", request.PlanSha256);
        insert.Parameters.AddWithValue("$created", Utc(request.CreatedAtUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> ClaimFromQaAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionExactAsync(
            jobId, "QA", "PROCESSING",
            "COMFYUI_CLAIMED_FROM_PRE_QA_BOUNDARY",
            operationId, nowUtc, cancellationToken);

    public Task<bool> ResumeRetryAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionExactAsync(
            jobId, "RETRYING", "PROCESSING",
            "COMFYUI_RETRY_RESUMED",
            operationId, nowUtc, cancellationToken);

    public Task<bool> ResumeInterruptedAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionExactAsync(
            jobId, "INTERRUPTED", "PROCESSING",
            "COMFYUI_INTERRUPTED_RESUMED",
            operationId, nowUtc, cancellationToken);

    public async Task<int> ScheduleRetryAsync(
        JobId jobId,
        string operationId,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateOperation(operationId);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        JobStateMachine.EnsureTransition(JobState.Processing, JobState.Retrying);
        await using var lease = await database.Writer.EnterAsync(
            cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs
            SET comfy_retry_count=comfy_retry_count+1,
                state='RETRYING',
                updated_at_utc=$now
            WHERE job_id=$jobId
              AND state='PROCESSING'
              AND comfy_retry_count < 2;
            """;
        command.Parameters.AddWithValue("$now", Utc(nowUtc));
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return -1;
        }

        await InsertTransitionAsync(
            connection, transaction, jobId,
            "PROCESSING", "RETRYING", reason, operationId, nowUtc,
            cancellationToken).ConfigureAwait(false);

        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText =
            "SELECT comfy_retry_count FROM jobs WHERE job_id=$jobId;";
        count.Parameters.AddWithValue("$jobId", jobId.Value);
        var retryCount = Convert.ToInt32(
            await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return retryCount;
    }

    public Task MarkInterruptedAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionAnyAsync(
            jobId,
            ["PROCESSING", "RETRYING"],
            "INTERRUPTED",
            "COMFYUI_INTERRUPTED",
            operationId,
            nowUtc,
            cancellationToken);

    public Task MarkErrorAsync(
        JobId jobId,
        string operationId,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionAnyAsync(
            jobId,
            ["QA", "PROCESSING", "RETRYING", "INTERRUPTED"],
            "ERROR",
            reason,
            operationId,
            nowUtc,
            cancellationToken);

    public async Task PersistCompleteAsync(
        ComfyPersistCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(
            cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (await CheckpointExistsAsync(
                connection, transaction, request.Job.Id, "COMFYUI_COMPLETE",
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO comfy_executions(
                    comfy_execution_id,
                    job_id,
                    attempt_id,
                    status,
                    input_path,
                    input_sha256,
                    output_path,
                    output_sha256,
                    output_size_bytes,
                    task_manifest_json,
                    workflow_manifest_json,
                    prompt_ids_json,
                    history_path,
                    completed_at_utc)
                VALUES(
                    $id,$jobId,$attempt,$status,$inputPath,$inputSha,
                    $outputPath,$outputSha,$outputSize,$tasks,$workflow,
                    $prompts,$history,$completed);
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$jobId", request.Job.Id.Value);
            insert.Parameters.AddWithValue("$attempt", request.AttemptId);
            insert.Parameters.AddWithValue("$status", request.Status);
            insert.Parameters.AddWithValue("$inputPath", request.Job.RevealPath);
            insert.Parameters.AddWithValue("$inputSha", request.Job.RevealSha256);
            insert.Parameters.AddWithValue("$outputPath", request.Artifact.Path);
            insert.Parameters.AddWithValue("$outputSha", request.Artifact.Sha256);
            insert.Parameters.AddWithValue("$outputSize", request.Artifact.SizeBytes);
            insert.Parameters.AddWithValue(
                "$tasks", request.TaskManifest.GetRawText());
            insert.Parameters.AddWithValue(
                "$workflow", request.Artifact.WorkflowManifest.GetRawText());
            insert.Parameters.AddWithValue(
                "$prompts", request.Artifact.PromptIds.GetRawText());
            insert.Parameters.AddWithValue("$history", request.HistoryPath);
            insert.Parameters.AddWithValue(
                "$completed", Utc(request.CompletedAtUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertCheckpointAsync(
            connection,
            transaction,
            request.Job.Id,
            "COMFYUI_COMPLETE",
            request.AttemptId,
            $"{request.Job.RevealSha256}:{request.Artifact.Sha256}",
            request.CompletedAtUtc,
            cancellationToken).ConfigureAwait(false);

        await using var stateRead = connection.CreateCommand();
        stateRead.Transaction = transaction;
        stateRead.CommandText = "SELECT state FROM jobs WHERE job_id=$jobId;";
        stateRead.Parameters.AddWithValue("$jobId", request.Job.Id.Value);
        var current = Convert.ToString(
            await stateRead.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("ComfyUI Job disappeared.");

        if (string.Equals(current, "PROCESSING", StringComparison.Ordinal))
        {
            JobStateMachine.EnsureTransition(JobState.Processing, JobState.Qa);
            if (await UpdateStateAsync(
                    connection, transaction, request.Job.Id,
                    "PROCESSING", "QA", request.CompletedAtUtc,
                    cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException(
                    "ComfyUI completed but PROCESSING -> QA could not be persisted.");

            await InsertTransitionAsync(
                connection,
                transaction,
                request.Job.Id,
                "PROCESSING",
                "QA",
                "COMFYUI_COMPLETE_WAITING_PHASE7_QA",
                $"comfy-complete:{request.AttemptId}",
                request.CompletedAtUtc,
                cancellationToken).ConfigureAwait(false);
        }
        else if (!string.Equals(current, "QA", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"COMFYUI_COMPLETE cannot be persisted from Job state {current}.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TransitionExactAsync(
        JobId jobId,
        string from,
        string to,
        string reason,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ValidateOperation(operationId);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        JobStateMachine.EnsureTransition(ParseState(from), ParseState(to));
        await using var lease = await database.Writer.EnterAsync(
            cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var changed = await UpdateStateAsync(
            connection, transaction, jobId, from, to, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        await InsertTransitionAsync(
            connection, transaction, jobId, from, to, reason, operationId,
            nowUtc, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task TransitionAnyAsync(
        JobId jobId,
        IReadOnlyList<string> allowedFrom,
        string toState,
        string reason,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ValidateOperation(operationId);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(
            cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT state FROM jobs WHERE job_id=$jobId;";
        read.Parameters.AddWithValue("$jobId", jobId.Value);
        var current = Convert.ToString(
            await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (current is null ||
            !allowedFrom.Contains(current, StringComparer.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        JobStateMachine.EnsureTransition(ParseState(current), ParseState(toState));
        if (await UpdateStateAsync(
                connection, transaction, jobId, current, toState, nowUtc,
                cancellationToken).ConfigureAwait(false) == 1)
        {
            await InsertTransitionAsync(
                connection, transaction, jobId, current, toState,
                reason, operationId, nowUtc, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> CheckpointExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobId jobId,
        string stage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*)
            FROM job_checkpoints
            WHERE job_id=$jobId AND stage_name=$stage;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        command.Parameters.AddWithValue("$stage", stage);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task InsertCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobId jobId,
        string stage,
        string attemptId,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO job_checkpoints(
                checkpoint_id,
                job_id,
                stage_name,
                attempt_id,
                input_fingerprint,
                created_at_utc)
            VALUES($id,$jobId,$stage,$attempt,$fingerprint,$created);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$attempt", attemptId);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$created", Utc(completedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> UpdateStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobId jobId,
        string expected,
        string next,
        DateTimeOffset nowUtc,
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
        return await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task InsertTransitionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobId jobId,
        string from,
        string to,
        string reason,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO job_state_transitions(
                transition_id,
                job_id,
                from_state,
                to_state,
                reason,
                operation_id,
                occurred_at_utc)
            VALUES($id,$jobId,$from,$to,$reason,$operation,$occurred);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$operation", operationId);
        command.Parameters.AddWithValue("$occurred", Utc(nowUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JobState ParseState(string value) =>
        Enum.Parse<JobState>(
            string.Concat(
                value.Split('_', StringSplitOptions.RemoveEmptyEntries)
                    .Select(part =>
                        char.ToUpperInvariant(part[0]) +
                        part[1..].ToLowerInvariant())),
            true);

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateOperation(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException(
                "A durable operation ID is required.",
                nameof(operationId));
    }
}
