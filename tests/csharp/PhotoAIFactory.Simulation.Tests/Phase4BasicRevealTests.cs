using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Processing;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;
using PhotoAIFactory.Infrastructure.Processing;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class Phase4BasicRevealTests
{
    [TestMethod]
    public void BasicRevealStateTransitions_AreAllowedByDomain()
    {
        Assert.IsTrue(
            JobStateMachine.CanTransition(JobState.Queued, JobState.Processing));
        Assert.IsTrue(
            JobStateMachine.CanTransition(JobState.Processing, JobState.Qa));
        Assert.IsTrue(
            JobStateMachine.CanTransition(JobState.Processing, JobState.Retrying));
        Assert.IsTrue(
            JobStateMachine.CanTransition(JobState.Processing, JobState.Interrupted));
        Assert.IsTrue(
            JobStateMachine.CanTransition(JobState.Interrupted, JobState.Processing));
    }

    [TestMethod]
    public void RecipeCompiler_AcceptsOnlyConservativeBaseline()
    {
        var compiler = new DarktableRecipeCompiler();
        var recipe = ConservativeRecipe();

        var plan = compiler.Compile(
            RevealMode.PreAi,
            recipe,
            Config(RevealMode.PreAi));

        Assert.AreEqual(
            "PRE_AI_CONSERVATIVE_DEFAULT_PIPELINE",
            plan.PolicyId);
        Assert.IsNull(plan.XmpPath);
        Assert.IsNull(plan.Style);
        Assert.IsFalse(plan.ApplyCustomPresets);
    }

    [TestMethod]
    public void DtAuto_UsesDefaultPipelineWithoutCustomPresetDatabase()
    {
        var compiler = new DarktableRecipeCompiler();

        var plan = compiler.Compile(
            RevealMode.DtAuto,
            recipe: null,
            Config(RevealMode.DtAuto));

        Assert.AreEqual("DT_AUTO_DEFAULT_PIPELINE", plan.PolicyId);
        Assert.IsNull(plan.XmpPath);
        Assert.IsNull(plan.Style);
        Assert.IsFalse(plan.ApplyCustomPresets);
    }

    [TestMethod]
    public void RecipeCompiler_RejectsUnvalidatedOperation()
    {
        var compiler = new DarktableRecipeCompiler();
        var recipe = JsonSerializer.SerializeToElement(new
        {
            schema_version = 1,
            recipe_version = "phase4-pre-ai-v1",
            strategy = "CONSERVATIVE_BASELINE",
            benchmark_status = "NOT_CALIBRATED",
            operations = new[]
            {
                new { type = "EXPOSURE", ev = 1.0 }
            },
            darktable_control = new
            {
                mode = "DEFAULT_PIPELINE"
            }
        });

        Assert.ThrowsExactly<InvalidDataException>(
            () => compiler.Compile(
                RevealMode.PreAi,
                recipe,
                Config(RevealMode.PreAi)));
    }

    [TestMethod]
    public async Task Migration005_IsAppliedAndIdempotent()
    {
        var root = TempRoot("Migration");
        try
        {
            var database = new SqliteProjectDatabase(
                Path.Combine(root, "project.db"));

            await database.InitializeAsync();
            await database.InitializeAsync();

            await using var connection =
                await database.OpenConfiguredConnectionAsync();

            await using var migration = connection.CreateCommand();
            migration.CommandText = """
                SELECT count(*)
                FROM schema_migrations
                WHERE version=5 AND name='basic_reveal';
                """;
            Assert.AreEqual(
                1L,
                Convert.ToInt64(await migration.ExecuteScalarAsync()));

            foreach (var table in new[]
            {
                "processing_recipes",
                "outputs",
                "processing_passes"
            })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT count(*)
                    FROM sqlite_master
                    WHERE type='table' AND name=$name;
                    """;
                command.Parameters.AddWithValue("$name", table);
                Assert.AreEqual(
                    1L,
                    Convert.ToInt64(await command.ExecuteScalarAsync()),
                    table);
            }

            await using var integrity = connection.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.AreEqual(
                "ok",
                Convert.ToString(await integrity.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Migration004_UpgradesTo005_WithBackupAndStableChecksums()
    {
        var root = TempRoot("Upgrade004");
        try
        {
            var path = Path.Combine(root, "project.db");
            var phase3 = new SqliteProjectDatabase(
                path,
                MigrationCatalog.All.Take(4).ToArray());
            await phase3.InitializeAsync();

            var upgraded = new SqliteProjectDatabase(
                path,
                MigrationCatalog.All.Take(5).ToArray());
            await upgraded.InitializeAsync();
            Assert.IsNotNull(upgraded.LastMigrationBackupPath);
            Assert.IsTrue(File.Exists(upgraded.LastMigrationBackupPath));

            await using var connection =
                await upgraded.OpenConfiguredConnectionAsync();
            Assert.AreEqual(5L, await ScalarAsync(
                connection,
                "SELECT max(version) FROM schema_migrations;"));
            Assert.AreEqual(1L, await ScalarAsync(
                connection,
                "SELECT count(*) FROM pragma_table_info('jobs') WHERE name='reveal_retry_count';"));
            Assert.AreEqual(1L, await ScalarAsync(connection, "PRAGMA foreign_keys;"));
            Assert.AreEqual(2L, await ScalarAsync(connection, "PRAGMA synchronous;"));

            for (var index = 0; index < 4; index++)
            {
                await using var checksum = connection.CreateCommand();
                checksum.CommandText =
                    "SELECT migration_sha256 FROM schema_migrations WHERE version=$version;";
                checksum.Parameters.AddWithValue("$version", index + 1);
                Assert.AreEqual(
                    MigrationCatalog.All[index].Sha256,
                    Convert.ToString(await checksum.ExecuteScalarAsync()));
            }

            var appliedAt = await ReadStringAsync(
                connection,
                "SELECT applied_at_utc FROM schema_migrations WHERE version=5;");
            await upgraded.InitializeAsync();
            Assert.AreEqual(
                appliedAt,
                await ReadStringAsync(
                    connection,
                    "SELECT applied_at_utc FROM schema_migrations WHERE version=5;"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ProcessingStore_ClaimsProcessNextBeforeFifo()
    {
        var root = TempRoot("Queue");
        try
        {
            var database = new SqliteProjectDatabase(
                Path.Combine(root, "project.db"));
            await database.InitializeAsync();

            await SeedQueuedJobAsync(
                database, "job-1", "photo-1", "asset-1", 1, processNext: false);
            await SeedQueuedJobAsync(
                database, "job-2", "photo-2", "asset-2", 2, processNext: true);

            var store = new SqliteProcessingStore(database);
            var next = await store.PeekNextQueuedAsync(new ProjectId("project"));

            Assert.IsNotNull(next);
            Assert.AreEqual("job-2", next.Id.Value);

            Assert.IsTrue(
                await store.TryClaimAsync(
                    next.Id,
                    "claim-job-2",
                    DateTimeOffset.UtcNow));

            var active = await store.GetActiveAsync(new ProjectId("project"));
            Assert.IsNotNull(active);
            Assert.AreEqual(JobState.Processing, active.State);
            Assert.AreEqual("job-2", active.Id.Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task BasicRevealCompletion_IsDurableAndReplaySafe()
    {
        var root = TempRoot("Complete");
        try
        {
            var database = new SqliteProjectDatabase(
                Path.Combine(root, "project.db"));
            await database.InitializeAsync();
            await SeedQueuedJobAsync(
                database, "job-1", "photo-1", "asset-1", 1, processNext: false);

            var store = new SqliteProcessingStore(database);
            var jobId = new JobId("job-1");

            Assert.IsTrue(
                await store.TryClaimAsync(
                    jobId,
                    "claim-job-1",
                    DateTimeOffset.UtcNow));

            var job = await store.GetActiveAsync(new ProjectId("project"));
            Assert.IsNotNull(job);

            var recipe = ConservativeRecipe();
            var plan = new DarktableRecipeCompiler().Compile(
                RevealMode.PreAi,
                recipe,
                Config(RevealMode.PreAi));
            var artifact = new BasicRevealArtifact(
                Path.Combine(root, "reveal.jpg"),
                new string('b', 64),
                1024,
                7008,
                4672,
                "darktable-cli 5.6.0",
                TimeSpan.FromSeconds(1));

            var request = new BasicRevealPersistRequest(
                job,
                "attempt-1",
                RevealMode.PreAi,
                recipe,
                1,
                new string('c', 64),
                plan,
                artifact,
                Path.Combine(root, "basic-reveal.json"),
                null,
                DateTimeOffset.UtcNow);

            await store.PersistBasicRevealCompleteAsync(request);
            await store.PersistBasicRevealCompleteAsync(request);

            Assert.IsTrue(
                await store.HasBasicRevealCheckpointAsync(jobId));
            var pass = await store.GetBasicRevealPassAsync(jobId);
            Assert.IsNotNull(pass);
            Assert.IsFalse(string.IsNullOrWhiteSpace(pass.OutputId));
            Assert.AreEqual(JobState.Qa, await ReadJobStateAsync(database, "job-1"));

            await using var connection =
                await database.OpenConfiguredConnectionAsync();

            Assert.AreEqual(
                0L,
                await ScalarAsync(
                    connection,
                    "SELECT count(*) FROM queue_entries WHERE job_id='job-1';"));
            Assert.AreEqual(
                1L,
                await ScalarAsync(
                    connection,
                    "SELECT count(*) FROM processing_recipes WHERE job_id='job-1';"));
            Assert.AreEqual(
                1L,
                await ScalarAsync(
                    connection,
                    "SELECT count(*) FROM outputs WHERE job_id='job-1';"));
            Assert.AreEqual(
                1L,
                await ScalarAsync(
                    connection,
                    "SELECT count(*) FROM processing_passes WHERE job_id='job-1';"));
            Assert.AreEqual(
                3L,
                await ScalarAsync(
                    connection,
                    "SELECT count(*) FROM job_checkpoints WHERE job_id='job-1';"));
            await ExpectSqliteFailureAsync(
                connection,
                "UPDATE processing_recipes SET recipe_sha256='" + new string('d', 64) + "' WHERE job_id='job-1';");
            await ExpectSqliteFailureAsync(
                connection,
                "UPDATE outputs SET path='changed.jpg' WHERE job_id='job-1';");
            await ExpectSqliteFailureAsync(
                connection,
                "DELETE FROM processing_passes WHERE job_id='job-1';");
            await ExpectSqliteFailureAsync(
                connection,
                "DELETE FROM job_checkpoints WHERE job_id='job-1' AND stage_name='BASIC_REVEAL_COMPLETE';");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PersistFailure_RollsBackBeforeCheckpointAndCanResume()
    {
        var root = TempRoot("PersistFailure");
        try
        {
            var database = new SqliteProjectDatabase(Path.Combine(root, "project.db"));
            await database.InitializeAsync();
            await SeedQueuedJobAsync(
                database, "job-1", "photo-1", "asset-1", 1, processNext: false);
            var store = new SqliteProcessingStore(database);
            var jobId = new JobId("job-1");
            Assert.IsTrue(await store.TryClaimAsync(
                jobId, "claim-persist-failure", DateTimeOffset.UtcNow));
            var job = await store.GetActiveAsync(new ProjectId("project"));
            Assert.IsNotNull(job);
            var plan = new DarktableRecipeCompiler().Compile(
                RevealMode.DtAuto, null, Config(RevealMode.DtAuto));
            var request = new BasicRevealPersistRequest(
                job,
                "attempt-persist-failure",
                RevealMode.DtAuto,
                null,
                null,
                null,
                plan,
                new BasicRevealArtifact(
                    Path.Combine(root, "reveal.jpg"), new string('b', 64),
                    1024, 7008, 4672, "darktable 5.6.0", TimeSpan.Zero),
                Path.Combine(root, "history.json"),
                null,
                DateTimeOffset.UtcNow);

            await using (var connection = await database.OpenConfiguredConnectionAsync())
            {
                await using var inject = connection.CreateCommand();
                inject.CommandText = """
                    CREATE TRIGGER phase4_injected_failure
                    BEFORE INSERT ON processing_passes
                    BEGIN
                        SELECT RAISE(ABORT, 'phase4 injected persistence failure');
                    END;
                    """;
                await inject.ExecuteNonQueryAsync();
            }

            var failed = false;
            try
            {
                await store.PersistBasicRevealCompleteAsync(request);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                failed = true;
            }
            Assert.IsTrue(failed);

            await using (var connection = await database.OpenConfiguredConnectionAsync())
            {
                Assert.AreEqual(0L, await ScalarAsync(
                    connection, "SELECT count(*) FROM processing_recipes WHERE job_id='job-1';"));
                Assert.AreEqual(0L, await ScalarAsync(
                    connection, "SELECT count(*) FROM outputs WHERE job_id='job-1';"));
                Assert.AreEqual(0L, await ScalarAsync(
                    connection, "SELECT count(*) FROM processing_passes WHERE job_id='job-1';"));
                Assert.AreEqual(0L, await ScalarAsync(
                    connection, "SELECT count(*) FROM job_checkpoints WHERE job_id='job-1' AND stage_name='BASIC_REVEAL_COMPLETE';"));
                Assert.AreEqual(1L, await ScalarAsync(
                    connection, "SELECT count(*) FROM queue_entries WHERE job_id='job-1';"));
                Assert.AreEqual("PROCESSING", await ReadStringAsync(
                    connection, "SELECT state FROM jobs WHERE job_id='job-1';"));

                await using var remove = connection.CreateCommand();
                remove.CommandText = "DROP TRIGGER phase4_injected_failure;";
                await remove.ExecuteNonQueryAsync();
            }

            await store.PersistBasicRevealCompleteAsync(request);
            Assert.IsTrue(await store.HasBasicRevealCheckpointAsync(jobId));
            Assert.AreEqual(JobState.Qa, await ReadJobStateAsync(database, "job-1"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RevealRetry_IsBoundedAndRecoverable()
    {
        var root = TempRoot("Retry");
        try
        {
            var database = new SqliteProjectDatabase(
                Path.Combine(root, "project.db"));
            await database.InitializeAsync();
            await SeedQueuedJobAsync(
                database, "job-1", "photo-1", "asset-1", 1, processNext: false);

            var store = new SqliteProcessingStore(database);
            var jobId = new JobId("job-1");
            Assert.IsTrue(
                await store.TryClaimAsync(
                    jobId, "claim", DateTimeOffset.UtcNow));

            Assert.AreEqual(
                1,
                await store.ScheduleRevealRetryAsync(
                    jobId, "retry-1", "DARKTABLE_TIMEOUT", DateTimeOffset.UtcNow));
            Assert.IsTrue(
                await store.ResumeRetryAsync(
                    jobId, "resume-1", DateTimeOffset.UtcNow));

            Assert.AreEqual(
                2,
                await store.ScheduleRevealRetryAsync(
                    jobId, "retry-2", "DARKTABLE_TIMEOUT", DateTimeOffset.UtcNow));
            Assert.IsTrue(
                await store.ResumeRetryAsync(
                    jobId, "resume-2", DateTimeOffset.UtcNow));

            Assert.AreEqual(
                -1,
                await store.ScheduleRevealRetryAsync(
                    jobId, "retry-3", "DARKTABLE_TIMEOUT", DateTimeOffset.UtcNow));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InterruptedReveal_CanResumeWithoutCreatingSecondJob()
    {
        var root = TempRoot("Interrupted");
        try
        {
            var database = new SqliteProjectDatabase(
                Path.Combine(root, "project.db"));
            await database.InitializeAsync();
            await SeedQueuedJobAsync(
                database, "job-1", "photo-1", "asset-1", 1, processNext: false);

            var store = new SqliteProcessingStore(database);
            var jobId = new JobId("job-1");

            Assert.IsTrue(
                await store.TryClaimAsync(
                    jobId, "claim", DateTimeOffset.UtcNow));

            await store.MarkInterruptedAsync(
                jobId, "interrupt", DateTimeOffset.UtcNow);

            var recoverable = await store.GetActiveAsync(new ProjectId("project"));
            Assert.IsNotNull(recoverable);
            Assert.AreEqual(JobState.Interrupted, recoverable.State);

            Assert.IsTrue(
                await store.ResumeInterruptedAsync(
                    jobId, "resume", DateTimeOffset.UtcNow));

            var resumed = await store.GetActiveAsync(new ProjectId("project"));
            Assert.IsNotNull(resumed);
            Assert.AreEqual(JobState.Processing, resumed.State);
            Assert.AreEqual("job-1", resumed.Id.Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RevealError_RemovesQueueEntrySoLaterJobsCanContinue()
    {
        var root = TempRoot("error-queue");
        try
        {
            var database = new SqliteProjectDatabase(Path.Combine(root, "project.db"));
            await database.InitializeAsync();
            await SeedQueuedJobAsync(database, "job-1", "photo-1", "asset-1", 1, false);
            await SeedQueuedJobAsync(database, "job-2", "photo-2", "asset-2", 2, false);

            var store = new SqliteProcessingStore(database);
            Assert.IsTrue(
                await store.TryClaimAsync(
                    new JobId("job-1"), "claim-error", DateTimeOffset.UtcNow));

            await store.MarkErrorAsync(
                new JobId("job-1"),
                "mark-error",
                "DARKTABLE_EXPORT_FAILED",
                DateTimeOffset.UtcNow);

            await using var connection = await database.OpenConfiguredConnectionAsync();
            await using var queueCount = connection.CreateCommand();
            queueCount.CommandText =
                "SELECT count(*) FROM queue_entries WHERE job_id='job-1';";
            Assert.AreEqual(
                0L,
                Convert.ToInt64(await queueCount.ExecuteScalarAsync()));

            var next = await store.PeekNextQueuedAsync(new ProjectId("project"));
            Assert.IsNotNull(next);
            Assert.AreEqual("job-2", next.Id.Value);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RecipeCompiler_RejectsWrongIdentityAndFeedback()
    {
        var compiler = new DarktableRecipeCompiler();
        var config = Config(RevealMode.PreAi);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            compiler.Compile(RevealMode.PreAi, ConservativeRecipe(schemaVersion: 2), config));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            compiler.Compile(RevealMode.PreAi, ConservativeRecipe(recipeVersion: "unknown"), config));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            compiler.Compile(RevealMode.PreAi, ConservativeRecipe(strategy: "AGGRESSIVE"), config));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            compiler.Compile(RevealMode.Feedback, ConservativeRecipe(), config));
    }

    [TestMethod]
    public async Task PortableHistory_IsImmutableSafeAndUnicodeCapable()
    {
        var root = TempRoot("History Ω");
        try
        {
            var output = Path.Combine(root, "salida con espacios ü");
            var config = Config(RevealMode.PreAi, output);
            var job = new BasicRevealJobSnapshot(
                new JobId("job-history"),
                new ProjectId("project-history"),
                new PhotoId("photo-history"),
                JobState.Processing,
                "config-history",
                "asset-history",
                Path.Combine(root, "managed Ω.ARW"),
                new string('a', 64),
                "ARW",
                0,
                1,
                false);
            var recipe = ConservativeRecipe();
            var plan = new DarktableRecipeCompiler().Compile(
                RevealMode.PreAi, recipe, config);
            var artifact = new BasicRevealArtifact(
                Path.Combine(root, "work", "basic-reveal.jpg"),
                new string('b', 64),
                2048,
                7008,
                4672,
                "darktable 5.6.0",
                TimeSpan.FromSeconds(1));
            WriteTestJpegWithDarktableXmp(artifact.Path);
            var writer = new ProcessingHistoryWriter();
            var historyPath = writer.GetHistoryPath(
                config, job.PhotoId, job.Id);

            var xmpPath = await writer.WriteAsync(
                config, job, RevealMode.PreAi, recipe,
                new string('c', 64), plan, artifact, "attempt-history", historyPath);
            var repeatedXmpPath = await writer.WriteAsync(
                config, job, RevealMode.PreAi, recipe,
                new string('c', 64), plan, artifact, "attempt-history", historyPath);

            var recovery = await writer.TryReadRecoveryAsync(
                config, job, RevealMode.PreAi, recipe,
                new string('c', 64), plan, historyPath);
            Assert.IsNotNull(recovery);
            Assert.AreEqual("attempt-history", recovery.AttemptId);
            Assert.AreEqual(artifact.Sha256, recovery.Artifact.Sha256);
            Assert.AreEqual(xmpPath, repeatedXmpPath);
            Assert.IsTrue(File.Exists(xmpPath));

            using var history = JsonDocument.Parse(
                await File.ReadAllTextAsync(historyPath));
            Assert.AreEqual("project-history", history.RootElement.GetProperty("project_id").GetString());
            Assert.IsFalse(history.RootElement.GetProperty("publication").GetProperty("final_published").GetBoolean());
            Assert.AreEqual(new string('b', 64), history.RootElement.GetProperty("output").GetProperty("sha256").GetString());

            var collision = false;
            try
            {
                await writer.WriteAsync(
                    config, job, RevealMode.PreAi, recipe,
                    new string('c', 64), plan,
                    artifact with { Sha256 = new string('d', 64) },
                    "attempt-history", historyPath);
            }
            catch (RevealHistoryCollisionException)
            {
                collision = true;
            }
            Assert.IsTrue(collision);

            Assert.ThrowsExactly<InvalidOperationException>(() =>
                writer.WriteAsync(
                    config, job, RevealMode.PreAi, recipe,
                    new string('c', 64), plan, artifact,
                    "attempt-history", Path.Combine(root, "escaped.json")).GetAwaiter().GetResult());

            // Fault boundary: an XMP may have reached disk before the JSON rename.
            // A new attempt preserves that orphan and writes its own immutable sidecar.
            File.Delete(historyPath);
            var restartedXmpPath = await writer.WriteAsync(
                config, job, RevealMode.PreAi, recipe,
                new string('c', 64), plan, artifact,
                "attempt-restart", historyPath);
            Assert.AreNotEqual(xmpPath, restartedXmpPath);
            Assert.IsTrue(File.Exists(xmpPath));
            Assert.IsTrue(File.Exists(restartedXmpPath));
            Assert.AreEqual(
                0,
                Directory.GetFiles(output, "*.partial-*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RevealExecutionCoordinator_AllowsOnlyOneHeavyJob()
    {
        var coordinator = new RevealExecutionCoordinator();
        var first = await coordinator.AcquireAsync();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = Task.Run(async () =>
        {
            await using var second = await coordinator.AcquireAsync();
            entered.SetResult();
        });

        await Task.Delay(50);
        Assert.IsFalse(entered.Task.IsCompleted);
        await first.DisposeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await waiter;
    }

    [TestMethod]
    public async Task Orchestrator_CompletesDtAutoAndPreAiWithoutPublishingFinal()
    {
        foreach (var mode in new[] { RevealMode.DtAuto, RevealMode.PreAi })
        {
            var python = FakePythonClient.Valid();
            var executor = new FakeRevealExecutor();
            using var fixture = await OrchestratorFixture.CreateAsync(
                mode, "RUNNING", python, executor);

            var result = await fixture.Orchestrator.ProcessNextAsync(
                new ProjectId("project"));

            Assert.AreEqual(RevealWorkStatus.Completed, result.Status);
            Assert.AreEqual(1, executor.ExportCalls);
            Assert.AreEqual(mode == RevealMode.PreAi ? 1 : 0, python.ExecuteCalls);
            Assert.AreEqual(JobState.Qa, await ReadJobStateAsync(
                fixture.Database, "job-1"));
            Assert.IsFalse(result.Pass!.ControlPlan.GetProperty(
                "arbitrary_xmp_compilation").GetBoolean());
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Pass.XmpHistoryPath));
            Assert.IsTrue(File.Exists(result.Pass.XmpHistoryPath));

            await using var connection =
                await fixture.Database.OpenConfiguredConnectionAsync();
            Assert.AreEqual(0L, await ScalarAsync(
                connection, "SELECT count(*) FROM queue_entries WHERE job_id='job-1';"));
            Assert.AreEqual(0L, await ScalarAsync(
                connection, "SELECT count(*) FROM job_state_transitions WHERE to_state IN ('COMPLETED','REVIEW_FINAL');"));
            Assert.IsFalse(Directory.Exists(Path.Combine(
                fixture.Root, "output", "FINAL")));
        }
    }

    [TestMethod]
    public async Task Orchestrator_RecoversHistoryAfterTransientDbFailureWithoutReexport()
    {
        var python = FakePythonClient.Valid();
        var executor = new FakeRevealExecutor();
        using var fixture = await OrchestratorFixture.CreateAsync(
            RevealMode.PreAi, "RUNNING", python, executor,
            failFirstPersist: true);

        var result = await fixture.Orchestrator.ProcessNextAsync(
            new ProjectId("project"));

        Assert.AreEqual(RevealWorkStatus.Completed, result.Status);
        Assert.AreEqual(1, executor.ExportCalls);
        Assert.AreEqual(1, executor.RecoverCalls);
        Assert.AreEqual(2, python.ExecuteCalls);
        Assert.AreEqual(1L, await CountAsync(
            fixture.Database,
            "SELECT count(*) FROM processing_passes WHERE job_id='job-1';"));
        Assert.AreEqual(1L, await CountAsync(
            fixture.Database,
            "SELECT count(*) FROM job_checkpoints WHERE job_id='job-1' AND stage_name='BASIC_REVEAL_COMPLETE';"));
    }

    [TestMethod]
    public async Task PreAiContractAndTransportFailures_AreBoundedAndDoNotCheckpoint()
    {
        var cases = new[]
        {
            FailureCase.WrongCorrelation,
            FailureCase.Malformed,
            FailureCase.StructuredPermanent,
            FailureCase.StructuredRetryable,
            FailureCase.Timeout,
            FailureCase.Crash
        };

        foreach (var failure in cases)
        {
            var python = FakePythonClient.Failing(failure);
            var executor = new FakeRevealExecutor();
            using var fixture = await OrchestratorFixture.CreateAsync(
                RevealMode.PreAi, "RUNNING", python, executor);

            var failed = false;
            try
            {
                await fixture.Orchestrator.ProcessNextAsync(
                    new ProjectId("project"));
            }
            catch (RevealStageException)
            {
                failed = true;
            }
            Assert.IsTrue(failed, failure.ToString());
            Assert.AreEqual(
                failure is FailureCase.StructuredRetryable or FailureCase.Timeout or FailureCase.Crash
                    ? 3 : 1,
                python.ExecuteCalls,
                failure.ToString());
            Assert.AreEqual(0, executor.ExportCalls, failure.ToString());
            Assert.AreEqual(JobState.Error, await ReadJobStateExtendedAsync(
                fixture.Database, "job-1"), failure.ToString());
            Assert.AreEqual(0L, await CountAsync(
                fixture.Database,
                "SELECT count(*) FROM job_checkpoints WHERE job_id='job-1' AND stage_name='BASIC_REVEAL_COMPLETE';"));
        }
    }

    [TestMethod]
    public async Task PausedProjectAndFeedbackHead_DoNotClaimPhase4Work()
    {
        var pausedExecutor = new FakeRevealExecutor();
        using (var paused = await OrchestratorFixture.CreateAsync(
                   RevealMode.DtAuto,
                   "PAUSED",
                   FakePythonClient.Valid(),
                   pausedExecutor))
        {
            var result = await paused.Orchestrator.ProcessNextAsync(
                new ProjectId("project"));
            Assert.AreEqual(RevealWorkStatus.NoWork, result.Status);
            Assert.AreEqual(0, pausedExecutor.ExportCalls);
            Assert.AreEqual(JobState.Queued, await ReadJobStateExtendedAsync(
                paused.Database, "job-1"));
        }

        var feedbackExecutor = new FakeRevealExecutor();
        using var feedback = await OrchestratorFixture.CreateAsync(
            RevealMode.Feedback,
            "RUNNING",
            FakePythonClient.Valid(),
            feedbackExecutor);
        var deferred = await feedback.Orchestrator.ProcessNextAsync(
            new ProjectId("project"));
        Assert.AreEqual(RevealWorkStatus.DeferredFeedback, deferred.Status);
        Assert.AreEqual(0, feedbackExecutor.ExportCalls);
        Assert.AreEqual(JobState.Queued, await ReadJobStateExtendedAsync(
            feedback.Database, "job-1"));
    }

    [TestMethod]
    public async Task Cancellation_LeavesJobInterruptedAndQueueRecoverable()
    {
        var executor = new FakeRevealExecutor
        {
            ExportBehavior = async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        };
        using var fixture = await OrchestratorFixture.CreateAsync(
            RevealMode.DtAuto, "RUNNING", FakePythonClient.Valid(), executor);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(100));

        var cancelled = false;
        try
        {
            await fixture.Orchestrator.ProcessNextAsync(
                new ProjectId("project"), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert.IsTrue(cancelled);
        Assert.AreEqual(JobState.Interrupted, await ReadJobStateExtendedAsync(
            fixture.Database, "job-1"));
        Assert.AreEqual(1L, await CountAsync(
            fixture.Database,
            "SELECT count(*) FROM queue_entries WHERE job_id='job-1';"));
        Assert.AreEqual(0L, await CountAsync(
            fixture.Database,
            "SELECT count(*) FROM job_checkpoints WHERE job_id='job-1' AND stage_name='BASIC_REVEAL_COMPLETE';"));
    }

    private static JsonElement ConservativeRecipe(
        int schemaVersion = 1,
        string recipeVersion = "phase4-pre-ai-v1",
        string strategy = "CONSERVATIVE_BASELINE") =>
        JsonSerializer.SerializeToElement(new
        {
            schema_version = schemaVersion,
            recipe_version = recipeVersion,
            strategy,
            benchmark_status = "NOT_CALIBRATED",
            operations = Array.Empty<object>(),
            darktable_control = new
            {
                mode = "DEFAULT_PIPELINE"
            }
        });

    private static void WriteTestJpegWithDarktableXmp(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var header = System.Text.Encoding.ASCII.GetBytes(
            "http://ns.adobe.com/xap/1.0/\0");
        var packet = System.Text.Encoding.UTF8.GetBytes(
            "<?xpacket begin='\uFEFF'?><x:xmpmeta xmlns:x='adobe:ns:meta/'>" +
            "<rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>" +
            "<rdf:Description xmlns:darktable='http://darktable.sf.net/'>" +
            "<darktable:history><rdf:Seq/></darktable:history>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end='w'?>");
        var payloadLength = header.Length + packet.Length;
        var segmentLength = payloadLength + 2;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.WriteByte(0xFF);
        stream.WriteByte(0xD8);
        stream.WriteByte(0xFF);
        stream.WriteByte(0xE1);
        stream.WriteByte((byte)(segmentLength >> 8));
        stream.WriteByte((byte)segmentLength);
        stream.Write(header);
        stream.Write(packet);
        stream.WriteByte(0xFF);
        stream.WriteByte(0xD9);
    }

    private static ProjectConfigV1 Config(RevealMode mode, string? output = null) =>
        new(
            @"C:\Input",
            output ?? @"D:\Output",
            false,
            mode,
            false,
            "DEFAULT",
            SemanticMode.Off,
            ComfyUiMode.Off,
            Array.Empty<string>(),
            Array.Empty<string>(),
            "JPEG",
            95,
            30);

    private static string TempRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "PhotoAIFactory-Phase4-" + suffix,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task SeedQueuedJobAsync(
        SqliteProjectDatabase database,
        string jobId,
        string photoId,
        string assetId,
        long sequence,
        bool processNext,
        ProjectConfigV1? projectConfig = null,
        string projectState = "RUNNING")
    {
        projectConfig ??= Config(RevealMode.DtAuto);
        var configJson = ProjectConfigCanonicalizer.Serialize(projectConfig);
        var configSha = ProjectConfigCanonicalizer.ComputeSha256(configJson);
        await using var connection =
            await database.OpenConfiguredConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO projects(
                project_id, name, creation_operation_key,
                created_at_utc, updated_at_utc,
                project_state, state_revision, state_changed_at_utc)
            VALUES(
                'project', 'Phase 4', 'create-phase4',
                $now, $now, $projectState, 1, $now);

            INSERT OR IGNORE INTO project_config_versions(
                config_version_id, project_id, version_number,
                schema_version, config_json, config_sha256,
                operation_key, created_at_utc)
            VALUES(
                'config-v1', 'project', 1,
                1, $configJson, $configSha,
                'config-phase4', $now);

            INSERT OR IGNORE INTO ingestion_sources(
                source_id, project_id, input_root, include_subfolders,
                config_version_id, created_at_utc)
            VALUES(
                'source', 'project', 'C:\\fixture', 0,
                'config-v1', $now);

            INSERT INTO photos(
                photo_id, project_id, source_id, association_key,
                state, master_asset_id, master_format,
                association_deadline_utc, created_at_utc, updated_at_utc)
            VALUES(
                $photo, 'project', 'source', $photo,
                'READY_FOR_ANALYSIS', $asset, 'JPEG',
                $now, $now, $now);

            INSERT INTO assets(
                asset_id, project_id, photo_id, source_id,
                source_path, source_relative_path, managed_path,
                format, role, archive_state, size_bytes, sha256,
                raw_support_status, raw_max_width, raw_max_height,
                raw_classification, observed_at_utc, archived_at_utc)
            VALUES(
                $asset, 'project', $photo, 'source',
                'C:\\fixture\\source.jpg', 'source.jpg',
                'C:\\fixture\\managed.jpg',
                'JPEG', 'JPEG_MASTER', 'ARCHIVED', 1, $sha,
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
                $job, 'project', $photo, NULL, 'QUEUED',
                'config-v1', 'config-v1',
                $asset, $sha,
                'JPEG_MASTER', 'C:\\fixture\\managed.jpg',
                0, 0, $now, $now, 0);

            INSERT INTO analysis_results(
                analysis_id, job_id, schema_version, result_json, created_at_utc)
            VALUES(
                'analysis-' || $job, $job, 1,
                '{"schema_version":1,"technical":{},"model_executions":[]}',
                $now);

            INSERT INTO preselection_results(
                preselection_id, job_id, decision, findings_json, created_at_utc)
            VALUES(
                'preselection-' || $job, $job, 'APPROVED', '[]', $now);

            INSERT INTO job_checkpoints(
                checkpoint_id, job_id, stage_name, attempt_id,
                input_fingerprint, created_at_utc)
            VALUES
                ('analysis-cp-' || $job, $job, 'ANALYSIS_COMPLETE',
                 'analysis-attempt', $sha, $now),
                ('preselection-cp-' || $job, $job, 'PRESELECTION_COMPLETE',
                 'preselection-attempt', $sha, $now);

            INSERT INTO queue_entries(
                queue_entry_id, project_id, job_id, sequence_number,
                process_next, enqueued_at_utc, process_next_requested_at_utc)
            VALUES(
                'queue-' || $job, 'project', $job, $sequence,
                $processNext, $now,
                CASE WHEN $processNext=1 THEN $now ELSE NULL END);
            """;
        command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UtcNow.ToString("O"));
        var hashMarker = (char)('a' + (int)((sequence - 1) % 26));
        command.Parameters.AddWithValue("$sha", new string(hashMarker, 64));
        command.Parameters.AddWithValue("$configJson", configJson);
        command.Parameters.AddWithValue("$configSha", configSha);
        command.Parameters.AddWithValue("$projectState", projectState);
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$photo", photoId);
        command.Parameters.AddWithValue("$asset", assetId);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$processNext", processNext ? 1 : 0);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<JobState> ReadJobStateAsync(
        SqliteProjectDatabase database,
        string jobId)
    {
        await using var connection =
            await database.OpenConfiguredConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT state FROM jobs WHERE job_id=$job;";
        command.Parameters.AddWithValue("$job", jobId);
        var value = Convert.ToString(await command.ExecuteScalarAsync());
        return value switch
        {
            "QA" => JobState.Qa,
            "PROCESSING" => JobState.Processing,
            "QUEUED" => JobState.Queued,
            _ => throw new InvalidDataException(
                $"Unexpected test Job state {value}.")
        };
    }

    private static async Task<long> ScalarAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExpectSqliteFailureAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var failed = false;
        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            failed = true;
        }
        Assert.IsTrue(failed, sql);
    }

    private static async Task<string?> ReadStringAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountAsync(
        SqliteProjectDatabase database,
        string sql)
    {
        await using var connection =
            await database.OpenConfiguredConnectionAsync();
        return await ScalarAsync(connection, sql);
    }

    private static async Task<JobState> ReadJobStateExtendedAsync(
        SqliteProjectDatabase database,
        string jobId)
    {
        await using var connection =
            await database.OpenConfiguredConnectionAsync();
        var state = await ReadStringAsync(
            connection,
            $"SELECT state FROM jobs WHERE job_id='{jobId}';");
        return state switch
        {
            "QUEUED" => JobState.Queued,
            "PROCESSING" => JobState.Processing,
            "QA" => JobState.Qa,
            "ERROR" => JobState.Error,
            "INTERRUPTED" => JobState.Interrupted,
            _ => throw new InvalidDataException($"Unexpected Job state {state}.")
        };
    }

    private enum FailureCase
    {
        WrongCorrelation,
        Malformed,
        StructuredPermanent,
        StructuredRetryable,
        Timeout,
        Crash
    }

    private sealed class FakePythonClient(
        Func<AiRequest, int, Task<AiResponse>> behavior) : IPythonAiClient
    {
        public int ExecuteCalls { get; private set; }

        public Task<HealthResponse> GetHealthAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthResponse(
                "HEALTHY", "v1", "phase4-test", "cpu", []));

        public Task<AiResponse> ExecuteAsync(
            string route,
            AiRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual("/v1/recipe/pre-ai", route);
            Assert.AreEqual("v1", request.ApiVersion);
            Assert.AreEqual("recipe.pre-ai", request.Operation);
            ExecuteCalls++;
            return behavior(request, ExecuteCalls);
        }

        public static FakePythonClient Valid() => new((request, _) =>
            Task.FromResult(new AiResponse(
                "v1", request.RequestId, true,
                ConservativeRecipe(), null, new Dictionary<string, double>())));

        public static FakePythonClient Failing(FailureCase failure) =>
            new((request, _) => failure switch
            {
                FailureCase.WrongCorrelation => Task.FromResult(new AiResponse(
                    "v1", "wrong-request-id", true,
                    ConservativeRecipe(), null, null)),
                FailureCase.Malformed => Task.FromResult(new AiResponse(
                    "v1", request.RequestId, true, null, null, null)),
                FailureCase.StructuredPermanent => Task.FromResult(new AiResponse(
                    "v1", request.RequestId, false, null,
                    new AiError(
                        "PERMANENT_RECIPE_ERROR", "contract", false,
                        "python-ai-worker", "permanent"), null)),
                FailureCase.StructuredRetryable => Task.FromResult(new AiResponse(
                    "v1", request.RequestId, false, null,
                    new AiError(
                        "RETRYABLE_RECIPE_ERROR", "runtime", true,
                        "python-ai-worker", "retryable"), null)),
                FailureCase.Timeout => Task.FromException<AiResponse>(
                    new TimeoutException("injected worker timeout")),
                FailureCase.Crash => Task.FromException<AiResponse>(
                    new HttpRequestException("injected worker crash")),
                _ => throw new ArgumentOutOfRangeException(nameof(failure))
            });
    }

    private sealed class FakeRevealExecutor : IBasicRevealExecutor
    {
        public int ExportCalls { get; private set; }
        public int RecoverCalls { get; private set; }
        public Func<CancellationToken, Task<BasicRevealArtifact>>? ExportBehavior { get; init; }
        public string OutputRoot { get; set; } = Path.GetTempPath();

        public string GetOutputPath(
            ProjectId projectId,
            JobId jobId,
            string attemptId) => Path.Combine(
                OutputRoot, projectId.Value, jobId.Value,
                attemptId, "reveal", "basic-reveal.jpg");

        public async Task<BasicRevealArtifact> ExportAsync(
            ProjectId projectId,
            JobId jobId,
            string attemptId,
            BasicRevealJobSnapshot job,
            DarktableControlPlan plan,
            int jpegQuality,
            CancellationToken cancellationToken = default)
        {
            ExportCalls++;
            if (ExportBehavior is not null)
                return await ExportBehavior(cancellationToken);
            var outputPath = GetOutputPath(projectId, jobId, attemptId);
            WriteTestJpegWithDarktableXmp(outputPath);
            return new BasicRevealArtifact(
                outputPath,
                new string('b', 64), 4096, 7008, 4672,
                "darktable 5.6.0", TimeSpan.FromMilliseconds(10));
        }

        public Task<BasicRevealArtifact> RecoverAsync(
            ProjectId projectId,
            JobId jobId,
            BasicRevealJobSnapshot job,
            BasicRevealRecovery recovery,
            CancellationToken cancellationToken = default)
        {
            RecoverCalls++;
            return Task.FromResult(recovery.Artifact with
            {
                Path = GetOutputPath(projectId, jobId, recovery.AttemptId)
            });
        }
    }

    private sealed class OrchestratorFixture : IDisposable
    {
        private OrchestratorFixture(
            string root,
            SqliteProjectDatabase database,
            BasicRevealOrchestrator orchestrator)
        {
            Root = root;
            Database = database;
            Orchestrator = orchestrator;
        }

        public string Root { get; }
        public SqliteProjectDatabase Database { get; }
        public BasicRevealOrchestrator Orchestrator { get; }

        public static async Task<OrchestratorFixture> CreateAsync(
            RevealMode mode,
            string projectState,
            FakePythonClient python,
            FakeRevealExecutor executor,
            bool failFirstPersist = false)
        {
            var root = TempRoot("Orchestrator");
            executor.OutputRoot = Path.Combine(root, "fake-work");
            var database = new SqliteProjectDatabase(
                Path.Combine(root, "project.db"));
            await database.InitializeAsync();
            var config = Config(mode, Path.Combine(root, "output"));
            await SeedQueuedJobAsync(
                database, "job-1", "photo-1", "asset-1", 1, false,
                config, projectState);

            IProcessingStore store = new SqliteProcessingStore(database);
            if (failFirstPersist)
                store = new FailFirstPersistStore(store);

            var orchestrator = new BasicRevealOrchestrator(
                new SingleProcessingStoreFactory(store),
                new SingleProjectStoreFactory(new SqliteProjectStore(database)),
                python,
                new DarktableRecipeCompiler(),
                executor,
                new ProcessingHistoryWriter(),
                new RevealExecutionCoordinator(),
                TimeProvider.System,
                NullLogger<BasicRevealOrchestrator>.Instance);
            return new OrchestratorFixture(root, database, orchestrator);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class SingleProcessingStoreFactory(IProcessingStore store)
        : IProcessingStoreFactory
    {
        public IProcessingStore Open(ProjectId projectId) => store;
    }

    private sealed class SingleProjectStoreFactory(IProjectStore store)
        : IProjectStoreFactory
    {
        public IProjectStore Open(ProjectId projectId) => store;
    }

    private sealed class FailFirstPersistStore(IProcessingStore inner)
        : IProcessingStore
    {
        private int remainingFailures = 1;

        public Task<BasicRevealJobSnapshot?> GetActiveAsync(
            ProjectId projectId, CancellationToken cancellationToken = default) =>
            inner.GetActiveAsync(projectId, cancellationToken);
        public Task<BasicRevealJobSnapshot?> PeekNextQueuedAsync(
            ProjectId projectId, CancellationToken cancellationToken = default) =>
            inner.PeekNextQueuedAsync(projectId, cancellationToken);
        public Task<bool> TryClaimAsync(
            JobId jobId, string operationId, DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.TryClaimAsync(jobId, operationId, nowUtc, cancellationToken);
        public Task<bool> ResumeRetryAsync(
            JobId jobId, string operationId, DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.ResumeRetryAsync(jobId, operationId, nowUtc, cancellationToken);
        public Task<bool> ResumeInterruptedAsync(
            JobId jobId, string operationId, DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.ResumeInterruptedAsync(jobId, operationId, nowUtc, cancellationToken);
        public Task<JsonElement?> GetAnalysisResultAsync(
            JobId jobId, CancellationToken cancellationToken = default) =>
            inner.GetAnalysisResultAsync(jobId, cancellationToken);
        public Task<BasicRevealPassSnapshot?> GetBasicRevealPassAsync(
            JobId jobId, CancellationToken cancellationToken = default) =>
            inner.GetBasicRevealPassAsync(jobId, cancellationToken);
        public Task<bool> HasBasicRevealCheckpointAsync(
            JobId jobId, CancellationToken cancellationToken = default) =>
            inner.HasBasicRevealCheckpointAsync(jobId, cancellationToken);
        public Task<int> ScheduleRevealRetryAsync(
            JobId jobId, string operationId, string reason,
            DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            inner.ScheduleRevealRetryAsync(
                jobId, operationId, reason, nowUtc, cancellationToken);
        public Task PersistBasicRevealCompleteAsync(
            BasicRevealPersistRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref remainingFailures, 0) == 1)
                throw new IOException("injected database write failure");
            return inner.PersistBasicRevealCompleteAsync(request, cancellationToken);
        }
        public Task MarkInterruptedAsync(
            JobId jobId, string operationId, DateTimeOffset nowUtc,
            CancellationToken cancellationToken = default) =>
            inner.MarkInterruptedAsync(jobId, operationId, nowUtc, cancellationToken);
        public Task MarkErrorAsync(
            JobId jobId, string operationId, string reason,
            DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            inner.MarkErrorAsync(
                jobId, operationId, reason, nowUtc, cancellationToken);
    }
}
