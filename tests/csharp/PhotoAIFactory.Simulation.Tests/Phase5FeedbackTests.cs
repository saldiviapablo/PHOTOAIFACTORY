using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Infrastructure.Persistence;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class Phase5FeedbackTests
{
    [TestMethod]
    public void FeedbackRecipePolicy_AcceptsConservativeReusePass1()
    {
        FeedbackRecipePolicy.Validate(Recipe());
    }

    [TestMethod]
    public void FeedbackRecipePolicy_RejectsCreativeOperation()
    {
        var recipe = JsonSerializer.SerializeToElement(new
        {
            schema_version = 1,
            recipe_version = "phase5-feedback-v1",
            strategy = "CONSERVATIVE_REUSE_PASS1",
            benchmark_status = "NOT_CALIBRATED",
            operations = new[] { new { type = "EXPOSURE", ev = 0.5 } },
            pass2_control = new
            {
                mode = "REUSE_PASS1_XMP",
                arbitrary_xmp_compilation = false,
                restart_from_managed_original = true,
                pass1_derivative_as_source = false
            },
            darktable_ai = DisabledAi()
        });

        Assert.ThrowsExactly<InvalidDataException>(
            () => FeedbackRecipePolicy.Validate(recipe));
    }

    [TestMethod]
    public void FeedbackRecipePolicy_RejectsPass1DerivativeAsPass2Source()
    {
        var recipe = JsonSerializer.SerializeToElement(new
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
                pass1_derivative_as_source = true
            },
            darktable_ai = DisabledAi()
        });

        Assert.ThrowsExactly<InvalidDataException>(
            () => FeedbackRecipePolicy.Validate(recipe));
    }

    [TestMethod]
    public void FeedbackRecipePolicy_RejectsUnprovenNeuralRestoreEnablement()
    {
        var recipe = JsonSerializer.SerializeToElement(new
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
                raw_denoise = new { enabled = true, reason = "test" },
                rgb_denoise = new { enabled = false, reason = "test" },
                upscale = new { enabled = false, reason = "test" }
            }
        });

        Assert.ThrowsExactly<InvalidDataException>(
            () => FeedbackRecipePolicy.Validate(recipe));
    }

    [TestMethod]
    public async Task Migration006_AppliesAndIsIdempotent()
    {
        var root = TempRoot("Migration006");
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
                WHERE version=6 AND name='feedback';
                """;
            Assert.AreEqual(
                1L,
                Convert.ToInt64(await migration.ExecuteScalarAsync()));

            foreach (var table in new[]
            {
                "feedback_passes",
                "feedback_inspections"
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

            await using var checkpointSchema = connection.CreateCommand();
            checkpointSchema.CommandText = """
                SELECT sql
                FROM sqlite_master
                WHERE type='table' AND name='job_checkpoints';
                """;
            var sql = Convert.ToString(
                await checkpointSchema.ExecuteScalarAsync()) ?? string.Empty;
            StringAssert.Contains(sql, "DARKTABLE_PASS1_COMPLETE");
            StringAssert.Contains(sql, "FEEDBACK_INSPECTION_COMPLETE");
            StringAssert.Contains(sql, "RAW_DENOISE_COMPLETE");
            StringAssert.Contains(sql, "DARKTABLE_PASS2_COMPLETE");

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
    public async Task Migration005_UpgradesTo006_WithBackupAndStableChecksums()
    {
        var root = TempRoot("Upgrade005");
        try
        {
            var path = Path.Combine(root, "project.db");
            var phase4 = new SqliteProjectDatabase(
                path,
                MigrationCatalog.All.Take(5).ToArray());
            await phase4.InitializeAsync();

            var upgraded = new SqliteProjectDatabase(
                path,
                MigrationCatalog.All.Take(6).ToArray());
            await upgraded.InitializeAsync();

            Assert.IsNotNull(upgraded.LastMigrationBackupPath);
            Assert.IsTrue(File.Exists(upgraded.LastMigrationBackupPath));

            await using var connection =
                await upgraded.OpenConfiguredConnectionAsync();

            await using var version = connection.CreateCommand();
            version.CommandText = "SELECT max(version) FROM schema_migrations;";
            Assert.AreEqual(
                6L,
                Convert.ToInt64(await version.ExecuteScalarAsync()));

            for (var index = 0; index < 5; index++)
            {
                await using var checksum = connection.CreateCommand();
                checksum.CommandText = """
                    SELECT migration_sha256
                    FROM schema_migrations
                    WHERE version=$version;
                    """;
                checksum.Parameters.AddWithValue("$version", index + 1);
                Assert.AreEqual(
                    MigrationCatalog.All[index].Sha256,
                    Convert.ToString(await checksum.ExecuteScalarAsync()));
            }

            await using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys;";
            Assert.AreEqual(
                1L,
                Convert.ToInt64(await foreignKeys.ExecuteScalarAsync()));

            await using var sync = connection.CreateCommand();
            sync.CommandText = "PRAGMA synchronous;";
            Assert.AreEqual(
                2L,
                Convert.ToInt64(await sync.ExecuteScalarAsync()));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonElement Recipe() =>
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
            darktable_ai = DisabledAi()
        });

    private static object DisabledAi() => new
    {
        raw_denoise = new
        {
            enabled = false,
            reason = "NOT_HEADLESS_PROVEN_AND_BENCHMARK_PENDING"
        },
        rgb_denoise = new
        {
            enabled = false,
            reason = "NOT_HEADLESS_PROVEN_AND_BENCHMARK_PENDING"
        },
        upscale = new
        {
            enabled = false,
            reason = "NOT_HEADLESS_PROVEN_AND_BENCHMARK_PENDING"
        }
    };

    private static string TempRoot(string name)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"paf-phase5-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
