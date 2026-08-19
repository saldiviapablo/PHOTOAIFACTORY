using Microsoft.Data.Sqlite;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Persistence;

public sealed class SqliteProjectDatabase
{
    private readonly IReadOnlyList<SqliteMigration> migrations;

    public SqliteProjectDatabase(string databasePath, IReadOnlyList<SqliteMigration>? migrations = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        Writer = new SqliteWriteCoordinator(DatabasePath);
        this.migrations = migrations ?? MigrationCatalog.All;
    }

    public string DatabasePath { get; }
    public SqliteWriteCoordinator Writer { get; }
    public string? LastMigrationBackupPath { get; private set; }

    public static string GetLiveDatabasePath(ProjectId projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId.Value))
        {
            throw new ArgumentException("Project ID is required.", nameof(projectId));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "PhotoAIFactory", "projects", projectId.Value, "project.db");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var runner = new MigrationRunner(this, migrations);
        await runner.ApplyAsync(cancellationToken).ConfigureAwait(false);
        LastMigrationBackupPath = runner.LastBackupPath ?? LastMigrationBackupPath;
    }

    public async Task<SqliteConnection> OpenConfiguredConnectionAsync(
        bool createIfMissing = false,
        CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection(createIfMissing);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    internal SqliteConnection CreateConnection(bool createIfMissing)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = createIfMissing ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        };
        return new SqliteConnection(builder.ToString());
    }

    internal static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
