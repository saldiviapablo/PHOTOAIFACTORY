using System.Diagnostics;
using System.Text.Json;

namespace PhotoAIFactory.Rec01;

internal static class RecoveryWorker
{
    public static async Task<int> RunAsync(WorkerOptions options)
    {
        var log = new StructuredLog(options.Log);
        log.Write("INFO", "RecoveryWorker", "worker_started", extra: new Dictionary<string, object?>
        {
            ["process_id"] = Environment.ProcessId,
            ["scenario"] = options.Scenario
        });

        using var store = new RecoveryStore(options.Database, log);
        store.EnsureJobs(options.Scenario, options.Jobs);
        store.RecoverInterrupted(log);

        foreach (var job in store.RunnableJobs())
        {
            await ProcessJobAsync(store, log, options, job);
        }

        log.Write("INFO", "RecoveryWorker", "worker_stopped", extra: new Dictionary<string, object?>
        {
            ["process_id"] = Environment.ProcessId,
            ["scenario"] = options.Scenario
        });
        return 0;
    }

    private static async Task ProcessJobAsync(RecoveryStore store, StructuredLog log, WorkerOptions options, JobRow job)
    {
        var start = ValidateCheckpointChain(store, log, job);
        if (job.State == "INTERRUPTED")
        {
            log.Write("INFO", "RecoveryWorker", "recovery_resume", job.ProjectId, job.PhotoId, job.JobId,
                stage: start < Rec01Model.Checkpoints.Length ? Rec01Model.Checkpoints[start] : "COMPLETION",
                extra: new Dictionary<string, object?>
                {
                    ["recovery_action"] = start == 0 ? "repeat_first_incomplete_stage" : $"resume_after_{Rec01Model.Checkpoints[start - 1]}"
                });
        }

        store.SetState(job, "PROCESSING", start < Rec01Model.Checkpoints.Length ? Rec01Model.Checkpoints[start] : "COMPLETION");
        log.Write("INFO", "RecoveryWorker", "job_processing", job.ProjectId, job.PhotoId, job.JobId,
            extra: new Dictionary<string, object?> { ["previous_state"] = job.State, ["new_state"] = "PROCESSING" });

        for (var index = start; index < Rec01Model.Checkpoints.Length; index++)
        {
            var stage = Rec01Model.Checkpoints[index];
            store.SetState(job, "PROCESSING", stage);
            var continueJob = await ExecuteStageWithPolicyAsync(store, log, options, job, stage);
            if (!continueJob) return;
        }

        store.SetState(job, "COMPLETED", "");
        log.Write("INFO", "RecoveryWorker", "job_completed", job.ProjectId, job.PhotoId, job.JobId,
            extra: new Dictionary<string, object?> { ["previous_state"] = "PROCESSING", ["new_state"] = "COMPLETED" });
    }

    private static int ValidateCheckpointChain(RecoveryStore store, StructuredLog log, JobRow job)
    {
        var checkpoints = store.Checkpoints(job.JobId).ToDictionary(item => item.Checkpoint, StringComparer.Ordinal);
        for (var index = 0; index < Rec01Model.Checkpoints.Length; index++)
        {
            var stage = Rec01Model.Checkpoints[index];
            if (!checkpoints.TryGetValue(stage, out var checkpoint) || !checkpoint.IsValid) return index;
            var valid = Rec01Model.ValidateArtifact(
                checkpoint.ArtifactPath,
                checkpoint.ArtifactSize,
                checkpoint.ArtifactSha256,
                stage == "OUTPUT_PUBLISHED");
            if (!valid)
            {
                var observed = File.Exists(checkpoint.ArtifactPath)
                    ? $"size={new FileInfo(checkpoint.ArtifactPath).Length};sha256={Rec01Model.Sha256(checkpoint.ArtifactPath)}"
                    : "missing";
                store.InvalidateFrom(job, index, "artifact_validation_failed");
                store.History(job.JobId, checkpoint.AttemptId, stage, "checkpoint_artifact_invalid", "VALID", "INVALID", observed);
                log.Write("ERROR", "RecoveryWorker", "checkpoint_artifact_invalid", job.ProjectId, job.PhotoId, job.JobId,
                    checkpoint.AttemptId, stage, new Dictionary<string, object?>
                    {
                        ["checkpoint"] = stage,
                        ["artifact_path"] = checkpoint.ArtifactPath,
                        ["expected_sha256"] = checkpoint.ArtifactSha256,
                        ["artifact_sha256"] = File.Exists(checkpoint.ArtifactPath) ? Rec01Model.Sha256(checkpoint.ArtifactPath) : "",
                        ["recovery_action"] = index == 0 ? "restart_pipeline" : $"fall_back_to_{Rec01Model.Checkpoints[index - 1]}"
                    });
                return index;
            }

            log.Write("INFO", "RecoveryWorker", "checkpoint_artifact_revalidated", job.ProjectId, job.PhotoId, job.JobId,
                checkpoint.AttemptId, stage, new Dictionary<string, object?>
                {
                    ["checkpoint"] = stage,
                    ["artifact_path"] = checkpoint.ArtifactPath,
                    ["artifact_sha256"] = checkpoint.ArtifactSha256,
                    ["expected_sha256"] = checkpoint.ArtifactSha256,
                    ["recovery_action"] = "reuse_completed_stage"
                });
        }
        return Rec01Model.Checkpoints.Length;
    }

    private static async Task<bool> ExecuteStageWithPolicyAsync(
        RecoveryStore store,
        StructuredLog log,
        WorkerOptions options,
        JobRow job,
        string stage)
    {
        while (true)
        {
            var attemptId = store.StartAttempt(job.JobId, stage);
            var attemptNumber = store.Attempt(attemptId).AttemptId.EndsWith("-01", StringComparison.Ordinal) ? 1 :
                store.NextAttemptNumber(job.JobId, stage) - 1;
            var artifactDirectory = Path.Combine(options.Work, "artifacts", Rec01Model.Safe(job.JobId), attemptId, stage);
            Directory.CreateDirectory(artifactDirectory);
            var artifact = Path.Combine(artifactDirectory, stage == "OUTPUT_PUBLISHED" ? "staging.jpg" : "artifact.json");

            log.Write("INFO", "RecoveryWorker", "stage_attempt_started", job.ProjectId, job.PhotoId, job.JobId,
                attemptId, stage, new Dictionary<string, object?> { ["retry_number"] = attemptNumber - 1 });

            if (options.Scenario == "retry-bounded" && stage == "ANALYSIS_COMPLETE")
            {
                if (job.JobId.EndsWith("JOB-A", StringComparison.Ordinal))
                {
                    File.WriteAllText(artifact, $"partial retryable {attemptId}");
                    store.FailAttempt(attemptId, artifact, "RETRYABLE");
                    log.Write("WARN", "RecoveryWorker", "retryable_stage_failure", job.ProjectId, job.PhotoId, job.JobId,
                        attemptId, stage, new Dictionary<string, object?> { ["retry_number"] = attemptNumber - 1 });
                    if (attemptNumber <= 2)
                    {
                        store.SetState(job, "RETRYING", stage);
                        store.SetState(job, "PROCESSING", stage);
                        continue;
                    }
                    store.SetState(job, "ERROR", stage);
                    log.Write("ERROR", "RecoveryWorker", "retry_budget_exhausted", job.ProjectId, job.PhotoId, job.JobId,
                        attemptId, stage, new Dictionary<string, object?> { ["retry_number"] = attemptNumber - 1, ["new_state"] = "ERROR" });
                    return false;
                }
                if (job.JobId.EndsWith("JOB-B", StringComparison.Ordinal))
                {
                    File.WriteAllText(artifact, $"partial permanent {attemptId}");
                    store.FailAttempt(attemptId, artifact, "PERMANENT");
                    store.SetState(job, "ERROR", stage);
                    log.Write("ERROR", "RecoveryWorker", "permanent_stage_failure", job.ProjectId, job.PhotoId, job.JobId,
                        attemptId, stage, new Dictionary<string, object?> { ["retry_number"] = 0, ["new_state"] = "ERROR" });
                    return false;
                }
            }

            if (options.Scenario == "child-process" && stage == "ANALYSIS_COMPLETE" && attemptNumber == 1)
            {
                var helperSucceeded = await RunHelperAsync(options, artifact);
                if (!helperSucceeded)
                {
                    store.FailAttempt(attemptId, artifact, "CHILD_PROCESS_CRASH");
                    store.SetState(job, "RETRYING", stage);
                    log.Write("WARN", "RecoveryWorker", "stage_helper_failed", job.ProjectId, job.PhotoId, job.JobId,
                        attemptId, stage, new Dictionary<string, object?> { ["retry_number"] = 0, ["recovery_action"] = "retry_stage" });
                    store.SetState(job, "PROCESSING", stage);
                    continue;
                }
            }

            string checkpointPath;
            long size;
            string sha;
            if (stage == "OUTPUT_PUBLISHED")
            {
                File.Copy(options.Fixture, artifact, false);
                if (options.Crash == "pub-generated") BarrierAndWait(options, log, job, attemptId, stage, "after_staging_generated_before_validation");
                size = new FileInfo(artifact).Length;
                sha = Rec01Model.Sha256(artifact);
                if (!Rec01Model.ValidateArtifact(artifact, size, sha, jpeg: true)) throw new InvalidDataException("Invalid staging JPEG.");
                store.ValidateAttempt(attemptId, artifact, size, sha);
                store.PersistStageHistory(job, attemptId, stage, artifact, sha);
                if (options.Crash == "pub-validated") BarrierAndWait(options, log, job, attemptId, stage, "after_staging_validated_before_publish");
                checkpointPath = PublishIdempotently(store, log, options, job, attemptId, artifact, sha);
                size = new FileInfo(checkpointPath).Length;
                sha = Rec01Model.Sha256(checkpointPath);
                if (options.Crash == "pub-moved") BarrierAndWait(options, log, job, attemptId, stage, "after_final_moved_before_checkpoint");
            }
            else
            {
                File.WriteAllText(artifact, JsonSerializer.Serialize(new
                {
                    schema = "rec01.synthetic-artifact.v1",
                    job_id = job.JobId,
                    attempt_id = attemptId,
                    stage,
                    attempt_number = attemptNumber
                }));
                size = new FileInfo(artifact).Length;
                sha = Rec01Model.Sha256(artifact);
                if (!Rec01Model.ValidateArtifact(artifact, size, sha)) throw new InvalidDataException("Synthetic artifact validation failed.");
                store.ValidateAttempt(attemptId, artifact, size, sha);
                store.PersistStageHistory(job, attemptId, stage, artifact, sha);
                checkpointPath = artifact;
            }

            log.Write("INFO", "RecoveryWorker", "stage_artifact_validated", job.ProjectId, job.PhotoId, job.JobId,
                attemptId, stage, new Dictionary<string, object?>
                {
                    ["artifact_path"] = checkpointPath,
                    ["artifact_sha256"] = sha,
                    ["expected_sha256"] = sha
                });

            if (options.Crash == "pre" && options.Target == stage)
                BarrierAndWait(options, log, job, attemptId, stage, "after_artifact_before_checkpoint");

            if (options.Crash == "tx" && options.Target == stage)
            {
                store.OpenUncommittedCheckpointAndBlock(job, stage, attemptId, checkpointPath, size, sha,
                    () => WriteBarrier(options, log, job, attemptId, stage, "inside_transaction_before_commit"));
            }

            store.CommitCheckpoint(job, stage, attemptId, checkpointPath, size, sha);
            log.Write("INFO", "RecoveryWorker", "checkpoint_committed", job.ProjectId, job.PhotoId, job.JobId,
                attemptId, stage, new Dictionary<string, object?>
                {
                    ["checkpoint"] = stage,
                    ["artifact_path"] = checkpointPath,
                    ["artifact_sha256"] = sha
                });

            if (options.Crash == "post" && options.Target == stage)
                BarrierAndWait(options, log, job, attemptId, stage, "immediately_after_checkpoint");
            return true;
        }
    }

    private static string PublishIdempotently(
        RecoveryStore store,
        StructuredLog log,
        WorkerOptions options,
        JobRow job,
        string attemptId,
        string staging,
        string expectedSha)
    {
        var finalDirectory = Path.Combine(options.Work, "FINAL");
        Directory.CreateDirectory(finalDirectory);
        foreach (var candidate in Directory.EnumerateFiles(finalDirectory, "*.jpg"))
        {
            var owner = candidate + ".rec01-owner.json";
            if (!File.Exists(owner)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(owner));
                var root = document.RootElement;
                if (root.GetProperty("job_id").GetString() == job.JobId &&
                    root.GetProperty("sha256").GetString() == expectedSha &&
                    Rec01Model.Sha256(candidate) == expectedSha)
                {
                    store.RecordPublication(job.JobId, candidate, expectedSha, owner);
                    log.Write("INFO", "RecoveryWorker", "existing_publication_adopted", job.ProjectId, job.PhotoId, job.JobId,
                        attemptId, "OUTPUT_PUBLISHED", new Dictionary<string, object?>
                        {
                            ["artifact_path"] = candidate,
                            ["artifact_sha256"] = expectedSha,
                            ["recovery_action"] = "complete_idempotently"
                        });
                    return candidate;
                }
            }
            catch (JsonException)
            {
                // An invalid sidecar never authorizes overwrite or adoption.
            }
        }

        var baseName = "fixture.jpg";
        var final = Path.Combine(finalDirectory, baseName);
        if (File.Exists(final))
        {
            var suffix = 2;
            do
            {
                final = Path.Combine(finalDirectory, $"fixture_v{suffix:D2}.jpg");
                suffix++;
            } while (File.Exists(final));
            log.Write("WARN", "RecoveryWorker", "publication_collision_versioned", job.ProjectId, job.PhotoId, job.JobId,
                attemptId, "OUTPUT_PUBLISHED", new Dictionary<string, object?>
                {
                    ["artifact_path"] = final,
                    ["recovery_action"] = "version_name_without_overwrite"
                });
        }

        File.Move(staging, final, false);
        var ownerPath = final + ".rec01-owner.json";
        Rec01Model.AtomicJson(ownerPath, new { job_id = job.JobId, sha256 = expectedSha, attempt_id = attemptId });
        store.RecordPublication(job.JobId, final, expectedSha, ownerPath);
        return final;
    }

    private static async Task<bool> RunHelperAsync(WorkerOptions options, string output)
    {
        if (string.IsNullOrWhiteSpace(options.HelperBarrier)) throw new InvalidOperationException("Missing helper barrier.");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--mode");
        start.ArgumentList.Add("stage-helper");
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--barrier");
        start.ArgumentList.Add(options.HelperBarrier);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start stage helper.");
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }

    public static Task<int> RunStageHelperAsync(string output, string barrier)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, $"INCOMPLETE helper_pid={Environment.ProcessId}");
        Rec01Model.AtomicJson(barrier, new { pid = Environment.ProcessId, event_name = "helper_mid_stage", output });
        Thread.Sleep(Timeout.Infinite);
        return Task.FromResult(0);
    }

    private static void BarrierAndWait(WorkerOptions options, StructuredLog log, JobRow job, string attemptId, string stage, string point)
    {
        WriteBarrier(options, log, job, attemptId, stage, point);
        Thread.Sleep(Timeout.Infinite);
    }

    private static void WriteBarrier(WorkerOptions options, StructuredLog log, JobRow job, string attemptId, string stage, string point)
    {
        if (string.IsNullOrWhiteSpace(options.Barrier)) throw new InvalidOperationException("Crash requested without barrier path.");
        Rec01Model.AtomicJson(options.Barrier, new
        {
            pid = Environment.ProcessId,
            crash_point = point,
            checkpoint = stage,
            job_id = job.JobId,
            attempt_id = attemptId
        });
        log.Write("WARN", "RecoveryWorker", "crash_barrier_reached", job.ProjectId, job.PhotoId, job.JobId,
            attemptId, stage, new Dictionary<string, object?> { ["checkpoint"] = stage, ["crash_point"] = point });
    }
}
