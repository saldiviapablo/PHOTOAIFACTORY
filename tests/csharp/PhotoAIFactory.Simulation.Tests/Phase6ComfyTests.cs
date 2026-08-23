using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Infrastructure;
using PhotoAIFactory.Infrastructure.Hosting;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Processing;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;
using PhotoAIFactory.Infrastructure.Processing;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class Phase6ComfyTests
{
    [TestMethod]
    public void Off_plan_rejects_execution()
    {
        var plan = JsonDocument.Parse("""
            {
              "schema_version":1,
              "plan_version":"phase6-comfy-v1",
              "mode":"OFF",
              "benchmark_status":"ENHANCEMENT_WORKFLOWS_BENCHMARK_PENDING",
              "decisions":[
                {"task_id":"UPSCALE","action":"EXECUTE","reason":"bad"}
              ],
              "execution_order":["UPSCALE"]
            }
            """).RootElement.Clone();

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ComfyPlanPolicy.Validate(
                plan,
                ComfyUiMode.Off,
                ["UPSCALE"]));
    }

    [TestMethod]
    public void Auto_conservative_plan_is_valid()
    {
        var plan = JsonDocument.Parse("""
            {
              "schema_version":1,
              "plan_version":"phase6-comfy-v1",
              "mode":"AUTO",
              "benchmark_status":"ENHANCEMENT_WORKFLOWS_BENCHMARK_PENDING",
              "decisions":[
                {
                  "task_id":"DENOISE_RGB",
                  "action":"SKIP",
                  "reason":"AUTO_POLICY_NOT_CALIBRATED"
                }
              ],
              "execution_order":[]
            }
            """).RootElement.Clone();

        var parsed = ComfyPlanPolicy.Validate(
            plan,
            ComfyUiMode.Auto,
            ["DENOISE_RGB"]);

        Assert.AreEqual(0, parsed.ExecutionOrder.Count);
        Assert.AreEqual(
            "AUTO_POLICY_NOT_CALIBRATED",
            parsed.Decisions[0].Reason);
    }

    [TestMethod]
    public void Unapproved_task_fails_closed()
    {
        var plan = JsonDocument.Parse("""
            {
              "schema_version":1,
              "plan_version":"phase6-comfy-v1",
              "mode":"ON",
              "benchmark_status":"ENHANCEMENT_WORKFLOWS_BENCHMARK_PENDING",
              "decisions":[
                {
                  "task_id":"UPSCALE",
                  "action":"EXECUTE",
                  "reason":"MODE_ON_AUTHORIZED"
                }
              ],
              "execution_order":["UPSCALE"]
            }
            """).RootElement.Clone();

        var parsed = ComfyPlanPolicy.Validate(
            plan,
            ComfyUiMode.On,
            ["UPSCALE"]);
        var catalog = new ComfyWorkflowCatalog();

        var error = Assert.ThrowsExactly<ComfyStageException>(() =>
            ComfyPlanPolicy.RequireApproved(parsed, catalog));
        Assert.AreEqual("COMFY_TASK_NOT_APPROVED", error.Code);
        Assert.IsFalse(error.Retryable);
    }

    [TestMethod]
    public void Catalog_contains_exact_v1_task_vocabulary()
    {
        var actual = new ComfyWorkflowCatalog().Tasks
            .Select(item => item.TaskId)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var expected = new[]
        {
            "COLOR",
            "DENOISE_RGB",
            "FACE_MASKS",
            "FACE_RETOUCH",
            "LOW_LIGHT",
            "SHARPNESS",
            "UPSCALE"
        };
        CollectionAssert.AreEqual(expected, actual);
        Assert.IsTrue(
            new ComfyWorkflowCatalog().Tasks.All(
                item => !item.ProductionApproved));
    }

    [TestMethod]
    public void Migration_007_is_registered_after_feedback()
    {
        var migration7 = MigrationCatalog.All[6];
        Assert.AreEqual(7, migration7.Version);
        Assert.AreEqual("comfyui", migration7.Name);
        StringAssert.Contains(migration7.Sql, "COMFYUI_COMPLETE");
        StringAssert.Contains(migration7.Sql, "comfy_retry_count");
        StringAssert.Contains(migration7.Sql, "comfy_plans");
        StringAssert.Contains(migration7.Sql, "comfy_executions");
    }

    [TestMethod]
    public void Validation_workflow_uses_only_core_model_free_nodes()
    {
        var workflow = new ComfyWorkflowCatalog().ValidationWorkflowJson;
        StringAssert.Contains(workflow, "\"EmptyImage\"");
        StringAssert.Contains(workflow, "\"SaveImage\"");
        Assert.IsFalse(
            workflow.Contains("CheckpointLoader", StringComparison.Ordinal));
        Assert.IsFalse(
            workflow.Contains("LoadImage", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Migration007_FreshDatabase_ConfiguresIntegrityAndTriggers()
    {
        var root = TempRoot("migration007-fresh");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();
            await using var connection = await database.OpenConfiguredConnectionAsync();

            Assert.AreEqual(
                MigrationCatalog.All[6].Sha256,
                await ScalarStringAsync(
                    connection,
                    "SELECT migration_sha256 FROM schema_migrations WHERE version=7;"));
            Assert.AreEqual(
                "wal",
                (await ScalarStringAsync(
                    connection, "PRAGMA journal_mode;"))!.ToLowerInvariant());
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
                    'comfy_plans_no_update',
                    'comfy_plans_no_delete',
                    'comfy_executions_no_update',
                    'comfy_executions_no_delete');
                """));

            var retrySql = await ScalarStringAsync(
                connection,
                """
                SELECT sql FROM sqlite_master
                WHERE type='table' AND name='jobs';
                """);
            StringAssert.Contains(retrySql, "comfy_retry_count");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Migration007_UpgradeFrom006_CreatesBackupAndPreservesChecksums()
    {
        var root = TempRoot("migration007-upgrade");
        try
        {
            var path = Path.Combine(root, "project.db");
            var phase5 = new SqliteProjectDatabase(
                path,
                MigrationCatalog.All.Take(6).ToArray());
            await phase5.InitializeAsync();

            var upgraded = new SqliteProjectDatabase(
                path,
                MigrationCatalog.All.Take(7).ToArray());
            await upgraded.InitializeAsync();

            Assert.IsNotNull(upgraded.LastMigrationBackupPath);
            Assert.IsTrue(File.Exists(upgraded.LastMigrationBackupPath));

            await using var connection = await upgraded.OpenConfiguredConnectionAsync();
            Assert.AreEqual(7L, await ScalarLongAsync(
                connection, "SELECT max(version) FROM schema_migrations;"));

            for (var index = 0; index < 6; index++)
            {
                await using var checksum = connection.CreateCommand();
                checksum.CommandText =
                    "SELECT migration_sha256 FROM schema_migrations WHERE version=$version;";
                checksum.Parameters.AddWithValue("$version", index + 1);
                Assert.AreEqual(
                    MigrationCatalog.All[index].Sha256,
                    Convert.ToString(await checksum.ExecuteScalarAsync()));
            }

            Assert.AreEqual(
                MigrationCatalog.All[6].Sha256,
                await ScalarStringAsync(
                    connection,
                    "SELECT migration_sha256 FROM schema_migrations WHERE version=7;"));

            var appliedAt7 = await ScalarStringAsync(
                connection,
                "SELECT applied_at_utc FROM schema_migrations WHERE version=7;");
            await upgraded.InitializeAsync();
            Assert.AreEqual(
                appliedAt7,
                await ScalarStringAsync(
                    connection,
                    "SELECT applied_at_utc FROM schema_migrations WHERE version=7;"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Migration007_ChecksumDriftIsRejected()
    {
        var root = TempRoot("migration007-drift");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();
            await using (var connection = await database.OpenConfiguredConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE schema_migrations SET migration_sha256=$sha WHERE version=7;";
                command.Parameters.AddWithValue("$sha", new string('0', 64));
                await command.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsExactlyAsync<MigrationIntegrityException>(
                () => new SqliteProjectDatabase(path).InitializeAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Migration007_FailureRollsBackTransaction()
    {
        var root = TempRoot("migration007-rollback");
        try
        {
            var path = Path.Combine(root, "project.db");
            var v6 = MigrationCatalog.All.Take(6).ToArray();
            await new SqliteProjectDatabase(path, v6).InitializeAsync();
            var failing = new SqliteMigration(
                7,
                "comfyui",
                "CREATE TABLE phase6_rollback_probe(value TEXT); INVALID SQL;");

            await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                new SqliteProjectDatabase(
                    path,
                    [.. v6, failing]).InitializeAsync());

            var phase5 = new SqliteProjectDatabase(path, v6);
            await using var connection = await phase5.OpenConfiguredConnectionAsync();
            Assert.AreEqual(6L, await ScalarLongAsync(
                connection, "SELECT max(version) FROM schema_migrations;"));
            Assert.AreEqual(0L, await ScalarLongAsync(
                connection,
                """
                SELECT count(*) FROM sqlite_master
                WHERE type='table' AND name='phase6_rollback_probe';
                """));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Migration007_ComfyRetryCount_ConstrainedBetween0And2()
    {
        var root = TempRoot("migration007-retry-constraint");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();

            await SeedJobAsync(database, new ProjectId("test-proj"), "job-retry-constraint", "QA", false);

            await using var connection = await database.OpenConfiguredConnectionAsync();
            await using var validUpdate = connection.CreateCommand();
            validUpdate.CommandText = "UPDATE jobs SET comfy_retry_count=2 WHERE job_id='job-retry-constraint';";
            Assert.AreEqual(1, await validUpdate.ExecuteNonQueryAsync());

            await using var invalidUpdate = connection.CreateCommand();
            invalidUpdate.CommandText = "UPDATE jobs SET comfy_retry_count=3 WHERE job_id='job-retry-constraint';";
            await Assert.ThrowsExactlyAsync<SqliteException>(
                () => invalidUpdate.ExecuteNonQueryAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Migration007_ComfyPlansAndExecutions_AreAppendOnlyAndImmutable()
    {
        var root = TempRoot("migration007-triggers");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();

            await SeedJobAsync(database, new ProjectId("test-proj"), "job-immutability", "QA", false);

            var store = new SqliteComfyStore(database);
            var now = DateTimeOffset.UtcNow;
            var planDoc = JsonDocument.Parse("""
                {"schema_version":1,"plan_version":"phase6-comfy-v1","mode":"OFF","decisions":[],"execution_order":[]}
                """);
            await store.PersistPlanAsync(new(
                new JobId("job-immutability"),
                1,
                "OFF",
                planDoc.RootElement,
                new string('a', 64),
                now));

            await using var connection = await database.OpenConfiguredConnectionAsync();
            await using var planUpdate = connection.CreateCommand();
            planUpdate.CommandText = "UPDATE comfy_plans SET mode='ON' WHERE job_id='job-immutability';";
            await Assert.ThrowsExactlyAsync<SqliteException>(() => planUpdate.ExecuteNonQueryAsync());

            await using var planDelete = connection.CreateCommand();
            planDelete.CommandText = "DELETE FROM comfy_plans WHERE job_id='job-immutability';";
            await Assert.ThrowsExactlyAsync<SqliteException>(() => planDelete.ExecuteNonQueryAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ComfyPlanPolicy_RejectsSchemaAndVersionMismatches()
    {
        var wrongSchema = JsonDocument.Parse("""
            {"schema_version":2,"plan_version":"phase6-comfy-v1","mode":"OFF","benchmark_status":"PENDING","decisions":[],"execution_order":[]}
            """).RootElement;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ComfyPlanPolicy.Validate(wrongSchema, ComfyUiMode.Off, []));

        var wrongVersion = JsonDocument.Parse("""
            {"schema_version":1,"plan_version":"phase5-v1","mode":"OFF","benchmark_status":"PENDING","decisions":[],"execution_order":[]}
            """).RootElement;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ComfyPlanPolicy.Validate(wrongVersion, ComfyUiMode.Off, []));
    }

    [TestMethod]
    public void ComfyPlanPolicy_RejectsModeMismatchAndUnrecordedTasks()
    {
        var modeMismatch = JsonDocument.Parse("""
            {"schema_version":1,"plan_version":"phase6-comfy-v1","mode":"ON","benchmark_status":"PENDING","decisions":[],"execution_order":[]}
            """).RootElement;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ComfyPlanPolicy.Validate(modeMismatch, ComfyUiMode.Off, []));

        var missingDecision = JsonDocument.Parse("""
            {"schema_version":1,"plan_version":"phase6-comfy-v1","mode":"OFF","benchmark_status":"PENDING","decisions":[],"execution_order":[]}
            """).RootElement;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ComfyPlanPolicy.Validate(missingDecision, ComfyUiMode.Off, ["DENOISE_RGB"]));
    }

    [TestMethod]
    public void ComfyPlanPolicy_RejectsExecutionOrderMismatchAndDuplicates()
    {
        var duplicateOrder = JsonDocument.Parse("""
            {
              "schema_version":1,
              "plan_version":"phase6-comfy-v1",
              "mode":"ON",
              "benchmark_status":"PENDING",
              "decisions":[
                {"task_id":"UPSCALE","action":"EXECUTE","reason":"ok"}
              ],
              "execution_order":["UPSCALE","UPSCALE"]
            }
            """).RootElement;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ComfyPlanPolicy.Validate(duplicateOrder, ComfyUiMode.On, ["UPSCALE"]));

        var orderMismatch = JsonDocument.Parse("""
            {
              "schema_version":1,
              "plan_version":"phase6-comfy-v1",
              "mode":"ON",
              "benchmark_status":"PENDING",
              "decisions":[
                {"task_id":"UPSCALE","action":"EXECUTE","reason":"ok"},
                {"task_id":"COLOR","action":"EXECUTE","reason":"ok"}
              ],
              "execution_order":["COLOR","UPSCALE"]
            }
            """).RootElement;
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ComfyPlanPolicy.Validate(orderMismatch, ComfyUiMode.On, ["UPSCALE", "COLOR"]));
    }

    [TestMethod]
    public async Task SqliteComfyStore_SelectsNextEligible_ForBasicRevealAndDarktablePass2InQa()
    {
        var root = TempRoot("store-eligibility");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();

            var projectId = new ProjectId("test-proj");
            await SeedJobAsync(database, projectId, "job-reveal", "QA", false);
            await SeedJobAsync(database, projectId, "job-pass2", "QA", true);

            var store = new SqliteComfyStore(database);
            var eligible1 = await store.GetNextEligibleAsync(projectId);
            Assert.IsNotNull(eligible1);
            Assert.AreEqual("job-reveal", eligible1.Id.Value);
            Assert.AreEqual("BASIC_REVEAL_COMPLETE", eligible1.RevealStage);

            var artifact = new ComfyExecutionArtifact(
                eligible1.RevealPath, eligible1.RevealSha256, eligible1.RevealSizeBytes,
                JsonSerializer.SerializeToElement(new { executed = false }),
                JsonSerializer.SerializeToElement(Array.Empty<string>()));
            await store.PersistCompleteAsync(new(
                eligible1,
                "skip-attempt",
                "SKIPPED",
                artifact,
                JsonSerializer.SerializeToElement(Array.Empty<object>()),
                Path.Combine(root, "history1.json"),
                DateTimeOffset.UtcNow));

            var eligible2 = await store.GetNextEligibleAsync(projectId);
            Assert.IsNotNull(eligible2);
            Assert.AreEqual("job-pass2", eligible2.Id.Value);
            Assert.AreEqual("DARKTABLE_PASS2_COMPLETE", eligible2.RevealStage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComfyOrchestrator_OffMode_PersistsDurableSkipAndLeavesJobInQa()
    {
        var root = TempRoot("orchestrator-off");
        try
        {
            var paths = CreateAppPaths(root);
            var config = Config(root, ComfyUiMode.Off, ["DENOISE_RGB"]);
            var project = await SeedProjectAsync(paths, config);
            var database = new SqliteProjectDatabase(paths.GetProjectDatabasePath(project.Id));
            await SeedJobAsync(database, project.Id, "job-off", "QA", false);

            var python = new FakePythonAiClient((route, req) =>
            {
                Assert.AreEqual("/v1/comfy/plan", route);
                return new AiResponse(
                    "v1",
                    req.RequestId,
                    true,
                    JsonSerializer.SerializeToElement(new
                    {
                        schema_version = 1,
                        plan_version = "phase6-comfy-v1",
                        mode = "OFF",
                        benchmark_status = "PENDING",
                        decisions = new[]
                        {
                            new { task_id = "DENOISE_RGB", action = "SKIP", reason = "COMFYUI_OFF" }
                        },
                        execution_order = Array.Empty<string>()
                    }),
                    null,
                    null);
            });

            var orchestrator = new ComfyOrchestrator(
                new SqliteComfyStoreFactory(paths),
                new SqliteProjectStoreFactory(paths),
                python,
                new ComfyWorkflowCatalog(),
                new ThrowingExecutor(),
                new ComfyHistoryWriter(),
                new GpuResourceCoordinator(),
                new RevealExecutionCoordinator(),
                TimeProvider.System,
                NullLogger<ComfyOrchestrator>.Instance);

            var result = await orchestrator.ProcessNextAsync(project.Id);
            Assert.AreEqual(ComfyWorkStatus.Skipped, result.Status);
            Assert.AreEqual("job-off", result.JobId!.Value);

            var store = new SqliteComfyStore(database);
            Assert.IsTrue(await store.HasCheckpointAsync(new JobId("job-off"), "COMFYUI_COMPLETE"));
            var execution = await store.GetExecutionAsync(new JobId("job-off"));
            Assert.IsNotNull(execution);
            Assert.AreEqual("SKIPPED", execution.Status);

            await using var connection = await database.OpenConfiguredConnectionAsync();
            Assert.AreEqual("QA", await ScalarStringAsync(connection, "SELECT state FROM jobs WHERE job_id='job-off';"));
            Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT count(*) FROM job_checkpoints WHERE stage_name='OUTPUT_PUBLISHED';"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComfyOrchestrator_AutoMode_PersistsDurableSkipAndLeavesJobInQa()
    {
        var root = TempRoot("orchestrator-auto");
        try
        {
            var paths = CreateAppPaths(root);
            var config = Config(root, ComfyUiMode.Auto, ["DENOISE_RGB"]);
            var project = await SeedProjectAsync(paths, config);
            var database = new SqliteProjectDatabase(paths.GetProjectDatabasePath(project.Id));
            await SeedJobAsync(database, project.Id, "job-auto", "QA", false);

            var python = new FakePythonAiClient((route, req) =>
            {
                return new AiResponse(
                    "v1",
                    req.RequestId,
                    true,
                    JsonSerializer.SerializeToElement(new
                    {
                        schema_version = 1,
                        plan_version = "phase6-comfy-v1",
                        mode = "AUTO",
                        benchmark_status = "PENDING",
                        decisions = new[]
                        {
                            new { task_id = "DENOISE_RGB", action = "SKIP", reason = "AUTO_POLICY_NOT_CALIBRATED" }
                        },
                        execution_order = Array.Empty<string>()
                    }),
                    null,
                    null);
            });

            var orchestrator = new ComfyOrchestrator(
                new SqliteComfyStoreFactory(paths),
                new SqliteProjectStoreFactory(paths),
                python,
                new ComfyWorkflowCatalog(),
                new ThrowingExecutor(),
                new ComfyHistoryWriter(),
                new GpuResourceCoordinator(),
                new RevealExecutionCoordinator(),
                TimeProvider.System,
                NullLogger<ComfyOrchestrator>.Instance);

            var result = await orchestrator.ProcessNextAsync(project.Id);
            Assert.AreEqual(ComfyWorkStatus.Skipped, result.Status);

            var store = new SqliteComfyStore(database);
            Assert.IsTrue(await store.HasCheckpointAsync(new JobId("job-auto"), "COMFYUI_COMPLETE"));
            var execution = await store.GetExecutionAsync(new JobId("job-auto"));
            Assert.IsNotNull(execution);
            Assert.AreEqual("SKIPPED", execution.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComfyOrchestrator_OnModeWithBlockedTask_FailsClosedWithNoRetry()
    {
        var root = TempRoot("orchestrator-blocked");
        try
        {
            var paths = CreateAppPaths(root);
            var config = Config(root, ComfyUiMode.On, ["UPSCALE"]);
            var project = await SeedProjectAsync(paths, config);
            var database = new SqliteProjectDatabase(paths.GetProjectDatabasePath(project.Id));
            await SeedJobAsync(database, project.Id, "job-blocked", "QA", false);

            var python = new FakePythonAiClient((route, req) =>
            {
                return new AiResponse(
                    "v1",
                    req.RequestId,
                    true,
                    JsonSerializer.SerializeToElement(new
                    {
                        schema_version = 1,
                        plan_version = "phase6-comfy-v1",
                        mode = "ON",
                        benchmark_status = "PENDING",
                        decisions = new[]
                        {
                            new { task_id = "UPSCALE", action = "EXECUTE", reason = "MODE_ON_AUTHORIZED" }
                        },
                        execution_order = new[] { "UPSCALE" }
                    }),
                    null,
                    null);
            });

            var orchestrator = new ComfyOrchestrator(
                new SqliteComfyStoreFactory(paths),
                new SqliteProjectStoreFactory(paths),
                python,
                new ComfyWorkflowCatalog(),
                new ThrowingExecutor(),
                new ComfyHistoryWriter(),
                new GpuResourceCoordinator(),
                new RevealExecutionCoordinator(),
                TimeProvider.System,
                NullLogger<ComfyOrchestrator>.Instance);

            var error = await Assert.ThrowsExactlyAsync<ComfyStageException>(
                () => orchestrator.ProcessNextAsync(project.Id));
            Assert.AreEqual("COMFY_TASK_NOT_APPROVED", error.Code);
            Assert.IsFalse(error.Retryable);

            await using var connection = await database.OpenConfiguredConnectionAsync();
            Assert.AreEqual("ERROR", await ScalarStringAsync(connection, "SELECT state FROM jobs WHERE job_id='job-blocked';"));
            Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT comfy_retry_count FROM jobs WHERE job_id='job-blocked';"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComfyOrchestrator_ReplayIsIdempotent_NoDuplicateRowsOrCheckpoints()
    {
        var root = TempRoot("orchestrator-replay");
        try
        {
            var paths = CreateAppPaths(root);
            var config = Config(root, ComfyUiMode.Off, ["DENOISE_RGB"]);
            var project = await SeedProjectAsync(paths, config);
            var database = new SqliteProjectDatabase(paths.GetProjectDatabasePath(project.Id));
            await SeedJobAsync(database, project.Id, "job-replay", "QA", false);

            var pythonCalls = 0;
            var python = new FakePythonAiClient((route, req) =>
            {
                pythonCalls++;
                return new AiResponse(
                    "v1",
                    req.RequestId,
                    true,
                    JsonSerializer.SerializeToElement(new
                    {
                        schema_version = 1,
                        plan_version = "phase6-comfy-v1",
                        mode = "OFF",
                        benchmark_status = "PENDING",
                        decisions = new[]
                        {
                            new { task_id = "DENOISE_RGB", action = "SKIP", reason = "COMFYUI_OFF" }
                        },
                        execution_order = Array.Empty<string>()
                    }),
                    null,
                    null);
            });

            var orchestrator = new ComfyOrchestrator(
                new SqliteComfyStoreFactory(paths),
                new SqliteProjectStoreFactory(paths),
                python,
                new ComfyWorkflowCatalog(),
                new ThrowingExecutor(),
                new ComfyHistoryWriter(),
                new GpuResourceCoordinator(),
                new RevealExecutionCoordinator(),
                TimeProvider.System,
                NullLogger<ComfyOrchestrator>.Instance);

            var first = await orchestrator.ProcessNextAsync(project.Id);
            Assert.AreEqual(ComfyWorkStatus.Skipped, first.Status);
            Assert.AreEqual(1, pythonCalls);

            var second = await orchestrator.ProcessNextAsync(project.Id);
            Assert.AreEqual(ComfyWorkStatus.NoWork, second.Status);
            Assert.AreEqual(1, pythonCalls);

            await using var connection = await database.OpenConfiguredConnectionAsync();
            Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM comfy_plans WHERE job_id='job-replay';"));
            Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM comfy_executions WHERE job_id='job-replay';"));
            Assert.AreEqual(1L, await ScalarLongAsync(connection, "SELECT count(*) FROM job_checkpoints WHERE job_id='job-replay' AND stage_name='COMFYUI_COMPLETE';"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComfyHistoryWriter_TryReadRecovery_RecoversDurableBoundaryAfterDbFailure()
    {
        var root = TempRoot("history-recovery");
        try
        {
            var config = Config(root, ComfyUiMode.Off, ["DENOISE_RGB"]);
            var outputImage = Path.Combine(root, "output.png");
            await File.WriteAllBytesAsync(outputImage, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            var outputSha = await Sha256Async(outputImage);
            var outputSize = new FileInfo(outputImage).Length;

            var job = new ComfyJobSnapshot(
                new JobId("job-rec"),
                new ProjectId("test-proj"),
                new PhotoId("photo-rec"),
                JobState.Processing,
                "cfg-1",
                "BASIC_REVEAL_COMPLETE",
                outputImage,
                outputSha,
                outputSize,
                0);

            var plan = new ComfyPlanSnapshot(
                "plan-1",
                job.Id,
                1,
                "OFF",
                new string('a', 64),
                JsonSerializer.SerializeToElement(new { }),
                DateTimeOffset.UtcNow);

            var artifact = new ComfyExecutionArtifact(
                outputImage,
                outputSha,
                outputSize,
                JsonSerializer.SerializeToElement(new { workflow_id = "test" }),
                JsonSerializer.SerializeToElement(new[] { "prompt-1" }));

            var writer = new ComfyHistoryWriter();
            var historyPath = writer.GetHistoryPath(config, job.PhotoId, job.Id);
            await writer.WriteAsync(
                config,
                job,
                "cfg-sha",
                plan,
                "attempt-rec",
                "COMPLETED",
                artifact,
                JsonSerializer.SerializeToElement(new[] { new { task_id = "DENOISE_RGB", action = "SKIP" } }),
                historyPath);

            var recovered = await writer.TryReadRecoveryAsync(job, plan, historyPath);
            Assert.IsNotNull(recovered);
            Assert.AreEqual("attempt-rec", recovered.AttemptId);
            Assert.AreEqual(outputSha, recovered.Artifact.Sha256);
            Assert.AreEqual(outputSize, recovered.Artifact.SizeBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComfyHistoryWriter_FailsClosedOnShaMismatchOrMissingOutput()
    {
        var root = TempRoot("history-failure");
        try
        {
            var config = Config(root, ComfyUiMode.Off, ["DENOISE_RGB"]);
            var outputImage = Path.Combine(root, "output.png");
            await File.WriteAllBytesAsync(outputImage, [1, 2, 3, 4]);
            var outputSha = await Sha256Async(outputImage);
            var outputSize = 4L;

            var job = new ComfyJobSnapshot(
                new JobId("job-rec-fail"),
                new ProjectId("test-proj"),
                new PhotoId("photo-rec-fail"),
                JobState.Processing,
                "cfg-1",
                "BASIC_REVEAL_COMPLETE",
                outputImage,
                outputSha,
                outputSize,
                0);

            var plan = new ComfyPlanSnapshot(
                "plan-1",
                job.Id,
                1,
                "OFF",
                new string('a', 64),
                JsonSerializer.SerializeToElement(new { }),
                DateTimeOffset.UtcNow);

            var artifact = new ComfyExecutionArtifact(
                outputImage,
                outputSha,
                outputSize,
                JsonSerializer.SerializeToElement(new { workflow_id = "test" }),
                JsonSerializer.SerializeToElement(new[] { "prompt-1" }));

            var writer = new ComfyHistoryWriter();
            var historyPath = writer.GetHistoryPath(config, job.PhotoId, job.Id);
            await writer.WriteAsync(
                config,
                job,
                "cfg-sha",
                plan,
                "attempt-rec",
                "COMPLETED",
                artifact,
                JsonSerializer.SerializeToElement(new[] { new { task_id = "DENOISE_RGB", action = "SKIP" } }),
                historyPath);

            // Mutate output file
            await File.WriteAllBytesAsync(outputImage, [9, 9, 9, 9]);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => writer.TryReadRecoveryAsync(job, plan, historyPath));

            // Missing output
            File.Delete(outputImage);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => writer.TryReadRecoveryAsync(job, plan, historyPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComfyOrchestrator_ReleasesPythonModels_AndAcquiresGpuBeforeComfyUi()
    {
        var root = TempRoot("gpu-and-release");
        try
        {
            var paths = CreateAppPaths(root);
            var config = Config(root, ComfyUiMode.On, ["DENOISE_RGB"]);
            var project = await SeedProjectAsync(paths, config);
            var database = new SqliteProjectDatabase(paths.GetProjectDatabasePath(project.Id));
            await SeedJobAsync(database, project.Id, "job-gpu", "QA", false);

            var sequence = new List<string>();
            var python = new FakePythonAiClient((route, req) =>
            {
                if (route == "/v1/comfy/plan")
                {
                    sequence.Add("python:plan");
                    return new AiResponse(
                        "v1",
                        req.RequestId,
                        true,
                        JsonSerializer.SerializeToElement(new
                        {
                            schema_version = 1,
                            plan_version = "phase6-comfy-v1",
                            mode = "ON",
                            benchmark_status = "PENDING",
                            decisions = new[]
                            {
                                new { task_id = "DENOISE_RGB", action = "EXECUTE", reason = "ok" }
                            },
                            execution_order = new[] { "DENOISE_RGB" }
                        }),
                        null,
                        null);
                }
                if (route == "/v1/models/release")
                {
                    sequence.Add("python:models_release");
                    return new AiResponse("v1", req.RequestId, true, null, null, null);
                }
                return new AiResponse("v1", req.RequestId, false, null, new("UNKNOWN", "unknown", false, "worker", "unknown"), null);
            });

            var gpu = new TrackingGpuCoordinator(sequence);
            var catalog = new FakeApprovedCatalog();
            var executor = new TrackingExecutor(sequence, root);

            var orchestrator = new ComfyOrchestrator(
                new SqliteComfyStoreFactory(paths),
                new SqliteProjectStoreFactory(paths),
                python,
                catalog,
                executor,
                new ComfyHistoryWriter(),
                gpu,
                new RevealExecutionCoordinator(),
                TimeProvider.System,
                NullLogger<ComfyOrchestrator>.Instance);

            var result = await orchestrator.ProcessNextAsync(project.Id);
            Assert.AreEqual(ComfyWorkStatus.Completed, result.Status);

            Assert.AreEqual("python:plan", sequence[0]);
            Assert.AreEqual("python:models_release", sequence[1]);
            Assert.AreEqual("gpu:acquire", sequence[2]);
            Assert.AreEqual("comfy:execute", sequence[3]);
            Assert.AreEqual("gpu:release", sequence[4]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComfyOrchestrator_TechnicalRetryLimit_IsBoundedAtTwo()
    {
        var root = TempRoot("retry-limit");
        try
        {
            var paths = CreateAppPaths(root);
            var config = Config(root, ComfyUiMode.On, ["DENOISE_RGB"]);
            var project = await SeedProjectAsync(paths, config);
            var database = new SqliteProjectDatabase(paths.GetProjectDatabasePath(project.Id));
            await SeedJobAsync(database, project.Id, "job-retry-bound", "QA", false);

            var python = new FakePythonAiClient((route, req) =>
            {
                if (route == "/v1/comfy/plan")
                {
                    return new AiResponse(
                        "v1",
                        req.RequestId,
                        true,
                        JsonSerializer.SerializeToElement(new
                        {
                            schema_version = 1,
                            plan_version = "phase6-comfy-v1",
                            mode = "ON",
                            benchmark_status = "PENDING",
                            decisions = new[]
                            {
                                new { task_id = "DENOISE_RGB", action = "EXECUTE", reason = "ok" }
                            },
                            execution_order = new[] { "DENOISE_RGB" }
                        }),
                        null,
                        null);
                }
                return new AiResponse("v1", req.RequestId, true, null, null, null);
            });

            var executionAttempts = 0;
            var executor = new CallbackExecutor(() =>
            {
                executionAttempts++;
                throw new ComfyStageException("COMFY_CRASH", "runtime", "Simulated ComfyUI crash", true);
            });

            var orchestrator = new ComfyOrchestrator(
                new SqliteComfyStoreFactory(paths),
                new SqliteProjectStoreFactory(paths),
                python,
                new FakeApprovedCatalog(),
                executor,
                new ComfyHistoryWriter(),
                new GpuResourceCoordinator(),
                new RevealExecutionCoordinator(),
                TimeProvider.System,
                NullLogger<ComfyOrchestrator>.Instance);

            var error = await Assert.ThrowsExactlyAsync<ComfyStageException>(
                () => orchestrator.ProcessNextAsync(project.Id));
            Assert.AreEqual("COMFY_CRASH", error.Code);
            Assert.AreEqual(3, executionAttempts); // Initial attempt + 2 retries = 3

            await using var connection = await database.OpenConfiguredConnectionAsync();
            Assert.AreEqual("ERROR", await ScalarStringAsync(connection, "SELECT state FROM jobs WHERE job_id='job-retry-bound';"));
            Assert.AreEqual(2L, await ScalarLongAsync(connection, "SELECT comfy_retry_count FROM jobs WHERE job_id='job-retry-bound';"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComfyRuntimeSupervisor_RealPinnedComfyUI_ExecutesCoreModelFreeRoundtrip()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var componentRoot = Path.Combine(localAppData, "PhotoAIFactory", "components", "comfyui");
        var pythonPath = Path.Combine(componentRoot, "python_embeded", "python.exe");
        var mainPath = Path.Combine(componentRoot, "ComfyUI", "main.py");

        if (!File.Exists(pythonPath) || !File.Exists(mainPath))
        {
            Assert.Inconclusive("Pinned ComfyUI runtime is not installed on this PC.");
            return;
        }

        var tempRoot = TempRoot("real-comfy-roundtrip");
        try
        {
            var options = Options.Create(new ComfyRuntimeOptions
            {
                ComponentRoot = componentRoot,
                RuntimeRoot = Path.Combine(tempRoot, "runtimes", "comfyui"),
                ReadinessTimeout = TimeSpan.FromSeconds(60)
            });

            await using var supervisor = new ComfyRuntimeSupervisor(
                options,
                NullLogger<ComfyRuntimeSupervisor>.Instance);

            var catalog = new ComfyWorkflowCatalog();
            var executor = new ComfyWorkflowExecutor(supervisor, catalog);

            var stopwatch = Stopwatch.StartNew();
            var artifact = await executor.ValidateCoreRoundTripAsync();
            stopwatch.Stop();

            Assert.IsNotNull(artifact);
            Assert.IsTrue(File.Exists(artifact.Path));
            Assert.IsTrue(artifact.SizeBytes > 0);
            Assert.IsFalse(string.IsNullOrWhiteSpace(artifact.Sha256));

            Assert.AreEqual(JsonValueKind.Array, artifact.PromptIds.ValueKind);
            Assert.AreEqual(1, artifact.PromptIds.GetArrayLength());
            var promptId = artifact.PromptIds[0].GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(promptId));

            var stats = await supervisor.Client.GetSystemStatsAsync();
            Assert.IsFalse(string.IsNullOrWhiteSpace(stats));
            StringAssert.Contains(stats, "system");

            var history = await supervisor.Client.GetHistoryAsync(promptId!);
            Assert.IsFalse(string.IsNullOrWhiteSpace(history));

            Assert.IsTrue(artifact.Path.StartsWith(
                supervisor.OutputDirectory,
                StringComparison.OrdinalIgnoreCase));

            await supervisor.StopAsync();
            Assert.IsNull(supervisor.ProcessId);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task ComfyRuntimeSupervisor_ForceKillAndRestart_Succeeds()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var componentRoot = Path.Combine(localAppData, "PhotoAIFactory", "components", "comfyui");
        var pythonPath = Path.Combine(componentRoot, "python_embeded", "python.exe");
        var mainPath = Path.Combine(componentRoot, "ComfyUI", "main.py");

        if (!File.Exists(pythonPath) || !File.Exists(mainPath))
        {
            Assert.Inconclusive("Pinned ComfyUI runtime is not installed on this PC.");
            return;
        }

        var tempRoot = TempRoot("real-comfy-restart");
        try
        {
            var options = Options.Create(new ComfyRuntimeOptions
            {
                ComponentRoot = componentRoot,
                RuntimeRoot = Path.Combine(tempRoot, "runtimes", "comfyui"),
                ReadinessTimeout = TimeSpan.FromSeconds(60)
            });

            await using var supervisor = new ComfyRuntimeSupervisor(
                options,
                NullLogger<ComfyRuntimeSupervisor>.Instance);

            await supervisor.EnsureReadyAsync();
            var pid1 = supervisor.ProcessId;
            Assert.IsNotNull(pid1);

            var proc = Process.GetProcessById(pid1.Value);
            proc.Kill(entireProcessTree: true);
            await proc.WaitForExitAsync();

            await supervisor.EnsureReadyAsync();
            var pid2 = supervisor.ProcessId;
            Assert.IsNotNull(pid2);
            Assert.AreNotEqual(pid1.Value, pid2.Value);

            var stats = await supervisor.Client.GetSystemStatsAsync();
            Assert.IsFalse(string.IsNullOrWhiteSpace(stats));

            await supervisor.StopAsync();
            Assert.IsNull(supervisor.ProcessId);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string TempRoot(string label)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "PAF.Phase6Tests",
            $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static WindowsAppPaths CreateAppPaths(string root) =>
        new(Options.Create(new PhotoAIFactoryRuntimeOptions
        {
            RootPath = root,
            LogFileName = "audit.jsonl"
        }));

    private static ProjectConfigV1 Config(
        string root,
        ComfyUiMode mode,
        IReadOnlyList<string> tasks) =>
        new(
            Path.Combine(root, "input"),
            Path.Combine(root, "output"),
            false,
            RevealMode.Feedback,
            false,
            "DEFAULT",
            SemanticMode.Off,
            mode,
            tasks,
            [],
            "JPEG",
            95,
            30);

    private static async Task<Project> SeedProjectAsync(
        IAppPaths paths,
        ProjectConfigV1 config)
    {
        var now = DateTimeOffset.UtcNow;
        var project = Project.Create("Phase 6 Project", now);
        var configVersion = ConfigVersion.Create(
            project.Id, 1, config, "init-cfg", now);
        var db = new SqliteProjectDatabase(paths.GetProjectDatabasePath(project.Id));
        await db.InitializeAsync();
        var store = new SqliteProjectStoreFactory(paths).Open(project.Id);
        await store.CreateAsync(project, configVersion, "create-init");
        var running = project.TransitionTo(ProjectState.Running, now.AddSeconds(1));
        await store.TryTransitionAsync(
            project.Id,
            ProjectState.Stopped,
            project.StateRevision,
            ProjectState.Running,
            "Start project",
            "start-init",
            now.AddSeconds(1));
        return running;
    }

    private static async Task SeedJobAsync(
        SqliteProjectDatabase database,
        ProjectId projectId,
        string jobId,
        string state,
        bool pass2)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var photoId = "photo-" + jobId;
        var assetId = "asset-" + jobId;
        var outId = "out-" + jobId;

        await using var lease = await database.Writer.EnterAsync();
        await using var connection = await database.OpenConfiguredConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        // 1. Ensure project exists
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR IGNORE INTO projects(
                    project_id, name, creation_operation_key, created_at_utc, updated_at_utc,
                    project_state, state_revision, state_changed_at_utc)
                VALUES(
                    $projectId, 'Test Project', 'create-' || $projectId, $now, $now,
                    'RUNNING', 1, $now);
                """;
            cmd.Parameters.AddWithValue("$projectId", projectId.Value);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Resolve or insert config version
        string configVersionId;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT config_version_id FROM project_config_versions WHERE project_id = $projectId ORDER BY version_number DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("$projectId", projectId.Value);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null and not DBNull)
            {
                configVersionId = Convert.ToString(existing)!;
            }
            else
            {
                configVersionId = "cfg-" + jobId;
                await using var ins = connection.CreateCommand();
                ins.Transaction = transaction;
                ins.CommandText = """
                    INSERT INTO project_config_versions(
                        config_version_id, project_id, version_number, schema_version,
                        config_json, config_sha256, operation_key, created_at_utc)
                    VALUES(
                        $cfgId, $projectId, 1, 1,
                        '{"output_folder":"C:\\out"}', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                        'init-' || $cfgId, $now);
                    """;
                ins.Parameters.AddWithValue("$cfgId", configVersionId);
                ins.Parameters.AddWithValue("$projectId", projectId.Value);
                ins.Parameters.AddWithValue("$now", now);
                await ins.ExecuteNonQueryAsync();
            }
        }

        // 3. Ingestion source
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR IGNORE INTO ingestion_sources(
                    source_id, project_id, input_root, include_subfolders,
                    config_version_id, created_at_utc)
                VALUES(
                    'source-1', $projectId, 'C:\input', 0,
                    $cfgId, $now);
                """;
            cmd.Parameters.AddWithValue("$projectId", projectId.Value);
            cmd.Parameters.AddWithValue("$cfgId", configVersionId);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        // 4. Photo
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR IGNORE INTO photos(
                    photo_id, project_id, source_id, association_key,
                    state, master_asset_id, master_format,
                    association_deadline_utc, created_at_utc, updated_at_utc)
                VALUES(
                    $photoId, $projectId, 'source-1', $photoId,
                    'READY_FOR_ANALYSIS', $assetId, 'JPEG',
                    $now, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$photoId", photoId);
            cmd.Parameters.AddWithValue("$projectId", projectId.Value);
            cmd.Parameters.AddWithValue("$assetId", assetId);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobId))).ToLowerInvariant();

        // 5. Asset
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR IGNORE INTO assets(
                    asset_id, project_id, photo_id, source_id,
                    source_path, source_relative_path, managed_path,
                    format, role, archive_state, size_bytes, sha256,
                    raw_support_status, raw_max_width, raw_max_height,
                    raw_classification, observed_at_utc, archived_at_utc)
                VALUES(
                    $assetId, $projectId, $photoId, 'source-1',
                    'C:\input\test.jpg', 'test.jpg', 'managed.jpg',
                    'JPEG', 'JPEG_MASTER', 'ARCHIVED', 1000,
                    $sha,
                    'NOT_APPLICABLE', 0, 0,
                    'NOT_RAW', $now, $now);
                """;
            cmd.Parameters.AddWithValue("$assetId", assetId);
            cmd.Parameters.AddWithValue("$projectId", projectId.Value);
            cmd.Parameters.AddWithValue("$photoId", photoId);
            cmd.Parameters.AddWithValue("$sha", sha);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        // 6. Job
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO jobs(
                    job_id, project_id, photo_id, parent_job_id, state,
                    preselection_config_id, processing_config_id,
                    analysis_source_asset_id, analysis_source_sha256,
                    analysis_input_kind, analysis_representation_path,
                    technical_retry_count, quality_reprocess_count,
                    created_at_utc, updated_at_utc, reveal_retry_count, comfy_retry_count)
                VALUES(
                    $jobId, $projectId, $photoId, NULL, $state,
                    $cfgId, $cfgId,
                    $assetId, $sha,
                    'JPEG_MASTER', 'managed.jpg',
                    0, 0, $now, $now, 0, 0);
                """;
            cmd.Parameters.AddWithValue("$jobId", jobId);
            cmd.Parameters.AddWithValue("$projectId", projectId.Value);
            cmd.Parameters.AddWithValue("$photoId", photoId);
            cmd.Parameters.AddWithValue("$cfgId", configVersionId);
            cmd.Parameters.AddWithValue("$assetId", assetId);
            cmd.Parameters.AddWithValue("$sha", sha);
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        if (pass2)
        {
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO feedback_passes(
                    feedback_pass_id, job_id, pass_number, attempt_id, input_asset_id,
                    input_sha256, input_kind, darktable_version, control_plan_json,
                    image_path, image_sha256, image_size_bytes, image_width, image_height,
                    bits_per_sample, channels, xmp_path, xmp_sha256, history_path, completed_at_utc)
                VALUES(
                    $fpId, $jobId, 2, 'att-pass2', $assetId,
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'JPEG', 'dt-4.6', '{"mode":"test"}',
                    'pass2.jpg', 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 1000, 100, 100,
                    8, 3, 'pass2.xmp', 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc', 'pass2_hist.json', $now);
                """;
            cmd.Parameters.AddWithValue("$fpId", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$jobId", jobId);
            cmd.Parameters.AddWithValue("$assetId", assetId);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();

            await using var cp = connection.CreateCommand();
            cp.Transaction = transaction;
            cp.CommandText = """
                INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
                VALUES($cpId, $jobId, 'DARKTABLE_PASS2_COMPLETE', 'att-pass2', 'fp', $now);
                """;
            cp.Parameters.AddWithValue("$cpId", Guid.NewGuid().ToString("N"));
            cp.Parameters.AddWithValue("$jobId", jobId);
            cp.Parameters.AddWithValue("$now", now);
            await cp.ExecuteNonQueryAsync();
        }
        else
        {
            await using var cmdOutput = connection.CreateCommand();
            cmdOutput.Transaction = transaction;
            cmdOutput.CommandText = """
                INSERT INTO outputs(
                    output_id, job_id, attempt_id, stage, role, path,
                    sha256, size_bytes, width, height, validated, permanent, created_at_utc)
                VALUES(
                    $outId, $jobId, 'att-reveal', 'BASIC_REVEAL', 'BASIC_REVEAL_STAGING', 'reveal.jpg',
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 1000, 100, 100, 1, 0, $now);
                """;
            cmdOutput.Parameters.AddWithValue("$outId", outId);
            cmdOutput.Parameters.AddWithValue("$jobId", jobId);
            cmdOutput.Parameters.AddWithValue("$now", now);
            await cmdOutput.ExecuteNonQueryAsync();

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO processing_passes(
                    processing_pass_id, job_id, attempt_id, reveal_mode, input_asset_id,
                    input_sha256, recipe_id, darktable_version, control_plan_json,
                    output_id, history_path, xmp_history_path, completed_at_utc)
                VALUES(
                    $ppId, $jobId, 'att-reveal', 'PRE_AI', $assetId,
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', NULL, 'dt-4.6', '{"mode":"test"}',
                    $outId, 'history.json', NULL, $now);
                """;
            cmd.Parameters.AddWithValue("$ppId", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$jobId", jobId);
            cmd.Parameters.AddWithValue("$assetId", assetId);
            cmd.Parameters.AddWithValue("$outId", outId);
            cmd.Parameters.AddWithValue("$now", now);
            await cmd.ExecuteNonQueryAsync();

            await using var cp = connection.CreateCommand();
            cp.Transaction = transaction;
            cp.CommandText = """
                INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
                VALUES($cpId, $jobId, 'BASIC_REVEAL_COMPLETE', 'att-reveal', 'fp', $now);
                """;
            cp.Parameters.AddWithValue("$cpId", Guid.NewGuid().ToString("N"));
            cp.Parameters.AddWithValue("$jobId", jobId);
            cp.Parameters.AddWithValue("$now", now);
            await cp.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var val = await cmd.ExecuteScalarAsync();
        return val is null or DBNull ? null : Convert.ToString(val);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private sealed class FakePythonAiClient(Func<string, AiRequest, AiResponse> handler) : IPythonAiClient
    {
        public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthResponse("HEALTHY", "v1", "1.0", "cuda", []));

        public Task<AiResponse> ExecuteAsync(string route, AiRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(route, request));
    }

    private sealed class ThrowingExecutor : IComfyWorkflowExecutor
    {
        public Task<ComfyExecutionArtifact> ExecuteApprovedAsync(ComfyJobSnapshot job, IReadOnlyList<ComfyTaskDescriptor> tasks, string attemptId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ComfyUI execution was not expected.");

        public Task<ComfyExecutionArtifact> ValidateCoreRoundTripAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validation was not expected.");
    }

    private sealed class CallbackExecutor(Action callback) : IComfyWorkflowExecutor
    {
        public Task<ComfyExecutionArtifact> ExecuteApprovedAsync(ComfyJobSnapshot job, IReadOnlyList<ComfyTaskDescriptor> tasks, string attemptId, CancellationToken cancellationToken = default)
        {
            callback();
            throw new InvalidOperationException("Callback did not throw.");
        }

        public Task<ComfyExecutionArtifact> ValidateCoreRoundTripAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }

    private sealed class TrackingExecutor(List<string> sequence, string root) : IComfyWorkflowExecutor
    {
        public Task<ComfyExecutionArtifact> ExecuteApprovedAsync(ComfyJobSnapshot job, IReadOnlyList<ComfyTaskDescriptor> tasks, string attemptId, CancellationToken cancellationToken = default)
        {
            sequence.Add("comfy:execute");
            var outPath = Path.Combine(root, "comfy-out.png");
            File.WriteAllBytes(outPath, [0x89, 0x50, 0x4E, 0x47]);
            return Task.FromResult(new ComfyExecutionArtifact(
                outPath,
                new string('e', 64),
                4L,
                JsonSerializer.SerializeToElement(new { workflow_id = "test" }),
                JsonSerializer.SerializeToElement(new[] { "prompt-track" })));
        }

        public Task<ComfyExecutionArtifact> ValidateCoreRoundTripAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }

    private sealed class TrackingGpuCoordinator(List<string> sequence) : IGpuResourceCoordinator
    {
        public string? CurrentOwner => null;

        public Task<IAsyncDisposable> AcquireAsync(string contextName, CancellationToken cancellationToken = default)
        {
            sequence.Add("gpu:acquire");
            return Task.FromResult<IAsyncDisposable>(new DisposableAction(() => sequence.Add("gpu:release")));
        }

        private sealed class DisposableAction(Action onDispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                onDispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeApprovedCatalog : IComfyWorkflowCatalog
    {
        public IReadOnlyCollection<ComfyTaskDescriptor> Tasks =>
            [new("DENOISE_RGB", true, "APPROVED", "workflow-denoise", "Approved for test")];

        public string ValidationWorkflowId => "val-id";
        public string ValidationWorkflowJson => "{}";

        public ComfyTaskDescriptor Require(string taskId) =>
            Tasks.Single(t => t.TaskId == taskId);
    }
}
