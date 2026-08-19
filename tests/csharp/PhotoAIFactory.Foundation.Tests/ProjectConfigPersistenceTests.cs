using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;

namespace PhotoAIFactory.Foundation.Tests;

[TestClass]
public sealed class ProjectConfigPersistenceTests
{
    [TestMethod]
    public async Task CreateProject_PersistsAndReopens()
    {
        using var scope = new TestScope();
        var created = await scope.CreateProjectAsync();
        var reopened = await scope.Service.OpenProjectAsync(created.Project.Id);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(created.Project.Id, reopened.Project.Id);
        Assert.AreEqual("Foundation Test", reopened.Project.Name);
    }

    [TestMethod]
    public async Task InitialConfig_IsVersion1()
    {
        using var scope = new TestScope();
        var created = await scope.CreateProjectAsync();
        Assert.AreEqual(1, created.LatestConfig.VersionNumber);
        Assert.AreEqual(1, created.LatestConfig.SchemaVersion);
    }

    [TestMethod]
    public async Task ConfigVersion_IsImmutable()
    {
        using var scope = new TestScope();
        var created = await scope.CreateProjectAsync();
        await using var connection = await scope.Database.OpenConfiguredConnectionAsync();
        await AssertSqliteFailureAsync(connection,
            "UPDATE project_config_versions SET config_json='{}' WHERE config_version_id=$id;",
            ("$id", created.LatestConfig.Id));
        await AssertSqliteFailureAsync(connection,
            "DELETE FROM project_config_versions WHERE config_version_id=$id;",
            ("$id", created.LatestConfig.Id));
    }

    [TestMethod]
    public async Task NewConfig_CreatesNewRow()
    {
        using var scope = new TestScope();
        var created = await scope.CreateProjectAsync();
        var appended = await scope.Store.AppendAsync(
            created.Project.Id, scope.Config(exportQuality: 91), "config-2", TestScope.Now);
        var rows = await scope.Store.ListAsync(created.Project.Id);
        Assert.AreEqual(2, appended.VersionNumber);
        Assert.AreEqual(2, rows.Count);
    }

    [TestMethod]
    public async Task PreviousConfig_RemainsByteForByteUnchanged()
    {
        using var scope = new TestScope();
        var created = await scope.CreateProjectAsync();
        var before = await scope.ScalarAsync<string>(
            "SELECT config_json FROM project_config_versions WHERE version_number=1;");
        await scope.Store.AppendAsync(created.Project.Id, scope.Config(exportQuality: 82), "config-2", TestScope.Now);
        var after = await scope.ScalarAsync<string>(
            "SELECT config_json FROM project_config_versions WHERE version_number=1;");
        Assert.AreEqual(before, after);
    }

    [TestMethod]
    public void SameSemanticConfig_ProducesSameHash()
    {
        using var scope = new TestScope();
        var first = scope.Config(tasks: ["upscale", "DENOISE", "upscale"], presets: ["portrait", "base"]);
        var second = scope.Config(tasks: ["denoise", "UPSCALE"], presets: ["BASE", "PORTRAIT"]);
        Assert.AreEqual(Hash(first), Hash(second));
    }

    [TestMethod]
    public void DifferentConfig_ProducesDifferentHash()
    {
        using var scope = new TestScope();
        Assert.AreNotEqual(Hash(scope.Config(exportQuality: 90)), Hash(scope.Config(exportQuality: 91)));
    }

    [TestMethod]
    public async Task ConfigHash_RevalidatesAfterReopen()
    {
        using var scope = new TestScope();
        var created = await scope.CreateProjectAsync();
        var reopenedStore = new SqliteProjectStore(new SqliteProjectDatabase(scope.Database.DatabasePath));
        var rows = await reopenedStore.ListAsync(created.Project.Id);
        Assert.AreEqual(rows[0].Sha256, ProjectConfigCanonicalizer.ComputeSha256(rows[0].CanonicalJson));
        Assert.AreEqual(ProjectConfigV1.CurrentSchemaVersion, rows[0].ReadConfig().SchemaVersion);
    }

    [TestMethod]
    public async Task FailedCreateProject_RollsBackProjectAndConfig()
    {
        using var scope = new TestScope();
        await scope.Database.InitializeAsync();
        var candidate = Project.Create("Rollback candidate", TestScope.Now);
        var config = ConfigVersion.Create(candidate.Id, 1, scope.Config(), "rollback-create", TestScope.Now);
        await scope.ExecuteAsync("""
            INSERT INTO projects(project_id,name,creation_operation_key,created_at_utc,updated_at_utc)
            VALUES('seed','Seed','seed-op',$now,$now);
            INSERT INTO project_config_versions(config_version_id,project_id,version_number,schema_version,config_json,config_sha256,operation_key,created_at_utc)
            VALUES($configId,'seed',1,1,$json,$hash,'seed-config',$now);
            """,
            ("$now", TestScope.Now.ToString("O", CultureInfo.InvariantCulture)),
            ("$configId", config.Id), ("$json", config.CanonicalJson), ("$hash", config.Sha256));

        await ExpectThrowsAsync<SqliteException>(() => scope.Store.CreateAsync(candidate, config, "rollback-create"));
        Assert.AreEqual(0L, await scope.ScalarAsync<long>(
            "SELECT count(*) FROM projects WHERE project_id=$id;", ("$id", candidate.Id.Value)));
    }

    [TestMethod]
    public async Task ForeignKeyViolation_Fails()
    {
        using var scope = new TestScope();
        await scope.Database.InitializeAsync();
        await using var connection = await scope.Database.OpenConfiguredConnectionAsync();
        await AssertSqliteFailureAsync(connection, """
            INSERT INTO project_config_versions(config_version_id,project_id,version_number,schema_version,config_json,config_sha256,operation_key,created_at_utc)
            VALUES('bad','missing',1,1,'{}','0000000000000000000000000000000000000000000000000000000000000000','bad-op','2026-01-01T00:00:00.0000000+00:00');
            """);
    }

    [TestMethod]
    public async Task Migration001_AppliesOnce()
    {
        using var scope = new TestScope();
        await scope.Database.InitializeAsync();
        await scope.Database.InitializeAsync();
        Assert.AreEqual(1L, await scope.ScalarAsync<long>("SELECT count(*) FROM schema_migrations WHERE version=1;"));
    }

    [TestMethod]
    public async Task MigrationRunner_IsIdempotent()
    {
        using var scope = new TestScope();
        await scope.Database.InitializeAsync();
        var first = await scope.ScalarAsync<string>("SELECT applied_at_utc FROM schema_migrations WHERE version=1;");
        await scope.Database.InitializeAsync();
        var second = await scope.ScalarAsync<string>("SELECT applied_at_utc FROM schema_migrations WHERE version=1;");
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public async Task ModifiedAppliedMigrationHash_IsRejected()
    {
        using var scope = new TestScope();
        await scope.Database.InitializeAsync();
        await scope.ExecuteAsync("UPDATE schema_migrations SET migration_sha256=$hash WHERE version=1;",
            ("$hash", new string('0', 64)));
        await ExpectThrowsAsync<MigrationIntegrityException>(() => scope.Database.InitializeAsync());
    }

    [TestMethod]
    public async Task SQLite_PragmaForeignKeys_IsOn()
    {
        using var scope = new TestScope();
        await scope.Database.InitializeAsync();
        Assert.AreEqual(1L, await scope.ScalarAsync<long>("PRAGMA foreign_keys;"));
    }

    [TestMethod]
    public async Task SQLite_JournalMode_IsWal()
    {
        using var scope = new TestScope();
        await scope.Database.InitializeAsync();
        Assert.AreEqual("wal", (await scope.ScalarAsync<string>("PRAGMA journal_mode;")).ToLowerInvariant());
    }

    [TestMethod]
    public async Task SQLite_Synchronous_IsFull()
    {
        using var scope = new TestScope();
        await scope.Database.InitializeAsync();
        Assert.AreEqual(2L, await scope.ScalarAsync<long>("PRAGMA synchronous;"));
    }

    [TestMethod]
    public async Task ConcurrentConfigWrites_AreSerialized()
    {
        using var scope = new TestScope();
        var created = await scope.CreateProjectAsync();
        var secondDatabase = new SqliteProjectDatabase(scope.Database.DatabasePath);
        var secondStore = new SqliteProjectStore(secondDatabase);
        var writes = Enumerable.Range(0, 24).Select(index => (index % 2 == 0 ? scope.Store : secondStore).AppendAsync(
            created.Project.Id,
            scope.Config(exportQuality: 60 + index),
            $"parallel-{index}",
            TestScope.Now.AddMilliseconds(index)));
        await Task.WhenAll(writes);
        Assert.AreEqual(1, scope.Database.Writer.MaxObservedConcurrentWriters);
        Assert.AreEqual(0, scope.Database.Writer.OverlapViolationCount);
        Assert.AreEqual(1, secondDatabase.Writer.MaxObservedConcurrentWriters);
        Assert.AreEqual(0, secondDatabase.Writer.OverlapViolationCount);
    }

    [TestMethod]
    public async Task ConfigVersionNumbers_AreUniqueAndOrdered()
    {
        using var scope = new TestScope();
        var created = await scope.CreateProjectAsync();
        var writes = Enumerable.Range(0, 12).Select(index => scope.Store.AppendAsync(
            created.Project.Id, scope.Config(exportQuality: 70 + index), $"ordered-{index}", TestScope.Now.AddSeconds(index)));
        await Task.WhenAll(writes);
        var rows = await scope.Store.ListAsync(created.Project.Id);
        CollectionAssert.AreEqual(Enumerable.Range(1, 13).ToArray(), rows.Select(item => item.VersionNumber).ToArray());
    }

    [TestMethod]
    public void UnsafeInputOutputRelationship_IsRejected()
    {
        using var scope = new TestScope();
        var inside = Path.Combine(scope.InputPath, "OUTPUT");
        Assert.ThrowsExactly<ArgumentException>(() => scope.Config(outputPath: inside, includeSubfolders: true));
        Assert.ThrowsExactly<ArgumentException>(() => scope.Config(outputPath: scope.InputPath.ToUpperInvariant(), includeSubfolders: true));
    }

    [TestMethod]
    public void SafeSiblingPaths_AreAccepted()
    {
        using var scope = new TestScope();
        var config = scope.Config();
        Assert.AreEqual(Path.GetFullPath(scope.OutputPath), config.OutputFolder);
    }

    [TestMethod]
    public async Task UnicodeAndSpacesPaths_RoundTrip()
    {
        using var scope = new TestScope(unicodePaths: true);
        var created = await scope.CreateProjectAsync();
        var reopened = await scope.Service.OpenProjectAsync(created.Project.Id);
        var config = reopened!.LatestConfig.ReadConfig();
        Assert.AreEqual(Path.GetFullPath(scope.InputPath), config.InputFolder);
        Assert.AreEqual(Path.GetFullPath(scope.OutputPath), config.OutputFolder);
    }

    [TestMethod]
    public async Task ReopenAfterDispose_Works()
    {
        using var scope = new TestScope();
        var created = await scope.CreateProjectAsync();
        await using (var connection = await scope.Database.OpenConfiguredConnectionAsync())
        {
            Assert.AreEqual(System.Data.ConnectionState.Open, connection.State);
        }

        var reopened = await new SqliteProjectStore(new SqliteProjectDatabase(scope.Database.DatabasePath)).GetAsync(created.Project.Id);
        Assert.IsNotNull(reopened);
    }

    [TestMethod]
    public async Task ExistingDbMigration_CreatesBackup()
    {
        using var scope = new TestScope();
        Directory.CreateDirectory(Path.GetDirectoryName(scope.Database.DatabasePath)!);
        var builder = new SqliteConnectionStringBuilder { DataSource = scope.Database.DatabasePath, Pooling = false };
        await using (var connection = new SqliteConnection(builder.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE legacy_marker(value TEXT NOT NULL); INSERT INTO legacy_marker VALUES('keep');";
            await command.ExecuteNonQueryAsync();
        }

        await scope.Database.InitializeAsync();
        Assert.IsNotNull(scope.Database.LastMigrationBackupPath);
        Assert.IsTrue(File.Exists(scope.Database.LastMigrationBackupPath));
        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = scope.Database.LastMigrationBackupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await backup.OpenAsync();
        await using var verify = backup.CreateCommand();
        verify.CommandText = "SELECT value FROM legacy_marker;";
        Assert.AreEqual("keep", Convert.ToString(await verify.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task MigrationFailure_RollsBack()
    {
        using var scope = new TestScope();
        var failingVersion = MigrationCatalog.All.Max(migration => migration.Version) + 1;
        var migrations = MigrationCatalog.All.Concat(
            [new SqliteMigration(failingVersion, "intentional_failure", "CREATE TABLE partial(value TEXT); INVALID SQL;")]).ToArray();
        var failing = new SqliteProjectDatabase(scope.Database.DatabasePath, migrations);
        await ExpectThrowsAsync<SqliteException>(() => failing.InitializeAsync());
        var builder = new SqliteConnectionStringBuilder { DataSource = scope.Database.DatabasePath, Pooling = false };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('projects','partial','schema_migrations');";
        Assert.AreEqual(0L, Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task integrity_check_IsOk()
    {
        using var scope = new TestScope();
        await scope.CreateProjectAsync();
        Assert.AreEqual("ok", (await scope.ScalarAsync<string>("PRAGMA integrity_check;")).ToLowerInvariant());
    }

    [TestMethod]
    public async Task CorruptConfigHash_IsRejectedOnRead()
    {
        using var scope = new TestScope();
        var created = await scope.CreateProjectAsync();
        await scope.ExecuteAsync("DROP TRIGGER project_config_versions_no_update;");
        await scope.ExecuteAsync(
            "UPDATE project_config_versions SET config_sha256=$hash WHERE config_version_id=$id;",
            ("$hash", new string('f', 64)), ("$id", created.LatestConfig.Id));
        await ExpectThrowsAsync<ConfigIntegrityException>(() => scope.Store.ListAsync(created.Project.Id));
    }

    [TestMethod]
    public async Task ConfigRetry_IsIdempotentAndConflictIsExplicit()
    {
        using var scope = new TestScope();
        var created = await scope.CreateProjectAsync();
        var first = await scope.Store.AppendAsync(created.Project.Id, scope.Config(exportQuality: 88), "retry", TestScope.Now);
        var retry = await scope.Store.AppendAsync(created.Project.Id, scope.Config(exportQuality: 88), "retry", TestScope.Now.AddSeconds(1));
        Assert.AreEqual(first.Id, retry.Id);
        await ExpectThrowsAsync<IdempotencyConflictException>(() => scope.Store.AppendAsync(
            created.Project.Id, scope.Config(exportQuality: 89), "retry", TestScope.Now.AddSeconds(2)));
        Assert.AreEqual(2, (await scope.Store.ListAsync(created.Project.Id)).Count);
    }

    private static string Hash(ProjectConfigV1 config)
    {
        var json = ProjectConfigCanonicalizer.Serialize(config);
        return ProjectConfigCanonicalizer.ComputeSha256(json);
    }

    private static async Task AssertSqliteFailureAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await ExpectThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    private static async Task<T> ExpectThrowsAsync<T>(Func<Task> action) where T : Exception
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
        throw new InvalidOperationException("Unreachable.");
    }

    private sealed class TestScope : IDisposable
    {
        private readonly string root;

        public TestScope(bool unicodePaths = false)
        {
            root = Path.Combine(Path.GetTempPath(), "PhotoAIFactory.Foundation.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            InputPath = Path.Combine(root, unicodePaths ? "Entrada Ñ con espacios" : "input");
            OutputPath = Path.Combine(root, unicodePaths ? "Salida 日本 con espacios" : "output");
            Database = new SqliteProjectDatabase(Path.Combine(root, "db", "project.db"));
            Store = new SqliteProjectStore(Database);
            Service = new ProjectService(Store);
        }

        public static DateTimeOffset Now => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        public string InputPath { get; }
        public string OutputPath { get; }
        public SqliteProjectDatabase Database { get; }
        public SqliteProjectStore Store { get; }
        public ProjectService Service { get; }

        public ProjectConfigV1 Config(
            int exportQuality = 90,
            IEnumerable<string>? tasks = null,
            IEnumerable<string>? presets = null,
            string? outputPath = null,
            bool includeSubfolders = true) =>
            new(
                InputPath,
                outputPath ?? OutputPath,
                includeSubfolders,
                RevealMode.DtAuto,
                true,
                "technical-standard",
                SemanticMode.Standard,
                ComfyUiMode.Auto,
                tasks ?? ["denoise", "upscale"],
                presets ?? ["base", "portrait"],
                "jpeg",
                exportQuality,
                30);

        public Task<ProjectSnapshot> CreateProjectAsync() =>
            Service.CreateProjectAsync("Foundation Test", Config(), "create-project", Now);

        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await Database.OpenConfiguredConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }

            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await Database.OpenConfiguredConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            }

            var result = await command.ExecuteScalarAsync();
            return (T)Convert.ChangeType(result!, typeof(T), CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
