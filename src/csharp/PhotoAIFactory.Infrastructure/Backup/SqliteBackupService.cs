using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoAIFactory.Application.Backup;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;

namespace PhotoAIFactory.Infrastructure.Backup;

public sealed class SqliteBackupService : IBackupService
{
    private readonly IProjectStoreFactory projectStores;
    private readonly ILogger<SqliteBackupService> logger;
    private readonly SemaphoreSlim backupLock = new(1, 1);

    public SqliteBackupService(
        IProjectStoreFactory projectStores,
        ILogger<SqliteBackupService>? logger = null)
    {
        this.projectStores = projectStores;
        this.logger = logger ?? NullLogger<SqliteBackupService>.Instance;
    }

    public async Task<BackupResult> CreateBackupAsync(
        ProjectId projectId,
        string backupRootFolder,
        CancellationToken cancellationToken = default)
    {
        await backupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var projectStore = projectStores.Open(projectId);
            var database = ((SqliteProjectStore)projectStore).Database;

            var targetDirectory = Path.Combine(backupRootFolder, ".photo-ai-factory", "backups", projectId.Value);
            Directory.CreateDirectory(targetDirectory);

            var nowUtc = DateTimeOffset.UtcNow;
            var timestampStr = nowUtc.ToString("yyyyMMddTHHmmssZ");
            var uniqueId = Guid.NewGuid().ToString("N")[..8];
            var backupBaseName = $"backup_{projectId.Value}_{timestampStr}_{uniqueId}";
            var tempBackupPath = Path.Combine(targetDirectory, $"{backupBaseName}.tmp.db");
            var finalBackupPath = Path.Combine(targetDirectory, $"{backupBaseName}.db");
            var manifestPath = Path.Combine(targetDirectory, $"{backupBaseName}.manifest.json");

            if (File.Exists(tempBackupPath)) File.Delete(tempBackupPath);

            // 1. Perform SQLite online backup using BackupDatabase
            await using (var sourceConn = await database.OpenConfiguredConnectionAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                await using var destConn = new SqliteConnection($"Data Source={tempBackupPath};Pooling=False");
                await destConn.OpenAsync(cancellationToken).ConfigureAwait(false);
                sourceConn.BackupDatabase(destConn);
            }

            // 2. Validate backup integrity
            var totalTables = 0;
            var actualSchemaVersion = 0;
            await using (var verifyConn = new SqliteConnection($"Data Source={tempBackupPath};Pooling=False"))
            {
                await verifyConn.OpenAsync(cancellationToken).ConfigureAwait(false);

                // Integrity check
                await using (var cmd = verifyConn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA integrity_check;";
                    var integrityResult = (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        SqliteConnection.ClearAllPools();
                        File.Delete(tempBackupPath);
                        return new BackupResult(false, string.Empty, string.Empty, null, $"Backup failed integrity check: {integrityResult}");
                    }
                }

                // Foreign key check
                await using (var cmd = verifyConn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA foreign_key_check;";
                    await using var fkReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    if (await fkReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        SqliteConnection.ClearAllPools();
                        File.Delete(tempBackupPath);
                        return new BackupResult(false, string.Empty, string.Empty, null, "Backup failed foreign key check.");
                    }
                }

                // Schema version
                await using (var cmd = verifyConn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
                    var val = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    if (val is not null and not DBNull)
                    {
                        actualSchemaVersion = Convert.ToInt32(val);
                    }
                }

                // Table count
                await using (var cmd = verifyConn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table';";
                    totalTables = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                }
            }

            SqliteConnection.ClearAllPools();

            // 3. Compute SHA-256 and promote candidate
            var backupBytes = await File.ReadAllBytesAsync(tempBackupPath, cancellationToken).ConfigureAwait(false);
            var sha256 = Convert.ToHexString(SHA256.HashData(backupBytes)).ToLowerInvariant();
            var sizeBytes = backupBytes.LongLength;

            File.Move(tempBackupPath, finalBackupPath, overwrite: false);

            var appVersion = typeof(SqliteBackupService).Assembly.GetName().Version?.ToString() ?? "1.0.0";
            var manifest = new BackupManifest(
                SchemaVersion: actualSchemaVersion,
                ProjectId: projectId.Value,
                BackupFileName: Path.GetFileName(finalBackupPath),
                BackupSha256: sha256,
                BackupSizeBytes: sizeBytes,
                TotalTables: totalTables,
                CreatedAtUtc: nowUtc,
                CreatorAppVersion: appVersion);

            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Backup created successfully for project {ProjectId} at {BackupPath}", projectId.Value, finalBackupPath);

            return new BackupResult(true, finalBackupPath, manifestPath, manifest, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create backup for project {ProjectId}", projectId.Value);
            return new BackupResult(false, string.Empty, string.Empty, null, ex.Message);
        }
        finally
        {
            backupLock.Release();
        }
    }

    public async Task<IReadOnlyList<BackupManifest>> ListVerifiedBackupsAsync(
        ProjectId projectId,
        string backupRootFolder,
        CancellationToken cancellationToken = default)
    {
        var targetDirectory = Path.Combine(backupRootFolder, ".photo-ai-factory", "backups", projectId.Value);
        if (!Directory.Exists(targetDirectory)) return [];

        var manifests = new List<BackupManifest>();
        var manifestFiles = Directory.GetFiles(targetDirectory, "*.manifest.json");

        foreach (var mf in manifestFiles)
        {
            try
            {
                var content = await File.ReadAllBytesAsync(mf, cancellationToken).ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<BackupManifest>(content);
                if (manifest is not null && string.Equals(manifest.ProjectId, projectId.Value, StringComparison.OrdinalIgnoreCase))
                {
                    // Verify backup file exists and matches hash
                    var dbPath = Path.Combine(targetDirectory, manifest.BackupFileName);
                    if (File.Exists(dbPath))
                    {
                        var bytes = await File.ReadAllBytesAsync(dbPath, cancellationToken).ConfigureAwait(false);
                        var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                        if (string.Equals(actualSha, manifest.BackupSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            manifests.Add(manifest);
                        }
                    }
                }
            }
            catch
            {
                // Skip invalid manifests
            }
        }

        return manifests.OrderByDescending(m => m.CreatedAtUtc).ToList();
    }

    public async Task<int> EnforceRetentionAsync(
        ProjectId projectId,
        string backupRootFolder,
        int keepCount = 5,
        CancellationToken cancellationToken = default)
    {
        var safeKeepCount = Math.Max(1, keepCount); // Always keep at least 1 known good backup!
        var verified = await ListVerifiedBackupsAsync(projectId, backupRootFolder, cancellationToken).ConfigureAwait(false);

        if (verified.Count <= safeKeepCount) return 0;

        var toDelete = verified.Skip(safeKeepCount).ToList();
        var targetDirectory = Path.Combine(backupRootFolder, ".photo-ai-factory", "backups", projectId.Value);
        var deletedCount = 0;

        foreach (var oldBackup in toDelete)
        {
            try
            {
                var dbPath = Path.Combine(targetDirectory, oldBackup.BackupFileName);
                var manifestPath = Path.Combine(targetDirectory, $"{Path.GetFileNameWithoutExtension(oldBackup.BackupFileName)}.manifest.json");

                if (File.Exists(dbPath)) File.Delete(dbPath);
                if (File.Exists(manifestPath)) File.Delete(manifestPath);
                deletedCount++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to prune old backup {BackupFile}", oldBackup.BackupFileName);
            }
        }

        return deletedCount;
    }
}
