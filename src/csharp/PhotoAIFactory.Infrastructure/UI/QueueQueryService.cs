using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Domain.Qa;

namespace PhotoAIFactory.Infrastructure.UI;

public sealed class QueueQueryService(
    IAppPaths paths,
    IProjectStoreFactory storeFactory,
    IComponentHealthTracker healthTracker) : IQueueQueryService
{
    public async Task<QueueOverviewDto?> GetQueueOverviewAsync(ProjectId projectId, CancellationToken cancellationToken = default)
    {
        var dbPath = paths.GetProjectDatabasePath(projectId);
        if (!File.Exists(dbPath))
            return null;

        var store = storeFactory.Open(projectId);
        var projectWrapper = await store.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (projectWrapper is null)
            return null;

        var project = projectWrapper.Project;
        var queueItems = new List<QueueItemDto>();
        ActiveJobSummaryDto? activeJob = null;

        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // 1. Fetch FIFO queue items from queue_entries
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT q.sequence_number, j.job_id, j.photo_id, p.association_key,
                       a.format, j.state, q.enqueued_at_utc,
                       j.processing_config_id,
                       COALESCE(pr.reveal_mode, 'PRE_AI') AS reveal_mode,
                       j.technical_retry_count
                FROM queue_entries q
                JOIN jobs j ON j.job_id = q.job_id
                JOIN photos p ON p.photo_id = j.photo_id
                LEFT JOIN assets a ON a.asset_id = j.analysis_source_asset_id
                LEFT JOIN processing_recipes pr ON pr.job_id = j.job_id
                WHERE j.state IN ('QUEUED', 'RECEIVED', 'RETRYING')
                ORDER BY q.process_next DESC, q.process_next_requested_at_utc ASC, q.sequence_number ASC;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var seq = reader.GetInt64(0);
                var jobId = new JobId(reader.GetString(1));
                var photoId = new PhotoId(reader.GetString(2));
                var photoName = reader.GetString(3);
                var format = reader.IsDBNull(4) ? "UNKNOWN" : reader.GetString(4);
                var stateStr = reader.GetString(5);
                var jState = DbEnumMapper.ToJobState(stateStr);
                var queuedAt = DateTimeOffset.Parse(reader.GetString(6));
                var cfgVer = reader.GetString(7);
                var revModeStr = reader.GetString(8);
                var revMode = DbEnumMapper.ToRevealMode(revModeStr);
                var retries = reader.GetInt32(9);

                queueItems.Add(new QueueItemDto(
                    seq,
                    jobId,
                    photoId,
                    photoName,
                    format,
                    jState,
                    queuedAt,
                    cfgVer,
                    revMode,
                    retries,
                    project.State is ProjectState.Paused or ProjectState.BlockedStorage or ProjectState.ComponentUnhealthy));
            }
        }

        // 2. Fetch Active Job (ANALYZING, PROCESSING, QA, RETRYING)
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT j.job_id, j.photo_id, p.association_key, j.state,
                       COALESCE(pr.reveal_mode, 'PRE_AI') AS reveal_mode,
                       j.processing_config_id, j.technical_retry_count, j.quality_reprocess_count,
                       (SELECT occurred_at_utc FROM job_state_transitions WHERE job_id = j.job_id ORDER BY occurred_at_utc DESC LIMIT 1) AS last_transition_utc,
                       COALESCE(ce.output_path, fp.image_path, o.path, j.analysis_representation_path) AS preview_path
                FROM jobs j
                JOIN photos p ON p.photo_id = j.photo_id
                LEFT JOIN processing_recipes pr ON pr.job_id = j.job_id
                LEFT JOIN comfy_executions ce ON ce.job_id = j.job_id
                LEFT JOIN feedback_passes fp ON fp.job_id = j.job_id AND fp.pass_number = 2
                LEFT JOIN processing_passes pp ON pp.job_id = j.job_id
                LEFT JOIN outputs o ON o.output_id = pp.output_id
                WHERE j.state IN ('ANALYZING', 'PROCESSING', 'QA', 'RETRYING')
                ORDER BY j.created_at_utc ASC
                LIMIT 1;
                """;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var jId = new JobId(reader.GetString(0));
                var pId = new PhotoId(reader.GetString(1));
                var pName = reader.GetString(2);
                var stateStr = reader.GetString(3);
                var jState = DbEnumMapper.ToJobState(stateStr);
                var revModeStr = reader.GetString(4);
                var revMode = DbEnumMapper.ToRevealMode(revModeStr);
                var cfgVer = reader.GetString(5);
                var retries = reader.GetInt32(6);
                var reprocesses = reader.GetInt32(7);
                var elapsed = TimeSpan.Zero;
                if (!reader.IsDBNull(8) && DateTimeOffset.TryParse(reader.GetString(8), out var transTime))
                {
                    elapsed = DateTimeOffset.UtcNow - transTime;
                }
                var preview = reader.IsDBNull(9) ? null : reader.GetString(9);

                var stageName = jState switch
                {
                    JobState.Analyzing => "Analysis & Preselection",
                    JobState.Processing => revMode == RevealMode.Feedback ? "Feedback Processing" : "Basic Reveal Processing",
                    JobState.Qa => "Quality Assurance",
                    JobState.Retrying => "Retrying Stage",
                    _ => jState.ToString()
                };

                activeJob = new ActiveJobSummaryDto(
                    jId,
                    pId,
                    pName,
                    jState,
                    stageName,
                    true,
                    0,
                    elapsed,
                    revMode,
                    1,
                    preview,
                    retries,
                    reprocesses);
            }
        }

        return new QueueOverviewDto(
            queueItems.Count,
            project.State == ProjectState.Paused,
            project.State == ProjectState.BlockedStorage,
            project.State == ProjectState.ComponentUnhealthy || healthTracker.GetAllStatuses().Any(s => s.CircuitBreakerOpen),
            activeJob,
            queueItems);
    }

    public async Task<JobDetailDto?> GetJobDetailAsync(ProjectId projectId, JobId jobId, CancellationToken cancellationToken = default)
    {
        var dbPath = paths.GetProjectDatabasePath(projectId);
        if (!File.Exists(dbPath))
            return null;

        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        JobDetailDto? detail = null;

        // 1. Core Job row
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT j.job_id, j.project_id, j.photo_id, p.association_key,
                       a.managed_path, a.sha256, a.format, a.size_bytes,
                       j.state, j.processing_config_id,
                       COALESCE(pr.reveal_mode, 'PRE_AI') AS reveal_mode,
                       j.technical_retry_count, j.quality_reprocess_count,
                       j.created_at_utc, j.updated_at_utc, j.parent_job_id,
                       pub.destination_path, pub.sha256,
                       (SELECT reason FROM job_state_transitions WHERE job_id = j.job_id AND to_state = 'ERROR' ORDER BY occurred_at_utc DESC LIMIT 1) AS error_reason
                FROM jobs j
                JOIN photos p ON p.photo_id = j.photo_id
                LEFT JOIN assets a ON a.asset_id = j.analysis_source_asset_id
                LEFT JOIN processing_recipes pr ON pr.job_id = j.job_id
                LEFT JOIN publications pub ON pub.job_id = j.job_id
                WHERE j.job_id = @jobId;
                """;
            cmd.Parameters.AddWithValue("@jobId", jobId.Value);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var jId = new JobId(reader.GetString(0));
                var pId = new ProjectId(reader.GetString(1));
                var phId = new PhotoId(reader.GetString(2));
                var phName = reader.GetString(3);
                var inputPath = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                var inputSha = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                var inputFormat = reader.IsDBNull(6) ? "UNKNOWN" : reader.GetString(6);
                var inputSize = reader.IsDBNull(7) ? 0L : reader.GetInt64(7);
                var stateStr = reader.GetString(8);
                var jState = DbEnumMapper.ToJobState(stateStr);
                var cfgVer = reader.GetString(9);
                var revModeStr = reader.GetString(10);
                var revMode = DbEnumMapper.ToRevealMode(revModeStr);
                var techRetries = reader.GetInt32(11);
                var qualReprocess = reader.GetInt32(12);
                var createdUtc = DateTimeOffset.Parse(reader.GetString(13));
                DateTimeOffset? completedUtc = jState == JobState.Completed && !reader.IsDBNull(14)
                    ? DateTimeOffset.Parse(reader.GetString(14))
                    : null;
                var parentJobId = reader.IsDBNull(15) ? null : reader.GetString(15);
                var pubPath = reader.IsDBNull(16) ? null : reader.GetString(16);
                var pubSha = reader.IsDBNull(17) ? null : reader.GetString(17);
                var errorReason = reader.IsDBNull(18) ? null : reader.GetString(18);

                var stage = jState switch
                {
                    JobState.Analyzing => "Analyzing",
                    JobState.Processing => "Processing",
                    JobState.Qa => "QA",
                    JobState.Completed => "Completed",
                    JobState.ReviewFinal => "Review Final",
                    JobState.ReviewPre => "Review Pre",
                    JobState.RejectedFinal => "Rejected Final",
                    JobState.RejectedPre => "Rejected Pre",
                    JobState.Error => "Error",
                    JobState.Retrying => "Retrying",
                    _ => jState.ToString()
                };

                detail = new JobDetailDto(
                    jId,
                    pId,
                    phId,
                    phName,
                    inputPath,
                    inputSha,
                    inputFormat,
                    inputSize,
                    jState,
                    stage,
                    cfgVer,
                    revMode,
                    techRetries,
                    qualReprocess,
                    createdUtc,
                    completedUtc,
                    pubPath,
                    pubPath,
                    pubSha,
                    [],
                    [],
                    null,
                    parentJobId,
                    errorReason);
            }
        }

        if (detail is null)
            return null;

        // 2. Checkpoints with durable stage artifacts joined from respective tables
        var checkpoints = new List<JobCheckpointDto>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT c.stage_name, c.attempt_id, c.input_fingerprint, c.created_at_utc,
                       COALESCE(pub.destination_path, ce.output_path, fp2.image_path, fp1.image_path, o.path, j.analysis_representation_path) AS artifact_path,
                       COALESCE(pub.sha256, ce.output_sha256, fp2.image_sha256, fp1.image_sha256, o.sha256, j.analysis_source_sha256) AS artifact_sha256
                FROM job_checkpoints c
                JOIN jobs j ON j.job_id = c.job_id
                LEFT JOIN outputs o ON o.job_id = c.job_id AND c.stage_name = 'BASIC_REVEAL_COMPLETE'
                LEFT JOIN feedback_passes fp1 ON fp1.job_id = c.job_id AND fp1.pass_number = 1 AND c.stage_name = 'DARKTABLE_PASS1_COMPLETE'
                LEFT JOIN feedback_passes fp2 ON fp2.job_id = c.job_id AND fp2.pass_number = 2 AND c.stage_name = 'DARKTABLE_PASS2_COMPLETE'
                LEFT JOIN comfy_executions ce ON ce.job_id = c.job_id AND c.stage_name = 'COMFYUI_COMPLETE'
                LEFT JOIN publications pub ON pub.job_id = c.job_id AND c.stage_name = 'OUTPUT_PUBLISHED'
                WHERE c.job_id = @jobId
                ORDER BY c.created_at_utc ASC;
                """;
            cmd.Parameters.AddWithValue("@jobId", jobId.Value);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                checkpoints.Add(new JobCheckpointDto(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    DateTimeOffset.Parse(reader.GetString(3))));
            }
        }

        // 3. Model executions
        var modelExecutions = new List<JobModelExecutionDto>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT model_id, model_version, artifact_set_sha256, parameters_json, timings_json
                FROM model_executions
                WHERE job_id = @jobId
                ORDER BY created_at_utc ASC;
                """;
            cmd.Parameters.AddWithValue("@jobId", jobId.Value);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var mId = reader.GetString(0);
                var mVer = reader.GetString(1);
                var artSha = reader.IsDBNull(2) ? null : reader.GetString(2);
                var paramsJson = JsonDocument.Parse(reader.GetString(3)).RootElement.Clone();
                var timingsJson = JsonDocument.Parse(reader.GetString(4)).RootElement.Clone();
                modelExecutions.Add(new JobModelExecutionDto(mId, mVer, artSha, paramsJson, timingsJson));
            }
        }

        // 4. QA Result
        QaResultSummaryDto? qaResult = null;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT qa_result_id, decision, result_json, created_at_utc
                FROM qa_results
                WHERE job_id = @jobId
                ORDER BY created_at_utc DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("@jobId", jobId.Value);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var qId = reader.GetString(0);
                var decStr = reader.GetString(1);
                var dec = DbEnumMapper.ToQaDecision(decStr) ?? QaDecision.Pass;
                var rawJson = reader.GetString(2);
                var qCreated = DateTimeOffset.Parse(reader.GetString(3));

                var nextAction = string.Empty;
                var score = 100;
                JsonElement findingsElement = default;

                try
                {
                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("suggested_correction", out var scProp))
                    {
                        nextAction = scProp.GetString() ?? string.Empty;
                    }
                    else if (root.TryGetProperty("suggested_next_action", out var snaProp))
                    {
                        nextAction = snaProp.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("technical", out var techProp))
                    {
                        if (techProp.TryGetProperty("score", out var scoreProp)) score = scoreProp.GetInt32();
                        else if (techProp.TryGetProperty("technical_score", out var tsProp)) score = tsProp.GetInt32();
                    }

                    if (root.TryGetProperty("findings", out var fProp))
                    {
                        findingsElement = fProp.Clone();
                    }
                }
                catch
                {
                    // Tolerates non-standard JSON gracefully
                }

                qaResult = new QaResultSummaryDto(qId, dec, nextAction, score, findingsElement, qCreated);
            }
        }

        return detail with
        {
            Checkpoints = checkpoints,
            ModelExecutions = modelExecutions,
            QaResult = qaResult,
            PreviewPath = checkpoints.LastOrDefault(c => !string.IsNullOrWhiteSpace(c.ArtifactPath))?.ArtifactPath ?? detail.PreviewPath
        };
    }
}
