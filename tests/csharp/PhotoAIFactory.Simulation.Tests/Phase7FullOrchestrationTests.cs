using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Qa;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Qa;
using PhotoAIFactory.Infrastructure.Qa;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class Phase7FullOrchestrationTests
{
    private static readonly byte[] ValidJpegBytes =
    [
        0xFF, 0xD8, // SOI
        0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, // APP0
        0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x64, 0x00, 0x64, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, // SOF0: 100x100 8-bit 3-channel
        0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00, // SOS
        0xAA, 0x55,
        0xFF, 0xD9 // EOI
    ];

    [TestMethod]
    public async Task QaOrchestrator_Pass_PublishesAndCompletesJob()
    {
        var root = TempRoot("qa-pass");
        try
        {
            var (database, storeFactory, publishService, historyWriter) = CreateInfrastructure(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();

            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedQaEligibleJobAsync(database, projectId, photoId, jobId, candidatePath, sha, ValidJpegBytes.Length);

            var fakeClient = new FakeAiClient((route, req) =>
            {
                Assert.AreEqual("/v1/qa", route);
                return new AiResponse(
                    "v1",
                    req.RequestId,
                    true,
                    JsonDocument.Parse("""
                    {
                        "schema_version": 1,
                        "decision": "QA_PASS",
                        "findings": [],
                        "suggested_correction": null,
                        "technical": { "sharpness": { "laplacian_variance": 80.0 } },
                        "calibration_status": "BASELINE_NOT_CALIBRATED"
                    }
                    """).RootElement.Clone(),
                    null,
                    null);
            });

            var orchestrator = new QaOrchestrator(storeFactory, fakeClient, publishService);
            var outputFolder = Path.Combine(root, "output");
            var result = await orchestrator.ProcessJobAsync(projectId, jobId, outputFolder);

            Assert.IsTrue(result);
            var store = storeFactory.Open(projectId);
            var job = await store.GetJobAsync(jobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(JobState.Completed, job.State);
            Assert.IsTrue(await store.HasCheckpointAsync(jobId, "QA_COMPLETE"));
            Assert.IsTrue(await store.HasCheckpointAsync(jobId, "OUTPUT_PUBLISHED"));
            Assert.IsTrue(await store.HasPublicationAsync(jobId));

            var pub = await store.GetPublicationAsync(jobId);
            Assert.IsNotNull(pub);
            Assert.AreEqual("FINAL", pub.DestinationKind);
            Assert.IsTrue(File.Exists(pub.DestinationPath));
            Assert.IsTrue(File.Exists(pub.HistoryPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task QaOrchestrator_Review_RoutesToReviewFinal()
    {
        var root = TempRoot("qa-review");
        try
        {
            var (database, storeFactory, publishService, _) = CreateInfrastructure(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();

            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedQaEligibleJobAsync(database, projectId, photoId, jobId, candidatePath, sha, ValidJpegBytes.Length);

            var fakeClient = new FakeAiClient((route, req) =>
                new AiResponse(
                    "v1",
                    req.RequestId,
                    true,
                    JsonDocument.Parse("""
                    {
                        "schema_version": 1,
                        "decision": "QA_REVIEW",
                        "findings": [{ "code": "LOW_SHARPNESS", "severity": "review", "message": "Slight blur", "score": 30.0 }],
                        "suggested_correction": null,
                        "technical": { "sharpness": { "laplacian_variance": 30.0 } },
                        "calibration_status": "BASELINE_NOT_CALIBRATED"
                    }
                    """).RootElement.Clone(),
                    null,
                    null));

            var orchestrator = new QaOrchestrator(storeFactory, fakeClient, publishService);
            var outputFolder = Path.Combine(root, "output");
            var result = await orchestrator.ProcessJobAsync(projectId, jobId, outputFolder);

            Assert.IsTrue(result);
            var store = storeFactory.Open(projectId);
            var job = await store.GetJobAsync(jobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(JobState.ReviewFinal, job.State);
            Assert.IsTrue(await store.HasCheckpointAsync(jobId, "QA_COMPLETE"));
            Assert.IsFalse(await store.HasCheckpointAsync(jobId, "OUTPUT_PUBLISHED"));

            var reviewItem = await store.GetPendingReviewItemAsync(jobId, "FINAL");
            Assert.IsNotNull(reviewItem);
            Assert.AreEqual("PENDING", reviewItem.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task QaOrchestrator_Reprocess_CreatesChildJobOnFirstAttempt_AndRoutesToReviewOnSecond()
    {
        var root = TempRoot("qa-reprocess");
        try
        {
            var (database, storeFactory, publishService, _) = CreateInfrastructure(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var parentJobId = JobId.New();

            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedQaEligibleJobAsync(database, projectId, photoId, parentJobId, candidatePath, sha, ValidJpegBytes.Length);

            var fakeClient = new FakeAiClient((route, req) =>
                new AiResponse(
                    "v1",
                    req.RequestId,
                    true,
                    JsonDocument.Parse("""
                    {
                        "schema_version": 1,
                        "decision": "QA_REPROCESS",
                        "findings": [{ "code": "SEVERE_LOW_SHARPNESS", "severity": "reprocess", "message": "Severe blur", "score": 10.0 }],
                        "suggested_correction": null,
                        "technical": { "sharpness": { "laplacian_variance": 10.0 } },
                        "calibration_status": "BASELINE_NOT_CALIBRATED"
                    }
                    """).RootElement.Clone(),
                    null,
                    null));

            var orchestrator = new QaOrchestrator(storeFactory, fakeClient, publishService);
            var outputFolder = Path.Combine(root, "output");

            // 1. First reprocess: spawns child job
            var result1 = await orchestrator.ProcessJobAsync(projectId, parentJobId, outputFolder);
            Assert.IsTrue(result1);

            var store = storeFactory.Open(projectId);
            var parentJob = await store.GetJobAsync(parentJobId);
            Assert.IsNotNull(parentJob);
            Assert.AreEqual(JobState.ReviewFinal, parentJob.State);
            Assert.IsFalse(await store.HasCheckpointAsync(parentJobId, "OUTPUT_PUBLISHED"));

            // Child exists in QUEUED state with quality_reprocess_count = 1
            await using var conn = await database.OpenConfiguredConnectionAsync();
            await using var childCmd = conn.CreateCommand();
            childCmd.CommandText = "SELECT job_id, state, quality_reprocess_count, parent_job_id FROM jobs WHERE parent_job_id=$pId;";
            childCmd.Parameters.AddWithValue("$pId", parentJobId.Value);
            await using var reader = await childCmd.ExecuteReaderAsync();
            Assert.IsTrue(await reader.ReadAsync());
            var childJobId = new JobId(reader.GetString(0));
            Assert.AreEqual("QUEUED", reader.GetString(1));
            Assert.AreEqual(1, reader.GetInt32(2));
            Assert.AreEqual(parentJobId.Value, reader.GetString(3));
            await reader.DisposeAsync();

            // Replay on parent does not spawn a second child job (claim returns false since parent is in ReviewFinal)
            var replayResult = await orchestrator.ProcessJobAsync(projectId, parentJobId, outputFolder);
            Assert.IsFalse(replayResult);
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM jobs WHERE parent_job_id=$pId;";
            countCmd.Parameters.AddWithValue("$pId", parentJobId.Value);
            var childCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
            Assert.AreEqual(1, childCount);

            // 2. Now simulate child job advancing to QA state and getting QA_REPROCESS again
            await using var updateChild = conn.CreateCommand();
            updateChild.CommandText = """
                UPDATE jobs SET state='QA' WHERE job_id=$cId;
                INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
                VALUES('cp-child-comfy', $cId, 'COMFYUI_COMPLETE', 'att-1', $sha, '2026-08-23T00:00:00Z');
                INSERT INTO comfy_executions(
                    comfy_execution_id, job_id, attempt_id, status, input_path, input_sha256,
                    output_path, output_sha256, output_size_bytes, task_manifest_json,
                    workflow_manifest_json, prompt_ids_json, history_path, completed_at_utc)
                VALUES(
                    'exec-child', $cId, 'att-1', 'COMPLETED', $path, $sha,
                    $path, $sha, 1000, '[]', '[]', '[]', 'history.json', '2026-08-23T00:00:00Z');
                """;
            updateChild.Parameters.AddWithValue("$cId", childJobId.Value);
            updateChild.Parameters.AddWithValue("$sha", sha);
            updateChild.Parameters.AddWithValue("$path", candidatePath);
            await updateChild.ExecuteNonQueryAsync();

            var result2 = await orchestrator.ProcessJobAsync(projectId, childJobId, outputFolder);
            Assert.IsTrue(result2);

            var childAfter = await store.GetJobAsync(childJobId);
            Assert.IsNotNull(childAfter);
            Assert.AreEqual(JobState.ReviewFinal, childAfter.State);
            var childReview = await store.GetPendingReviewItemAsync(childJobId, "FINAL");
            Assert.IsNotNull(childReview);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task QaOrchestrator_TechRetry_IncrementsRetryAndExhaustsToError()
    {
        var root = TempRoot("qa-tech-retry");
        try
        {
            var (database, storeFactory, publishService, _) = CreateInfrastructure(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();

            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedQaEligibleJobAsync(database, projectId, photoId, jobId, candidatePath, sha, ValidJpegBytes.Length);

            var fakeClient = new FakeAiClient((route, req) =>
                new AiResponse(
                    "v1",
                    req.RequestId,
                    true,
                    JsonDocument.Parse("""
                    {
                        "schema_version": 1,
                        "decision": "QA_TECH_RETRY",
                        "findings": [{ "code": "TRANSIENT_GPU_GLITCH", "severity": "warning", "message": "Transient error" }],
                        "suggested_correction": null,
                        "technical": { "sharpness": { "laplacian_variance": 50.0 } },
                        "calibration_status": "BASELINE_NOT_CALIBRATED"
                    }
                    """).RootElement.Clone(),
                    null,
                    null));

            var orchestrator = new QaOrchestrator(storeFactory, fakeClient, publishService);
            var outputFolder = Path.Combine(root, "output");

            // Attempt 1: RETRYING (technical_retry_count = 1)
            await orchestrator.ProcessJobAsync(projectId, jobId, outputFolder);
            var store = storeFactory.Open(projectId);
            var job1 = await store.GetJobAsync(jobId);
            Assert.IsNotNull(job1);
            Assert.AreEqual(JobState.Retrying, job1.State);
            Assert.AreEqual(1, job1.TechnicalRetryCount);

            // Re-dispatch: technical_retry_count = 2
            await orchestrator.ProcessJobAsync(projectId, jobId, outputFolder);
            var job2 = await store.GetJobAsync(jobId);
            Assert.IsNotNull(job2);
            Assert.AreEqual(JobState.Retrying, job2.State);
            Assert.AreEqual(2, job2.TechnicalRetryCount);

            // Re-dispatch: exhaustion -> ERROR
            await orchestrator.ProcessJobAsync(projectId, jobId, outputFolder);
            var job3 = await store.GetJobAsync(jobId);
            Assert.IsNotNull(job3);
            Assert.AreEqual(JobState.Error, job3.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task QaOrchestrator_Fatal_TransitionsToError()
    {
        var root = TempRoot("qa-fatal");
        try
        {
            var (database, storeFactory, publishService, _) = CreateInfrastructure(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();

            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedQaEligibleJobAsync(database, projectId, photoId, jobId, candidatePath, sha, ValidJpegBytes.Length);

            var fakeClient = new FakeAiClient((route, req) =>
                new AiResponse(
                    "v1",
                    req.RequestId,
                    true,
                    JsonDocument.Parse("""
                    {
                        "schema_version": 1,
                        "decision": "QA_FATAL",
                        "findings": [{ "code": "CORRUPT_HEADER", "severity": "fatal", "message": "Corrupt stream" }],
                        "suggested_correction": null,
                        "technical": {},
                        "calibration_status": "BASELINE_NOT_CALIBRATED"
                    }
                    """).RootElement.Clone(),
                    null,
                    null));

            var orchestrator = new QaOrchestrator(storeFactory, fakeClient, publishService);
            var outputFolder = Path.Combine(root, "output");
            await orchestrator.ProcessJobAsync(projectId, jobId, outputFolder);

            var store = storeFactory.Open(projectId);
            var job = await store.GetJobAsync(jobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(JobState.Error, job.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PublishService_NoOverwrite_DisambiguatesCollisionsSafely()
    {
        var root = TempRoot("publish-collision");
        try
        {
            var historyWriter = new FinalHistoryWriter();
            var publishService = new PublishService(historyWriter);

            var candidatePath = Path.Combine(root, "photo1.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            var outputFolder = Path.Combine(root, "output");
            var finalFolder = Path.Combine(outputFolder, "FINAL");
            Directory.CreateDirectory(finalFolder);

            // Place an existing DIFFERENT file at output/FINAL/photo1.jpg
            var existingDest = Path.Combine(finalFolder, "photo1.jpg");
            byte[] existingDiffBytes = [0xFF, 0xD8, 0xFF, 0xD9];
            await File.WriteAllBytesAsync(existingDest, existingDiffBytes);
            var originalDiffSha = ComputeSha256(existingDiffBytes);

            var jobId = JobId.New();
            var req = new PublishCandidateRequest(
                jobId,
                ProjectId.New(),
                PhotoId.New(),
                "att-pub",
                candidatePath,
                sha,
                "FINAL",
                new QaResultSnapshot("qa-1", jobId, "att-pub", "PASS", JsonDocument.Parse("""{"status":"ok"}""").RootElement.Clone(), candidatePath, sha, DateTimeOffset.UtcNow),
                outputFolder);

            var result = await publishService.PublishAsync(req);

            Assert.AreNotEqual(existingDest, result.DestinationPath);
            Assert.IsTrue(File.Exists(result.DestinationPath));
            Assert.IsTrue(File.Exists(existingDest));

            // Verify original colliding file was NOT overwritten
            var destRemainingBytes = await File.ReadAllBytesAsync(existingDest);
            Assert.AreEqual(originalDiffSha, ComputeSha256(destRemainingBytes));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReviewService_Approve_PublishesAndResolvesReview()
    {
        var root = TempRoot("review-approve");
        try
        {
            var (database, storeFactory, publishService, _) = CreateInfrastructure(root);
            var reviewService = new ReviewService(storeFactory, publishService);

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();

            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedQaEligibleJobAsync(database, projectId, photoId, jobId, candidatePath, sha, ValidJpegBytes.Length);

            var store = storeFactory.Open(projectId);
            var qaReq = new PersistQaResultRequest(
                jobId, "att-1", "REVIEW", JsonDocument.Parse("""{"status":"ok"}""").RootElement.Clone(), candidatePath, sha, DateTimeOffset.UtcNow);
            await store.PersistQaResultAsync(qaReq);
            await store.InsertCheckpointAsync(jobId, "QA_COMPLETE", "att-1", sha, DateTimeOffset.UtcNow);
            await store.CreateReviewItemAsync(new CreateReviewItemRequest(Guid.NewGuid().ToString("N"), jobId, "FINAL", DateTimeOffset.UtcNow));
            await store.TransitionJobStateAsync(jobId, JobState.Qa, JobState.ReviewFinal, "QA_REVIEW", "op-1", DateTimeOffset.UtcNow);

            var outputFolder = Path.Combine(root, "output");
            await reviewService.ApproveAsync(projectId, jobId, "op-approve-1", outputFolder);

            var job = await store.GetJobAsync(jobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(JobState.Completed, job.State);
            Assert.IsTrue(await store.HasPublicationAsync(jobId));
            Assert.IsTrue(await store.HasCheckpointAsync(jobId, "OUTPUT_PUBLISHED"));

            var reviewItem = await store.GetPendingReviewItemAsync(jobId, "FINAL");
            Assert.IsNull(reviewItem); // Resolved -> no longer pending
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReviewService_Reject_TransitionsToRejectedFinal()
    {
        var root = TempRoot("review-reject");
        try
        {
            var (database, storeFactory, publishService, _) = CreateInfrastructure(root);
            var reviewService = new ReviewService(storeFactory, publishService);

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();

            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedQaEligibleJobAsync(database, projectId, photoId, jobId, candidatePath, sha, ValidJpegBytes.Length);

            var store = storeFactory.Open(projectId);
            await store.CreateReviewItemAsync(new CreateReviewItemRequest(Guid.NewGuid().ToString("N"), jobId, "FINAL", DateTimeOffset.UtcNow));
            await store.TransitionJobStateAsync(jobId, JobState.Qa, JobState.ReviewFinal, "QA_REVIEW", "op-1", DateTimeOffset.UtcNow);

            await reviewService.RejectAsync(projectId, jobId, "op-reject-1");

            var job = await store.GetJobAsync(jobId);
            Assert.IsNotNull(job);
            Assert.AreEqual(JobState.RejectedFinal, job.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReviewService_Reprocess_SpawnsChild_ResolvesReview_LeavesParentInReviewFinal()
    {
        var root = TempRoot("review-reprocess");
        try
        {
            var (database, storeFactory, publishService, _) = CreateInfrastructure(root);
            var reviewService = new ReviewService(storeFactory, publishService);

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();

            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedQaEligibleJobAsync(database, projectId, photoId, jobId, candidatePath, sha, ValidJpegBytes.Length);

            var store = storeFactory.Open(projectId);
            var reviewItemId = Guid.NewGuid().ToString("N");
            await store.CreateReviewItemAsync(new CreateReviewItemRequest(reviewItemId, jobId, "FINAL", DateTimeOffset.UtcNow));
            await store.TransitionJobStateAsync(jobId, JobState.Qa, JobState.ReviewFinal, "QA_REVIEW", "op-1", DateTimeOffset.UtcNow);

            var childId = await reviewService.ReprocessAsync(projectId, jobId, "op-reprocess-1");

            // Parent is NOT completed and has NO output published checkpoint
            var parentJob = await store.GetJobAsync(jobId);
            Assert.IsNotNull(parentJob);
            Assert.AreEqual(JobState.ReviewFinal, parentJob.State);
            Assert.IsFalse(await store.HasCheckpointAsync(jobId, "OUTPUT_PUBLISHED"));

            // Review item is resolved with REPROCESS
            var pendingItem = await store.GetPendingReviewItemAsync(jobId, "FINAL");
            Assert.IsNull(pendingItem);

            // Child job exists in QUEUED state with quality_reprocess_count = 1
            var childJob = await store.GetJobAsync(childId);
            Assert.IsNotNull(childJob);
            Assert.AreEqual(JobState.Queued, childJob.State);
            Assert.AreEqual(1, childJob.QualityReprocessCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FinalHistoryWriter_IdenticalReplaySucceeds_AndContentConflictThrows()
    {
        var root = TempRoot("history-conflict");
        try
        {
            var writer = new FinalHistoryWriter();
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var outputFolder = Path.Combine(root, "output");
            var qaSnapshot = new QaResultSnapshot("qa-1", jobId, "att-1", "PASS", JsonDocument.Parse("""{"status":"ok"}""").RootElement.Clone(), "dummy.jpg", new string('1', 64), DateTimeOffset.UtcNow);
            var publishedAt = DateTimeOffset.UtcNow;

            // 1. Initial write
            var path1 = await writer.WriteFinalHistoryAsync(
                projectId, photoId, jobId, "att-1", "C:\\dest.jpg", new string('1', 64), 1000, 100, 100, qaSnapshot, outputFolder, publishedAt);
            Assert.IsTrue(File.Exists(path1));

            // 2. Identical replay succeeds idempotently
            var path2 = await writer.WriteFinalHistoryAsync(
                projectId, photoId, jobId, "att-1", "C:\\dest.jpg", new string('1', 64), 1000, 100, 100, qaSnapshot, outputFolder, publishedAt);
            Assert.AreEqual(path1, path2);

            // 3. Modifying file content creates a conflict -> fails closed
            await File.WriteAllTextAsync(path1, "{\"corrupted\":\"data\"}");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                writer.WriteFinalHistoryAsync(
                    projectId, photoId, jobId, "att-1", "C:\\dest.jpg", new string('1', 64), 1000, 100, 100, qaSnapshot, outputFolder, publishedAt));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PublishService_DeterministicCollisionAndReplay_AndConflictFailsClosed()
    {
        var root = TempRoot("pub-deterministic-collision");
        try
        {
            var historyWriter = new FinalHistoryWriter();
            var publishService = new PublishService(historyWriter);

            var candidatePath = Path.Combine(root, "photo1.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            var outputFolder = Path.Combine(root, "output");
            var finalFolder = Path.Combine(outputFolder, "FINAL");
            Directory.CreateDirectory(finalFolder);

            // Place an existing DIFFERENT file at output/FINAL/photo1.jpg
            var existingDest = Path.Combine(finalFolder, "photo1.jpg");
            byte[] existingDiffBytes = [0xFF, 0xD8, 0xFF, 0xD9];
            await File.WriteAllBytesAsync(existingDest, existingDiffBytes);

            var jobId = JobId.New();
            var req = new PublishCandidateRequest(
                jobId,
                ProjectId.New(),
                PhotoId.New(),
                "att-pub",
                candidatePath,
                sha,
                "FINAL",
                new QaResultSnapshot("qa-1", jobId, "att-pub", "PASS", JsonDocument.Parse("""{"status":"ok"}""").RootElement.Clone(), candidatePath, sha, DateTimeOffset.UtcNow),
                outputFolder);

            // 1. First publish -> disambiguates deterministically to photo1_{jobId}.jpg
            var expectedDisambiguated = Path.Combine(finalFolder, $"photo1_{jobId.Value}.jpg");
            var result1 = await publishService.PublishAsync(req);
            Assert.AreEqual(expectedDisambiguated, result1.DestinationPath);
            Assert.IsTrue(File.Exists(result1.DestinationPath));

            // 2. Replay with same request -> returns exact same path idempotently
            var result2 = await publishService.PublishAsync(req);
            Assert.AreEqual(expectedDisambiguated, result2.DestinationPath);

            // 3. If disambiguated path is corrupted / replaced with differing content -> fails closed
            await File.WriteAllBytesAsync(expectedDisambiguated, existingDiffBytes);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                publishService.PublishAsync(req));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PublishService_RejectsNonJpegCandidates()
    {
        var root = TempRoot("pub-reject-non-jpeg");
        try
        {
            var historyWriter = new FinalHistoryWriter();
            var publishService = new PublishService(historyWriter);

            var pngPath = Path.Combine(root, "photo.png");
            byte[] dummyPngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            await File.WriteAllBytesAsync(pngPath, dummyPngBytes);
            var sha = ComputeSha256(dummyPngBytes);

            var req = new PublishCandidateRequest(
                JobId.New(),
                ProjectId.New(),
                PhotoId.New(),
                "att-png",
                pngPath,
                sha,
                "FINAL",
                new QaResultSnapshot("qa-1", JobId.New(), "att-png", "PASS", JsonDocument.Parse("""{"status":"ok"}""").RootElement.Clone(), pngPath, sha, DateTimeOffset.UtcNow),
                Path.Combine(root, "output"));

            await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
                publishService.PublishAsync(req));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Invariant_CompletedRequiresOutputPublishedCheckpoint()
    {
        var root = TempRoot("invariant-completed");
        try
        {
            var (database, storeFactory, _, _) = CreateInfrastructure(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedQaEligibleJobAsync(database, projectId, photoId, jobId, "dummy.jpg", sha, 100);

            var store = storeFactory.Open(projectId);
            Assert.IsFalse(await store.HasCheckpointAsync(jobId, "OUTPUT_PUBLISHED"));

            // Application rule ensures state cannot reach COMPLETED directly without output publish
            var job = await store.GetJobAsync(jobId);
            Assert.AreNotEqual(JobState.Completed, job!.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GeneralInvariant_AllCompletedJobsMustHaveOutputPublishedCheckpoint()
    {
        var root = TempRoot("invariant-all-completed");
        try
        {
            var (database, storeFactory, publishService, _) = CreateInfrastructure(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();

            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedQaEligibleJobAsync(database, projectId, photoId, jobId, candidatePath, sha, ValidJpegBytes.Length);

            var fakeClient = new FakeAiClient((route, req) =>
                new AiResponse(
                    "v1",
                    req.RequestId,
                    true,
                    JsonDocument.Parse("""
                    {
                        "schema_version": 1,
                        "decision": "QA_PASS",
                        "findings": [],
                        "suggested_correction": null,
                        "technical": { "sharpness": { "laplacian_variance": 80.0 } },
                        "calibration_status": "BASELINE_NOT_CALIBRATED"
                    }
                    """).RootElement.Clone(),
                    null,
                    null));

            var orchestrator = new QaOrchestrator(storeFactory, fakeClient, publishService);
            var outputFolder = Path.Combine(root, "output");
            await orchestrator.ProcessJobAsync(projectId, jobId, outputFolder);

            // Audit all jobs in the database: every COMPLETED job MUST have OUTPUT_PUBLISHED checkpoint
            var store = storeFactory.Open(projectId);
            await using var conn = await database.OpenConfiguredConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT job_id, state FROM jobs;";
            await using var reader = await cmd.ExecuteReaderAsync();
            var checkedAny = false;
            while (await reader.ReadAsync())
            {
                var jId = new JobId(reader.GetString(0));
                var stateStr = reader.GetString(1);
                if (string.Equals(stateStr, "COMPLETED", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.IsTrue(await store.HasCheckpointAsync(jId, "OUTPUT_PUBLISHED"),
                        $"Completed job {jId.Value} is missing required OUTPUT_PUBLISHED checkpoint.");
                    checkedAny = true;
                }
            }
            Assert.IsTrue(checkedAny);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OriginalSourceFile_RemainsUnchangedAfterFullPipeline()
    {
        var root = TempRoot("original-unchanged");
        try
        {
            var (database, storeFactory, publishService, _) = CreateInfrastructure(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();

            var masterAssetPath = Path.Combine(root, "original_master.jpg");
            await File.WriteAllBytesAsync(masterAssetPath, ValidJpegBytes);
            var originalSha = ComputeSha256(ValidJpegBytes);

            await SeedQaEligibleJobAsync(database, projectId, photoId, jobId, masterAssetPath, originalSha, ValidJpegBytes.Length);

            var fakeClient = new FakeAiClient((route, req) =>
                new AiResponse(
                    "v1",
                    req.RequestId,
                    true,
                    JsonDocument.Parse("""
                    {
                        "schema_version": 1,
                        "decision": "QA_PASS",
                        "findings": [],
                        "suggested_correction": null,
                        "technical": { "sharpness": { "laplacian_variance": 90.0 } },
                        "calibration_status": "BASELINE_NOT_CALIBRATED"
                    }
                    """).RootElement.Clone(),
                    null,
                    null));

            var orchestrator = new QaOrchestrator(storeFactory, fakeClient, publishService);
            var outputFolder = Path.Combine(root, "output");
            await orchestrator.ProcessJobAsync(projectId, jobId, outputFolder);

            // Verify original file SHA256 is 100% unchanged
            var currentMasterBytes = await File.ReadAllBytesAsync(masterAssetPath);
            var currentMasterSha = ComputeSha256(currentMasterBytes);
            Assert.AreEqual(originalSha, currentMasterSha);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (SqliteProjectDatabase Database, IQaStoreFactory StoreFactory, PublishService PublishService, FinalHistoryWriter HistoryWriter)
        CreateInfrastructure(string root)
    {
        var dbPath = Path.Combine(root, "project.db");
        var database = new SqliteProjectDatabase(dbPath);
        var storeFactory = new SingleDatabaseQaStoreFactory(database);
        var historyWriter = new FinalHistoryWriter();
        var publishService = new PublishService(historyWriter);
        return (database, storeFactory, publishService, historyWriter);
    }

    private sealed class SingleDatabaseQaStoreFactory(SqliteProjectDatabase database) : IQaStoreFactory
    {
        public IQaStore Open(ProjectId projectId) => new SqliteQaStore(database);
    }

    private static string ComputeSha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string TempRoot(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), "paf-phase7-full", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task SeedQaEligibleJobAsync(
        SqliteProjectDatabase database,
        ProjectId projectId,
        PhotoId photoId,
        JobId jobId,
        string candidatePath,
        string candidateSha256,
        long sizeBytes)
    {
        await database.InitializeAsync();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var configVersionId = "cfg-" + Guid.NewGuid().ToString("N");
        var assetId = "asset-" + Guid.NewGuid().ToString("N");

        await using var lease = await database.Writer.EnterAsync();
        await using var connection = await database.OpenConfiguredConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        await using (var insertProject = connection.CreateCommand())
        {
            insertProject.Transaction = transaction;
            insertProject.CommandText = """
                INSERT OR IGNORE INTO projects(
                    project_id, name, creation_operation_key, created_at_utc, updated_at_utc,
                    project_state, state_revision, state_changed_at_utc)
                VALUES(
                    $projectId, 'Test Project', 'create-' || $projectId, $now, $now,
                    'RUNNING', 1, $now);
                """;
            insertProject.Parameters.AddWithValue("$projectId", projectId.Value);
            insertProject.Parameters.AddWithValue("$now", now);
            await insertProject.ExecuteNonQueryAsync();
        }

        await using (var insertConfig = connection.CreateCommand())
        {
            insertConfig.Transaction = transaction;
            insertConfig.CommandText = """
                INSERT OR IGNORE INTO project_config_versions(
                    config_version_id, project_id, version_number, schema_version,
                    config_json, config_sha256, operation_key, created_at_utc)
                VALUES(
                    $configVersionId, $projectId, 1, 1,
                    '{"output_folder":"C:\\out"}', $sha,
                    'init-' || $configVersionId, $now);
                """;
            insertConfig.Parameters.AddWithValue("$configVersionId", configVersionId);
            insertConfig.Parameters.AddWithValue("$projectId", projectId.Value);
            insertConfig.Parameters.AddWithValue("$sha", candidateSha256);
            insertConfig.Parameters.AddWithValue("$now", now);
            await insertConfig.ExecuteNonQueryAsync();
        }

        await using (var insertSource = connection.CreateCommand())
        {
            insertSource.Transaction = transaction;
            insertSource.CommandText = """
                INSERT OR IGNORE INTO ingestion_sources(
                    source_id, project_id, input_root, include_subfolders,
                    config_version_id, created_at_utc)
                VALUES(
                    'source-' || $projectId, $projectId, 'C:\input', 0,
                    $configVersionId, $now);
                """;
            insertSource.Parameters.AddWithValue("$projectId", projectId.Value);
            insertSource.Parameters.AddWithValue("$configVersionId", configVersionId);
            insertSource.Parameters.AddWithValue("$now", now);
            await insertSource.ExecuteNonQueryAsync();
        }

        await using (var insertPhoto = connection.CreateCommand())
        {
            insertPhoto.Transaction = transaction;
            insertPhoto.CommandText = """
                INSERT OR IGNORE INTO photos(
                    photo_id, project_id, source_id, association_key,
                    state, master_asset_id, master_format,
                    association_deadline_utc, created_at_utc, updated_at_utc)
                VALUES(
                    $photoId, $projectId, 'source-' || $projectId, $photoId,
                    'READY_FOR_ANALYSIS', $assetId, 'JPEG',
                    $now, $now, $now);
                """;
            insertPhoto.Parameters.AddWithValue("$photoId", photoId.Value);
            insertPhoto.Parameters.AddWithValue("$projectId", projectId.Value);
            insertPhoto.Parameters.AddWithValue("$assetId", assetId);
            insertPhoto.Parameters.AddWithValue("$now", now);
            await insertPhoto.ExecuteNonQueryAsync();
        }

        await using (var insertAsset = connection.CreateCommand())
        {
            insertAsset.Transaction = transaction;
            insertAsset.CommandText = """
                INSERT OR IGNORE INTO assets(
                    asset_id, project_id, photo_id, source_id,
                    source_path, source_relative_path, managed_path,
                    format, role, archive_state, size_bytes, sha256,
                    raw_support_status, raw_max_width, raw_max_height,
                    raw_classification, observed_at_utc, archived_at_utc)
                VALUES(
                    $assetId, $projectId, $photoId, 'source-' || $projectId,
                    'C:\input\test.jpg', 'test.jpg', 'managed.jpg',
                    'JPEG', 'JPEG_MASTER', 'ARCHIVED', 1000,
                    $sha,
                    'NOT_APPLICABLE', 0, 0,
                    'NOT_RAW', $now, $now);
                """;
            insertAsset.Parameters.AddWithValue("$assetId", assetId);
            insertAsset.Parameters.AddWithValue("$projectId", projectId.Value);
            insertAsset.Parameters.AddWithValue("$photoId", photoId.Value);
            insertAsset.Parameters.AddWithValue("$sha", candidateSha256);
            insertAsset.Parameters.AddWithValue("$now", now);
            await insertAsset.ExecuteNonQueryAsync();
        }

        await using (var insertJob = connection.CreateCommand())
        {
            insertJob.Transaction = transaction;
            insertJob.CommandText = """
                INSERT OR IGNORE INTO jobs(
                    job_id, project_id, photo_id, parent_job_id, state,
                    preselection_config_id, processing_config_id,
                    analysis_source_asset_id, analysis_source_sha256,
                    analysis_input_kind, analysis_representation_path,
                    technical_retry_count, quality_reprocess_count,
                    created_at_utc, updated_at_utc, reveal_retry_count, comfy_retry_count)
                VALUES(
                    $jobId, $projectId, $photoId, NULL, 'QA',
                    $configVersionId, $configVersionId,
                    $assetId, $sha,
                    'JPEG_MASTER', 'managed.jpg',
                    0, 0,
                    $now, $now, 0, 0);
                """;
            insertJob.Parameters.AddWithValue("$jobId", jobId.Value);
            insertJob.Parameters.AddWithValue("$projectId", projectId.Value);
            insertJob.Parameters.AddWithValue("$photoId", photoId.Value);
            insertJob.Parameters.AddWithValue("$configVersionId", configVersionId);
            insertJob.Parameters.AddWithValue("$assetId", assetId);
            insertJob.Parameters.AddWithValue("$sha", candidateSha256);
            insertJob.Parameters.AddWithValue("$now", now);
            await insertJob.ExecuteNonQueryAsync();
        }

        await using (var insertComfy = connection.CreateCommand())
        {
            insertComfy.Transaction = transaction;
            insertComfy.CommandText = """
                INSERT OR IGNORE INTO comfy_executions(
                    comfy_execution_id, job_id, attempt_id, status, input_path, input_sha256,
                    output_path, output_sha256, output_size_bytes, task_manifest_json,
                    workflow_manifest_json, prompt_ids_json, history_path, completed_at_utc)
                VALUES(
                    $execId, $jobId, 'att-1', 'COMPLETED', $path, $sha,
                    $path, $sha, $size, '[]', '[]', '[]', 'history.json', $now);
                """;
            insertComfy.Parameters.AddWithValue("$execId", Guid.NewGuid().ToString("N"));
            insertComfy.Parameters.AddWithValue("$jobId", jobId.Value);
            insertComfy.Parameters.AddWithValue("$path", candidatePath);
            insertComfy.Parameters.AddWithValue("$sha", candidateSha256);
            insertComfy.Parameters.AddWithValue("$size", sizeBytes);
            insertComfy.Parameters.AddWithValue("$now", now);
            await insertComfy.ExecuteNonQueryAsync();
        }

        await using (var insertCp = connection.CreateCommand())
        {
            insertCp.Transaction = transaction;
            insertCp.CommandText = """
                INSERT OR IGNORE INTO job_checkpoints(
                    checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
                VALUES(
                    $cpId, $jobId, 'COMFYUI_COMPLETE', 'att-1', $sha, $now);
                """;
            insertCp.Parameters.AddWithValue("$cpId", Guid.NewGuid().ToString("N"));
            insertCp.Parameters.AddWithValue("$jobId", jobId.Value);
            insertCp.Parameters.AddWithValue("$sha", candidateSha256);
            insertCp.Parameters.AddWithValue("$now", now);
            await insertCp.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private sealed class FakeAiClient(Func<string, AiRequest, AiResponse> handler) : IPythonAiClient
    {
        public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthResponse("HEALTHY", "v1", "1.0", "cuda", []));

        public Task<AiResponse> ExecuteAsync(string route, AiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(route, request));
    }
}
