using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Infrastructure;
using PhotoAIFactory.Infrastructure.Analysis;
using PhotoAIFactory.Infrastructure.Hosting;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Processing;
using PhotoAIFactory.Infrastructure.Processing;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class Phase5FeedbackRegressionTests
{
    [TestMethod]
    public void FeedbackRecipePolicy_RejectsUnapprovedDisabledReason()
    {
        var recipe = ConservativeRecipe();
        var altered = JsonSerializer.SerializeToElement(new
        {
            schema_version = 1,
            recipe_version = "phase5-feedback-v1",
            strategy = "CONSERVATIVE_REUSE_PASS1",
            benchmark_status = "NOT_CALIBRATED",
            operations = Array.Empty<object>(),
            pass2_control = recipe.GetProperty("pass2_control"),
            darktable_ai = new
            {
                raw_denoise = new { enabled = false, reason = "UNREVIEWED" },
                rgb_denoise = new
                {
                    enabled = false,
                    reason = FeedbackRecipePolicy.NeuralRestoreDisabledReason
                },
                upscale = new
                {
                    enabled = false,
                    reason = FeedbackRecipePolicy.NeuralRestoreDisabledReason
                }
            }
        });

        Assert.ThrowsExactly<InvalidDataException>(
            () => FeedbackRecipePolicy.Validate(altered));
    }

    [TestMethod]
    public async Task Migration006_RecordsChecksumPragmasAndImmutableSchema()
    {
        var root = TempRoot("migration-contract");
        try
        {
            var database = new SqliteProjectDatabase(
                Path.Combine(root, "project.db"));
            await database.InitializeAsync();
            await using var connection =
                await database.OpenConfiguredConnectionAsync();

            Assert.AreEqual(
                MigrationCatalog.All[5].Sha256,
                await ScalarStringAsync(
                    connection,
                    "SELECT migration_sha256 FROM schema_migrations WHERE version=6;"));
            Assert.AreEqual(
                "wal",
                (await ScalarStringAsync(
                    connection, "PRAGMA journal_mode;")).ToLowerInvariant());
            Assert.AreEqual(2L, await ScalarLongAsync(
                connection, "PRAGMA synchronous;"));
            Assert.AreEqual(1L, await ScalarLongAsync(
                connection, "PRAGMA foreign_keys;"));
            Assert.AreEqual("ok", await ScalarStringAsync(
                connection, "PRAGMA integrity_check;"));
            Assert.AreEqual(4L, await ScalarLongAsync(
                connection,
                """
                SELECT count(*)
                FROM sqlite_master
                WHERE type='trigger'
                  AND name IN (
                    'feedback_passes_no_update',
                    'feedback_passes_no_delete',
                    'feedback_inspections_no_update',
                    'feedback_inspections_no_delete');
                """));
            Assert.AreEqual(0L, await ScalarLongAsync(
                connection,
                """
                SELECT count(*) FROM job_checkpoints
                WHERE stage_name='RAW_DENOISE_COMPLETE';
                """));

            var passSql = await ScalarStringAsync(
                connection,
                """
                SELECT sql FROM sqlite_master
                WHERE type='table' AND name='feedback_passes';
                """);
            StringAssert.Contains(passSql, "UNIQUE(job_id, pass_number)");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Migration006_HashDriftIsRejected()
    {
        var root = TempRoot("migration-drift");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();
            await using (var connection =
                await database.OpenConfiguredConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE schema_migrations SET migration_sha256=$sha WHERE version=6;";
                command.Parameters.AddWithValue("$sha", new string('0', 64));
                await command.ExecuteNonQueryAsync();
            }

            await ThrowsAsync<MigrationIntegrityException>(
                () => new SqliteProjectDatabase(path).InitializeAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Migration006_FailureRollsBack()
    {
        var root = TempRoot("migration-rollback");
        try
        {
            var path = Path.Combine(root, "project.db");
            var v5 = MigrationCatalog.All.Take(5).ToArray();
            await new SqliteProjectDatabase(path, v5).InitializeAsync();
            var failing = new SqliteMigration(
                6,
                "feedback",
                "CREATE TABLE phase5_rollback_probe(value TEXT); INVALID SQL;");

            await ThrowsAsync<SqliteException>(() =>
                new SqliteProjectDatabase(
                    path,
                    [.. v5, failing]).InitializeAsync());

            var phase4 = new SqliteProjectDatabase(path, v5);
            await using var connection =
                await phase4.OpenConfiguredConnectionAsync();
            Assert.AreEqual(5L, await ScalarLongAsync(
                connection, "SELECT max(version) FROM schema_migrations;"));
            Assert.AreEqual(0L, await ScalarLongAsync(
                connection,
                """
                SELECT count(*) FROM sqlite_master
                WHERE type='table' AND name='phase5_rollback_probe';
                """));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SqliteFeedbackStore_PersistsOrderedBoundariesAndReplays()
    {
        var root = TempRoot("store-boundaries");
        try
        {
            var input = Path.Combine(root, "managed.jpg");
            WriteMinimalJpeg(input);
            var sha = await HashAsync(input);
            var database = new SqliteProjectDatabase(
                Path.Combine(root, "project.db"));
            await database.InitializeAsync();
            await SeedQueuedFeedbackJobAsync(database, input, sha);
            var store = new SqliteFeedbackStore(database);
            var queued = await store.PeekNextQueuedAsync(
                new ProjectId("project"));
            Assert.IsNotNull(queued);
            Assert.IsTrue(await store.TryClaimAsync(
                queued.Id,
                "claim",
                DateTimeOffset.UtcNow));
            var job = await store.GetActiveAsync(new ProjectId("project"));
            Assert.IsNotNull(job);

            var pass1Artifact = new FeedbackImageArtifact(
                Path.Combine(root, "pass1.tif"),
                new string('b', 64),
                100,
                10,
                10,
                16,
                3,
                "darktable 5.6.0",
                TimeSpan.Zero,
                Encoding.UTF8.GetBytes(
                    "http://darktable.sf.net/ darktable:history"));
            var pass1Request = new FeedbackPersistPass1Request(
                job,
                "attempt-1",
                pass1Artifact,
                Path.Combine(root, "pass1.xmp"),
                new string('c', 64),
                JsonSerializer.SerializeToElement(new { pass = 1 }),
                DateTimeOffset.UtcNow);
            await store.PersistPass1CompleteAsync(pass1Request);
            await store.PersistPass1CompleteAsync(pass1Request);
            Assert.IsTrue(await store.HasCheckpointAsync(
                job.Id, "DARKTABLE_PASS1_COMPLETE"));
            Assert.AreEqual(1L, await DatabaseScalarAsync(
                database,
                "SELECT count(*) FROM feedback_passes WHERE pass_number=1;"));

            var inspectionRequest = new FeedbackPersistInspectionRequest(
                job,
                1,
                ConservativeRecipe(),
                new string('d', 64),
                JsonSerializer.SerializeToElement(new { technical = new { } }),
                DateTimeOffset.UtcNow);
            await store.PersistInspectionCompleteAsync(inspectionRequest);
            await store.PersistInspectionCompleteAsync(inspectionRequest);
            Assert.IsTrue(await store.HasCheckpointAsync(
                job.Id, "FEEDBACK_INSPECTION_COMPLETE"));
            Assert.AreEqual(0L, await DatabaseScalarAsync(
                database,
                """
                SELECT count(*) FROM job_checkpoints
                WHERE stage_name='RAW_DENOISE_COMPLETE';
                """));

            var pass2Artifact = new FeedbackImageArtifact(
                Path.Combine(root, "pass2.jpg"),
                new string('e', 64),
                80,
                10,
                10,
                8,
                3,
                "darktable 5.6.0",
                TimeSpan.Zero,
                Encoding.UTF8.GetBytes(
                    "http://darktable.sf.net/ darktable:history"));
            var pass2Request = new FeedbackPersistPass2Request(
                job,
                "attempt-2",
                pass2Artifact,
                Path.Combine(root, "pass2.xmp"),
                new string('f', 64),
                Path.Combine(root, "history.json"),
                JsonSerializer.SerializeToElement(new
                {
                    source = "MANAGED_JPEG_ORIGINAL",
                    pass1_derivative_as_source = false
                }),
                DateTimeOffset.UtcNow);
            await store.PersistPass2CompleteAsync(pass2Request);
            await store.PersistPass2CompleteAsync(pass2Request);

            Assert.IsTrue(await store.HasCheckpointAsync(
                job.Id, "DARKTABLE_PASS2_COMPLETE"));
            Assert.AreEqual(2L, await DatabaseScalarAsync(
                database, "SELECT count(*) FROM feedback_passes;"));
            Assert.AreEqual(1L, await DatabaseScalarAsync(
                database, "SELECT count(*) FROM feedback_inspections;"));
            Assert.AreEqual(0L, await DatabaseScalarAsync(
                database, "SELECT count(*) FROM queue_entries;"));
            Assert.AreEqual("QA", await DatabaseScalarStringAsync(
                database,
                "SELECT state FROM jobs WHERE job_id='job-feedback';"));
            Assert.AreEqual(0L, await DatabaseScalarAsync(
                database,
                """
                SELECT count(*) FROM job_checkpoints
                WHERE stage_name IN ('OUTPUT_PUBLISHED','RAW_DENOISE_COMPLETE');
                """));

            var reopened = new SqliteFeedbackStore(
                new SqliteProjectDatabase(database.DatabasePath));
            Assert.IsNotNull(await reopened.GetPassAsync(job.Id, 1));
            Assert.IsNotNull(await reopened.GetPassAsync(job.Id, 2));
            Assert.IsNotNull(await reopened.GetInspectionAsync(job.Id));

            await using var connection =
                await database.OpenConfiguredConnectionAsync();
            await using var immutable = connection.CreateCommand();
            immutable.CommandText =
                "UPDATE feedback_passes SET image_size_bytes=101 WHERE pass_number=1;";
            await ThrowsAsync<SqliteException>(
                () => immutable.ExecuteNonQueryAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CorruptManagedJpeg_IsPermanentBeforeDarktableStarts()
    {
        var root = TempRoot("corrupt-jpeg");
        try
        {
            var input = Path.Combine(root, "corrupt.jpg");
            await File.WriteAllBytesAsync(
                input, [0xff, 0xd8, 0x00, 0x01, 0xff, 0xd9]);
            var job = Job(input, await HashAsync(input), "JPEG", "NOT_APPLICABLE");
            var executor = Executor(root);

            var error = await ThrowsAsync<RevealStageException>(() =>
                executor.ExportPass1Async(
                    job.ProjectId,
                    job.Id,
                    "attempt",
                    job));

            Assert.AreEqual("FEEDBACK_JPEG_INPUT_INVALID", error.Code);
            Assert.IsFalse(error.Retryable);
            Assert.IsFalse(Directory.EnumerateFiles(
                root, "*.partial-*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Pass1Cleanup_RefusesPathOutsideAttemptWorkspace()
    {
        var root = TempRoot("cleanup-path");
        try
        {
            var outside = Path.Combine(root, "must-remain.tif");
            await File.WriteAllBytesAsync(outside, [1, 2, 3]);
            var pass = PassSnapshot(
                1,
                outside,
                "attempt",
                ConservativeRecipe());

            await ThrowsAsync<InvalidDataException>(() =>
                Executor(Path.Combine(root, "runtime"))
                    .CleanupPass1TemporaryAsync(pass));

            Assert.IsTrue(File.Exists(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Pass2Recovery_RefusesHistoryPathOutsideAttemptWorkspace()
    {
        var root = TempRoot("recovery-path");
        try
        {
            var input = Path.Combine(root, "managed.jpg");
            WriteMinimalJpeg(input);
            var job = Job(input, await HashAsync(input), "JPEG", "NOT_APPLICABLE");
            var outside = Path.Combine(root, "outside.jpg");
            WriteMinimalJpeg(outside);
            var artifact = new FeedbackImageArtifact(
                outside,
                await HashAsync(outside),
                new FileInfo(outside).Length,
                1,
                1,
                8,
                3,
                "darktable 5.6.0",
                TimeSpan.Zero,
                []);

            var error = await ThrowsAsync<RevealStageException>(() =>
                Executor(Path.Combine(root, "runtime"))
                    .RecoverPass2Async(
                        job,
                        new FeedbackPass2Recovery(
                            "attempt",
                            artifact,
                            Path.Combine(root, "outside.xmp"),
                            new string('0', 64))));

            Assert.AreEqual(
                "FEEDBACK_PASS2_RECOVERY_PATH_INVALID",
                error.Code);
            Assert.IsFalse(error.Retryable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CompletedPass2Recovery_AttemptsDeferredPass1Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        var project = Project.Create("Phase 5 recovery", now)
            .TransitionTo(ProjectState.Running, now.AddSeconds(1));
        var config = Config();
        var configVersion = ConfigVersion.Create(
            project.Id, 1, config, "config-op", now);
        var job = new FeedbackJobSnapshot(
            new JobId("job-recovery"),
            project.Id,
            new PhotoId("photo-recovery"),
            JobState.Processing,
            configVersion.Id,
            "asset-recovery",
            "managed.ARW",
            new string('a', 64),
            "RAW",
            "SUPPORTED_FULL_SIZE",
            0,
            1,
            false);
        var pass1 = PassSnapshot(
            1, "pass1.tif", "attempt-1", ConservativeRecipe(), job);
        var pass2 = PassSnapshot(
            2, "pass2.jpg", "attempt-2", ConservativeRecipe(), job);
        var inspection = new FeedbackInspectionSnapshot(
            "inspection",
            job.Id,
            1,
            new string('b', 64),
            ConservativeRecipe(),
            JsonSerializer.SerializeToElement(new { technical = new { } }),
            now);
        var store = new CompletedFeedbackStore(job, pass1, pass2, inspection);
        var executor = new CompletedFeedbackExecutor();
        var orchestrator = new FeedbackOrchestrator(
            new FakeFeedbackStoreFactory(store),
            new FakeProjectStoreFactory(
                new ProjectSnapshot(project, [configVersion])),
            new NeverPythonClient(),
            executor,
            new NeverHistoryWriter(),
            new RevealExecutionCoordinator(),
            TimeProvider.System,
            NullLogger<FeedbackOrchestrator>.Instance);

        var result = await orchestrator.ProcessNextAsync(project.Id);

        Assert.AreEqual(FeedbackWorkStatus.Completed, result.Status);
        Assert.AreEqual(1, executor.CleanupCalls);
        Assert.AreEqual(1, executor.ValidatePass1Calls);
    }

    [TestMethod]
    public async Task UnsupportedExportFormat_IsPermanentAndMarksJobError()
    {
        var now = DateTimeOffset.UtcNow;
        var project = Project.Create("Phase 5 invalid config", now)
            .TransitionTo(ProjectState.Running, now.AddSeconds(1));
        var configVersion = ConfigVersion.Create(
            project.Id, 1, Config("TIFF"), "config-op", now);
        var job = new FeedbackJobSnapshot(
            new JobId("job-invalid-export"),
            project.Id,
            new PhotoId("photo-invalid-export"),
            JobState.Processing,
            configVersion.Id,
            "asset",
            "managed.ARW",
            new string('a', 64),
            "RAW",
            "SUPPORTED_FULL_SIZE",
            0,
            1,
            false);
        var store = new CompletedFeedbackStore(
            job,
            PassSnapshot(1, "pass1.tif", "attempt-1", ConservativeRecipe(), job),
            PassSnapshot(2, "pass2.jpg", "attempt-2", ConservativeRecipe(), job),
            new FeedbackInspectionSnapshot(
                "inspection",
                job.Id,
                1,
                new string('b', 64),
                ConservativeRecipe(),
                JsonSerializer.SerializeToElement(new { technical = new { } }),
                now));
        var orchestrator = new FeedbackOrchestrator(
            new FakeFeedbackStoreFactory(store),
            new FakeProjectStoreFactory(
                new ProjectSnapshot(project, [configVersion])),
            new NeverPythonClient(),
            new CompletedFeedbackExecutor(),
            new NeverHistoryWriter(),
            new RevealExecutionCoordinator(),
            TimeProvider.System,
            NullLogger<FeedbackOrchestrator>.Instance);

        var error = await ThrowsAsync<RevealStageException>(
            () => orchestrator.ProcessNextAsync(project.Id));

        Assert.AreEqual("UNSUPPORTED_EXPORT_FORMAT", error.Code);
        Assert.IsFalse(error.Retryable);
        Assert.AreEqual(1, store.MarkErrorCalls);
    }

    [TestMethod]
    public async Task Pass1DatabaseFailure_RetriesTwiceThenStops()
    {
        var now = DateTimeOffset.UtcNow;
        var project = Project.Create("Phase 5 retry", now)
            .TransitionTo(ProjectState.Running, now.AddSeconds(1));
        var configVersion = ConfigVersion.Create(
            project.Id, 1, Config(), "config-op", now);
        var job = new FeedbackJobSnapshot(
            new JobId("job-db-retry"),
            project.Id,
            new PhotoId("photo-db-retry"),
            JobState.Processing,
            configVersion.Id,
            "asset",
            "managed.ARW",
            new string('a', 64),
            "RAW",
            "SUPPORTED_FULL_SIZE",
            0,
            1,
            false);
        var store = new FailingPass1Store(job);
        var executor = new Pass1OnlyExecutor();
        var orchestrator = new FeedbackOrchestrator(
            new FakeFeedbackStoreFactory(store),
            new FakeProjectStoreFactory(
                new ProjectSnapshot(project, [configVersion])),
            new NeverPythonClient(),
            executor,
            new Pass1OnlyHistoryWriter(),
            new RevealExecutionCoordinator(),
            TimeProvider.System,
            NullLogger<FeedbackOrchestrator>.Instance);

        var error = await ThrowsAsync<RevealStageException>(
            () => orchestrator.ProcessNextAsync(project.Id));

        Assert.AreEqual("FEEDBACK_PASS1_PERSIST_FAILED", error.Code);
        Assert.IsTrue(error.Retryable);
        Assert.AreEqual(3, executor.ExportCalls);
        Assert.AreEqual(3, store.PersistCalls);
        Assert.AreEqual(2, store.ScheduleRetryCalls);
        Assert.AreEqual(1, store.MarkErrorCalls);
    }

    [TestMethod]
    [DataRow(false, "FEEDBACK_RESPONSE_INVALID")]
    [DataRow(true, "PYTHON_CORRELATION_MISMATCH")]
    public async Task InvalidFeedbackWorkerContract_IsPermanent(
        bool wrongCorrelation,
        string expectedCode)
    {
        var now = DateTimeOffset.UtcNow;
        var project = Project.Create("Phase 5 worker contract", now)
            .TransitionTo(ProjectState.Running, now.AddSeconds(1));
        var configVersion = ConfigVersion.Create(
            project.Id, 1, Config(), "config-op", now);
        var job = Job(
            "managed.ARW",
            new string('a', 64),
            "RAW",
            "SUPPORTED_FULL_SIZE") with
        {
            ProjectId = project.Id,
            ProcessingConfigId = configVersion.Id
        };
        var pass1 = PassSnapshot(
            1, "pass1.tif", "attempt-1", ConservativeRecipe(), job);
        var store = new CompletedFeedbackStore(job, pass1, null, null);
        var python = new FeedbackPythonClient(request => new AiResponse(
            "v1",
            wrongCorrelation ? "wrong-request-id" : request.RequestId,
            true,
            wrongCorrelation
                ? JsonSerializer.SerializeToElement(new
                {
                    recipe = ConservativeRecipe(),
                    inspection = new { technical = new { } }
                })
                : null,
            null,
            null));
        var orchestrator = new FeedbackOrchestrator(
            new FakeFeedbackStoreFactory(store),
            new FakeProjectStoreFactory(
                new ProjectSnapshot(project, [configVersion])),
            python,
            new CompletedFeedbackExecutor(),
            new NeverHistoryWriter(),
            new RevealExecutionCoordinator(),
            TimeProvider.System,
            NullLogger<FeedbackOrchestrator>.Instance);

        var error = await ThrowsAsync<RevealStageException>(
            () => orchestrator.ProcessNextAsync(project.Id));

        Assert.AreEqual(expectedCode, error.Code);
        Assert.IsFalse(error.Retryable);
        Assert.AreEqual(1, store.MarkErrorCalls);
    }

    [TestMethod]
    public async Task FeedbackHistoryCollision_IsPermanent()
    {
        var now = DateTimeOffset.UtcNow;
        var project = Project.Create("Phase 5 history collision", now)
            .TransitionTo(ProjectState.Running, now.AddSeconds(1));
        var configVersion = ConfigVersion.Create(
            project.Id, 1, Config(), "config-op", now);
        var job = Job(
            "managed.ARW",
            new string('a', 64),
            "RAW",
            "SUPPORTED_FULL_SIZE") with
        {
            ProjectId = project.Id,
            ProcessingConfigId = configVersion.Id
        };
        var pass1 = PassSnapshot(
            1, "pass1.tif", "attempt-1", ConservativeRecipe(), job);
        var inspection = new FeedbackInspectionSnapshot(
            "inspection",
            job.Id,
            1,
            new string('b', 64),
            ConservativeRecipe(),
            JsonSerializer.SerializeToElement(new { technical = new { } }),
            now);
        var store = new CompletedFeedbackStore(
            job, pass1, null, inspection);
        var orchestrator = new FeedbackOrchestrator(
            new FakeFeedbackStoreFactory(store),
            new FakeProjectStoreFactory(
                new ProjectSnapshot(project, [configVersion])),
            new NeverPythonClient(),
            new Pass2OnlyExecutor(),
            new CollisionHistoryWriter(),
            new RevealExecutionCoordinator(),
            TimeProvider.System,
            NullLogger<FeedbackOrchestrator>.Instance);

        var error = await ThrowsAsync<RevealStageException>(
            () => orchestrator.ProcessNextAsync(project.Id));

        Assert.AreEqual("FEEDBACK_HISTORY_COLLISION", error.Code);
        Assert.IsFalse(error.Retryable);
        Assert.AreEqual(1, store.MarkErrorCalls);
    }

    private static DarktableFeedbackExecutor Executor(string root)
    {
        Directory.CreateDirectory(root);
        var paths = new WindowsAppPaths(Options.Create(
            new PhotoAIFactoryRuntimeOptions { RootPath = root }));
        return new(
            new GpuResourceCoordinator(),
            paths,
            new ProcessRunner(),
            new ComponentLockReader(),
            Options.Create(new AnalysisRuntimeOptions
            {
                DarktableCliPath = Path.Combine(root, "not-needed.exe")
            }));
    }

    private static FeedbackJobSnapshot Job(
        string input,
        string sha,
        string format,
        string rawSupport) =>
        new(
            new JobId("job"),
            new ProjectId("project"),
            new PhotoId("photo"),
            JobState.Processing,
            "config",
            "asset",
            input,
            sha,
            format,
            rawSupport,
            0,
            1,
            false);

    private static FeedbackPassSnapshot PassSnapshot(
        int passNumber,
        string path,
        string attempt,
        JsonElement control,
        FeedbackJobSnapshot? job = null)
    {
        job ??= Job(
            "managed.ARW",
            new string('a', 64),
            "RAW",
            "SUPPORTED_FULL_SIZE");
        return new(
            $"pass-{passNumber}",
            job.Id,
            passNumber,
            attempt,
            job.InputAssetId,
            job.InputSha256,
            job.InputFormat,
            "darktable 5.6.0",
            control,
            path,
            new string('b', 64),
            100,
            10,
            10,
            passNumber == 1 ? 16 : 8,
            3,
            $"pass{passNumber}.xmp",
            new string('c', 64),
            passNumber == 2 ? "history.json" : null,
            DateTimeOffset.UtcNow);
    }

    private static ProjectConfigV1 Config(string exportFormat = "JPEG") => new(
        @"C:\input",
        @"C:\output",
        false,
        RevealMode.Feedback,
        false,
        "DEFAULT",
        SemanticMode.Off,
        ComfyUiMode.Off,
        [],
        [],
        exportFormat,
        95,
        30);

    private static JsonElement ConservativeRecipe() =>
        JsonSerializer.SerializeToElement(new
        {
            schema_version = 1,
            recipe_version = "phase5-feedback-v1",
            strategy = "CONSERVATIVE_REUSE_PASS1",
            benchmark_status = "NOT_CALIBRATED",
            operations = Array.Empty<object>(),
            pass2_control = new
            {
                mode = "REUSE_PASS1_XMP",
                arbitrary_xmp_compilation = false,
                restart_from_managed_original = true,
                pass1_derivative_as_source = false
            },
            darktable_ai = new
            {
                raw_denoise = DisabledTask(),
                rgb_denoise = DisabledTask(),
                upscale = DisabledTask()
            }
        });

    private static object DisabledTask() => new
    {
        enabled = false,
        reason = FeedbackRecipePolicy.NeuralRestoreDisabledReason
    };

    private static void WriteMinimalJpeg(string path) =>
        File.WriteAllBytes(
            path,
            [
                0xff, 0xd8,
                0xff, 0xc0, 0x00, 0x11,
                0x08, 0x00, 0x01, 0x00, 0x01,
                0x03,
                0x01, 0x11, 0x00,
                0x02, 0x11, 0x00,
                0x03, 0x11, 0x00,
                0xff, 0xd9
            ]);

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream))
            .ToLowerInvariant();
    }

    private static async Task SeedQueuedFeedbackJobAsync(
        SqliteProjectDatabase database,
        string managedPath,
        string sha)
    {
        var configJson = ProjectConfigCanonicalizer.Serialize(Config());
        var configSha = ProjectConfigCanonicalizer.ComputeSha256(configJson);
        await using var connection =
            await database.OpenConfiguredConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projects(
                project_id, name, creation_operation_key,
                created_at_utc, updated_at_utc,
                project_state, state_revision, state_changed_at_utc)
            VALUES(
                'project', 'Phase 5', 'create-phase5',
                $now, $now, 'RUNNING', 1, $now);

            INSERT INTO project_config_versions(
                config_version_id, project_id, version_number,
                schema_version, config_json, config_sha256,
                operation_key, created_at_utc)
            VALUES(
                'config-v1', 'project', 1,
                1, $configJson, $configSha,
                'config-phase5', $now);

            INSERT INTO ingestion_sources(
                source_id, project_id, input_root, include_subfolders,
                config_version_id, created_at_utc)
            VALUES(
                'source', 'project', 'C:\fixture', 0,
                'config-v1', $now);

            INSERT INTO photos(
                photo_id, project_id, source_id, association_key,
                state, master_asset_id, master_format,
                association_deadline_utc, created_at_utc, updated_at_utc)
            VALUES(
                'photo-feedback', 'project', 'source', 'photo-feedback',
                'READY_FOR_ANALYSIS', 'asset-feedback', 'JPEG',
                $now, $now, $now);

            INSERT INTO assets(
                asset_id, project_id, photo_id, source_id,
                source_path, source_relative_path, managed_path,
                format, role, archive_state, size_bytes, sha256,
                raw_support_status, raw_max_width, raw_max_height,
                raw_classification, observed_at_utc, archived_at_utc)
            VALUES(
                'asset-feedback', 'project', 'photo-feedback', 'source',
                'C:\fixture\source.jpg', 'source.jpg', $managed,
                'JPEG', 'JPEG_MASTER', 'ARCHIVED', 24, $sha,
                'NOT_APPLICABLE', 0, 0,
                'NOT_RAW', $now, $now);

            INSERT INTO jobs(
                job_id, project_id, photo_id, parent_job_id, state,
                preselection_config_id, processing_config_id,
                analysis_source_asset_id, analysis_source_sha256,
                analysis_input_kind, analysis_representation_path,
                technical_retry_count, quality_reprocess_count,
                created_at_utc, updated_at_utc, reveal_retry_count)
            VALUES(
                'job-feedback', 'project', 'photo-feedback', NULL, 'QUEUED',
                'config-v1', 'config-v1',
                'asset-feedback', $sha,
                'JPEG_MASTER', $managed,
                0, 0, $now, $now, 0);

            INSERT INTO analysis_results(
                analysis_id, job_id, schema_version, result_json, created_at_utc)
            VALUES(
                'analysis-feedback', 'job-feedback', 1,
                '{"schema_version":1,"technical":{},"model_executions":[]}',
                $now);

            INSERT INTO preselection_results(
                preselection_id, job_id, decision, findings_json, created_at_utc)
            VALUES(
                'preselection-feedback', 'job-feedback',
                'APPROVED', '[]', $now);

            INSERT INTO job_checkpoints(
                checkpoint_id, job_id, stage_name, attempt_id,
                input_fingerprint, created_at_utc)
            VALUES
                ('analysis-cp', 'job-feedback', 'ANALYSIS_COMPLETE',
                 'analysis-attempt', $sha, $now),
                ('preselection-cp', 'job-feedback', 'PRESELECTION_COMPLETE',
                 'preselection-attempt', $sha, $now);

            INSERT INTO queue_entries(
                queue_entry_id, project_id, job_id, sequence_number,
                process_next, enqueued_at_utc, process_next_requested_at_utc)
            VALUES(
                'queue-feedback', 'project', 'job-feedback', 1,
                0, $now, NULL);
            """;
        command.Parameters.AddWithValue(
            "$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$configJson", configJson);
        command.Parameters.AddWithValue("$configSha", configSha);
        command.Parameters.AddWithValue("$managed", managedPath);
        command.Parameters.AddWithValue("$sha", sha);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> DatabaseScalarAsync(
        SqliteProjectDatabase database,
        string sql)
    {
        await using var connection =
            await database.OpenConfiguredConnectionAsync();
        return await ScalarLongAsync(connection, sql);
    }

    private static async Task<string> DatabaseScalarStringAsync(
        SqliteProjectDatabase database,
        string sql)
    {
        await using var connection =
            await database.OpenConfiguredConnectionAsync();
        return await ScalarStringAsync(connection, sql);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())
            ?? string.Empty;
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T exception)
        {
            return exception;
        }

        Assert.Fail($"Expected {typeof(T).Name}.");
        throw new InvalidOperationException();
    }

    private static string TempRoot(string name)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"paf-phase5-regression-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FakeFeedbackStoreFactory(IFeedbackStore store)
        : IFeedbackStoreFactory
    {
        public IFeedbackStore Open(ProjectId projectId) => store;
    }

    private sealed class FakeProjectStoreFactory(ProjectSnapshot snapshot)
        : IProjectStoreFactory
    {
        public IProjectStore Open(ProjectId projectId) =>
            new FakeProjectStore(snapshot);
    }

    private sealed class FakeProjectStore(ProjectSnapshot snapshot)
        : IProjectStore
    {
        public Task<ProjectSnapshot?> GetAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ProjectSnapshot?>(snapshot);

        public Task<ProjectSnapshot> CreateAsync(
            Project project,
            ConfigVersion initialConfig,
            string creationOperationKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConfigVersion> AppendAsync(
            ProjectId projectId,
            ProjectConfigV1 config,
            string operationKey,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConfigVersion>> ListAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TransitionWriteResult> TryTransitionAsync(
            ProjectId projectId,
            ProjectState expectedState,
            long expectedRevision,
            ProjectState nextState,
            string reason,
            string operationId,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProjectStateTransition>> ListTransitionsAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConfigWriteResult> ApplyWhenPausedAsync(
            ProjectId projectId,
            ProjectConfigV1 config,
            string expectedConfigVersionId,
            string operationId,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CompletedFeedbackStore(
        FeedbackJobSnapshot job,
        FeedbackPassSnapshot pass1,
        FeedbackPassSnapshot? pass2,
        FeedbackInspectionSnapshot? inspection) : IFeedbackStore
    {
        private FeedbackJobSnapshot current = job;
        public int MarkErrorCalls { get; private set; }

        public Task<FeedbackJobSnapshot?> GetActiveAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FeedbackJobSnapshot?>(current);

        public Task MarkInterruptedAsync(
            JobId jobId,
            string operationId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            current = current with { State = JobState.Interrupted };
            return Task.CompletedTask;
        }

        public Task<bool> ResumeInterruptedAsync(
            JobId jobId,
            string operationId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            current = current with { State = JobState.Processing };
            return Task.FromResult(true);
        }

        public Task<FeedbackPassSnapshot?> GetPassAsync(
            JobId jobId,
            int passNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FeedbackPassSnapshot?>(
                passNumber == 1 ? pass1 : pass2);

        public Task<FeedbackInspectionSnapshot?> GetInspectionAsync(
            JobId jobId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(inspection);

        public Task<bool> HasCheckpointAsync(
            JobId jobId,
            string stageName,
            CancellationToken cancellationToken = default) => Task.FromResult(
                stageName switch
                {
                    "DARKTABLE_PASS1_COMPLETE" => true,
                    "FEEDBACK_INSPECTION_COMPLETE" => inspection is not null,
                    "DARKTABLE_PASS2_COMPLETE" => pass2 is not null,
                    _ => false
                });

        public Task<FeedbackJobSnapshot?> PeekNextQueuedAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FeedbackJobSnapshot?>(null);

        public Task<bool> TryClaimAsync(
            JobId jobId,
            string operationId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> ResumeRetryAsync(
            JobId jobId,
            string operationId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<JsonElement?> GetAnalysisResultAsync(
            JobId jobId,
            CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(
                JsonSerializer.SerializeToElement(new
                {
                    schema_version = 1,
                    technical = new { },
                    model_executions = Array.Empty<object>()
                }));
        public Task PersistPass1CompleteAsync(
            FeedbackPersistPass1Request request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task PersistInspectionCompleteAsync(
            FeedbackPersistInspectionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task PersistPass2CompleteAsync(
            FeedbackPersistPass2Request request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> ScheduleRetryAsync(
            JobId jobId,
            string operationId,
            string reason,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task MarkErrorAsync(
            JobId jobId,
            string operationId,
            string reason,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            MarkErrorCalls++;
            current = current with { State = JobState.Error };
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPass1Store(FeedbackJobSnapshot job)
        : IFeedbackStore
    {
        private FeedbackJobSnapshot current = job;
        public int PersistCalls { get; private set; }
        public int ScheduleRetryCalls { get; private set; }
        public int MarkErrorCalls { get; private set; }

        public Task<FeedbackJobSnapshot?> GetActiveAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FeedbackJobSnapshot?>(current);

        public Task MarkInterruptedAsync(
            JobId jobId,
            string operationId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            current = current with { State = JobState.Interrupted };
            return Task.CompletedTask;
        }

        public Task<bool> ResumeInterruptedAsync(
            JobId jobId,
            string operationId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            current = current with { State = JobState.Processing };
            return Task.FromResult(true);
        }

        public Task<bool> ResumeRetryAsync(
            JobId jobId,
            string operationId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            current = current with { State = JobState.Processing };
            return Task.FromResult(true);
        }

        public Task<FeedbackPassSnapshot?> GetPassAsync(
            JobId jobId,
            int passNumber,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FeedbackPassSnapshot?>(null);

        public Task PersistPass1CompleteAsync(
            FeedbackPersistPass1Request request,
            CancellationToken cancellationToken = default)
        {
            PersistCalls++;
            throw new IOException("Injected SQLite write failure.");
        }

        public Task<int> ScheduleRetryAsync(
            JobId jobId,
            string operationId,
            string reason,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            ScheduleRetryCalls++;
            current = current with
            {
                State = JobState.Retrying,
                RevealRetryCount = ScheduleRetryCalls
            };
            return Task.FromResult(ScheduleRetryCalls);
        }

        public Task MarkErrorAsync(
            JobId jobId,
            string operationId,
            string reason,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default)
        {
            MarkErrorCalls++;
            current = current with { State = JobState.Error };
            return Task.CompletedTask;
        }

        public Task<FeedbackJobSnapshot?> PeekNextQueuedAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FeedbackJobSnapshot?>(null);
        public Task<bool> TryClaimAsync(
            JobId jobId,
            string operationId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<JsonElement?> GetAnalysisResultAsync(
            JobId jobId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FeedbackInspectionSnapshot?> GetInspectionAsync(
            JobId jobId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FeedbackInspectionSnapshot?>(null);
        public Task<bool> HasCheckpointAsync(
            JobId jobId,
            string stageName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task PersistInspectionCompleteAsync(
            FeedbackPersistInspectionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task PersistPass2CompleteAsync(
            FeedbackPersistPass2Request request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Pass1OnlyExecutor : IDarktableFeedbackExecutor
    {
        public int ExportCalls { get; private set; }

        public Task<FeedbackImageArtifact> ExportPass1Async(
            ProjectId projectId,
            JobId jobId,
            string attemptId,
            FeedbackJobSnapshot job,
            CancellationToken cancellationToken = default)
        {
            ExportCalls++;
            return Task.FromResult(new FeedbackImageArtifact(
                $"pass1-{ExportCalls}.tif",
                new string('b', 64),
                100,
                10,
                10,
                16,
                3,
                "darktable 5.6.0",
                TimeSpan.Zero,
                Encoding.UTF8.GetBytes(
                    "http://darktable.sf.net/ darktable:history")));
        }

        public Task<FeedbackImageArtifact> ValidatePersistedPass1Async(
            FeedbackJobSnapshot job,
            FeedbackPassSnapshot pass,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FeedbackImageArtifact> ExportPass2Async(
            ProjectId projectId,
            JobId jobId,
            string attemptId,
            FeedbackJobSnapshot job,
            FeedbackPassSnapshot pass1,
            int jpegQuality,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FeedbackImageArtifact> RecoverPass2Async(
            FeedbackJobSnapshot job,
            FeedbackPass2Recovery recovery,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CleanupPass1TemporaryAsync(
            FeedbackPassSnapshot pass1,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Pass1OnlyHistoryWriter : IFeedbackHistoryWriter
    {
        public Task<string> WriteXmpImmutableAsync(
            ProjectConfigV1 config,
            PhotoId photoId,
            JobId jobId,
            int passNumber,
            byte[] xmp,
            CancellationToken cancellationToken = default) =>
            Task.FromResult($"pass{passNumber}.xmp");

        public string GetHistoryPath(
            ProjectConfigV1 config,
            PhotoId photoId,
            JobId jobId) => throw new NotSupportedException();
        public string GetXmpPath(
            ProjectConfigV1 config,
            PhotoId photoId,
            JobId jobId,
            int passNumber) => throw new NotSupportedException();
        public Task WriteFinalAsync(
            ProjectConfigV1 config,
            FeedbackJobSnapshot job,
            string processingConfigSha256,
            FeedbackPassSnapshot pass1,
            FeedbackInspectionSnapshot inspection,
            FeedbackImageArtifact pass2,
            string pass2AttemptId,
            string pass2XmpPath,
            string historyPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FeedbackPass2Recovery?> TryReadPass2RecoveryAsync(
            ProjectConfigV1 config,
            FeedbackJobSnapshot job,
            string processingConfigSha256,
            FeedbackPassSnapshot pass1,
            FeedbackInspectionSnapshot inspection,
            string historyPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CompletedFeedbackExecutor : IDarktableFeedbackExecutor
    {
        public int CleanupCalls { get; private set; }
        public int ValidatePass1Calls { get; private set; }

        public Task<FeedbackImageArtifact> ValidatePersistedPass1Async(
            FeedbackJobSnapshot job,
            FeedbackPassSnapshot pass,
            CancellationToken cancellationToken = default)
        {
            ValidatePass1Calls++;
            return Task.FromResult(Artifact(pass));
        }

        public Task CleanupPass1TemporaryAsync(
            FeedbackPassSnapshot pass1,
            CancellationToken cancellationToken = default)
        {
            CleanupCalls++;
            return Task.CompletedTask;
        }

        public Task<FeedbackImageArtifact> ExportPass1Async(
            ProjectId projectId,
            JobId jobId,
            string attemptId,
            FeedbackJobSnapshot job,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FeedbackImageArtifact> ExportPass2Async(
            ProjectId projectId,
            JobId jobId,
            string attemptId,
            FeedbackJobSnapshot job,
            FeedbackPassSnapshot pass1,
            int jpegQuality,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FeedbackImageArtifact> RecoverPass2Async(
            FeedbackJobSnapshot job,
            FeedbackPass2Recovery recovery,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static FeedbackImageArtifact Artifact(
            FeedbackPassSnapshot pass) => new(
                pass.ImagePath,
                pass.ImageSha256,
                pass.ImageSizeBytes,
                pass.ImageWidth,
                pass.ImageHeight,
                pass.BitsPerSample,
                pass.Channels,
                pass.DarktableVersion,
                TimeSpan.Zero,
                []);
    }

    private sealed class NeverPythonClient : IPythonAiClient
    {
        public Task<HealthResponse> GetHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AiResponse> ExecuteAsync(
            string route,
            AiRequest request,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Python must not rerun after checkpoint.");
    }

    private sealed class FeedbackPythonClient(
        Func<AiRequest, AiResponse> response) : IPythonAiClient
    {
        public Task<HealthResponse> GetHealthAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiResponse> ExecuteAsync(
            string route,
            AiRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("/v1/feedback/inspect", route);
            Assert.AreEqual("feedback.inspect", request.Operation);
            return Task.FromResult(response(request));
        }
    }

    private sealed class Pass2OnlyExecutor : IDarktableFeedbackExecutor
    {
        public Task<FeedbackImageArtifact> ValidatePersistedPass1Async(
            FeedbackJobSnapshot job,
            FeedbackPassSnapshot pass,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new FeedbackImageArtifact(
                    pass.ImagePath,
                    pass.ImageSha256,
                    pass.ImageSizeBytes,
                    pass.ImageWidth,
                    pass.ImageHeight,
                    pass.BitsPerSample,
                    pass.Channels,
                    pass.DarktableVersion,
                    TimeSpan.Zero,
                    Encoding.UTF8.GetBytes(
                        "http://darktable.sf.net/ darktable:history")));

        public Task<FeedbackImageArtifact> ExportPass2Async(
            ProjectId projectId,
            JobId jobId,
            string attemptId,
            FeedbackJobSnapshot job,
            FeedbackPassSnapshot pass1,
            int jpegQuality,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new FeedbackImageArtifact(
                    "pass2.jpg",
                    new string('d', 64),
                    80,
                    10,
                    10,
                    8,
                    3,
                    "darktable 5.6.0",
                    TimeSpan.Zero,
                    Encoding.UTF8.GetBytes(
                        "http://darktable.sf.net/ darktable:history")));

        public Task<FeedbackImageArtifact> ExportPass1Async(
            ProjectId projectId,
            JobId jobId,
            string attemptId,
            FeedbackJobSnapshot job,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FeedbackImageArtifact> RecoverPass2Async(
            FeedbackJobSnapshot job,
            FeedbackPass2Recovery recovery,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CleanupPass1TemporaryAsync(
            FeedbackPassSnapshot pass1,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CollisionHistoryWriter : IFeedbackHistoryWriter
    {
        public string GetHistoryPath(
            ProjectConfigV1 config,
            PhotoId photoId,
            JobId jobId) => "history.json";

        public string GetXmpPath(
            ProjectConfigV1 config,
            PhotoId photoId,
            JobId jobId,
            int passNumber) => $"pass{passNumber}.xmp";

        public Task<string> WriteXmpImmutableAsync(
            ProjectConfigV1 config,
            PhotoId photoId,
            JobId jobId,
            int passNumber,
            byte[] xmp,
            CancellationToken cancellationToken = default) =>
            Task.FromResult($"pass{passNumber}.xmp");

        public Task WriteFinalAsync(
            ProjectConfigV1 config,
            FeedbackJobSnapshot job,
            string processingConfigSha256,
            FeedbackPassSnapshot pass1,
            FeedbackInspectionSnapshot inspection,
            FeedbackImageArtifact pass2,
            string pass2AttemptId,
            string pass2XmpPath,
            string historyPath,
            CancellationToken cancellationToken = default) =>
            throw new RevealHistoryCollisionException(
                "Injected immutable FEEDBACK history collision.");

        public Task<FeedbackPass2Recovery?> TryReadPass2RecoveryAsync(
            ProjectConfigV1 config,
            FeedbackJobSnapshot job,
            string processingConfigSha256,
            FeedbackPassSnapshot pass1,
            FeedbackInspectionSnapshot inspection,
            string historyPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FeedbackPass2Recovery?>(null);
    }

    private sealed class NeverHistoryWriter : IFeedbackHistoryWriter
    {
        public string GetHistoryPath(
            ProjectConfigV1 config,
            PhotoId photoId,
            JobId jobId) =>
            throw new AssertFailedException("History must not rerun after checkpoint.");
        public string GetXmpPath(
            ProjectConfigV1 config,
            PhotoId photoId,
            JobId jobId,
            int passNumber) =>
            throw new NotSupportedException();
        public Task<string> WriteXmpImmutableAsync(
            ProjectConfigV1 config,
            PhotoId photoId,
            JobId jobId,
            int passNumber,
            byte[] xmp,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task WriteFinalAsync(
            ProjectConfigV1 config,
            FeedbackJobSnapshot job,
            string processingConfigSha256,
            FeedbackPassSnapshot pass1,
            FeedbackInspectionSnapshot inspection,
            FeedbackImageArtifact pass2,
            string pass2AttemptId,
            string pass2XmpPath,
            string historyPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FeedbackPass2Recovery?> TryReadPass2RecoveryAsync(
            ProjectConfigV1 config,
            FeedbackJobSnapshot job,
            string processingConfigSha256,
            FeedbackPassSnapshot pass1,
            FeedbackInspectionSnapshot inspection,
            string historyPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
