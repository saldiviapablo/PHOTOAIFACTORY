using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Analysis;
using PhotoAIFactory.Domain.Ingestion;
using PhotoAIFactory.Infrastructure;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Analysis;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class Phase3AnalysisTests
{
    [TestMethod]
    public void InterruptedJob_CanResumeAtAnalysis()
    {
        Assert.IsTrue(JobStateMachine.CanTransition(JobState.Interrupted, JobState.Analyzing));
    }

    [TestMethod]
    public void UnbenchmarkedRejectedSuggestion_IsConservativelyReviewPre()
    {
        using var document = JsonDocument.Parse("""{"decision":"REJECTED_PRE","findings":[]}""");
        Assert.AreEqual(
            PreselectionDecision.ReviewPre,
            AnalysisPolicy.ValidateSuggestedDecision(document.RootElement, allowAutomaticReject: false));
    }

    [TestMethod]
    public void ApprovedSuggestion_RemainsApproved()
    {
        using var document = JsonDocument.Parse("""{"decision":"APPROVED","findings":[]}""");
        Assert.AreEqual(
            PreselectionDecision.Approved,
            AnalysisPolicy.ValidateSuggestedDecision(document.RootElement, allowAutomaticReject: false));
    }

    [TestMethod]
    public void InvalidPreselectionPayload_RoutesToReview()
    {
        using var document = JsonDocument.Parse("""{"unexpected":true}""");
        Assert.AreEqual(
            PreselectionDecision.ReviewPre,
            AnalysisPolicy.ValidateSuggestedDecision(document.RootElement, allowAutomaticReject: false));
    }

    [TestMethod]
    public void EmbeddingSimilarity_IsStableForNormalizedVectors()
    {
        Assert.AreEqual(1.0, EmbeddingSimilarity.CosineSimilarity([1, 0], [1, 0]), 1e-12);
        Assert.AreEqual(0.0, EmbeddingSimilarity.CosineSimilarity([1, 0], [0, 1]), 1e-12);
        Assert.AreEqual(-1.0, EmbeddingSimilarity.CosineSimilarity([1, 0], [-1, 0]), 1e-12);
    }

    [TestMethod]
    public async Task Migration004_IsAppliedAndIdempotent()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "PhotoAIFactory-Phase3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new SqliteProjectDatabase(Path.Combine(root, "project.db"));
            await database.InitializeAsync();
            await database.InitializeAsync();

            await using var connection = await database.OpenConfiguredConnectionAsync();
            await using var migration = connection.CreateCommand();
            migration.CommandText = "SELECT count(*) FROM schema_migrations WHERE version=4 AND name='analysis_preselection_queue';";
            Assert.AreEqual(1L, Convert.ToInt64(await migration.ExecuteScalarAsync()));

            foreach (var table in new[]
            {
                "jobs", "analysis_results", "model_executions",
                "preselection_results", "job_checkpoints", "queue_entries"
            })
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name;";
                command.Parameters.AddWithValue("$name", table);
                Assert.AreEqual(1L, Convert.ToInt64(await command.ExecuteScalarAsync()), table);
            }

            await using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.AreEqual("ok", Convert.ToString(await integrity.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StandardAndFull_ReplayWithoutDuplicateWorkerOrPersistence()
    {
        foreach (var mode in new[] { SemanticMode.Standard, SemanticMode.Full })
        {
            await VerifyReplayAsync(mode);
        }
    }

    private static async Task VerifyReplayAsync(SemanticMode mode)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "PhotoAIFactory-Phase3-Replay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new SqliteProjectDatabase(Path.Combine(root, "project.db"));
            await database.InitializeAsync();
            await SeedAnalysisInputAsync(database);

            var store = new SqliteAnalysisStore(database);
            var worker = new ReplayWorker();
            var orchestrator = new AnalysisOrchestrator(
                new ReplayInputResolver(),
                new ReplayStoreFactory(store),
                worker,
                new GpuResourceCoordinator(),
                TimeProvider.System,
                NullLogger<AnalysisOrchestrator>.Instance);

            var projectId = new ProjectId("replay-project");
            var photoId = new PhotoId("replay-photo");
            var first = await orchestrator.ProcessPhotoAsync(
                projectId, photoId, "config-v1", "config-v1", mode, true);
            var replay = await orchestrator.ProcessPhotoAsync(
                projectId, photoId, "config-v1", "config-v1", mode, true);

            Assert.AreEqual(first.Job.Id, replay.Job.Id, mode.ToString());
            Assert.AreEqual(first.Analysis.AnalysisId, replay.Analysis.AnalysisId, mode.ToString());
            Assert.AreEqual(first.Preselection.PreselectionId, replay.Preselection.PreselectionId, mode.ToString());
            Assert.AreEqual(1, worker.AnalyzeCalls, mode.ToString());
            Assert.AreEqual(1, worker.PreselectCalls, mode.ToString());
            Assert.AreEqual(mode.ToString().ToUpperInvariant(), worker.ObservedMode, mode.ToString());

            await using var connection = await database.OpenConfiguredConnectionAsync();
            foreach (var expected in new Dictionary<string, long>
            {
                ["analysis_results"] = 1,
                ["model_executions"] = 1,
                ["preselection_results"] = 1,
                ["job_checkpoints"] = 2,
                ["queue_entries"] = 1
            })
            {
                await using var count = connection.CreateCommand();
                count.CommandText = $"SELECT count(*) FROM {expected.Key};";
                Assert.AreEqual(expected.Value, Convert.ToInt64(await count.ExecuteScalarAsync()),
                    $"{mode}: {expected.Key}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SeedAnalysisInputAsync(SqliteProjectDatabase database)
    {
        await using var connection = await database.OpenConfiguredConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projects(
                project_id, name, creation_operation_key, created_at_utc, updated_at_utc,
                project_state, state_revision, state_changed_at_utc)
            VALUES('replay-project', 'Replay audit', 'create-replay', $now, $now, 'RUNNING', 1, $now);
            INSERT INTO project_config_versions(
                config_version_id, project_id, version_number, schema_version, config_json,
                config_sha256, operation_key, created_at_utc)
            VALUES('config-v1', 'replay-project', 1, 1, '{"x":1}', $sha, 'config-replay', $now);
            INSERT INTO ingestion_sources(
                source_id, project_id, input_root, include_subfolders, config_version_id, created_at_utc)
            VALUES('source-replay', 'replay-project', 'C:\\fixture', 0, 'config-v1', $now);
            INSERT INTO photos(
                photo_id, project_id, source_id, association_key, state, master_asset_id,
                master_format, association_deadline_utc, created_at_utc, updated_at_utc)
            VALUES('replay-photo', 'replay-project', 'source-replay', 'fixture',
                'READY_FOR_ANALYSIS', 'asset-replay', 'JPEG', $now, $now, $now);
            INSERT INTO assets(
                asset_id, project_id, photo_id, source_id, source_path, source_relative_path,
                managed_path, format, role, archive_state, size_bytes, sha256,
                raw_support_status, raw_max_width, raw_max_height, raw_classification,
                observed_at_utc, archived_at_utc)
            VALUES('asset-replay', 'replay-project', 'replay-photo', 'source-replay',
                'C:\\fixture\\source.jpg', 'source.jpg', 'C:\\fixture\\managed.jpg',
                'JPEG', 'JPEG_MASTER', 'ARCHIVED', 1, $sha, 'NOT_APPLICABLE', 0, 0,
                'NOT_RAW', $now, $now);
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$sha", new string('a', 64));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ReplayInputResolver : IAnalysisInputResolver
    {
        public Task<ResolvedAnalysisInput> ResolveAsync(
            ProjectId projectId, PhotoId photoId, JobId jobId, string attemptId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResolvedAnalysisInput(
                new AssetId("asset-replay"), new string('a', 64),
                AnalysisInputKind.JpegMaster, @"C:\fixture\managed.jpg", false));

        public Task EnsureRepresentationAsync(
            ProjectId projectId, JobId jobId, string attemptId, ResolvedAnalysisInput input,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ReplayStoreFactory(IAnalysisStore store) : IAnalysisStoreFactory
    {
        public IAnalysisStore Open(ProjectId projectId) => store;
    }

    private sealed class ReplayWorker : IPythonAiClient
    {
        public int AnalyzeCalls { get; private set; }
        public int PreselectCalls { get; private set; }
        public string? ObservedMode { get; private set; }

        public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthResponse("HEALTHY", "v1", "replay", null, []));

        public Task<AiResponse> ExecuteAsync(
            string route, AiRequest request, CancellationToken cancellationToken = default)
        {
            JsonElement result;
            if (route.EndsWith("/analyze", StringComparison.Ordinal))
            {
                AnalyzeCalls++;
                ObservedMode = request.Config.GetProperty("semantic_mode").GetString();
                result = JsonSerializer.SerializeToElement(new
                {
                    schema_version = 1,
                    technical = new { },
                    model_executions = new[]
                    {
                        new
                        {
                            model_id = "florence-2-large",
                            model_version = "4271c66b88cdbc05735372ec13b2360108de5317",
                            artifact_set_sha256 = new string('7', 64),
                            parameters = new { semantic_mode = ObservedMode },
                            timings = new { total_ms = 1.0 }
                        }
                    }
                }, ContractJson.Options);
            }
            else if (route.EndsWith("/preselect", StringComparison.Ordinal))
            {
                PreselectCalls++;
                result = JsonSerializer.SerializeToElement(
                    new { decision = "APPROVED", findings = Array.Empty<object>() },
                    ContractJson.Options);
            }
            else
            {
                result = JsonSerializer.SerializeToElement(new { }, ContractJson.Options);
            }

            return Task.FromResult(new AiResponse(
                "v1", request.RequestId, true, result, null,
                new Dictionary<string, double>()));
        }
    }
}
