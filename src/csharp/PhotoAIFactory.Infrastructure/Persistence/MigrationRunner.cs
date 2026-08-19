using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PhotoAIFactory.Infrastructure.Persistence;

public sealed record SqliteMigration(int Version, string Name, string Sql)
{
    public string Sha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Sql))).ToLowerInvariant();
}

public sealed class MigrationIntegrityException(string message) : IOException(message);

public static class MigrationCatalog
{
    public static IReadOnlyList<SqliteMigration> All { get; } =
    [
        new(1, "initial_project_config", ReadEmbedded("001_initial_project_config.sql")),
        new(2, "project_lifecycle", ReadEmbedded("002_project_lifecycle.sql")),
        new(3, "ingestion", ReadEmbedded("003_ingestion.sql"))
    ];

    private static string ReadEmbedded(string fileName)
    {
        var assembly = typeof(MigrationCatalog).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Migration resource {resource} was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}

public sealed class MigrationRunner(SqliteProjectDatabase database, IReadOnlyList<SqliteMigration> migrations)
{
    public string? LastBackupPath { get; private set; }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        ValidateMigrationList();
        var parent = Path.GetDirectoryName(database.DatabasePath)
            ?? throw new InvalidOperationException("Database parent path is unavailable.");
        Directory.CreateDirectory(parent);
        var existedWithContent = File.Exists(database.DatabasePath) && new FileInfo(database.DatabasePath).Length > 0;

        await using var writerLease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = database.CreateConnection(createIfMissing: true);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var applied = await ReadAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
        ValidateApplied(applied);
        var pending = migrations.Where(migration => !applied.ContainsKey(migration.Version)).ToArray();
        if (pending.Length == 0)
        {
            await SqliteProjectDatabase.ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureWalAsync(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (existedWithContent)
        {
            LastBackupPath = await BackupAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        await SqliteProjectDatabase.ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureWalAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, transaction, SchemaMigrationsSql, cancellationToken).ConfigureAwait(false);
            foreach (var migration in pending)
            {
                await ExecuteAsync(connection, transaction, migration.Sql, cancellationToken).ConfigureAwait(false);
                await using var record = connection.CreateCommand();
                record.Transaction = transaction;
                record.CommandText = """
                    INSERT INTO schema_migrations(version, name, migration_sha256, applied_at_utc)
                    VALUES ($version, $name, $sha256, $appliedAtUtc);
                    """;
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$sha256", migration.Sha256);
                record.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private void ValidateMigrationList()
    {
        if (migrations.Count == 0 || migrations.Any(item => item.Version <= 0 || string.IsNullOrWhiteSpace(item.Name)))
        {
            throw new InvalidOperationException("At least one valid migration is required.");
        }

        var ordered = migrations.OrderBy(item => item.Version).Select(item => item.Version).ToArray();
        if (!ordered.SequenceEqual(migrations.Select(item => item.Version)) || ordered.Distinct().Count() != ordered.Length)
        {
            throw new InvalidOperationException("Migrations must be uniquely ordered by version.");
        }
    }

    private void ValidateApplied(IReadOnlyDictionary<int, AppliedMigration> applied)
    {
        foreach (var item in applied)
        {
            var known = migrations.SingleOrDefault(migration => migration.Version == item.Key)
                ?? throw new MigrationIntegrityException($"Database contains unknown migration {item.Key}.");
            if (!string.Equals(known.Name, item.Value.Name, StringComparison.Ordinal) ||
                !string.Equals(known.Sha256, item.Value.Sha256, StringComparison.Ordinal))
            {
                throw new MigrationIntegrityException($"Applied migration {item.Key} name or SHA-256 differs from the executable catalog.");
            }
        }
    }

    private static async Task<Dictionary<int, AppliedMigration>> ReadAppliedAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations';";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
        {
            return [];
        }

        var result = new Dictionary<int, AppliedMigration>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, name, migration_sha256 FROM schema_migrations ORDER BY version;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetInt32(0), new AppliedMigration(reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }

    private async Task<string> BackupAsync(SqliteConnection source, CancellationToken cancellationToken)
    {
        var backupDirectory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "backups");
        Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(
            backupDirectory,
            $"project.pre-migration.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}.{Guid.NewGuid():N}.db");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        };
        await using var destination = new SqliteConnection(builder.ToString());
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
        return backupPath;
    }

    private static async Task EnsureWalAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (!string.Equals(value, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SQLite refused WAL mode; reported '{value}'.");
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record AppliedMigration(string Name, string Sha256);

    private const string SchemaMigrationsSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY NOT NULL CHECK (version > 0),
            name TEXT NOT NULL CHECK (length(trim(name)) > 0),
            migration_sha256 TEXT NOT NULL CHECK (length(migration_sha256) = 64),
            applied_at_utc TEXT NOT NULL
        );
        """;
}
