using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Application.Backup;
using PhotoAIFactory.Application.Cleanup;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Application.Recovery;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.Storage;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Analysis;
using PhotoAIFactory.Domain.Ingestion;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Domain.Qa;
using PhotoAIFactory.Infrastructure;
using PhotoAIFactory.Infrastructure.Analysis;
using PhotoAIFactory.Infrastructure.Backup;
using PhotoAIFactory.Infrastructure.Cleanup;
using PhotoAIFactory.Infrastructure.Health;
using PhotoAIFactory.Infrastructure.Ingestion;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Analysis;
using PhotoAIFactory.Infrastructure.Persistence.Ingestion;
using PhotoAIFactory.Infrastructure.Persistence.Qa;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;
using PhotoAIFactory.Infrastructure.Qa;
using PhotoAIFactory.Infrastructure.Recovery;
using PhotoAIFactory.Infrastructure.Storage;
using PhotoAIFactory.Simulation.Tests.Simulation;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class Phase8RecoveryHardeningTests
{
    private static readonly byte[] ValidJpegBytes =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x60,
        0x00, 0x60, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x40, 0x00, 0x40, 0x03, 0x01, 0x22,
        0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01, 0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11,
        0x03, 0x11, 0x00, 0x3F, 0x00, 0xBF, 0x00, 0xFF, 0xD9
    ];

    private static string TempRoot(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), "paf-phase8-tests", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ComputeSha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static async Task<(SqliteProjectDatabase Db, SqliteProjectStore Store, IProjectStoreFactory StoreFactory, IQaStoreFactory QaFactory, PublishService PublishSvc, ProductionRecoveryCoordinator Recovery, TestAppPaths Paths)> CreateInfrastructureAsync(string root)
    {
        var dbPath = Path.Combine(root, "project.db");
        var database = new SqliteProjectDatabase(dbPath);
        await database.InitializeAsync();

        var paths = new TestAppPaths(root);
        var store = new SqliteProjectStore(database);
        var storeFactory = new SingleProjectStoreFactory(store);
        var qaFactory = new SingleDatabaseQaStoreFactory(database);
        var historyWriter = new FinalHistoryWriter();
        var publishSvc = new PublishService(historyWriter);
        var recovery = new ProductionRecoveryCoordinator(storeFactory, qaFactory, publishSvc);

        return (database, store, storeFactory, qaFactory, publishSvc, recovery, paths);
    }

    private sealed class SingleDatabaseQaStoreFactory(SqliteProjectDatabase database) : IQaStoreFactory
    {
        public IQaStore Open(ProjectId projectId) => new SqliteQaStore(database);
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string RootDirectory { get; } = root;
        public string ProjectsDirectory { get; } = Path.Combine(root, "projects");
        public string WorkDirectory { get; } = Path.Combine(root, "work");
        public string LogsDirectory { get; } = Path.Combine(root, "logs");
        public string ModelsDirectory { get; } = Path.Combine(root, "models");
        public string ComponentsDirectory { get; } = Path.Combine(root, "components");
        public string GetProjectDatabasePath(ProjectId projectId) => Path.Combine(ProjectsDirectory, projectId.Value, "project.db");
    }

    private sealed class FakeAiClient(Func<string, AiRequest, AiResponse> handler) : IPythonAiClient
    {
        public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthResponse("ok", "v1", "1.0", "cuda", []));

        public Task<AiResponse> ExecuteAsync(string route, AiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(route, request));
    }

    private static async Task SeedProjectAndJobAsync(
        SqliteProjectDatabase database,
        ProjectId projectId,
        PhotoId photoId,
        JobId jobId,
        JobState initialState,
        string candidatePath,
        string sha256)
    {
        var config = new ProjectConfigV1(
            "C:\\input",
            "C:\\output",
            includeSubfolders: true,
            RevealMode.DtAuto,
            preselectionEnabled: true,
            preselectionProfile: "default",
            SemanticMode.Standard,
            ComfyUiMode.Off,
            authorizedComfyUiTasks: [],
            presetProfiles: ["baseline"],
            exportFormat: "JPEG",
            exportQuality: 92,
            associationWindowSeconds: 30);
        var configVersion = ConfigVersion.Create(projectId, 1, config, "init-cfg", DateTimeOffset.UtcNow);
        var configVersionId = configVersion.Id;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var assetId = "asset-" + Guid.NewGuid().ToString("N");

        await using var connection = await database.OpenConfiguredConnectionAsync();

        await using (var insertProject = connection.CreateCommand())
        {
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
            insertConfig.CommandText = """
                INSERT OR IGNORE INTO project_config_versions(
                    config_version_id, project_id, version_number, schema_version,
                    config_json, config_sha256, operation_key, created_at_utc)
                VALUES(
                    $configVersionId, $projectId, 1, 1,
                    $cfgJson, $cfgSha,
                    'init-' || $configVersionId, $now);
                """;
            insertConfig.Parameters.AddWithValue("$configVersionId", configVersionId);
            insertConfig.Parameters.AddWithValue("$projectId", projectId.Value);
            insertConfig.Parameters.AddWithValue("$cfgJson", configVersion.CanonicalJson);
            insertConfig.Parameters.AddWithValue("$cfgSha", configVersion.Sha256);
            insertConfig.Parameters.AddWithValue("$now", now);
            await insertConfig.ExecuteNonQueryAsync();
        }

        await using (var insertSource = connection.CreateCommand())
        {
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
            insertAsset.CommandText = """
                INSERT OR IGNORE INTO assets(
                    asset_id, project_id, photo_id, source_id,
                    source_path, source_relative_path, managed_path,
                    format, role, archive_state, size_bytes, sha256,
                    raw_support_status, raw_max_width, raw_max_height,
                    raw_classification, observed_at_utc, archived_at_utc)
                VALUES(
                    $assetId, $projectId, $photoId, 'source-' || $projectId,
                    'C:\input\test.jpg', 'test.jpg', $path,
                    'JPEG', 'JPEG_MASTER', 'ARCHIVED', 1000,
                    $sha,
                    'NOT_APPLICABLE', 0, 0,
                    'NOT_RAW', $now, $now);
                """;
            insertAsset.Parameters.AddWithValue("$assetId", assetId);
            insertAsset.Parameters.AddWithValue("$projectId", projectId.Value);
            insertAsset.Parameters.AddWithValue("$photoId", photoId.Value);
            insertAsset.Parameters.AddWithValue("$sha", sha256);
            insertAsset.Parameters.AddWithValue("$path", candidatePath);
            insertAsset.Parameters.AddWithValue("$now", now);
            await insertAsset.ExecuteNonQueryAsync();
        }

        await using (var insertJob = connection.CreateCommand())
        {
            insertJob.CommandText = """
                INSERT OR IGNORE INTO jobs(
                    job_id, project_id, photo_id, parent_job_id, state,
                    preselection_config_id, processing_config_id,
                    analysis_source_asset_id, analysis_source_sha256,
                    analysis_input_kind, analysis_representation_path,
                    technical_retry_count, quality_reprocess_count,
                    created_at_utc, updated_at_utc, reveal_retry_count, comfy_retry_count)
                VALUES(
                    $jobId, $projectId, $photoId, NULL, $state,
                    $configVersionId, $configVersionId,
                    $assetId, $sha,
                    'JPEG_MASTER', $path,
                    0, 0,
                    $now, $now, 0, 0);
                """;
            insertJob.Parameters.AddWithValue("$jobId", jobId.Value);
            insertJob.Parameters.AddWithValue("$projectId", projectId.Value);
            insertJob.Parameters.AddWithValue("$photoId", photoId.Value);
            insertJob.Parameters.AddWithValue("$state", initialState.ToString().ToUpperInvariant());
            insertJob.Parameters.AddWithValue("$configVersionId", configVersionId);
            insertJob.Parameters.AddWithValue("$assetId", assetId);
            insertJob.Parameters.AddWithValue("$sha", sha256);
            insertJob.Parameters.AddWithValue("$path", candidatePath);
            insertJob.Parameters.AddWithValue("$now", now);
            await insertJob.ExecuteNonQueryAsync();
        }
    }

    // =========================================================================
    // 1. RECOVERY OF ALL DURABLE CHECKPOINTS TESTS
    // =========================================================================

    [TestMethod]
    public async Task Recovery_AllCheckpoints_AnalysisComplete_ResumesToAnalyzing()
    {
        var root = TempRoot("rec-analysis");
        try
        {
            var (database, _, _, _, _, recovery, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Analyzing, candidatePath, sha);

            // Insert ANALYSIS_COMPLETE checkpoint & analysis result
            await using var conn = await database.OpenConfiguredConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
                VALUES('cp-ana', $jId, 'ANALYSIS_COMPLETE', 'att-1', $sha, '2026-08-23T00:00:00Z');

                INSERT INTO analysis_results(analysis_id, job_id, schema_version, result_json, created_at_utc)
                VALUES('res-1', $jId, 1, '{"quality_score": 0.9}', '2026-08-23T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("$jId", jobId.Value);
            cmd.Parameters.AddWithValue("$sha", sha);
            await cmd.ExecuteNonQueryAsync();

            var report = await recovery.ReconcileAndRecoverProjectAsync(projectId, Path.Combine(root, "out"));
            Assert.AreEqual(JobState.Analyzing, report.JobResults[0].FinalState);
            Assert.AreEqual(JobRecoveryAction.ResumedToAnalyzing, report.JobResults[0].Action);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Recovery_AllCheckpoints_PreselectionComplete_ResumesToQueued()
    {
        var root = TempRoot("rec-preselection");
        try
        {
            var (database, _, _, _, _, recovery, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Analyzing, candidatePath, sha);

            await using var conn = await database.OpenConfiguredConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
                VALUES('cp-presel', $jId, 'PRESELECTION_COMPLETE', 'att-1', $sha, '2026-08-23T00:00:00Z');

                INSERT INTO preselection_results(preselection_id, job_id, decision, findings_json, created_at_utc)
                VALUES('dec-1', $jId, 'APPROVED', '{"sharpness": 0.9}', '2026-08-23T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("$jId", jobId.Value);
            cmd.Parameters.AddWithValue("$sha", sha);
            await cmd.ExecuteNonQueryAsync();

            var report = await recovery.ReconcileAndRecoverProjectAsync(projectId, Path.Combine(root, "out"));
            Assert.AreEqual(JobState.Queued, report.JobResults[0].FinalState);
            Assert.AreEqual(JobRecoveryAction.ResumedToQueued, report.JobResults[0].Action);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Recovery_AllCheckpoints_BasicRevealComplete_ResumesToProcessing()
    {
        var root = TempRoot("rec-reveal");
        try
        {
            var (database, _, _, _, _, recovery, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var candidatePath = Path.Combine(root, "reveal_out.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Processing, candidatePath, sha);

            await using var conn = await database.OpenConfiguredConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
                VALUES('cp-reveal', $jId, 'BASIC_REVEAL_COMPLETE', 'att-1', $sha, '2026-08-23T00:00:00Z');

                INSERT INTO outputs(output_id, job_id, attempt_id, stage, role, path, sha256, size_bytes, width, height, validated, permanent, created_at_utc)
                VALUES('out-1', $jId, 'att-1', 'BASIC_REVEAL', 'BASIC_REVEAL_STAGING', $path, $sha, 1000, 64, 64, 1, 0, '2026-08-23T00:00:00Z');

                INSERT INTO processing_passes(processing_pass_id, job_id, attempt_id, reveal_mode, input_asset_id, input_sha256, darktable_version, control_plan_json, output_id, history_path, completed_at_utc)
                VALUES('pp-1', $jId, 'att-1', 'DT_AUTO', (SELECT master_asset_id FROM photos WHERE photo_id = $pId), $sha, '4.8', '{"mode":"auto"}', 'out-1', 'history.json', '2026-08-23T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("$jId", jobId.Value);
            cmd.Parameters.AddWithValue("$pId", photoId.Value);
            cmd.Parameters.AddWithValue("$sha", sha);
            cmd.Parameters.AddWithValue("$path", candidatePath);
            await cmd.ExecuteNonQueryAsync();

            var report = await recovery.ReconcileAndRecoverProjectAsync(projectId, Path.Combine(root, "out"));
            Assert.AreEqual(JobState.Processing, report.JobResults[0].FinalState);
            Assert.AreEqual(JobRecoveryAction.ResumedToProcessing, report.JobResults[0].Action);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Recovery_AllCheckpoints_DarktablePass1Complete_ResumesToProcessing()
    {
        var root = TempRoot("rec-pass1");
        try
        {
            var (database, _, _, _, _, recovery, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var pass1Path = Path.Combine(root, "pass1.tiff");
            await File.WriteAllBytesAsync(pass1Path, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Processing, pass1Path, sha);

            await using var conn = await database.OpenConfiguredConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
                VALUES('cp-pass1', $jId, 'DARKTABLE_PASS1_COMPLETE', 'att-1', $sha, '2026-08-23T00:00:00Z');

                INSERT INTO feedback_passes(
                    feedback_pass_id, job_id, pass_number, attempt_id, input_asset_id, input_sha256, input_kind,
                    darktable_version, control_plan_json, image_path, image_sha256, image_size_bytes, image_width,
                    image_height, bits_per_sample, channels, xmp_path, xmp_sha256, history_path, completed_at_utc)
                VALUES(
                    'fpass-1', $jId, 1, 'att-1', (SELECT master_asset_id FROM photos WHERE photo_id = $pId), $sha, 'JPEG',
                    '4.8', '{"mode":"feedback"}', $path, $sha, 1000, 64, 64, 8, 3, 'xmp.xmp', $sha, 'history.json', '2026-08-23T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("$jId", jobId.Value);
            cmd.Parameters.AddWithValue("$pId", photoId.Value);
            cmd.Parameters.AddWithValue("$sha", sha);
            cmd.Parameters.AddWithValue("$path", pass1Path);
            await cmd.ExecuteNonQueryAsync();

            var report = await recovery.ReconcileAndRecoverProjectAsync(projectId, Path.Combine(root, "out"));
            Assert.AreEqual(JobState.Processing, report.JobResults[0].FinalState);
            Assert.AreEqual(JobRecoveryAction.ResumedToProcessing, report.JobResults[0].Action);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Recovery_AllCheckpoints_DarktablePass2Complete_ResumesToProcessing()
    {
        var root = TempRoot("rec-pass2");
        try
        {
            var (database, _, _, _, _, recovery, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var pass2Path = Path.Combine(root, "pass2.jpg");
            await File.WriteAllBytesAsync(pass2Path, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Processing, pass2Path, sha);

            await using var conn = await database.OpenConfiguredConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
                VALUES('cp-pass2', $jId, 'DARKTABLE_PASS2_COMPLETE', 'att-1', $sha, '2026-08-23T00:00:00Z');

                INSERT INTO feedback_passes(
                    feedback_pass_id, job_id, pass_number, attempt_id, input_asset_id, input_sha256, input_kind,
                    darktable_version, control_plan_json, image_path, image_sha256, image_size_bytes, image_width,
                    image_height, bits_per_sample, channels, xmp_path, xmp_sha256, history_path, completed_at_utc)
                VALUES(
                    'fpass-2', $jId, 2, 'att-1', (SELECT master_asset_id FROM photos WHERE photo_id = $pId), $sha, 'JPEG',
                    '4.8', '{"mode":"feedback"}', $path, $sha, 1000, 64, 64, 8, 3, 'xmp.xmp', $sha, 'history.json', '2026-08-23T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("$jId", jobId.Value);
            cmd.Parameters.AddWithValue("$pId", photoId.Value);
            cmd.Parameters.AddWithValue("$sha", sha);
            cmd.Parameters.AddWithValue("$path", pass2Path);
            await cmd.ExecuteNonQueryAsync();

            var report = await recovery.ReconcileAndRecoverProjectAsync(projectId, Path.Combine(root, "out"));
            Assert.AreEqual(JobState.Processing, report.JobResults[0].FinalState);
            Assert.AreEqual(JobRecoveryAction.ResumedToProcessing, report.JobResults[0].Action);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Recovery_AllCheckpoints_ComfyUiComplete_ResumesToQa()
    {
        var root = TempRoot("rec-comfy");
        try
        {
            var (database, _, _, _, _, recovery, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Processing, candidatePath, sha);

            await using var conn = await database.OpenConfiguredConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
                VALUES('cp-comfy', $jId, 'COMFYUI_COMPLETE', 'att-1', $sha, '2026-08-23T00:00:00Z');

                INSERT INTO comfy_executions(
                    comfy_execution_id, job_id, attempt_id, status, input_path, input_sha256,
                    output_path, output_sha256, output_size_bytes, task_manifest_json,
                    workflow_manifest_json, prompt_ids_json, history_path, completed_at_utc)
                VALUES(
                    'exec-comfy', $jId, 'att-1', 'COMPLETED', $path, $sha,
                    $path, $sha, 1000, '[]', '[]', '[]', 'history.json', '2026-08-23T00:00:00Z');
                """;
            cmd.Parameters.AddWithValue("$jId", jobId.Value);
            cmd.Parameters.AddWithValue("$sha", sha);
            cmd.Parameters.AddWithValue("$path", candidatePath);
            await cmd.ExecuteNonQueryAsync();

            var report = await recovery.ReconcileAndRecoverProjectAsync(projectId, Path.Combine(root, "output"));
            Assert.AreEqual(JobState.Qa, report.JobResults[0].FinalState);
            Assert.AreEqual(JobRecoveryAction.ResumedToQa, report.JobResults[0].Action);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    // =========================================================================
    // 2. HISTORY VALIDATION IN RECOVERY TESTS
    // =========================================================================

    [TestMethod]
    public async Task Recovery_ValidJpeg_MissingHistory_DoesNotCompleteJob()
    {
        var root = TempRoot("rec-missing-history");
        try
        {
            var (database, _, _, qaFactory, _, recovery, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var publishedPath = Path.Combine(root, "output", "FINAL", "photo.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(publishedPath)!);
            await File.WriteAllBytesAsync(publishedPath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Qa, publishedPath, sha);

            var qaStore = qaFactory.Open(projectId);
            var missingHistoryPath = Path.Combine(root, "missing_final_history.json");

            await qaStore.PersistPublicationAsync(new PersistPublicationRequest(
                "pub-1", jobId, "att-1", "FINAL", publishedPath, sha, ValidJpegBytes.Length, 64, 64, missingHistoryPath, DateTimeOffset.UtcNow));
            await qaStore.InsertCheckpointAsync(jobId, "OUTPUT_PUBLISHED", "att-1", sha, DateTimeOffset.UtcNow);

            var report = await recovery.ReconcileAndRecoverProjectAsync(projectId, Path.Combine(root, "output"));
            Assert.AreEqual(JobState.Interrupted, report.JobResults[0].FinalState);
            Assert.AreEqual(JobRecoveryAction.RolledBackCorruptCheckpoint, report.JobResults[0].Action);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Recovery_ValidJpeg_WrongHistoryJobId_DoesNotCompleteJob()
    {
        var root = TempRoot("rec-wrong-history");
        try
        {
            var (database, _, _, qaFactory, _, recovery, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var publishedPath = Path.Combine(root, "output", "FINAL", "photo.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(publishedPath)!);
            await File.WriteAllBytesAsync(publishedPath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Qa, publishedPath, sha);

            var historyPath = Path.Combine(root, "final_history.json");
            var invalidHistoryJson = JsonSerializer.Serialize(new
            {
                job_id = "wrong-job-id",
                publication = new { sha256 = sha }
            });
            await File.WriteAllTextAsync(historyPath, invalidHistoryJson);

            var qaStore = qaFactory.Open(projectId);
            await qaStore.PersistPublicationAsync(new PersistPublicationRequest(
                "pub-1", jobId, "att-1", "FINAL", publishedPath, sha, ValidJpegBytes.Length, 64, 64, historyPath, DateTimeOffset.UtcNow));
            await qaStore.InsertCheckpointAsync(jobId, "OUTPUT_PUBLISHED", "att-1", sha, DateTimeOffset.UtcNow);

            var report = await recovery.ReconcileAndRecoverProjectAsync(projectId, Path.Combine(root, "output"));
            Assert.AreEqual(JobState.Interrupted, report.JobResults[0].FinalState);
            Assert.AreEqual(JobRecoveryAction.RolledBackCorruptCheckpoint, report.JobResults[0].Action);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Recovery_ValidJpeg_AndValidHistory_CompletesJob()
    {
        var root = TempRoot("rec-valid-history");
        try
        {
            var (database, _, _, qaFactory, _, recovery, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var publishedPath = Path.Combine(root, "output", "FINAL", "photo.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(publishedPath)!);
            await File.WriteAllBytesAsync(publishedPath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Qa, publishedPath, sha);

            var historyPath = Path.Combine(root, "final_history.json");
            var validHistoryJson = JsonSerializer.Serialize(new
            {
                job_id = jobId.Value,
                publication = new { sha256 = sha }
            });
            await File.WriteAllTextAsync(historyPath, validHistoryJson);

            var qaStore = qaFactory.Open(projectId);
            await qaStore.PersistPublicationAsync(new PersistPublicationRequest(
                "pub-1", jobId, "att-1", "FINAL", publishedPath, sha, ValidJpegBytes.Length, 64, 64, historyPath, DateTimeOffset.UtcNow));
            await qaStore.InsertCheckpointAsync(jobId, "OUTPUT_PUBLISHED", "att-1", sha, DateTimeOffset.UtcNow);

            var report = await recovery.ReconcileAndRecoverProjectAsync(projectId, Path.Combine(root, "output"));
            Assert.AreEqual(JobState.Completed, report.JobResults[0].FinalState);
            Assert.AreEqual(JobRecoveryAction.CompletedFromOutputCheckpoint, report.JobResults[0].Action);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    // =========================================================================
    // 3. SAFE CLEANUP SERVICE SECURITY TESTS
    // =========================================================================

    [TestMethod]
    public async Task SafeCleanup_ArbitraryUserDirectory_Rejected()
    {
        var root = TempRoot("cleanup-arbitrary");
        try
        {
            var paths = new TestAppPaths(root);
            var cleanup = new SafeCleanupService(paths);
            var projectId = ProjectId.New();

            var arbitraryDir = Path.Combine(Path.GetTempPath(), "arbitrary-user-photos");
            Directory.CreateDirectory(arbitraryDir);

            var result = await cleanup.CleanupStaleTemporaryArtifactsAsync(projectId, arbitraryDir);
            Assert.AreEqual(0, result.TotalDeleted);
            Assert.IsTrue(result.Errors.Count > 0);
            Assert.IsTrue(result.Errors[0].Contains("Security rejection"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SafeCleanup_ProtectedDirectories_NeverDeleted()
    {
        var root = TempRoot("cleanup-protected");
        try
        {
            var paths = new TestAppPaths(root);
            var cleanup = new SafeCleanupService(paths);
            var projectId = ProjectId.New();

            var projectWorkDir = Path.Combine(paths.WorkDirectory, projectId.Value);
            Directory.CreateDirectory(projectWorkDir);

            var finalsDir = Path.Combine(projectWorkDir, "final");
            Directory.CreateDirectory(finalsDir);
            var finalFile = Path.Combine(finalsDir, "output.jpg");
            await File.WriteAllBytesAsync(finalFile, ValidJpegBytes);
            File.SetLastWriteTimeUtc(finalFile, DateTime.UtcNow.AddHours(-10));

            var result = await cleanup.CleanupStaleTemporaryArtifactsAsync(projectId, projectWorkDir);
            Assert.AreEqual(0, result.TotalDeleted);
            Assert.IsTrue(File.Exists(finalFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SafeCleanup_ValidStaleOwnedTemp_Deleted_AndRecentPreserved()
    {
        var root = TempRoot("cleanup-valid-temp");
        try
        {
            var paths = new TestAppPaths(root);
            var cleanup = new SafeCleanupService(paths);
            var projectId = ProjectId.New();

            var projectWorkDir = Path.Combine(paths.WorkDirectory, projectId.Value, "scratch");
            Directory.CreateDirectory(projectWorkDir);

            var staleFile = Path.Combine(projectWorkDir, "scratch_old.tmp");
            await File.WriteAllBytesAsync(staleFile, [1, 2, 3]);
            File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddHours(-5));

            var recentFile = Path.Combine(projectWorkDir, "scratch_recent.tmp");
            await File.WriteAllBytesAsync(recentFile, [4, 5, 6]);

            var result = await cleanup.CleanupStaleTemporaryArtifactsAsync(projectId, projectWorkDir, new CleanupOptions(TimeSpan.FromHours(1)));
            Assert.AreEqual(1, result.TotalDeleted);
            Assert.IsFalse(File.Exists(staleFile));
            Assert.IsTrue(File.Exists(recentFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // =========================================================================
    // 4. STORAGE PREFLIGHT INTEGRATION IN ORCHESTRATOR
    // =========================================================================

    [TestMethod]
    public async Task StoragePreflight_QaOrchestrator_InsufficientSpace_TransitionsToBlockedStorageAndDoesNotInvokeWorker()
    {
        var root = TempRoot("qa-storage-block");
        try
        {
            var (database, store, storeFactory, qaFactory, publishService, _, _) = await CreateInfrastructureAsync(root);
            var projectService = new ProjectService(store);
            var config = new ProjectConfigV1(
                Path.Combine(root, "in"),
                Path.Combine(root, "out"),
                includeSubfolders: true,
                RevealMode.DtAuto,
                preselectionEnabled: true,
                preselectionProfile: "default",
                SemanticMode.Standard,
                ComfyUiMode.Off,
                authorizedComfyUiTasks: [],
                presetProfiles: ["baseline"],
                exportFormat: "JPEG",
                exportQuality: 92,
                associationWindowSeconds: 30);

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Qa, candidatePath, sha);

            var lifecycle = new ProjectLifecycleService(storeFactory, new ScriptedProjectWorkStatus(), TimeProvider.System);

            var fakeInspector = new FakeStorageInspector(0);
            var preflight = new DefaultStoragePreflightService(fakeInspector);

            var pythonCalled = false;
            var fakeClient = new FakeAiClient((route, req) =>
            {
                pythonCalled = true;
                return new AiResponse("v1", req.RequestId, true, JsonDocument.Parse("{}").RootElement.Clone(), null, null);
            });

            var orchestrator = new QaOrchestrator(qaFactory, fakeClient, publishService, preflight, lifecycle);
            var result = await orchestrator.ProcessJobAsync(projectId, jobId, Path.Combine(root, "out"));

            Assert.IsFalse(result);
            Assert.IsFalse(pythonCalled);

            var projectWrapper = await store.GetAsync(projectId);
            Assert.AreEqual(ProjectState.BlockedStorage, projectWrapper!.Project.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    // =========================================================================
    // 5. RESTORE SAFETY & FAILURE SAFETY TESTS
    // =========================================================================

    [TestMethod]
    public async Task SqliteRestoreService_FutureOrIncompatibleSchema_Rejected()
    {
        var root = TempRoot("restore-future-schema");
        try
        {
            var (database, _, storeFactory, _, _, _, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var restoreService = new SqliteRestoreService();

            var futureDbPath = Path.Combine(root, "future_schema.db");
            var futureDb = new SqliteProjectDatabase(futureDbPath);
            await futureDb.InitializeAsync();

            var valid64Sha = new string('a', 64);
            await using (var conn = await futureDb.OpenConfiguredConnectionAsync())
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO schema_migrations(version, name, migration_sha256, applied_at_utc) VALUES(99, 'future_migration', $sha, '2026-08-23T00:00:00Z');";
                cmd.Parameters.AddWithValue("$sha", valid64Sha);
                await cmd.ExecuteNonQueryAsync();
            }

            SqliteConnection.ClearAllPools();

            var restoreRes = await restoreService.RestoreDatabaseAsync(projectId, futureDbPath, database.DatabasePath, root);
            Assert.IsFalse(restoreRes.Success);
            Assert.IsTrue(restoreRes.Error!.Contains("Unsupported or future schema version"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    // =========================================================================
    // 6. GPU / OOM RECOVERY POLICY TESTS
    // =========================================================================

    [TestMethod]
    public async Task GpuExecutionPolicy_OomSingleRetry_SucceedsOnSecondAttempt()
    {
        var coordinator = new GpuResourceCoordinator();
        var policy = new GpuExecutionPolicy(coordinator);

        var attempts = 0;
        var memoryReleased = false;

        var result = await policy.ExecuteWithGpuAsync(
            "ComfyUI",
            async () =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new OutOfMemoryException("CUDA out of memory");
                }
                return await Task.FromResult("SUCCESS_ON_RETRY");
            },
            releaseMemory: async () =>
            {
                memoryReleased = true;
                await Task.CompletedTask;
            });

        Assert.AreEqual(2, attempts);
        Assert.IsTrue(memoryReleased);
        Assert.AreEqual("SUCCESS_ON_RETRY", result);
        Assert.IsNull(coordinator.CurrentOwner);
    }

    [TestMethod]
    public async Task GpuExecutionPolicy_OomPersistent_FailsOnSecondAttemptWithoutSilentDegradation()
    {
        var coordinator = new GpuResourceCoordinator();
        var policy = new GpuExecutionPolicy(coordinator);

        var attempts = 0;
        var threwOom = false;

        try
        {
            await policy.ExecuteWithGpuAsync(
                "ComfyUI",
                async () =>
                {
                    attempts++;
                    throw new OutOfMemoryException("CUDA out of memory");
                });
        }
        catch (GpuOutOfMemoryException)
        {
            threwOom = true;
        }

        Assert.IsTrue(threwOom);
        Assert.AreEqual(2, attempts);
        Assert.IsNull(coordinator.CurrentOwner);
    }

    // =========================================================================
    // 7. PROJECT LIFECYCLE & STATE MACHINE ALIGNMENT TESTS
    // =========================================================================

    [TestMethod]
    public async Task ProjectLifecycle_PauseAndStopFromBlockedStorage_TransitionsCleanly()
    {
        var root = TempRoot("lifecycle-blocked-storage");
        try
        {
            var (database, store, storeFactory, _, _, _, _) = await CreateInfrastructureAsync(root);
            var projectService = new ProjectService(store);
            var config = new ProjectConfigV1(
                Path.Combine(root, "in"),
                Path.Combine(root, "out"),
                includeSubfolders: true,
                RevealMode.DtAuto,
                preselectionEnabled: true,
                preselectionProfile: "default",
                SemanticMode.Standard,
                ComfyUiMode.Off,
                authorizedComfyUiTasks: [],
                presetProfiles: ["baseline"],
                exportFormat: "JPEG",
                exportQuality: 92,
                associationWindowSeconds: 30);

            var createRes = await projectService.CreateProjectAsync("Lifecycle Blocked Test", config, "op-create", DateTimeOffset.UtcNow);
            var projectId = createRes.Project.Id;
            var lifecycle = new ProjectLifecycleService(storeFactory, new ScriptedProjectWorkStatus(), TimeProvider.System);

            await lifecycle.StartOrResumeAsync(projectId, "op-start");
            await lifecycle.EnterBlockedStorageAsync(projectId, "op-block");

            // Pause while in BlockedStorage
            var pauseRes = await lifecycle.RequestPauseAsync(projectId, "op-pause");
            Assert.AreEqual(LifecycleResultStatus.Transitioned, pauseRes.Status);
            Assert.AreEqual(ProjectState.Paused, pauseRes.Project!.State);

            // Resume back to Running
            var resumeRes = await lifecycle.StartOrResumeAsync(projectId, "op-resume");
            Assert.AreEqual(ProjectState.Running, resumeRes.Project!.State);

            // Block again and Stop while in BlockedStorage
            await lifecycle.EnterBlockedStorageAsync(projectId, "op-block-2");
            var stopRes = await lifecycle.RequestStopAsync(projectId, "op-stop");
            Assert.AreEqual(LifecycleResultStatus.Transitioned, stopRes.Status);
            Assert.AreEqual(ProjectState.Stopped, stopRes.Project!.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProjectLifecycle_PauseAndStopFromComponentUnhealthy_TransitionsCleanly()
    {
        var root = TempRoot("lifecycle-unhealthy");
        try
        {
            var (database, store, storeFactory, _, _, _, _) = await CreateInfrastructureAsync(root);
            var projectService = new ProjectService(store);
            var config = new ProjectConfigV1(
                Path.Combine(root, "in"),
                Path.Combine(root, "out"),
                includeSubfolders: true,
                RevealMode.DtAuto,
                preselectionEnabled: true,
                preselectionProfile: "default",
                SemanticMode.Standard,
                ComfyUiMode.Off,
                authorizedComfyUiTasks: [],
                presetProfiles: ["baseline"],
                exportFormat: "JPEG",
                exportQuality: 92,
                associationWindowSeconds: 30);

            var createRes = await projectService.CreateProjectAsync("Lifecycle Unhealthy Test", config, "op-create", DateTimeOffset.UtcNow);
            var projectId = createRes.Project.Id;
            var lifecycle = new ProjectLifecycleService(storeFactory, new ScriptedProjectWorkStatus(), TimeProvider.System);

            await lifecycle.StartOrResumeAsync(projectId, "op-start");
            await lifecycle.EnterComponentUnhealthyAsync(projectId, "PythonWorker", "op-unhealthy");

            // Pause while in ComponentUnhealthy
            var pauseRes = await lifecycle.RequestPauseAsync(projectId, "op-pause");
            Assert.AreEqual(LifecycleResultStatus.Transitioned, pauseRes.Status);
            Assert.AreEqual(ProjectState.Paused, pauseRes.Project!.State);

            // Resume
            await lifecycle.StartOrResumeAsync(projectId, "op-resume");
            await lifecycle.EnterComponentUnhealthyAsync(projectId, "PythonWorker", "op-unhealthy-2");

            // Stop while in ComponentUnhealthy
            var stopRes = await lifecycle.RequestStopAsync(projectId, "op-stop");
            Assert.AreEqual(LifecycleResultStatus.Transitioned, stopRes.Status);
            Assert.AreEqual(ProjectState.Stopped, stopRes.Project!.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    // =========================================================================
    // 7. ORCHESTRATOR INTEGRATION TESTS (PREFLIGHT, GPU POLICY, HEALTH GATES)
    // =========================================================================

    [TestMethod]
    public async Task StoragePreflight_Ingestion_InsufficientSpace_DoesNotArchiveAndTransitionsToBlockedStorage()
    {
        var root = TempRoot("ingest-preflight");
        try
        {
            var (database, store, storeFactory, _, _, _, _) = await CreateInfrastructureAsync(root);
            var projectService = new ProjectService(store);
            var config = new ProjectConfigV1(
                Path.Combine(root, "in"),
                Path.Combine(root, "out"),
                includeSubfolders: true,
                RevealMode.DtAuto,
                preselectionEnabled: true,
                preselectionProfile: "default",
                SemanticMode.Standard,
                ComfyUiMode.Off,
                authorizedComfyUiTasks: [],
                presetProfiles: ["baseline"],
                exportFormat: "JPEG",
                exportQuality: 92,
                associationWindowSeconds: 30);

            var createRes = await projectService.CreateProjectAsync("Ingest Storage Test", config, "op-create", DateTimeOffset.UtcNow);
            var projectId = createRes.Project.Id;

            var lifecycle = new ProjectLifecycleService(storeFactory, new ScriptedProjectWorkStatus(), TimeProvider.System);
            await lifecycle.StartOrResumeAsync(projectId, "op-start");

            var fakeInspector = new FakeStorageInspector(0); // 0 bytes free
            var preflight = new DefaultStoragePreflightService(fakeInspector);

            var archiveCalled = false;
            var fakeArchive = new FakeArchive(() => archiveCalled = true);
            var fakeStability = new FakeStability();
            var fakeClassifier = new FakeRawClassifier();
            var ingestStore = new SqliteIngestionStore(database);
            var sourceSnapshot = new IngestionSourceSnapshot(IngestionSourceId.New(), projectId, "cfg-1", false, root, DateTimeOffset.UtcNow, null);

            var coordinator = new IngestionCoordinator(
                config,
                sourceSnapshot,
                ingestStore,
                fakeStability,
                fakeArchive,
                fakeClassifier,
                TimeProvider.System,
                storagePreflight: preflight,
                lifecycleService: lifecycle);

            var inDir = Path.Combine(root, "in");
            Directory.CreateDirectory(inDir);
            var sampleFile = Path.Combine(inDir, "sample.jpg");
            await File.WriteAllBytesAsync(sampleFile, ValidJpegBytes);

            var result = await coordinator.IngestPathAsync(sampleFile, TimeSpan.Zero, TimeSpan.FromSeconds(5));

            Assert.IsNull(result);
            Assert.IsFalse(archiveCalled, "Archive copy must not be initiated when storage preflight fails.");

            var projectWrapper = await store.GetAsync(projectId);
            Assert.AreEqual(ProjectState.BlockedStorage, projectWrapper!.Project.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GpuPolicy_AnalysisOrchestrator_SingleOom_RetriesAndSucceeds()
    {
        var root = TempRoot("gpu-policy-analysis-single");
        try
        {
            var (database, _, _, _, _, _, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Received, candidatePath, sha);

            var gpu = new GpuResourceCoordinator();
            var gpuPolicy = new GpuExecutionPolicy(gpu);
            var analyzeCallCount = 0;

            var fakeClient = new FakeAiClient((route, req) =>
            {
                if (route == "/v1/analyze")
                {
                    analyzeCallCount++;
                    if (analyzeCallCount == 1)
                    {
                        throw new GpuOutOfMemoryException("Analysis", "CUDA out of memory in analysis test");
                    }
                    return new AiResponse(
                        "v1",
                        req.RequestId,
                        true,
                        JsonDocument.Parse("""
                        {
                            "schema_version": 1,
                            "technical": {
                                "sharpness": 0.95,
                                "quality_score": 0.9
                            },
                            "model_executions": []
                        }
                        """).RootElement.Clone(),
                        null,
                        null);
                }

                if (route == "/v1/preselect")
                {
                    return new AiResponse(
                        "v1",
                        req.RequestId,
                        true,
                        JsonDocument.Parse("""
                        {
                            "suggested_decision": "Approved",
                            "findings": []
                        }
                        """).RootElement.Clone(),
                        null,
                        null);
                }

                return new AiResponse("v1", req.RequestId, true, JsonDocument.Parse("{}").RootElement.Clone(), null, null);
            });

            var resolver = new FakeInputResolver(candidatePath, sha);
            var analysisStoreFactory = new SingleAnalysisStoreFactory(database);

            var orchestrator = new AnalysisOrchestrator(
                resolver,
                analysisStoreFactory,
                fakeClient,
                gpu,
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalysisOrchestrator>.Instance,
                gpuPolicy: gpuPolicy);

            var result = await orchestrator.ProcessPhotoAsync(
                projectId, photoId, "cfg-1", "cfg-1", SemanticMode.Standard, preselectionEnabled: true);

            Assert.AreEqual(2, analyzeCallCount, "Should retry exactly once after first OOM.");
            Assert.IsNull(gpu.CurrentOwner, "Residual GPU lease must be null (0 residual leases).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task GpuPolicy_AnalysisOrchestrator_DoubleOom_FailsCleanlyWithZeroResidualLease()
    {
        var root = TempRoot("gpu-policy-analysis-double");
        try
        {
            var (database, _, _, _, _, _, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Received, candidatePath, sha);

            var gpu = new GpuResourceCoordinator();
            var gpuPolicy = new GpuExecutionPolicy(gpu);
            var analyzeCallCount = 0;

            var fakeClient = new FakeAiClient((route, req) =>
            {
                if (route == "/v1/analyze")
                {
                    analyzeCallCount++;
                    throw new GpuOutOfMemoryException("Analysis", "Persistent CUDA OOM");
                }

                return new AiResponse("v1", req.RequestId, true, JsonDocument.Parse("{}").RootElement.Clone(), null, null);
            });

            var resolver = new FakeInputResolver(candidatePath, sha);
            var analysisStoreFactory = new SingleAnalysisStoreFactory(database);

            var orchestrator = new AnalysisOrchestrator(
                resolver,
                analysisStoreFactory,
                fakeClient,
                gpu,
                TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AnalysisOrchestrator>.Instance,
                gpuPolicy: gpuPolicy);

            var threw = false;
            try
            {
                await orchestrator.ProcessPhotoAsync(
                    projectId, photoId, "cfg-1", "cfg-1", SemanticMode.Standard, preselectionEnabled: true);
            }
            catch (GpuOutOfMemoryException)
            {
                threw = true;
            }

            Assert.IsTrue(threw, "Must throw GpuOutOfMemoryException on persistent second OOM.");
            Assert.AreEqual(2, analyzeCallCount, "Must not attempt more than 2 calls.");
            Assert.IsNull(gpu.CurrentOwner, "Residual GPU lease must be null (0 residual leases).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ComponentHealth_DependencyGate_UnhealthyPythonBlocksAnalysisAndQa_AllowsReveal()
    {
        var tracker = new ComponentHealthTracker();

        // 3 failures open circuit on PythonWorker
        tracker.RecordFailure("PythonWorker", "Crash 1");
        tracker.RecordFailure("PythonWorker", "Crash 2");
        tracker.RecordFailure("PythonWorker", "Crash 3");

        Assert.IsTrue(tracker.IsStageBlocked("PythonWorker"), "PythonWorker stage must be blocked.");
        Assert.IsFalse(tracker.IsStageBlocked("Darktable"), "Darktable must not be blocked when PythonWorker is unhealthy.");
        Assert.IsFalse(tracker.IsStageBlocked("ComfyUI"), "ComfyUI must not be blocked when PythonWorker is unhealthy.");
    }

    [TestMethod]
    public void ComponentHealth_DependencyGate_UnhealthyDarktableBlocksRevealAndFeedback_AllowsComfy()
    {
        var tracker = new ComponentHealthTracker();

        // 3 failures open circuit on Darktable
        tracker.RecordFailure("Darktable", "Crash 1");
        tracker.RecordFailure("Darktable", "Crash 2");
        tracker.RecordFailure("Darktable", "Crash 3");

        Assert.IsTrue(tracker.IsStageBlocked("Darktable"), "Darktable stage must be blocked.");
        Assert.IsFalse(tracker.IsStageBlocked("PythonWorker"), "PythonWorker must not be blocked when Darktable is unhealthy.");
        Assert.IsFalse(tracker.IsStageBlocked("ComfyUI"), "ComfyUI must not be blocked when Darktable is unhealthy.");
    }

    [TestMethod]
    public void ComponentHealth_BoundedRestarts_PythonSupervisorRejectsThirdRestartAndOpensCircuit()
    {
        var tracker = new ComponentHealthTracker();

        Assert.IsTrue(tracker.TryRequestRestart("PythonWorker", out var r1));
        Assert.AreEqual(1, r1);
        Assert.IsFalse(tracker.IsStageBlocked("PythonWorker"));

        Assert.IsTrue(tracker.TryRequestRestart("PythonWorker", out var r2));
        Assert.AreEqual(2, r2);
        Assert.IsFalse(tracker.IsStageBlocked("PythonWorker"));

        Assert.IsFalse(tracker.TryRequestRestart("PythonWorker", out var r3), "Third restart request must be rejected (budget = 2 restarts).");
        Assert.AreEqual(2, r3);
        Assert.IsTrue(tracker.IsStageBlocked("PythonWorker"), "Stage must be blocked when restart budget is exhausted.");
    }

    [TestMethod]
    public void ComponentHealth_BoundedRestarts_ComfySupervisorRejectsThirdRestartAndOpensCircuit()
    {
        var tracker = new ComponentHealthTracker();

        Assert.IsTrue(tracker.TryRequestRestart("ComfyUI", out var r1));
        Assert.AreEqual(1, r1);
        Assert.IsFalse(tracker.IsStageBlocked("ComfyUI"));

        Assert.IsTrue(tracker.TryRequestRestart("ComfyUI", out var r2));
        Assert.AreEqual(2, r2);
        Assert.IsFalse(tracker.IsStageBlocked("ComfyUI"));

        Assert.IsFalse(tracker.TryRequestRestart("ComfyUI", out var r3), "Third restart request must be rejected.");
        Assert.AreEqual(2, r3);
        Assert.IsTrue(tracker.IsStageBlocked("ComfyUI"), "ComfyUI stage must be blocked when restart budget is exhausted.");
    }

    [TestMethod]
    public async Task StoragePreflight_Qa_InsufficientSpace_DoesNotPublishAndEntersBlockedStorage()
    {
        var root = TempRoot("qa-preflight-blocked");
        try
        {
            var (database, store, storeFactory, qaFactory, publishSvc, _, _) = await CreateInfrastructureAsync(root);
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var candidatePath = Path.Combine(root, "candidate.jpg");
            await File.WriteAllBytesAsync(candidatePath, ValidJpegBytes);
            var sha = ComputeSha256(ValidJpegBytes);

            var projectService = new ProjectService(store);
            var config = new ProjectConfigV1(
                Path.Combine(root, "in"),
                Path.Combine(root, "out"),
                includeSubfolders: true,
                RevealMode.DtAuto,
                preselectionEnabled: true,
                preselectionProfile: "default",
                SemanticMode.Standard,
                ComfyUiMode.Off,
                authorizedComfyUiTasks: [],
                presetProfiles: ["baseline"],
                exportFormat: "JPEG",
                exportQuality: 92,
                associationWindowSeconds: 30);

            await SeedProjectAndJobAsync(database, projectId, photoId, jobId, JobState.Qa, candidatePath, sha);

            var lifecycle = new ProjectLifecycleService(storeFactory, new ScriptedProjectWorkStatus(), TimeProvider.System);
            await lifecycle.StartOrResumeAsync(projectId, "op-start");

            var fakeInspector = new FakeStorageInspector(0); // 0 bytes
            var preflight = new DefaultStoragePreflightService(fakeInspector);

            var fakeClient = new FakeAiClient((route, req) => new AiResponse("v1", req.RequestId, true, null, null, null));
            var qaOrchestrator = new QaOrchestrator(qaFactory, fakeClient, publishSvc, preflight, lifecycle);

            var processed = await qaOrchestrator.ProcessJobAsync(projectId, jobId, config.OutputFolder);

            Assert.IsFalse(processed, "QA process must not proceed when storage preflight is insufficient.");
            var projectWrapper = await store.GetAsync(projectId);
            Assert.AreEqual(ProjectState.BlockedStorage, projectWrapper!.Project.State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ComponentHealth_Recovery_SuccessfulProbeClosesCircuit()
    {
        var tracker = new ComponentHealthTracker();

        tracker.RecordFailure("PythonWorker", "Crash 1");
        tracker.RecordFailure("PythonWorker", "Crash 2");
        tracker.RecordFailure("PythonWorker", "Crash 3");

        Assert.IsTrue(tracker.IsStageBlocked("PythonWorker"));

        // Probe succeeds -> recovers circuit
        tracker.RecordSuccess("PythonWorker");

        Assert.IsFalse(tracker.IsStageBlocked("PythonWorker"), "Successful health probe must close circuit and unblock stage.");
    }

    // =========================================================================
    // FAKE INFRASTRUCTURE CLASSES FOR TESTS
    // =========================================================================

    private sealed class FakeArchive(Action onArchive) : IManagedOriginalArchive
    {
        public Task<ArchivedOriginal> ArchiveAsync(
            string sourcePath,
            string outputRootFolder,
            AssetFormat format,
            long expectedSize,
            string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            onArchive();
            return Task.FromResult(new ArchivedOriginal(
                Path.Combine(outputRootFolder, "originals", Path.GetFileName(sourcePath)),
                expectedSize,
                expectedSha256,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeStability : IFileStabilityProbe
    {
        public Task<StableFileInfo> WaitUntilStableAsync(
            string path,
            TimeSpan stableFor,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var info = new FileInfo(path);
            return Task.FromResult(new StableFileInfo(
                path,
                info.Exists ? info.Length : 1000,
                info.Exists ? info.LastWriteTimeUtc : DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeRawClassifier : IRawSupportClassifier
    {
        public Task<RawSupportInfo> ClassifyAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(RawSupportInfo.NotApplicable);
    }

    private sealed class FakeInputResolver(string repPath, string sha) : IAnalysisInputResolver
    {
        public Task<ResolvedAnalysisInput> ResolveAsync(
            ProjectId projectId,
            PhotoId photoId,
            JobId jobId,
            string attemptId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ResolvedAnalysisInput(
                AssetId.New(),
                sha,
                AnalysisInputKind.JpegCamera,
                repPath,
                false));

        public Task EnsureRepresentationAsync(
            ProjectId projectId,
            JobId jobId,
            string attemptId,
            ResolvedAnalysisInput input,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SingleAnalysisStoreFactory(SqliteProjectDatabase database) : IAnalysisStoreFactory
    {
        public IAnalysisStore Open(ProjectId projectId) => new SqliteAnalysisStore(database);
    }

    private sealed class FakeStorageInspector(long freeBytes) : IStorageSpaceInspector
    {
        public long GetAvailableFreeSpaceBytes(string path) => freeBytes;
    }
}
