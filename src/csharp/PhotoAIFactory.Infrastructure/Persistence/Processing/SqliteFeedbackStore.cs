using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;

namespace PhotoAIFactory.Infrastructure.Persistence.Processing;

public sealed class SqliteFeedbackStore(SqliteProjectDatabase database) : IFeedbackStore
{
    public async Task<FeedbackJobSnapshot?> GetActiveAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCandidateAsync(
            """
            WHERE j.project_id=$projectId
              AND q.job_id IS NOT NULL
              AND j.state IN ('PROCESSING','RETRYING','INTERRUPTED')
            ORDER BY
              CASE j.state
                WHEN 'PROCESSING' THEN 0
                WHEN 'RETRYING' THEN 1
                ELSE 2
              END,
              j.updated_at_utc,
              j.job_id
            LIMIT 1
            """,
            projectId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FeedbackJobSnapshot?> PeekNextQueuedAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCandidateAsync(
            """
            WHERE j.project_id=$projectId
              AND q.job_id IS NOT NULL
              AND j.state='QUEUED'
            ORDER BY
              q.process_next DESC,
              q.process_next_requested_at_utc,
              q.sequence_number
            LIMIT 1
            """,
            projectId,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> TryClaimAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionExactAsync(
            jobId,
            "QUEUED",
            "PROCESSING",
            "FEEDBACK_CLAIMED",
            operationId,
            nowUtc,
            cancellationToken);

    public Task<bool> ResumeRetryAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionExactAsync(
            jobId,
            "RETRYING",
            "PROCESSING",
            "FEEDBACK_RETRY_RESUMED",
            operationId,
            nowUtc,
            cancellationToken);

    public Task<bool> ResumeInterruptedAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionExactAsync(
            jobId,
            "INTERRUPTED",
            "PROCESSING",
            "FEEDBACK_INTERRUPTED_RESUMED",
            operationId,
            nowUtc,
            cancellationToken);

    public async Task<JsonElement?> GetAnalysisResultAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT result_json FROM analysis_results WHERE job_id=$jobId;";
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        var raw = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    public async Task<FeedbackPassSnapshot?> GetPassAsync(
        JobId jobId,
        int passNumber,
        CancellationToken cancellationToken = default)
    {
        if (passNumber is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(passNumber));

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                feedback_pass_id,
                job_id,
                pass_number,
                attempt_id,
                input_asset_id,
                input_sha256,
                input_kind,
                darktable_version,
                control_plan_json,
                image_path,
                image_sha256,
                image_size_bytes,
                image_width,
                image_height,
                bits_per_sample,
                channels,
                xmp_path,
                xmp_sha256,
                history_path,
                completed_at_utc
            FROM feedback_passes
            WHERE job_id=$jobId AND pass_number=$pass;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        command.Parameters.AddWithValue("$pass", passNumber);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return ReadPass(reader);
    }

    public async Task<FeedbackInspectionSnapshot?> GetInspectionAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                feedback_inspection_id,
                job_id,
                schema_version,
                recipe_sha256,
                recipe_json,
                inspection_json,
                completed_at_utc
            FROM feedback_inspections
            WHERE job_id=$jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        await using var reader = await command.ExecuteReaderAsync(
            cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        using var recipe = JsonDocument.Parse(reader.GetString(4));
        using var inspection = JsonDocument.Parse(reader.GetString(5));
        return new(
            reader.GetString(0),
            new JobId(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetString(3),
            recipe.RootElement.Clone(),
            inspection.RootElement.Clone(),
            DateTimeOffset.Parse(
                reader.GetString(6),
                CultureInfo.InvariantCulture));
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

    public async Task PersistPass1CompleteAsync(
        FeedbackPersistPass1Request request,
        CancellationToken cancellationToken = default)
    {
        await PersistStageAsync(
            request.Job.Id,
            "DARKTABLE_PASS1_COMPLETE",
            request.AttemptId,
            request.Job.InputSha256,
            request.CompletedAtUtc,
            async (connection, transaction) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO feedback_passes(
                        feedback_pass_id,
                        job_id,
                        pass_number,
                        attempt_id,
                        input_asset_id,
                        input_sha256,
                        input_kind,
                        darktable_version,
                        control_plan_json,
                        image_path,
                        image_sha256,
                        image_size_bytes,
                        image_width,
                        image_height,
                        bits_per_sample,
                        channels,
                        xmp_path,
                        xmp_sha256,
                        history_path,
                        completed_at_utc)
                    VALUES(
                        $id,$jobId,1,$attemptId,$inputAssetId,$inputSha,$inputKind,
                        $darktableVersion,$control,$imagePath,$imageSha,$size,$width,
                        $height,$bits,$channels,$xmpPath,$xmpSha,NULL,$completed);
                    """;
                BindPass(
                    command,
                    request.Job,
                    request.AttemptId,
                    request.Artifact,
                    request.XmpPath,
                    request.XmpSha256,
                    request.ControlPlan,
                    request.CompletedAtUtc);
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PersistInspectionCompleteAsync(
        FeedbackPersistInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        await PersistStageAsync(
            request.Job.Id,
            "FEEDBACK_INSPECTION_COMPLETE",
            $"feedback-inspection:{request.RecipeSha256[..16]}",
            request.RecipeSha256,
            request.CompletedAtUtc,
            async (connection, transaction) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO feedback_inspections(
                        feedback_inspection_id,
                        job_id,
                        schema_version,
                        recipe_json,
                        recipe_sha256,
                        inspection_json,
                        completed_at_utc)
                    VALUES(
                        $id,$jobId,$schema,$recipe,$recipeSha,$inspection,$completed);
                    """;
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$jobId", request.Job.Id.Value);
                command.Parameters.AddWithValue("$schema", request.SchemaVersion);
                command.Parameters.AddWithValue("$recipe", request.Recipe.GetRawText());
                command.Parameters.AddWithValue("$recipeSha", request.RecipeSha256);
                command.Parameters.AddWithValue(
                    "$inspection", request.Inspection.GetRawText());
                command.Parameters.AddWithValue(
                    "$completed", Utc(request.CompletedAtUtc));
                await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PersistPass2CompleteAsync(
        FeedbackPersistPass2Request request,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(
            cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        if (await CheckpointExistsAsync(
                connection,
                transaction,
                request.Job.Id,
                "DARKTABLE_PASS2_COMPLETE",
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using (var insertPass = connection.CreateCommand())
        {
            insertPass.Transaction = transaction;
            insertPass.CommandText = """
                INSERT INTO feedback_passes(
                    feedback_pass_id,
                    job_id,
                    pass_number,
                    attempt_id,
                    input_asset_id,
                    input_sha256,
                    input_kind,
                    darktable_version,
                    control_plan_json,
                    image_path,
                    image_sha256,
                    image_size_bytes,
                    image_width,
                    image_height,
                    bits_per_sample,
                    channels,
                    xmp_path,
                    xmp_sha256,
                    history_path,
                    completed_at_utc)
                VALUES(
                    $id,$jobId,2,$attemptId,$inputAssetId,$inputSha,$inputKind,
                    $darktableVersion,$control,$imagePath,$imageSha,$size,$width,
                    $height,$bits,$channels,$xmpPath,$xmpSha,$history,$completed);
                """;
            BindPass(
                insertPass,
                request.Job,
                request.AttemptId,
                request.Artifact,
                request.XmpPath,
                request.XmpSha256,
                request.ControlPlan,
                request.CompletedAtUtc);
            insertPass.Parameters.AddWithValue("$history", request.HistoryPath);
            await insertPass.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await InsertCheckpointAsync(
            connection,
            transaction,
            request.Job.Id,
            "DARKTABLE_PASS2_COMPLETE",
            request.AttemptId,
            request.Artifact.Sha256,
            request.CompletedAtUtc,
            cancellationToken).ConfigureAwait(false);

        JobStateMachine.EnsureTransition(JobState.Processing, JobState.Qa);
        var changed = await UpdateStateAsync(
            connection,
            transaction,
            request.Job.Id,
            "PROCESSING",
            "QA",
            request.CompletedAtUtc,
            cancellationToken).ConfigureAwait(false);
        if (changed != 1)
            throw new InvalidOperationException(
                "FEEDBACK Pass 2 completed but the Job was no longer PROCESSING.");

        await InsertTransitionAsync(
            connection,
            transaction,
            request.Job.Id,
            "PROCESSING",
            "QA",
            "FEEDBACK_BASIC_REVEAL_COMPLETE_WAITING_QA",
            $"feedback-pass2:{request.AttemptId}",
            request.CompletedAtUtc,
            cancellationToken).ConfigureAwait(false);

        await using (var deleteQueue = connection.CreateCommand())
        {
            deleteQueue.Transaction = transaction;
            deleteQueue.CommandText =
                "DELETE FROM queue_entries WHERE job_id=$jobId;";
            deleteQueue.Parameters.AddWithValue("$jobId", request.Job.Id.Value);
            await deleteQueue.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE jobs
            SET reveal_retry_count=reveal_retry_count+1,
                state='RETRYING',
                updated_at_utc=$now
            WHERE job_id=$jobId
              AND state='PROCESSING'
              AND reveal_retry_count < 2;
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
            connection,
            transaction,
            jobId,
            "PROCESSING",
            "RETRYING",
            reason,
            operationId,
            nowUtc,
            cancellationToken).ConfigureAwait(false);

        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText =
            "SELECT reveal_retry_count FROM jobs WHERE job_id=$jobId;";
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
            "FEEDBACK_INTERRUPTED",
            operationId,
            nowUtc,
            removeQueueEntry: false,
            cancellationToken);

    public Task MarkErrorAsync(
        JobId jobId,
        string operationId,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) =>
        TransitionAnyAsync(
            jobId,
            ["PROCESSING", "RETRYING"],
            "ERROR",
            reason,
            operationId,
            nowUtc,
            removeQueueEntry: true,
            cancellationToken);

    private async Task<FeedbackJobSnapshot?> ReadCandidateAsync(
        string whereAndOrder,
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                j.job_id,
                j.project_id,
                j.photo_id,
                j.state,
                j.processing_config_id,
                a.asset_id,
                a.managed_path,
                a.sha256,
                a.format,
                a.raw_support_status,
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
            reader.GetString(9),
            reader.GetInt32(10),
            reader.GetInt64(11),
            reader.GetInt32(12) != 0);
    }

    private async Task PersistStageAsync(
        JobId jobId,
        string stage,
        string attemptId,
        string inputFingerprint,
        DateTimeOffset completedAtUtc,
        Func<SqliteConnection, SqliteTransaction, Task> insertBody,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(
            cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        if (await CheckpointExistsAsync(
                connection,
                transaction,
                jobId,
                stage,
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await insertBody(connection, transaction).ConfigureAwait(false);
        await InsertCheckpointAsync(
            connection,
            transaction,
            jobId,
            stage,
            attemptId,
            inputFingerprint,
            completedAtUtc,
            cancellationToken).ConfigureAwait(false);
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
        string inputFingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var checkpoint = connection.CreateCommand();
        checkpoint.Transaction = transaction;
        checkpoint.CommandText = """
            INSERT INTO job_checkpoints(
                checkpoint_id,
                job_id,
                stage_name,
                attempt_id,
                input_fingerprint,
                created_at_utc)
            VALUES($id,$jobId,$stage,$attemptId,$fingerprint,$created);
            """;
        checkpoint.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        checkpoint.Parameters.AddWithValue("$jobId", jobId.Value);
        checkpoint.Parameters.AddWithValue("$stage", stage);
        checkpoint.Parameters.AddWithValue("$attemptId", attemptId);
        checkpoint.Parameters.AddWithValue("$fingerprint", inputFingerprint);
        checkpoint.Parameters.AddWithValue("$created", Utc(completedAtUtc));
        await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindPass(
        SqliteCommand command,
        FeedbackJobSnapshot job,
        string attemptId,
        FeedbackImageArtifact artifact,
        string xmpPath,
        string xmpSha256,
        JsonElement controlPlan,
        DateTimeOffset completedAtUtc)
    {
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$jobId", job.Id.Value);
        command.Parameters.AddWithValue("$attemptId", attemptId);
        command.Parameters.AddWithValue("$inputAssetId", job.InputAssetId);
        command.Parameters.AddWithValue("$inputSha", job.InputSha256);
        command.Parameters.AddWithValue(
            "$inputKind",
            string.Equals(job.InputFormat, "RAW", StringComparison.OrdinalIgnoreCase)
                ? "RAW"
                : "JPEG");
        command.Parameters.AddWithValue(
            "$darktableVersion", artifact.DarktableVersion);
        command.Parameters.AddWithValue("$control", controlPlan.GetRawText());
        command.Parameters.AddWithValue("$imagePath", artifact.Path);
        command.Parameters.AddWithValue("$imageSha", artifact.Sha256);
        command.Parameters.AddWithValue("$size", artifact.SizeBytes);
        command.Parameters.AddWithValue("$width", artifact.Width);
        command.Parameters.AddWithValue("$height", artifact.Height);
        command.Parameters.AddWithValue("$bits", artifact.BitsPerSample);
        command.Parameters.AddWithValue("$channels", artifact.Channels);
        command.Parameters.AddWithValue("$xmpPath", xmpPath);
        command.Parameters.AddWithValue("$xmpSha", xmpSha256);
        command.Parameters.AddWithValue("$completed", Utc(completedAtUtc));
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
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        var changed = await UpdateStateAsync(
            connection,
            transaction,
            jobId,
            from,
            to,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
        if (changed == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await InsertTransitionAsync(
            connection,
            transaction,
            jobId,
            from,
            to,
            reason,
            operationId,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
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
        bool removeQueueEntry,
        CancellationToken cancellationToken)
    {
        ValidateOperation(operationId);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);

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

        if (current is null ||
            !allowedFrom.Contains(current, StringComparer.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        JobStateMachine.EnsureTransition(ParseState(current), ParseState(toState));
        var changed = await UpdateStateAsync(
            connection,
            transaction,
            jobId,
            current,
            toState,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
        if (changed == 1)
        {
            await InsertTransitionAsync(
                connection,
                transaction,
                jobId,
                current,
                toState,
                reason,
                operationId,
                nowUtc,
                cancellationToken).ConfigureAwait(false);

            if (removeQueueEntry)
            {
                await using var deleteQueue = connection.CreateCommand();
                deleteQueue.Transaction = transaction;
                deleteQueue.CommandText =
                    "DELETE FROM queue_entries WHERE job_id=$jobId;";
                deleteQueue.Parameters.AddWithValue("$jobId", jobId.Value);
                await deleteQueue.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
            VALUES($id,$jobId,$from,$to,$reason,$operationId,$occurred);
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

    private static FeedbackPassSnapshot ReadPass(SqliteDataReader reader)
    {
        using var control = JsonDocument.Parse(reader.GetString(8));
        return new(
            reader.GetString(0),
            new JobId(reader.GetString(1)),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            control.RootElement.Clone(),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetInt64(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetString(16),
            reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            DateTimeOffset.Parse(
                reader.GetString(19),
                CultureInfo.InvariantCulture));
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

    private static void ValidateOperation(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "A durable operation ID is required.",
                nameof(value));
    }
}
