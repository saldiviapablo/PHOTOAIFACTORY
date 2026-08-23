using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Application.Recovery;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Qa;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;

namespace PhotoAIFactory.Infrastructure.Recovery;

public sealed class ProductionRecoveryCoordinator : IRecoveryCoordinator
{
    private readonly IProjectStoreFactory projectStores;
    private readonly IQaStoreFactory qaStores;
    private readonly IPublishService publishService;
    private readonly ILogger<ProductionRecoveryCoordinator> logger;

    public ProductionRecoveryCoordinator(
        IProjectStoreFactory projectStores,
        IQaStoreFactory qaStores,
        IPublishService publishService,
        ILogger<ProductionRecoveryCoordinator>? logger = null)
    {
        this.projectStores = projectStores;
        this.qaStores = qaStores;
        this.publishService = publishService;
        this.logger = logger ?? NullLogger<ProductionRecoveryCoordinator>.Instance;
    }

    public async Task<ProjectRecoveryReport> ReconcileAndRecoverProjectAsync(
        ProjectId projectId,
        string outputRootFolder,
        CancellationToken cancellationToken = default)
    {
        var projectStore = projectStores.Open(projectId);
        var projectWrapper = await projectStore.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (projectWrapper is null)
        {
            return new ProjectRecoveryReport(projectId, 0, 0, 0, 0, 0, []);
        }

        var database = ((SqliteProjectStore)projectStore).Database;
        await using var conn = await database.OpenConfiguredConnectionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // Fetch all jobs in strict FIFO order (created_at_utc ASC)
        var jobIds = new List<JobId>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT job_id FROM jobs ORDER BY created_at_utc ASC;";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                jobIds.Add(new JobId(reader.GetString(0)));
            }
        }

        var results = new List<JobRecoveryResult>();
        var normalizedCount = 0;
        var resumedCount = 0;
        var completedCount = 0;
        var rolledBackCount = 0;

        foreach (var jobId in jobIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var res = await ReconcileJobAsync(projectId, jobId, outputRootFolder, cancellationToken).ConfigureAwait(false);
            results.Add(res);

            if (res.Action == JobRecoveryAction.NormalizedToInterrupted) normalizedCount++;
            else if (res.Action is JobRecoveryAction.ResumedToAnalyzing or JobRecoveryAction.ResumedToPreselection or
                     JobRecoveryAction.ResumedToQueued or JobRecoveryAction.ResumedToProcessing or
                     JobRecoveryAction.ResumedToQa or JobRecoveryAction.ResumedToReviewFinal) resumedCount++;
            else if (res.Action is JobRecoveryAction.CompletedFromDurablePublication or JobRecoveryAction.CompletedFromOutputCheckpoint) completedCount++;
            else if (res.Action == JobRecoveryAction.RolledBackCorruptCheckpoint) rolledBackCount++;
        }

        return new ProjectRecoveryReport(
            projectId,
            jobIds.Count,
            normalizedCount,
            resumedCount,
            completedCount,
            rolledBackCount,
            results);
    }

    public async Task<JobRecoveryResult> ReconcileJobAsync(
        ProjectId projectId,
        JobId jobId,
        string outputRootFolder,
        CancellationToken cancellationToken = default)
    {
        var projectStore = projectStores.Open(projectId);
        var database = ((SqliteProjectStore)projectStore).Database;
        var qaStore = qaStores.Open(projectId);

        await using var conn = await database.OpenConfiguredConnectionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        // 1. Read job details
        string currentStateStr;
        int techRetries;
        int qualityReprocessCount;
        string? parentJobId;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT state, technical_retry_count, quality_reprocess_count, parent_job_id FROM jobs WHERE job_id = $jobId;";
            cmd.Parameters.AddWithValue("$jobId", jobId.Value);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new JobRecoveryResult(jobId, JobState.Error, JobState.Error, JobRecoveryAction.FailedUnrecoverable, null, "Job not found in database");
            }

            currentStateStr = reader.GetString(0);
            techRetries = reader.GetInt32(1);
            qualityReprocessCount = reader.GetInt32(2);
            parentJobId = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        var initialState = MapStringToJobState(currentStateStr);

        // 2. Read all checkpoints for job ordered by created_at_utc DESC
        var checkpoints = new List<(string StageName, string AttemptId, string Fingerprint, string CreatedAtUtc)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT stage_name, attempt_id, input_fingerprint, created_at_utc FROM job_checkpoints WHERE job_id = $jobId ORDER BY created_at_utc DESC;";
            cmd.Parameters.AddWithValue("$jobId", jobId.Value);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                checkpoints.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        var latestCheckpoint = checkpoints.FirstOrDefault();

        // 3. Terminal job checks
        if (initialState == JobState.Completed)
        {
            var isValidPub = await ValidatePublicationAndHistoryAsync(conn, qaStore, jobId, cancellationToken).ConfigureAwait(false);
            if (isValidPub)
            {
                if (!checkpoints.Any(c => c.StageName == "OUTPUT_PUBLISHED"))
                {
                    await qaStore.InsertCheckpointAsync(jobId, "OUTPUT_PUBLISHED", "recovery-repair", "sha-repair", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                }
                return new JobRecoveryResult(jobId, initialState, JobState.Completed, JobRecoveryAction.None, "OUTPUT_PUBLISHED", "Job completed and durable publication verified");
            }

            // Invariant failure: marked Completed without valid publication or history -> transition to Error / Interrupted
            await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Error, "RECOVERY_COMPLETED_STATE_INVALID_PUBLICATION_OR_HISTORY", "recovery:repair-completed", cancellationToken).ConfigureAwait(false);
            return new JobRecoveryResult(jobId, initialState, JobState.Error, JobRecoveryAction.FailedUnrecoverable, null, "Completed job missing valid publication or final history");
        }

        if (initialState is JobState.RejectedFinal or JobState.Error or JobState.Cancelled)
        {
            return new JobRecoveryResult(jobId, initialState, initialState, JobRecoveryAction.None, latestCheckpoint.StageName, "Terminal state preserved");
        }

        // 4. Inspect Checkpoints in descending order

        // --- A. OUTPUT_PUBLISHED ---
        if (latestCheckpoint.StageName == "OUTPUT_PUBLISHED" || checkpoints.Any(c => c.StageName == "OUTPUT_PUBLISHED"))
        {
            var isPubValid = await ValidatePublicationAndHistoryAsync(conn, qaStore, jobId, cancellationToken).ConfigureAwait(false);
            if (isPubValid)
            {
                var targetState = JobState.Completed;
                if (initialState == JobState.Interrupted)
                {
                    // Controlled path: Interrupted -> Qa -> Completed
                    await TransitionJobSafelyAsync(conn, jobId, JobState.Interrupted, JobState.Qa, "RECOVERY_OUTPUT_PUBLISHED_INTERRUPTED_TO_QA", "recovery:qa-step", cancellationToken).ConfigureAwait(false);
                    await TransitionJobSafelyAsync(conn, jobId, JobState.Qa, JobState.Completed, "RECOVERY_OUTPUT_PUBLISHED_VALIDATED", "recovery:output-publish", cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await TransitionJobSafelyAsync(conn, jobId, initialState, targetState, "RECOVERY_OUTPUT_PUBLISHED_VALIDATED", "recovery:output-publish", cancellationToken).ConfigureAwait(false);
                }
                return new JobRecoveryResult(jobId, initialState, JobState.Completed, JobRecoveryAction.CompletedFromOutputCheckpoint, "OUTPUT_PUBLISHED", "Completed from validated publication and history");
            }
            else
            {
                // Publication or history was corrupted/missing
                await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Interrupted, "RECOVERY_CORRUPT_OUTPUT_PUBLICATION_OR_HISTORY", "recovery:corrupt-pub", cancellationToken).ConfigureAwait(false);
                return new JobRecoveryResult(jobId, initialState, JobState.Interrupted, JobRecoveryAction.RolledBackCorruptCheckpoint, "OUTPUT_PUBLISHED", "Publication artifact or history missing or corrupted");
            }
        }

        // --- B. QA_COMPLETE ---
        if (latestCheckpoint.StageName == "QA_COMPLETE")
        {
            var qaResult = await qaStore.GetQaResultAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (qaResult is not null)
            {
                if (qaResult.Decision == "PASS")
                {
                    var isPubValid = await ValidatePublicationAndHistoryAsync(conn, qaStore, jobId, cancellationToken).ConfigureAwait(false);
                    if (isPubValid)
                    {
                        await qaStore.InsertCheckpointAsync(jobId, "OUTPUT_PUBLISHED", "recovery-qa-pass", qaResult.InputSha256, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                        if (initialState == JobState.Interrupted)
                        {
                            await TransitionJobSafelyAsync(conn, jobId, JobState.Interrupted, JobState.Qa, "RECOVERY_QA_PASS_INTERRUPTED_TO_QA", "recovery:qa-step", cancellationToken).ConfigureAwait(false);
                            await TransitionJobSafelyAsync(conn, jobId, JobState.Qa, JobState.Completed, "RECOVERY_QA_PASS_PUBLISHED", "recovery:qa-pass", cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Completed, "RECOVERY_QA_PASS_PUBLISHED", "recovery:qa-pass", cancellationToken).ConfigureAwait(false);
                        }
                        return new JobRecoveryResult(jobId, initialState, JobState.Completed, JobRecoveryAction.CompletedFromDurablePublication, "OUTPUT_PUBLISHED", "Completed from validated QA PASS publication");
                    }

                    // Publication pending: resume in QA
                    if (initialState != JobState.Qa)
                    {
                        await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Qa, "RECOVERY_RESUME_QA_PUBLISH", "recovery:qa-resume", cancellationToken).ConfigureAwait(false);
                    }
                    return new JobRecoveryResult(jobId, initialState, JobState.Qa, JobRecoveryAction.ResumedToQa, "QA_COMPLETE", "Resumed to QA stage for pending publication");
                }

                if (qaResult.Decision == "REVIEW")
                {
                    var reviewItem = await qaStore.GetPendingReviewItemAsync(jobId, "FINAL", cancellationToken).ConfigureAwait(false);
                    if (reviewItem is null)
                    {
                        await qaStore.CreateReviewItemAsync(new CreateReviewItemRequest(Guid.NewGuid().ToString("N"), jobId, "FINAL", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                    }

                    if (initialState != JobState.ReviewFinal)
                    {
                        await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.ReviewFinal, "RECOVERY_QA_REVIEW", "recovery:qa-review", cancellationToken).ConfigureAwait(false);
                    }
                    return new JobRecoveryResult(jobId, initialState, JobState.ReviewFinal, JobRecoveryAction.ResumedToReviewFinal, "QA_COMPLETE", "Resumed to ReviewFinal with pending review item");
                }

                if (qaResult.Decision == "REPROCESS")
                {
                    var childCount = 0;
                    await using (var countCmd = conn.CreateCommand())
                    {
                        countCmd.CommandText = "SELECT COUNT(*) FROM jobs WHERE parent_job_id = $jobId;";
                        countCmd.Parameters.AddWithValue("$jobId", jobId.Value);
                        childCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                    }

                    if (childCount == 0 && qualityReprocessCount == 0)
                    {
                        var childId = JobId.New();
                        await qaStore.CreateChildQualityReprocessJobAsync(jobId, childId, "recovery:spawn-reprocess", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                    }

                    if (initialState != JobState.ReviewFinal)
                    {
                        await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.ReviewFinal, "RECOVERY_QA_REPROCESS_PARENT", "recovery:qa-reprocess", cancellationToken).ConfigureAwait(false);
                    }
                    return new JobRecoveryResult(jobId, initialState, JobState.ReviewFinal, JobRecoveryAction.ResumedToReviewFinal, "QA_COMPLETE", "Parent in ReviewFinal with child reprocess job");
                }
            }
        }

        // --- C. COMFYUI_COMPLETE ---
        if (latestCheckpoint.StageName == "COMFYUI_COMPLETE")
        {
            string? outPath = null;
            string? outSha = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT output_path, output_sha256 FROM comfy_executions WHERE job_id = $jobId AND status = 'COMPLETED' ORDER BY completed_at_utc DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("$jobId", jobId.Value);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    outPath = reader.GetString(0);
                    outSha = reader.GetString(1);
                }
            }

            if (outPath is not null && outSha is not null && File.Exists(outPath) && VerifySha256(outPath, outSha))
            {
                if (initialState != JobState.Qa)
                {
                    await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Qa, "RECOVERY_COMFY_COMPLETE_TO_QA", "recovery:comfy-to-qa", cancellationToken).ConfigureAwait(false);
                }
                return new JobRecoveryResult(jobId, initialState, JobState.Qa, JobRecoveryAction.ResumedToQa, "COMFYUI_COMPLETE", "Resumed to QA from validated ComfyUI execution");
            }

            await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Interrupted, "RECOVERY_CORRUPT_COMFYUI_ARTIFACT", "recovery:comfy-corrupt", cancellationToken).ConfigureAwait(false);
            return new JobRecoveryResult(jobId, initialState, JobState.Interrupted, JobRecoveryAction.RolledBackCorruptCheckpoint, "COMFYUI_COMPLETE", "ComfyUI output missing or SHA mismatch");
        }

        // --- D. DARKTABLE_PASS2_COMPLETE ---
        if (latestCheckpoint.StageName == "DARKTABLE_PASS2_COMPLETE")
        {
            string? outPath = null;
            string? outSha = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT image_path, image_sha256 FROM feedback_passes WHERE job_id = $jobId AND pass_number = 2 ORDER BY completed_at_utc DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("$jobId", jobId.Value);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    outPath = reader.GetString(0);
                    outSha = reader.GetString(1);
                }
            }

            if (outPath is not null && outSha is not null && File.Exists(outPath) && VerifySha256(outPath, outSha))
            {
                if (initialState != JobState.Processing)
                {
                    await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Processing, "RECOVERY_PASS2_COMPLETE", "recovery:pass2", cancellationToken).ConfigureAwait(false);
                }
                return new JobRecoveryResult(jobId, initialState, JobState.Processing, JobRecoveryAction.ResumedToProcessing, "DARKTABLE_PASS2_COMPLETE", "Resumed to Processing from Darktable Pass 2");
            }

            await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Interrupted, "RECOVERY_CORRUPT_PASS2_ARTIFACT", "recovery:pass2-corrupt", cancellationToken).ConfigureAwait(false);
            return new JobRecoveryResult(jobId, initialState, JobState.Interrupted, JobRecoveryAction.RolledBackCorruptCheckpoint, "DARKTABLE_PASS2_COMPLETE", "Pass 2 output missing or SHA mismatch");
        }

        // --- E. RAW_DENOISE_COMPLETE / FEEDBACK_INSPECTION_COMPLETE ---
        if (latestCheckpoint.StageName is "RAW_DENOISE_COMPLETE" or "FEEDBACK_INSPECTION_COMPLETE")
        {
            var hasFeedback = false;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM feedback_inspections WHERE job_id = $jobId;";
                cmd.Parameters.AddWithValue("$jobId", jobId.Value);
                hasFeedback = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
            }

            if (hasFeedback)
            {
                if (initialState != JobState.Processing)
                {
                    await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Processing, "RECOVERY_FEEDBACK_INSPECTION_RESUME", "recovery:feedback", cancellationToken).ConfigureAwait(false);
                }
                return new JobRecoveryResult(jobId, initialState, JobState.Processing, JobRecoveryAction.ResumedToProcessing, latestCheckpoint.StageName, "Resumed to Processing for Darktable Pass 2");
            }

            await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Interrupted, "RECOVERY_CORRUPT_FEEDBACK_DATA", "recovery:feedback-corrupt", cancellationToken).ConfigureAwait(false);
            return new JobRecoveryResult(jobId, initialState, JobState.Interrupted, JobRecoveryAction.RolledBackCorruptCheckpoint, latestCheckpoint.StageName, "Feedback inspection record missing");
        }

        // --- F. DARKTABLE_PASS1_COMPLETE ---
        if (latestCheckpoint.StageName == "DARKTABLE_PASS1_COMPLETE")
        {
            string? outPath = null;
            string? outSha = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT image_path, image_sha256 FROM feedback_passes WHERE job_id = $jobId AND pass_number = 1 ORDER BY completed_at_utc DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("$jobId", jobId.Value);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    outPath = reader.GetString(0);
                    outSha = reader.GetString(1);
                }
            }

            if (outPath is not null && outSha is not null && File.Exists(outPath) && VerifySha256(outPath, outSha))
            {
                if (initialState != JobState.Processing)
                {
                    await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Processing, "RECOVERY_PASS1_COMPLETE", "recovery:pass1", cancellationToken).ConfigureAwait(false);
                }
                return new JobRecoveryResult(jobId, initialState, JobState.Processing, JobRecoveryAction.ResumedToProcessing, "DARKTABLE_PASS1_COMPLETE", "Resumed to Processing from Darktable Pass 1");
            }

            await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Interrupted, "RECOVERY_CORRUPT_PASS1_ARTIFACT", "recovery:pass1-corrupt", cancellationToken).ConfigureAwait(false);
            return new JobRecoveryResult(jobId, initialState, JobState.Interrupted, JobRecoveryAction.RolledBackCorruptCheckpoint, "DARKTABLE_PASS1_COMPLETE", "Pass 1 TIFF missing or SHA mismatch");
        }

        // --- G. BASIC_REVEAL_COMPLETE ---
        if (latestCheckpoint.StageName == "BASIC_REVEAL_COMPLETE")
        {
            string? outPath = null;
            string? outSha = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT o.path, o.sha256
                    FROM processing_passes pp
                    JOIN outputs o ON pp.output_id = o.output_id
                    WHERE pp.job_id = $jobId
                    ORDER BY pp.completed_at_utc DESC LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$jobId", jobId.Value);
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    outPath = reader.GetString(0);
                    outSha = reader.GetString(1);
                }
            }

            if (outPath is not null && outSha is not null && File.Exists(outPath) && VerifySha256(outPath, outSha))
            {
                if (initialState != JobState.Processing)
                {
                    await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Processing, "RECOVERY_BASIC_REVEAL_COMPLETE", "recovery:reveal", cancellationToken).ConfigureAwait(false);
                }
                return new JobRecoveryResult(jobId, initialState, JobState.Processing, JobRecoveryAction.ResumedToProcessing, "BASIC_REVEAL_COMPLETE", "Resumed to Processing from Basic Reveal");
            }

            await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Interrupted, "RECOVERY_CORRUPT_REVEAL_ARTIFACT", "recovery:reveal-corrupt", cancellationToken).ConfigureAwait(false);
            return new JobRecoveryResult(jobId, initialState, JobState.Interrupted, JobRecoveryAction.RolledBackCorruptCheckpoint, "BASIC_REVEAL_COMPLETE", "Reveal output missing or SHA mismatch");
        }

        // --- H. PRESELECTION_COMPLETE ---
        if (latestCheckpoint.StageName == "PRESELECTION_COMPLETE")
        {
            string? decision = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT decision FROM preselection_results WHERE job_id = $jobId ORDER BY created_at_utc DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("$jobId", jobId.Value);
                decision = (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            var targetState = decision switch
            {
                "APPROVED" => JobState.Queued,
                "REVIEW_PRE" => JobState.ReviewPre,
                "REJECTED_PRE" => JobState.RejectedPre,
                _ => JobState.Queued
            };

            if (initialState != targetState)
            {
                await TransitionJobSafelyAsync(conn, jobId, initialState, targetState, "RECOVERY_PRESELECTION_COMPLETE", "recovery:preselection", cancellationToken).ConfigureAwait(false);
            }

            var action = targetState == JobState.Queued ? JobRecoveryAction.ResumedToQueued : JobRecoveryAction.ResumedToPreselection;
            return new JobRecoveryResult(jobId, initialState, targetState, action, "PRESELECTION_COMPLETE", $"Resumed to {targetState} from valid preselection");
        }

        // --- I. ANALYSIS_COMPLETE ---
        if (latestCheckpoint.StageName == "ANALYSIS_COMPLETE")
        {
            var hasAnalysis = false;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM analysis_results WHERE job_id = $jobId;";
                cmd.Parameters.AddWithValue("$jobId", jobId.Value);
                hasAnalysis = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) > 0;
            }

            if (hasAnalysis)
            {
                if (initialState != JobState.Analyzing)
                {
                    await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Analyzing, "RECOVERY_ANALYSIS_COMPLETE", "recovery:analysis", cancellationToken).ConfigureAwait(false);
                }
                return new JobRecoveryResult(jobId, initialState, JobState.Analyzing, JobRecoveryAction.ResumedToAnalyzing, "ANALYSIS_COMPLETE", "Resumed to Analyzing for preselection evaluation");
            }

            await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Interrupted, "RECOVERY_CORRUPT_ANALYSIS_DATA", "recovery:analysis-corrupt", cancellationToken).ConfigureAwait(false);
            return new JobRecoveryResult(jobId, initialState, JobState.Interrupted, JobRecoveryAction.RolledBackCorruptCheckpoint, "ANALYSIS_COMPLETE", "Analysis results record missing");
        }

        // If in an active processing state without matching valid checkpoints, normalize through INTERRUPTED
        if (initialState is JobState.Analyzing or JobState.Processing or JobState.Qa or JobState.Retrying)
        {
            await TransitionJobSafelyAsync(conn, jobId, initialState, JobState.Interrupted, "RECOVERY_ACTIVE_JOB_INTERRUPTED_ON_CRASH", "recovery:normalize-interrupted", cancellationToken).ConfigureAwait(false);
            return new JobRecoveryResult(jobId, initialState, JobState.Interrupted, JobRecoveryAction.NormalizedToInterrupted, latestCheckpoint.StageName, "Normalized in-flight job to INTERRUPTED");
        }

        return new JobRecoveryResult(jobId, initialState, initialState, JobRecoveryAction.None, latestCheckpoint.StageName, "No recovery action needed");
    }

    private static async Task<bool> ValidatePublicationAndHistoryAsync(
        SqliteConnection conn,
        IQaStore qaStore,
        JobId jobId,
        CancellationToken cancellationToken)
    {
        var pub = await qaStore.GetPublicationAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (pub is null) return false;

        // 1. Validate destination JPEG
        if (!File.Exists(pub.DestinationPath) || !VerifySha256(pub.DestinationPath, pub.Sha256))
        {
            return false;
        }

        // 2. Validate history file
        if (string.IsNullOrWhiteSpace(pub.HistoryPath) || !File.Exists(pub.HistoryPath))
        {
            return false;
        }

        try
        {
            var historyBytes = await File.ReadAllBytesAsync(pub.HistoryPath, cancellationToken).ConfigureAwait(false);
            if (historyBytes.Length == 0) return false;

            using var doc = JsonDocument.Parse(historyBytes);
            var root = doc.RootElement;

            // Verify history ownership and publication SHA
            if (root.TryGetProperty("job_id", out var historyJobId) &&
                string.Equals(historyJobId.GetString(), jobId.Value, StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("publication", out var pubElem) &&
                    pubElem.TryGetProperty("sha256", out var pubSha) &&
                    string.Equals(pubSha.GetString(), pub.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task TransitionJobSafelyAsync(
        SqliteConnection conn,
        JobId jobId,
        JobState fromState,
        JobState toState,
        string reason,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (fromState == toState) return;

        // Validate state machine rule
        JobStateMachine.EnsureTransition(fromState, toState);

        var nowUtc = DateTimeOffset.UtcNow.ToString("O");

        await using var trans = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = trans;
            cmd.CommandText = """
                UPDATE jobs
                SET state = $toState, updated_at_utc = $nowUtc
                WHERE job_id = $jobId AND state = $fromState;

                INSERT OR IGNORE INTO job_state_transitions(transition_id, job_id, from_state, to_state, reason, operation_id, occurred_at_utc)
                VALUES($transId, $jobId, $fromState, $toState, $reason, $opId, $nowUtc);
                """;
            cmd.Parameters.AddWithValue("$jobId", jobId.Value);
            cmd.Parameters.AddWithValue("$fromState", MapJobStateToString(fromState));
            cmd.Parameters.AddWithValue("$toState", MapJobStateToString(toState));
            cmd.Parameters.AddWithValue("$transId", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$opId", $"{operationId}:{jobId.Value}");
            cmd.Parameters.AddWithValue("$nowUtc", nowUtc);

            var affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (affected < 1)
            {
                throw new InvalidOperationException(
                    $"Concurrency or invariant conflict: Failed to update job {jobId.Value} from state {fromState} to {toState}.");
            }

            await trans.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await trans.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static bool VerifySha256(string filePath, string expectedSha256)
    {
        try
        {
            if (!File.Exists(filePath)) return false;
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0) return false;

            using var stream = File.OpenRead(filePath);
            var hashBytes = SHA256.HashData(stream);
            var computedSha = Convert.ToHexString(hashBytes).ToLowerInvariant();
            return string.Equals(computedSha, expectedSha256.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

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
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown job state string")
    };

    private static string MapJobStateToString(JobState state) => state switch
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
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown job state")
    };
}
