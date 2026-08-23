using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Qa;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class Phase7Slice1PersistenceTests
{
    [TestMethod]
    public void Migration_008_is_registered_after_comfyui()
    {
        var migration8 = MigrationCatalog.All.Single(m => m.Version == 8);
        Assert.AreEqual(8, migration8.Version);
        Assert.AreEqual("qa_review_publish", migration8.Name);
        StringAssert.Contains(migration8.Sql, "QA_COMPLETE");
        StringAssert.Contains(migration8.Sql, "OUTPUT_PUBLISHED");
        StringAssert.Contains(migration8.Sql, "qa_results");
        StringAssert.Contains(migration8.Sql, "review_items");
        StringAssert.Contains(migration8.Sql, "publications");
    }

    [TestMethod]
    public async Task Migration008_FreshDatabase_ConfiguresIntegrityTriggersAndCheckpoints()
    {
        var root = TempRoot("migration008-fresh");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();
            await using var connection = await database.OpenConfiguredConnectionAsync();

            Assert.AreEqual(
                MigrationCatalog.All[7].Sha256,
                await ScalarStringAsync(
                    connection,
                    "SELECT migration_sha256 FROM schema_migrations WHERE version=8;"));
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
            Assert.IsNull(await ScalarStringAsync(
                connection, "PRAGMA foreign_key_check;"));

            Assert.AreEqual(8L, await ScalarLongAsync(
                connection,
                """
                SELECT count(*)
                FROM sqlite_master
                WHERE type='trigger'
                  AND name IN (
                      'job_checkpoints_no_update',
                      'job_checkpoints_no_delete',
                      'qa_results_no_update',
                      'qa_results_no_delete',
                      'publications_no_update',
                      'publications_no_delete',
                      'review_items_no_delete',
                      'review_items_lifecycle_update'
                  );
                """));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Migration008_UpgradeFrom007_CreatesBackupAndPreservesCheckpoints()
    {
        var root = TempRoot("migration008-upgrade");
        try
        {
            var path = Path.Combine(root, "project.db");
            var phase6 = new SqliteProjectDatabase(
                path,
                MigrationCatalog.All.Take(7).ToArray());
            await phase6.InitializeAsync();

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var configId = Guid.NewGuid().ToString("N");
            var assetId = Guid.NewGuid().ToString("N");
            var sha = new string('a', 64);

            await using (var seedConn = await phase6.OpenConfiguredConnectionAsync())
            {
                await SeedJobAsync(seedConn, projectId, photoId, jobId, configId, assetId, sha);
                await using var cmd = seedConn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc)
                    VALUES('cp-comfy', $jobId, 'COMFYUI_COMPLETE', 'att-1', $sha, '2026-08-23T00:00:00.0000000Z');
                    """;
                cmd.Parameters.AddWithValue("$jobId", jobId.Value);
                cmd.Parameters.AddWithValue("$sha", sha);
                await cmd.ExecuteNonQueryAsync();
            }

            var upgraded = new SqliteProjectDatabase(
                path,
                MigrationCatalog.All.Take(8).ToArray());
            await upgraded.InitializeAsync();

            Assert.IsNotNull(upgraded.LastMigrationBackupPath);
            Assert.IsTrue(File.Exists(upgraded.LastMigrationBackupPath));

            await using var connection = await upgraded.OpenConfiguredConnectionAsync();
            Assert.AreEqual(8L, await ScalarLongAsync(
                connection, "SELECT max(version) FROM schema_migrations;"));

            for (var index = 0; index < 8; index++)
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
                1L,
                await ScalarLongAsync(
                    connection,
                    $"SELECT count(*) FROM job_checkpoints WHERE job_id='{jobId.Value}' AND stage_name='COMFYUI_COMPLETE';"));

            Assert.AreEqual("ok", await ScalarStringAsync(connection, "PRAGMA integrity_check;"));
            Assert.IsNull(await ScalarStringAsync(connection, "PRAGMA foreign_key_check;"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Migration008_ChecksumDriftIsRejected()
    {
        var root = TempRoot("migration008-drift");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();
            await using (var connection = await database.OpenConfiguredConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "UPDATE schema_migrations SET migration_sha256=$sha WHERE version=8;";
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
    public async Task JobCheckpoints_AcceptsNewStagesAndRejectsInvalid()
    {
        var root = TempRoot("checkpoints-stages");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();
            var store = new SqliteQaStore(database);

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var configId = Guid.NewGuid().ToString("N");
            var assetId = Guid.NewGuid().ToString("N");
            var sha = new string('b', 64);

            await using (var conn = await database.OpenConfiguredConnectionAsync())
            {
                await SeedJobAsync(conn, projectId, photoId, jobId, configId, assetId, sha);
            }

            await store.InsertCheckpointAsync(
                jobId, "QA_COMPLETE", "att-1", sha, DateTimeOffset.UtcNow);
            Assert.IsTrue(await store.HasCheckpointAsync(jobId, "QA_COMPLETE"));

            await store.InsertCheckpointAsync(
                jobId, "OUTPUT_PUBLISHED", "att-1", sha, DateTimeOffset.UtcNow);
            Assert.IsTrue(await store.HasCheckpointAsync(jobId, "OUTPUT_PUBLISHED"));

            await Assert.ThrowsExactlyAsync<SqliteException>(
                () => store.InsertCheckpointAsync(
                    jobId, "INVALID_STAGE_NAME", "att-1", sha, DateTimeOffset.UtcNow));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task QaResults_PersistReadReopenAndImmutability()
    {
        var root = TempRoot("qa-results-crud");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();
            var store = new SqliteQaStore(database);

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var configId = Guid.NewGuid().ToString("N");
            var assetId = Guid.NewGuid().ToString("N");
            var sha = new string('c', 64);

            await using (var conn = await database.OpenConfiguredConnectionAsync())
            {
                await SeedJobAsync(conn, projectId, photoId, jobId, configId, assetId, sha);
            }

            var request = new PersistQaResultRequest(
                jobId,
                "attempt-qa-1",
                "PASS",
                JsonDocument.Parse("""{"decision":"PASS","score":99.5}""").RootElement.Clone(),
                @"C:\images\image1.jpg",
                sha,
                DateTimeOffset.UtcNow);

            await store.PersistQaResultAsync(request);

            Assert.IsTrue(await store.HasQaResultAsync(jobId));
            var read = await store.GetQaResultAsync(jobId);
            Assert.IsNotNull(read);
            Assert.AreEqual(jobId.Value, read.JobId.Value);
            Assert.AreEqual("PASS", read.Decision);
            Assert.AreEqual("attempt-qa-1", read.AttemptId);
            Assert.AreEqual(sha, read.InputSha256);

            // Idempotent duplicate insert
            await store.PersistQaResultAsync(request);

            // Differing duplicate insert throws
            var differing = request with { Decision = "REVIEW" };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => store.PersistQaResultAsync(differing));

            // Reopen database
            var reopenedDatabase = new SqliteProjectDatabase(path);
            var reopenedStore = new SqliteQaStore(reopenedDatabase);
            var reopenedRead = await reopenedStore.GetQaResultAsync(jobId);
            Assert.IsNotNull(reopenedRead);
            Assert.AreEqual("PASS", reopenedRead.Decision);

            // Immutability triggers
            await using var connDirect = await reopenedDatabase.OpenConfiguredConnectionAsync();
            await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            {
                await using var updateCmd = connDirect.CreateCommand();
                updateCmd.CommandText = "UPDATE qa_results SET decision='FATAL' WHERE job_id=$jobId;";
                updateCmd.Parameters.AddWithValue("$jobId", jobId.Value);
                await updateCmd.ExecuteNonQueryAsync();
            });

            await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            {
                await using var deleteCmd = connDirect.CreateCommand();
                deleteCmd.CommandText = "DELETE FROM qa_results WHERE job_id=$jobId;";
                deleteCmd.Parameters.AddWithValue("$jobId", jobId.Value);
                await deleteCmd.ExecuteNonQueryAsync();
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task QaResults_RejectsInvalidConstraintsAndFk()
    {
        var root = TempRoot("qa-results-constraints");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();
            var store = new SqliteQaStore(database);

            var validJobId = JobId.New();
            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var configId = Guid.NewGuid().ToString("N");
            var assetId = Guid.NewGuid().ToString("N");
            var sha = new string('d', 64);

            await using (var conn = await database.OpenConfiguredConnectionAsync())
            {
                await SeedJobAsync(conn, projectId, photoId, validJobId, configId, assetId, sha);
            }

            // FK violation: invalid job_id
            var nonExistentJob = JobId.New();
            await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                store.PersistQaResultAsync(new PersistQaResultRequest(
                    nonExistentJob,
                    "att-1",
                    "PASS",
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    "path.jpg",
                    sha,
                    DateTimeOffset.UtcNow)));

            // Check violation: invalid decision
            await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                store.PersistQaResultAsync(new PersistQaResultRequest(
                    validJobId,
                    "att-1",
                    "INVALID_DECISION",
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    "path.jpg",
                    sha,
                    DateTimeOffset.UtcNow)));

            // Check violation: invalid sha length
            await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                store.PersistQaResultAsync(new PersistQaResultRequest(
                    validJobId,
                    "att-1",
                    "PASS",
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    "path.jpg",
                    "short_sha",
                    DateTimeOffset.UtcNow)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReviewItems_PersistReadDuplicatePendingBlockedAndResolveImmutability()
    {
        var root = TempRoot("review-items-tests");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();
            var store = new SqliteQaStore(database);

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var configId = Guid.NewGuid().ToString("N");
            var assetId = Guid.NewGuid().ToString("N");
            var sha = new string('e', 64);

            await using (var conn = await database.OpenConfiguredConnectionAsync())
            {
                await SeedJobAsync(conn, projectId, photoId, jobId, configId, assetId, sha);
            }

            var itemId = Guid.NewGuid().ToString("N");
            await store.CreateReviewItemAsync(new CreateReviewItemRequest(
                itemId,
                jobId,
                "FINAL",
                DateTimeOffset.UtcNow));

            var item = await store.GetPendingReviewItemAsync(jobId, "FINAL");
            Assert.IsNotNull(item);
            Assert.AreEqual(itemId, item.ReviewItemId);
            Assert.AreEqual("PENDING", item.Status);
            Assert.AreEqual("FINAL", item.ReviewKind);

            // Duplicate pending item for same job and review_kind is blocked by store check and unique index
            var secondItemId = Guid.NewGuid().ToString("N");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                store.CreateReviewItemAsync(new CreateReviewItemRequest(
                    secondItemId,
                    jobId,
                    "FINAL",
                    DateTimeOffset.UtcNow)));

            // Direct SQL insert blocked by partial index ux_review_items_pending
            await using (var connDirect = await database.OpenConfiguredConnectionAsync())
            {
                await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
                {
                    await using var dupCmd = connDirect.CreateCommand();
                    dupCmd.CommandText = "INSERT INTO review_items(review_item_id, job_id, review_kind, status, created_at_utc) VALUES('dup-pending', $jobId, 'FINAL', 'PENDING', '2026-08-23T00:00:00Z');";
                    dupCmd.Parameters.AddWithValue("$jobId", jobId.Value);
                    await dupCmd.ExecuteNonQueryAsync();
                });
            }

            // Another kind (PRE) is allowed
            var preItemId = Guid.NewGuid().ToString("N");
            await store.CreateReviewItemAsync(new CreateReviewItemRequest(
                preItemId,
                jobId,
                "PRE",
                DateTimeOffset.UtcNow));

            var preItem = await store.GetPendingReviewItemAsync(jobId, "PRE");
            Assert.IsNotNull(preItem);
            Assert.AreEqual(preItemId, preItem.ReviewItemId);

            // Resolve item via direct update
            await using (var conn = await database.OpenConfiguredConnectionAsync())
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.CommandText = """
                    UPDATE review_items
                    SET status='RESOLVED',
                        resolved_at_utc='2026-08-23T01:00:00Z',
                        resolution='APPROVED',
                        resolution_operation_id='op-123'
                    WHERE review_item_id=$id;
                    """;
                updateCmd.Parameters.AddWithValue("$id", itemId);
                await updateCmd.ExecuteNonQueryAsync();

                // Once resolved, further updates fail (trigger review_items_resolved_immutable)
                await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
                {
                    await using var modifyResolved = conn.CreateCommand();
                    modifyResolved.CommandText = """
                        UPDATE review_items SET resolution='REJECTED' WHERE review_item_id=$id;
                        """;
                    modifyResolved.Parameters.AddWithValue("$id", itemId);
                    await modifyResolved.ExecuteNonQueryAsync();
                });

                // Deletes fail (trigger review_items_no_delete)
                await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
                {
                    await using var deleteCmd = conn.CreateCommand();
                    deleteCmd.CommandText = "DELETE FROM review_items WHERE review_item_id=$id;";
                    deleteCmd.Parameters.AddWithValue("$id", itemId);
                    await deleteCmd.ExecuteNonQueryAsync();
                });
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReviewItems_EnforcesLifecycleCheckConstraintsAndFieldImmutability()
    {
        var root = TempRoot("review-items-lifecycle-checks");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var configId = Guid.NewGuid().ToString("N");
            var assetId = Guid.NewGuid().ToString("N");
            var sha = new string('9', 64);

            await using var conn = await database.OpenConfiguredConnectionAsync();
            await SeedJobAsync(conn, projectId, photoId, jobId, configId, assetId, sha);

            // Invalid check constraint: PENDING with non-null resolution
            await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            {
                await using var badPending = conn.CreateCommand();
                badPending.CommandText = """
                    INSERT INTO review_items(review_item_id, job_id, review_kind, status, created_at_utc, resolution)
                    VALUES('bad-1', $jobId, 'FINAL', 'PENDING', '2026-08-23T00:00:00Z', 'APPROVED');
                    """;
                badPending.Parameters.AddWithValue("$jobId", jobId.Value);
                await badPending.ExecuteNonQueryAsync();
            });

            // Invalid check constraint: RESOLVED with null resolution
            await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            {
                await using var badResolved = conn.CreateCommand();
                badResolved.CommandText = """
                    INSERT INTO review_items(review_item_id, job_id, review_kind, status, created_at_utc, resolved_at_utc)
                    VALUES('bad-2', $jobId, 'FINAL', 'RESOLVED', '2026-08-23T00:00:00Z', '2026-08-23T01:00:00Z');
                    """;
                badResolved.Parameters.AddWithValue("$jobId", jobId.Value);
                await badResolved.ExecuteNonQueryAsync();
            });

            // Valid insert pending
            await using (var validPending = conn.CreateCommand())
            {
                validPending.CommandText = """
                    INSERT INTO review_items(review_item_id, job_id, review_kind, status, created_at_utc)
                    VALUES('good-1', $jobId, 'FINAL', 'PENDING', '2026-08-23T00:00:00Z');
                    """;
                validPending.Parameters.AddWithValue("$jobId", jobId.Value);
                await validPending.ExecuteNonQueryAsync();
            }

            // Attempting to modify immutable field (job_id or review_kind) fails trigger
            await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
            {
                await using var updateField = conn.CreateCommand();
                updateField.CommandText = "UPDATE review_items SET review_kind='PRE' WHERE review_item_id='good-1';";
                await updateField.ExecuteNonQueryAsync();
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task JobCheckpoints_StrictIdempotencyEnforced()
    {
        var root = TempRoot("checkpoints-strict-idempotency");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();
            var store = new SqliteQaStore(database);

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var configId = Guid.NewGuid().ToString("N");
            var assetId = Guid.NewGuid().ToString("N");
            var sha = new string('7', 64);

            await using (var conn = await database.OpenConfiguredConnectionAsync())
            {
                await SeedJobAsync(conn, projectId, photoId, jobId, configId, assetId, sha);
            }

            // Insert QA_COMPLETE
            await store.InsertCheckpointAsync(jobId, "QA_COMPLETE", "att-1", sha, DateTimeOffset.UtcNow);

            // Replay with identical parameters succeeds
            await store.InsertCheckpointAsync(jobId, "QA_COMPLETE", "att-1", sha, DateTimeOffset.UtcNow);

            // Replay with different attempt throws InvalidOperationException
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                store.InsertCheckpointAsync(jobId, "QA_COMPLETE", "att-2", sha, DateTimeOffset.UtcNow));

            // Replay with different fingerprint throws InvalidOperationException
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                store.InsertCheckpointAsync(jobId, "QA_COMPLETE", "att-1", new string('8', 64), DateTimeOffset.UtcNow));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Publications_PersistReadReopenConstraintsAndImmutability()
    {
        var root = TempRoot("publications-crud");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();
            var store = new SqliteQaStore(database);

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var configId = Guid.NewGuid().ToString("N");
            var assetId = Guid.NewGuid().ToString("N");
            var sha = new string('f', 64);

            await using (var conn = await database.OpenConfiguredConnectionAsync())
            {
                await SeedJobAsync(conn, projectId, photoId, jobId, configId, assetId, sha);
            }

            var pubId = Guid.NewGuid().ToString("N");
            var pubReq = new PersistPublicationRequest(
                pubId,
                jobId,
                "attempt-pub-1",
                "FINAL",
                @"C:\export\FINAL\IMG_0001.jpg",
                sha,
                1024 * 1024,
                4000,
                3000,
                @"C:\export\.photo-ai-factory\history\IMG_0001.json",
                DateTimeOffset.UtcNow);

            await store.PersistPublicationAsync(pubReq);

            Assert.IsTrue(await store.HasPublicationAsync(jobId));
            var read = await store.GetPublicationAsync(jobId);
            Assert.IsNotNull(read);
            Assert.AreEqual(pubId, read.PublicationId);
            Assert.AreEqual("FINAL", read.DestinationKind);
            Assert.AreEqual(4000, read.Width);
            Assert.AreEqual(3000, read.Height);
            Assert.AreEqual(1024 * 1024, read.SizeBytes);

            // Duplicate identical insert is idempotent
            await store.PersistPublicationAsync(pubReq);

            // Differing duplicate insert throws
            var differingPub = pubReq with { DestinationPath = @"C:\other\path.jpg" };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => store.PersistPublicationAsync(differingPub));

            // Immutability triggers
            await using (var connDirect = await database.OpenConfiguredConnectionAsync())
            {
                await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
                {
                    await using var updateCmd = connDirect.CreateCommand();
                    updateCmd.CommandText = "UPDATE publications SET size_bytes=999 WHERE job_id=$jobId;";
                    updateCmd.Parameters.AddWithValue("$jobId", jobId.Value);
                    await updateCmd.ExecuteNonQueryAsync();
                });

                await Assert.ThrowsExactlyAsync<SqliteException>(async () =>
                {
                    await using var deleteCmd = connDirect.CreateCommand();
                    deleteCmd.CommandText = "DELETE FROM publications WHERE job_id=$jobId;";
                    deleteCmd.Parameters.AddWithValue("$jobId", jobId.Value);
                    await deleteCmd.ExecuteNonQueryAsync();
                });
            }

            // Constraint violations
            var job2 = JobId.New();
            await using (var conn = await database.OpenConfiguredConnectionAsync())
            {
                await SeedJobAsync(conn, projectId, PhotoId.New(), job2, configId, Guid.NewGuid().ToString("N"), new string('1', 64));
            }

            // Invalid destination kind
            await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                store.PersistPublicationAsync(pubReq with { JobId = job2, DestinationKind = "INVALID_KIND" }));

            // Invalid size_bytes (<= 0)
            await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                store.PersistPublicationAsync(pubReq with { JobId = job2, SizeBytes = 0 }));

            // Invalid width (<= 0)
            await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                store.PersistPublicationAsync(pubReq with { JobId = job2, Width = 0 }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TransactionRollback_LeavesNoPartialData()
    {
        var root = TempRoot("rollback-test");
        try
        {
            var path = Path.Combine(root, "project.db");
            var database = new SqliteProjectDatabase(path);
            await database.InitializeAsync();

            var projectId = ProjectId.New();
            var photoId = PhotoId.New();
            var jobId = JobId.New();
            var configId = Guid.NewGuid().ToString("N");
            var assetId = Guid.NewGuid().ToString("N");
            var sha = new string('8', 64);

            await using (var conn = await database.OpenConfiguredConnectionAsync())
            {
                await SeedJobAsync(conn, projectId, photoId, jobId, configId, assetId, sha);
            }

            await using (var conn = await database.OpenConfiguredConnectionAsync())
            {
                await using var transaction = (SqliteTransaction)await conn.BeginTransactionAsync();
                try
                {
                    await using var cmd1 = conn.CreateCommand();
                    cmd1.Transaction = transaction;
                    cmd1.CommandText = """
                        INSERT INTO qa_results(qa_result_id, job_id, attempt_id, decision, result_json, input_path, input_sha256, created_at_utc)
                        VALUES('qa-temp', $jobId, 'att-1', 'PASS', '{"score":100}', 'p.jpg', $sha, '2026-08-23T00:00:00Z');
                        """;
                    cmd1.Parameters.AddWithValue("$jobId", jobId.Value);
                    cmd1.Parameters.AddWithValue("$sha", sha);
                    await cmd1.ExecuteNonQueryAsync();

                    // Force deliberate violation
                    await using var cmd2 = conn.CreateCommand();
                    cmd2.Transaction = transaction;
                    cmd2.CommandText = "INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc) VALUES('bad', $jobId, 'INVALID_STAGE', 'att-1', 'f', '2026-08-23T00:00:00Z');";
                    cmd2.Parameters.AddWithValue("$jobId", jobId.Value);
                    await cmd2.ExecuteNonQueryAsync();

                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                }
            }

            var store = new SqliteQaStore(database);
            Assert.IsFalse(await store.HasQaResultAsync(jobId));
            Assert.IsFalse(await store.HasCheckpointAsync(jobId, "QA_COMPLETE"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TempRoot(string label)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "paf-phase7-tests",
            $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task SeedJobAsync(
        SqliteConnection connection,
        ProjectId projectId,
        PhotoId photoId,
        JobId jobId,
        string configVersionId,
        string assetId,
        string assetSha256)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

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
                    '{"output_folder":"C:\\out"}', $sha,
                    'init-' || $configVersionId, $now);
                """;
            insertConfig.Parameters.AddWithValue("$configVersionId", configVersionId);
            insertConfig.Parameters.AddWithValue("$projectId", projectId.Value);
            insertConfig.Parameters.AddWithValue("$sha", assetSha256);
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
                    'C:\input\test.jpg', 'test.jpg', 'managed.jpg',
                    'JPEG', 'JPEG_MASTER', 'ARCHIVED', 1000,
                    $sha,
                    'NOT_APPLICABLE', 0, 0,
                    'NOT_RAW', $now, $now);
                """;
            insertAsset.Parameters.AddWithValue("$assetId", assetId);
            insertAsset.Parameters.AddWithValue("$projectId", projectId.Value);
            insertAsset.Parameters.AddWithValue("$photoId", photoId.Value);
            insertAsset.Parameters.AddWithValue("$sha", assetSha256);
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
            insertJob.Parameters.AddWithValue("$sha", assetSha256);
            insertJob.Parameters.AddWithValue("$now", now);
            await insertJob.ExecuteNonQueryAsync();
        }
    }
}
