using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Qa;

namespace PhotoAIFactory.Infrastructure.Persistence.Qa;

public sealed class SqliteQaStore(SqliteProjectDatabase database) : IQaStore
{
    public async Task<QaJobCandidateSnapshot?> GetNextEligibleQaJobAsync(
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
                COALESCE(ce.output_path, fp.image_path, o.path) AS candidate_path,
                COALESCE(ce.output_sha256, fp.image_sha256, o.sha256) AS candidate_sha,
                COALESCE(ce.output_size_bytes, fp.image_size_bytes, o.size_bytes) AS candidate_size,
                j.technical_retry_count,
                j.quality_reprocess_count,
                j.parent_job_id
            FROM jobs j
            LEFT JOIN comfy_executions ce ON ce.job_id = j.job_id
            LEFT JOIN feedback_passes fp ON fp.job_id = j.job_id AND fp.pass_number = 2
            LEFT JOIN processing_passes pp ON pp.job_id = j.job_id
            LEFT JOIN outputs o ON o.output_id = pp.output_id
            WHERE j.project_id = $projectId
              AND j.state IN ('QA', 'PROCESSING', 'RETRYING', 'INTERRUPTED')
              AND EXISTS (
                  SELECT 1 FROM job_checkpoints c
                  WHERE c.job_id = j.job_id AND c.stage_name = 'COMFYUI_COMPLETE'
              )
              AND NOT EXISTS (
                  SELECT 1 FROM job_checkpoints c
                  WHERE c.job_id = j.job_id AND c.stage_name = 'QA_COMPLETE'
              )
            ORDER BY j.created_at_utc ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return MapCandidate(reader);
    }

    public async Task<QaJobCandidateSnapshot?> GetJobAsync(
        JobId jobId,
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
                COALESCE(ce.output_path, fp.image_path, o.path) AS candidate_path,
                COALESCE(ce.output_sha256, fp.image_sha256, o.sha256) AS candidate_sha,
                COALESCE(ce.output_size_bytes, fp.image_size_bytes, o.size_bytes) AS candidate_size,
                j.technical_retry_count,
                j.quality_reprocess_count,
                j.parent_job_id
            FROM jobs j
            LEFT JOIN comfy_executions ce ON ce.job_id = j.job_id
            LEFT JOIN feedback_passes fp ON fp.job_id = j.job_id AND fp.pass_number = 2
            LEFT JOIN processing_passes pp ON pp.job_id = j.job_id
            LEFT JOIN outputs o ON o.output_id = pp.output_id
            WHERE j.job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return MapCandidate(reader);
    }

    public async Task<QaResultSnapshot?> GetQaResultAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                qa_result_id,
                job_id,
                attempt_id,
                decision,
                result_json,
                input_path,
                input_sha256,
                created_at_utc
            FROM qa_results
            WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return MapQaResult(reader);
    }

    public async Task<bool> HasQaResultAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM qa_results WHERE job_id = $jobId;";
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return count > 0;
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
            WHERE job_id = $jobId AND stage_name = $stageName;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        command.Parameters.AddWithValue("$stageName", stageName);
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return count > 0;
    }

    public async Task PersistQaResultAsync(
        PersistQaResultRequest request,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writeLock = await database.Writer.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            createIfMissing: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var existing = await ReadQaResultForUpdateAsync(
                connection, transaction, request.JobId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                var reqCreatedStr = request.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture);
                var extCreatedStr = existing.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture);

                if (string.Equals(existing.AttemptId, request.AttemptId, StringComparison.Ordinal) &&
                    string.Equals(existing.Decision, request.Decision, StringComparison.Ordinal) &&
                    string.Equals(existing.ResultJson.GetRawText(), request.ResultJson.GetRawText(), StringComparison.Ordinal) &&
                    string.Equals(existing.InputPath, request.InputPath, StringComparison.Ordinal) &&
                    string.Equals(existing.InputSha256, request.InputSha256, StringComparison.Ordinal) &&
                    string.Equals(extCreatedStr, reqCreatedStr, StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                throw new InvalidOperationException(
                    $"Job {request.JobId.Value} already has a differing QA result persisted.");
            }

            var qaResultId = Guid.NewGuid().ToString("N");
            await using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = """
                INSERT INTO qa_results(
                    qa_result_id,
                    job_id,
                    attempt_id,
                    decision,
                    result_json,
                    input_path,
                    input_sha256,
                    created_at_utc)
                VALUES(
                    $id,
                    $jobId,
                    $attemptId,
                    $decision,
                    $resultJson,
                    $inputPath,
                    $inputSha256,
                    $createdAtUtc);
                """;
            insertCmd.Parameters.AddWithValue("$id", qaResultId);
            insertCmd.Parameters.AddWithValue("$jobId", request.JobId.Value);
            insertCmd.Parameters.AddWithValue("$attemptId", request.AttemptId);
            insertCmd.Parameters.AddWithValue("$decision", request.Decision);
            insertCmd.Parameters.AddWithValue("$resultJson", request.ResultJson.GetRawText());
            insertCmd.Parameters.AddWithValue("$inputPath", request.InputPath);
            insertCmd.Parameters.AddWithValue("$inputSha256", request.InputSha256);
            insertCmd.Parameters.AddWithValue(
                "$createdAtUtc",
                request.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));

            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ReviewItemSnapshot?> GetPendingReviewItemAsync(
        JobId jobId,
        string reviewKind,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                review_item_id,
                job_id,
                review_kind,
                status,
                created_at_utc,
                resolved_at_utc,
                resolution,
                resolution_operation_id
            FROM review_items
            WHERE job_id = $jobId
              AND review_kind = $reviewKind
              AND status = 'PENDING';
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        command.Parameters.AddWithValue("$reviewKind", reviewKind);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return MapReviewItem(reader);
    }

    public async Task<ReviewItemSnapshot?> GetReviewItemByIdAsync(
        string reviewItemId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                review_item_id,
                job_id,
                review_kind,
                status,
                created_at_utc,
                resolved_at_utc,
                resolution,
                resolution_operation_id
            FROM review_items
            WHERE review_item_id = $reviewItemId;
            """;
        command.Parameters.AddWithValue("$reviewItemId", reviewItemId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return MapReviewItem(reader);
    }

    public async Task CreateReviewItemAsync(
        CreateReviewItemRequest request,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writeLock = await database.Writer.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            createIfMissing: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var checkCmd = connection.CreateCommand();
            checkCmd.Transaction = transaction;
            checkCmd.CommandText = """
                SELECT review_item_id
                FROM review_items
                WHERE job_id = $jobId
                  AND review_kind = $reviewKind
                  AND status = 'PENDING';
                """;
            checkCmd.Parameters.AddWithValue("$jobId", request.JobId.Value);
            checkCmd.Parameters.AddWithValue("$reviewKind", request.ReviewKind);
            var existingId = await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (existingId is not null and not DBNull)
            {
                if (string.Equals(Convert.ToString(existingId), request.ReviewItemId, StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                throw new InvalidOperationException(
                    $"Job {request.JobId.Value} already has a pending {request.ReviewKind} review item ({existingId}).");
            }

            await using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = """
                INSERT INTO review_items(
                    review_item_id,
                    job_id,
                    review_kind,
                    status,
                    created_at_utc)
                VALUES(
                    $id,
                    $jobId,
                    $reviewKind,
                    'PENDING',
                    $createdAtUtc);
                """;
            insertCmd.Parameters.AddWithValue("$id", request.ReviewItemId);
            insertCmd.Parameters.AddWithValue("$jobId", request.JobId.Value);
            insertCmd.Parameters.AddWithValue("$reviewKind", request.ReviewKind);
            insertCmd.Parameters.AddWithValue(
                "$createdAtUtc",
                request.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));

            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ResolveReviewItemAsync(
        ResolveReviewItemRequest request,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writeLock = await database.Writer.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            createIfMissing: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var readCmd = connection.CreateCommand();
            readCmd.Transaction = transaction;
            readCmd.CommandText = """
                SELECT status, resolution, resolution_operation_id
                FROM review_items
                WHERE review_item_id = $id;
                """;
            readCmd.Parameters.AddWithValue("$id", request.ReviewItemId);
            await using var reader = await readCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException($"Review item {request.ReviewItemId} not found.");

            var status = reader.GetString(0);
            var resolution = reader.IsDBNull(1) ? null : reader.GetString(1);
            var opId = reader.IsDBNull(2) ? null : reader.GetString(2);
            await reader.DisposeAsync().ConfigureAwait(false);

            if (string.Equals(status, "RESOLVED", StringComparison.Ordinal))
            {
                if (string.Equals(resolution, request.Resolution, StringComparison.Ordinal) &&
                    string.Equals(opId, request.ResolutionOperationId, StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                throw new InvalidOperationException($"Review item {request.ReviewItemId} already resolved with different resolution.");
            }

            await using var updateCmd = connection.CreateCommand();
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = """
                UPDATE review_items
                SET status = 'RESOLVED',
                    resolved_at_utc = $resolvedAtUtc,
                    resolution = $resolution,
                    resolution_operation_id = $opId
                WHERE review_item_id = $id
                  AND status = 'PENDING';
                """;
            updateCmd.Parameters.AddWithValue("$id", request.ReviewItemId);
            updateCmd.Parameters.AddWithValue("$resolvedAtUtc", request.ResolvedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            updateCmd.Parameters.AddWithValue("$resolution", request.Resolution);
            updateCmd.Parameters.AddWithValue("$opId", request.ResolutionOperationId);

            var rows = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (rows == 0)
                throw new InvalidOperationException($"Failed to resolve review item {request.ReviewItemId}.");

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<PublicationSnapshot?> GetPublicationAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                publication_id,
                job_id,
                attempt_id,
                destination_kind,
                destination_path,
                sha256,
                size_bytes,
                width,
                height,
                history_path,
                published_at_utc
            FROM publications
            WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return MapPublication(reader);
    }

    public async Task<bool> HasPublicationAsync(
        JobId jobId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM publications WHERE job_id = $jobId;";
        command.Parameters.AddWithValue("$jobId", jobId.Value);
        var count = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return count > 0;
    }

    public async Task PersistPublicationAsync(
        PersistPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writeLock = await database.Writer.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            createIfMissing: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var existing = await ReadPublicationForUpdateAsync(
                connection, transaction, request.JobId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                var reqPubStr = request.PublishedAtUtc.ToString("O", CultureInfo.InvariantCulture);
                var extPubStr = existing.PublishedAtUtc.ToString("O", CultureInfo.InvariantCulture);

                if (string.Equals(existing.AttemptId, request.AttemptId, StringComparison.Ordinal) &&
                    string.Equals(existing.DestinationKind, request.DestinationKind, StringComparison.Ordinal) &&
                    string.Equals(existing.DestinationPath, request.DestinationPath, StringComparison.Ordinal) &&
                    string.Equals(existing.Sha256, request.Sha256, StringComparison.Ordinal) &&
                    existing.SizeBytes == request.SizeBytes &&
                    existing.Width == request.Width &&
                    existing.Height == request.Height &&
                    string.Equals(existing.HistoryPath, request.HistoryPath, StringComparison.Ordinal) &&
                    string.Equals(extPubStr, reqPubStr, StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                throw new InvalidOperationException(
                    $"Job {request.JobId.Value} already has a differing publication record persisted.");
            }

            await using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = """
                INSERT INTO publications(
                    publication_id,
                    job_id,
                    attempt_id,
                    destination_kind,
                    destination_path,
                    sha256,
                    size_bytes,
                    width,
                    height,
                    history_path,
                    published_at_utc)
                VALUES(
                    $id,
                    $jobId,
                    $attemptId,
                    $destinationKind,
                    $destinationPath,
                    $sha256,
                    $sizeBytes,
                    $width,
                    $height,
                    $historyPath,
                    $publishedAtUtc);
                """;
            insertCmd.Parameters.AddWithValue("$id", request.PublicationId);
            insertCmd.Parameters.AddWithValue("$jobId", request.JobId.Value);
            insertCmd.Parameters.AddWithValue("$attemptId", request.AttemptId);
            insertCmd.Parameters.AddWithValue("$destinationKind", request.DestinationKind);
            insertCmd.Parameters.AddWithValue("$destinationPath", request.DestinationPath);
            insertCmd.Parameters.AddWithValue("$sha256", request.Sha256);
            insertCmd.Parameters.AddWithValue("$sizeBytes", request.SizeBytes);
            insertCmd.Parameters.AddWithValue("$width", request.Width);
            insertCmd.Parameters.AddWithValue("$height", request.Height);
            insertCmd.Parameters.AddWithValue("$historyPath", request.HistoryPath);
            insertCmd.Parameters.AddWithValue(
                "$publishedAtUtc",
                request.PublishedAtUtc.ToString("O", CultureInfo.InvariantCulture));

            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task InsertCheckpointAsync(
        JobId jobId,
        string stageName,
        string attemptId,
        string inputFingerprint,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writeLock = await database.Writer.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            createIfMissing: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var checkCmd = connection.CreateCommand();
            checkCmd.Transaction = transaction;
            checkCmd.CommandText = """
                SELECT attempt_id, input_fingerprint
                FROM job_checkpoints
                WHERE job_id = $jobId AND stage_name = $stageName;
                """;
            checkCmd.Parameters.AddWithValue("$jobId", jobId.Value);
            checkCmd.Parameters.AddWithValue("$stageName", stageName);
            await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var existingAttempt = reader.GetString(0);
                var existingFingerprint = reader.GetString(1);
                await reader.DisposeAsync().ConfigureAwait(false);

                if (string.Equals(existingAttempt, attemptId, StringComparison.Ordinal) &&
                    string.Equals(existingFingerprint, inputFingerprint, StringComparison.Ordinal))
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                throw new InvalidOperationException(
                    $"Job {jobId.Value} already has a differing checkpoint for stage {stageName}. Existing=({existingAttempt}, {existingFingerprint}), Requested=({attemptId}, {inputFingerprint}).");
            }

            await reader.DisposeAsync().ConfigureAwait(false);

            var checkpointId = Guid.NewGuid().ToString("N");
            await using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = """
                INSERT INTO job_checkpoints(
                    checkpoint_id,
                    job_id,
                    stage_name,
                    attempt_id,
                    input_fingerprint,
                    created_at_utc)
                VALUES(
                    $id,
                    $jobId,
                    $stageName,
                    $attemptId,
                    $inputFingerprint,
                    $createdAtUtc);
                """;
            insertCmd.Parameters.AddWithValue("$id", checkpointId);
            insertCmd.Parameters.AddWithValue("$jobId", jobId.Value);
            insertCmd.Parameters.AddWithValue("$stageName", stageName);
            insertCmd.Parameters.AddWithValue("$attemptId", attemptId);
            insertCmd.Parameters.AddWithValue("$inputFingerprint", inputFingerprint);
            insertCmd.Parameters.AddWithValue(
                "$createdAtUtc",
                nowUtc.ToString("O", CultureInfo.InvariantCulture));

            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> ClaimJobForQaAsync(
        JobId jobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT state FROM jobs WHERE job_id = $jobId;";
        checkCmd.Parameters.AddWithValue("$jobId", jobId.Value);
        var stateStr = Convert.ToString(await checkCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        return string.Equals(stateStr, "QA", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stateStr, "RETRYING", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stateStr, "INTERRUPTED", StringComparison.OrdinalIgnoreCase);
    }

    public async Task TransitionJobStateAsync(
        JobId jobId,
        JobState fromState,
        JobState toState,
        string reason,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        JobStateMachine.EnsureTransition(fromState, toState);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writeLock = await database.Writer.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            createIfMissing: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var nowStr = nowUtc.ToString("O", CultureInfo.InvariantCulture);

            await using var updateCmd = connection.CreateCommand();
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = """
                UPDATE jobs
                SET state = $toState,
                    updated_at_utc = $now
                WHERE job_id = $jobId;
                """;
            updateCmd.Parameters.AddWithValue("$jobId", jobId.Value);
            updateCmd.Parameters.AddWithValue("$toState", MapStateToString(toState));
            updateCmd.Parameters.AddWithValue("$now", nowStr);
            var affected = await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (affected == 0)
                throw new InvalidOperationException($"Failed to update state for job {jobId.Value}.");

            await using var transCmd = connection.CreateCommand();
            transCmd.Transaction = transaction;
            transCmd.CommandText = """
                INSERT INTO job_state_transitions(
                    transition_id,
                    job_id,
                    from_state,
                    to_state,
                    reason,
                    operation_id,
                    occurred_at_utc)
                VALUES(
                    $id,
                    $jobId,
                    $fromState,
                    $toState,
                    $reason,
                    $opId,
                    $now);
                """;
            transCmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            transCmd.Parameters.AddWithValue("$jobId", jobId.Value);
            transCmd.Parameters.AddWithValue("$fromState", MapStateToString(fromState));
            transCmd.Parameters.AddWithValue("$toState", MapStateToString(toState));
            transCmd.Parameters.AddWithValue("$reason", reason);
            transCmd.Parameters.AddWithValue("$opId", operationId);
            transCmd.Parameters.AddWithValue("$now", nowStr);
            await transCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> ScheduleTechnicalRetryAsync(
        JobId jobId,
        string operationId,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writeLock = await database.Writer.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            createIfMissing: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var readCmd = connection.CreateCommand();
            readCmd.Transaction = transaction;
            readCmd.CommandText = "SELECT technical_retry_count, state FROM jobs WHERE job_id = $jobId;";
            readCmd.Parameters.AddWithValue("$jobId", jobId.Value);
            await using var reader = await readCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException($"Job {jobId.Value} not found.");

            var currentRetries = reader.GetInt32(0);
            var currentState = reader.GetString(1);
            await reader.DisposeAsync().ConfigureAwait(false);

            if (currentRetries >= 2)
            {
                // Exhausted -> transition to ERROR
                await using var errCmd = connection.CreateCommand();
                errCmd.Transaction = transaction;
                errCmd.CommandText = """
                    UPDATE jobs
                    SET state = 'ERROR',
                        updated_at_utc = $now
                    WHERE job_id = $jobId;
                    """;
                var now = nowUtc.ToString("O", CultureInfo.InvariantCulture);
                errCmd.Parameters.AddWithValue("$jobId", jobId.Value);
                errCmd.Parameters.AddWithValue("$now", now);
                await errCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await using var trCmd = connection.CreateCommand();
                trCmd.Transaction = transaction;
                trCmd.CommandText = """
                    INSERT INTO job_state_transitions(
                        transition_id, job_id, from_state, to_state, reason, operation_id, occurred_at_utc)
                    VALUES($id, $jobId, $fromState, 'ERROR', $reason, $opId, $now);
                    """;
                trCmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                trCmd.Parameters.AddWithValue("$jobId", jobId.Value);
                trCmd.Parameters.AddWithValue("$fromState", currentState);
                trCmd.Parameters.AddWithValue("$reason", "TECH_RETRIES_EXHAUSTED: " + reason);
                trCmd.Parameters.AddWithValue("$opId", $"{operationId}-{Guid.NewGuid().ToString("N")[..6]}");
                trCmd.Parameters.AddWithValue("$now", now);
                await trCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return currentRetries;
            }

            var nextRetries = currentRetries + 1;
            var nowS = nowUtc.ToString("O", CultureInfo.InvariantCulture);

            await using var updateCmd = connection.CreateCommand();
            updateCmd.Transaction = transaction;
            updateCmd.CommandText = """
                UPDATE jobs
                SET state = 'RETRYING',
                    technical_retry_count = $nextRetries,
                    updated_at_utc = $now
                WHERE job_id = $jobId;
                """;
            updateCmd.Parameters.AddWithValue("$jobId", jobId.Value);
            updateCmd.Parameters.AddWithValue("$nextRetries", nextRetries);
            updateCmd.Parameters.AddWithValue("$now", nowS);
            await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var logCmd = connection.CreateCommand();
            logCmd.Transaction = transaction;
            logCmd.CommandText = """
                INSERT INTO job_state_transitions(
                    transition_id, job_id, from_state, to_state, reason, operation_id, occurred_at_utc)
                VALUES($id, $jobId, $fromState, 'RETRYING', $reason, $opId, $now);
                """;
            logCmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            logCmd.Parameters.AddWithValue("$jobId", jobId.Value);
            logCmd.Parameters.AddWithValue("$fromState", currentState);
            logCmd.Parameters.AddWithValue("$reason", reason);
            logCmd.Parameters.AddWithValue("$opId", $"{operationId}-{Guid.NewGuid().ToString("N")[..6]}");
            logCmd.Parameters.AddWithValue("$now", nowS);
            await logCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return nextRetries;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JobId> CreateChildQualityReprocessJobAsync(
        JobId parentJobId,
        JobId childJobId,
        string operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var writeLock = await database.Writer.EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            createIfMissing: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Check if child already exists for this parent
            await using var checkChildCmd = connection.CreateCommand();
            checkChildCmd.Transaction = transaction;
            checkChildCmd.CommandText = """
                SELECT job_id FROM jobs WHERE parent_job_id = $parentJobId;
                """;
            checkChildCmd.Parameters.AddWithValue("$parentJobId", parentJobId.Value);
            var existingChild = await checkChildCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (existingChild is not null and not DBNull)
            {
                var existingChildId = Convert.ToString(existingChild)!;
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new JobId(existingChildId);
            }

            // Read parent job
            await using var readParentCmd = connection.CreateCommand();
            readParentCmd.Transaction = transaction;
            readParentCmd.CommandText = """
                SELECT
                    project_id,
                    photo_id,
                    preselection_config_id,
                    processing_config_id,
                    analysis_source_asset_id,
                    analysis_source_sha256,
                    analysis_input_kind,
                    analysis_representation_path,
                    quality_reprocess_count
                FROM jobs
                WHERE job_id = $parentJobId;
                """;
            readParentCmd.Parameters.AddWithValue("$parentJobId", parentJobId.Value);
            await using var reader = await readParentCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException($"Parent job {parentJobId.Value} not found.");

            var projectId = reader.GetString(0);
            var photoId = reader.GetString(1);
            var preConfigId = reader.GetString(2);
            var procConfigId = reader.GetString(3);
            var srcAssetId = reader.GetString(4);
            var srcSha = reader.GetString(5);
            var srcKind = reader.GetString(6);
            var srcRepPath = reader.GetString(7);
            var parentReprocessCount = reader.GetInt32(8);
            await reader.DisposeAsync().ConfigureAwait(false);

            if (parentReprocessCount >= 1)
                throw new InvalidOperationException($"Parent job {parentJobId.Value} already has quality_reprocess_count={parentReprocessCount}. Maximum 1 allowed.");

            var nowStr = nowUtc.ToString("O", CultureInfo.InvariantCulture);

            // Insert child job
            await using var insertJobCmd = connection.CreateCommand();
            insertJobCmd.Transaction = transaction;
            insertJobCmd.CommandText = """
                INSERT INTO jobs(
                    job_id,
                    project_id,
                    photo_id,
                    parent_job_id,
                    state,
                    preselection_config_id,
                    processing_config_id,
                    analysis_source_asset_id,
                    analysis_source_sha256,
                    analysis_input_kind,
                    analysis_representation_path,
                    technical_retry_count,
                    quality_reprocess_count,
                    created_at_utc,
                    updated_at_utc,
                    reveal_retry_count,
                    comfy_retry_count)
                VALUES(
                    $jobId,
                    $projectId,
                    $photoId,
                    $parentJobId,
                    'QUEUED',
                    $preConfigId,
                    $procConfigId,
                    $srcAssetId,
                    $srcSha,
                    $srcKind,
                    $srcRepPath,
                    0,
                    1,
                    $now,
                    $now,
                    0,
                    0);
                """;
            insertJobCmd.Parameters.AddWithValue("$jobId", childJobId.Value);
            insertJobCmd.Parameters.AddWithValue("$projectId", projectId);
            insertJobCmd.Parameters.AddWithValue("$photoId", photoId);
            insertJobCmd.Parameters.AddWithValue("$parentJobId", parentJobId.Value);
            insertJobCmd.Parameters.AddWithValue("$preConfigId", preConfigId);
            insertJobCmd.Parameters.AddWithValue("$procConfigId", procConfigId);
            insertJobCmd.Parameters.AddWithValue("$srcAssetId", srcAssetId);
            insertJobCmd.Parameters.AddWithValue("$srcSha", srcSha);
            insertJobCmd.Parameters.AddWithValue("$srcKind", srcKind);
            insertJobCmd.Parameters.AddWithValue("$srcRepPath", srcRepPath);
            insertJobCmd.Parameters.AddWithValue("$now", nowStr);
            await insertJobCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Initial transition for child
            await using var initialTransCmd = connection.CreateCommand();
            initialTransCmd.Transaction = transaction;
            initialTransCmd.CommandText = """
                INSERT INTO job_state_transitions(
                    transition_id,
                    job_id,
                    from_state,
                    to_state,
                    reason,
                    operation_id,
                    occurred_at_utc)
                VALUES(
                    $id,
                    $jobId,
                    NULL,
                    'QUEUED',
                    'QUALITY_REPROCESS_SPAWNED',
                    $opId,
                    $now);
                """;
            initialTransCmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            initialTransCmd.Parameters.AddWithValue("$jobId", childJobId.Value);
            initialTransCmd.Parameters.AddWithValue("$opId", operationId);
            initialTransCmd.Parameters.AddWithValue("$now", nowStr);
            await initialTransCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Get next queue sequence number
            await using var seqCmd = connection.CreateCommand();
            seqCmd.Transaction = transaction;
            seqCmd.CommandText = """
                SELECT COALESCE(MAX(sequence_number), 0) + 1
                FROM queue_entries
                WHERE project_id = $projectId;
                """;
            seqCmd.Parameters.AddWithValue("$projectId", projectId);
            var nextSeq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

            // Enqueue in queue_entries
            await using var queueCmd = connection.CreateCommand();
            queueCmd.Transaction = transaction;
            queueCmd.CommandText = """
                INSERT INTO queue_entries(
                    queue_entry_id,
                    project_id,
                    job_id,
                    sequence_number,
                    process_next,
                    enqueued_at_utc)
                VALUES(
                    $id,
                    $projectId,
                    $jobId,
                    $seq,
                    0,
                    $now);
                """;
            queueCmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            queueCmd.Parameters.AddWithValue("$projectId", projectId);
            queueCmd.Parameters.AddWithValue("$jobId", childJobId.Value);
            queueCmd.Parameters.AddWithValue("$seq", nextSeq);
            queueCmd.Parameters.AddWithValue("$now", nowStr);
            await queueCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return childJobId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<QaResultSnapshot?> ReadQaResultForUpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobId jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                qa_result_id,
                job_id,
                attempt_id,
                decision,
                result_json,
                input_path,
                input_sha256,
                created_at_utc
            FROM qa_results
            WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return MapQaResult(reader);
    }

    private static async Task<PublicationSnapshot?> ReadPublicationForUpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobId jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                publication_id,
                job_id,
                attempt_id,
                destination_kind,
                destination_path,
                sha256,
                size_bytes,
                width,
                height,
                history_path,
                published_at_utc
            FROM publications
            WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return MapPublication(reader);
    }

    private static QaJobCandidateSnapshot MapCandidate(SqliteDataReader reader) =>
        new(
            new JobId(reader.GetString(reader.GetOrdinal("job_id"))),
            new ProjectId(reader.GetString(reader.GetOrdinal("project_id"))),
            new PhotoId(reader.GetString(reader.GetOrdinal("photo_id"))),
            MapStringToJobState(reader.GetString(reader.GetOrdinal("state"))),
            reader.IsDBNull(reader.GetOrdinal("processing_config_id")) ? string.Empty : reader.GetString(reader.GetOrdinal("processing_config_id")),
            reader.IsDBNull(reader.GetOrdinal("candidate_path")) ? string.Empty : reader.GetString(reader.GetOrdinal("candidate_path")),
            reader.IsDBNull(reader.GetOrdinal("candidate_sha")) ? string.Empty : reader.GetString(reader.GetOrdinal("candidate_sha")),
            reader.IsDBNull(reader.GetOrdinal("candidate_size")) ? 0L : reader.GetInt64(reader.GetOrdinal("candidate_size")),
            reader.GetInt32(reader.GetOrdinal("technical_retry_count")),
            reader.GetInt32(reader.GetOrdinal("quality_reprocess_count")),
            reader.IsDBNull(reader.GetOrdinal("parent_job_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("parent_job_id")));

    private static QaResultSnapshot MapQaResult(SqliteDataReader reader) =>
        new(
            reader.GetString(reader.GetOrdinal("qa_result_id")),
            new JobId(reader.GetString(reader.GetOrdinal("job_id"))),
            reader.GetString(reader.GetOrdinal("attempt_id")),
            reader.GetString(reader.GetOrdinal("decision")),
            JsonDocument.Parse(reader.GetString(reader.GetOrdinal("result_json"))).RootElement.Clone(),
            reader.GetString(reader.GetOrdinal("input_path")),
            reader.GetString(reader.GetOrdinal("input_sha256")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static ReviewItemSnapshot MapReviewItem(SqliteDataReader reader) =>
        new(
            reader.GetString(reader.GetOrdinal("review_item_id")),
            new JobId(reader.GetString(reader.GetOrdinal("job_id"))),
            reader.GetString(reader.GetOrdinal("review_kind")),
            reader.GetString(reader.GetOrdinal("status")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(reader.GetOrdinal("resolved_at_utc"))
                ? null
                : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("resolved_at_utc")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(reader.GetOrdinal("resolution"))
                ? null
                : reader.GetString(reader.GetOrdinal("resolution")),
            reader.IsDBNull(reader.GetOrdinal("resolution_operation_id"))
                ? null
                : reader.GetString(reader.GetOrdinal("resolution_operation_id")));

    private static PublicationSnapshot MapPublication(SqliteDataReader reader) =>
        new(
            reader.GetString(reader.GetOrdinal("publication_id")),
            new JobId(reader.GetString(reader.GetOrdinal("job_id"))),
            reader.GetString(reader.GetOrdinal("attempt_id")),
            reader.GetString(reader.GetOrdinal("destination_kind")),
            reader.GetString(reader.GetOrdinal("destination_path")),
            reader.GetString(reader.GetOrdinal("sha256")),
            reader.GetInt64(reader.GetOrdinal("size_bytes")),
            reader.GetInt32(reader.GetOrdinal("width")),
            reader.GetInt32(reader.GetOrdinal("height")),
            reader.GetString(reader.GetOrdinal("history_path")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("published_at_utc")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private static string MapStateToString(JobState state) => state switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    private static JobState MapStringToJobState(string state) => state switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };
}
