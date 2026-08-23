using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoAIFactory.Application.Backup;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Infrastructure.Persistence;

namespace PhotoAIFactory.Infrastructure.Backup;

public sealed class SqliteRestoreService : IRestoreService
{
    private readonly ILogger<SqliteRestoreService> logger;
    private static readonly int MaxSupportedSchemaVersion = MigrationCatalog.All.Max(m => m.Version);

    public SqliteRestoreService(ILogger<SqliteRestoreService>? logger = null)
    {
        this.logger = logger ?? NullLogger<SqliteRestoreService>.Instance;
    }

    public async Task<RestoreVerificationResult> VerifyBackupAsync(
        string backupFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
        {
            return new RestoreVerificationResult(false, 0, null, $"Backup file not found at '{backupFilePath}'.");
        }

        try
        {
            await using var conn = new SqliteConnection($"Data Source={backupFilePath};Mode=ReadOnly;Pooling=False");
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

            // 1. Integrity check
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check;";
                var integrity = (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return new RestoreVerificationResult(false, 0, null, $"Integrity check failed: {integrity}");
                }
            }

            // 2. Foreign key check
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_key_check;";
                await using var fkReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await fkReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new RestoreVerificationResult(false, 0, null, "Foreign key check failed in backup.");
                }
            }

            // 3. Extract project ID
            string? projectId = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT project_id FROM projects LIMIT 1;";
                projectId = (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            // 4. Extract schema version
            var schemaVersion = 0;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
                var val = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (val is not null and not DBNull)
                {
                    schemaVersion = Convert.ToInt32(val);
                }
            }

            // Verify supported schema compatibility
            if (schemaVersion < 1 || schemaVersion > MaxSupportedSchemaVersion)
            {
                return new RestoreVerificationResult(
                    false,
                    schemaVersion,
                    projectId,
                    $"Unsupported or future schema version {schemaVersion} in backup. Max supported version is {MaxSupportedSchemaVersion}.");
            }

            return new RestoreVerificationResult(true, schemaVersion, projectId, null);
        }
        catch (Exception ex)
        {
            return new RestoreVerificationResult(false, 0, null, $"Failed to verify backup: {ex.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    public async Task<RestoreResult> RestoreDatabaseAsync(
        ProjectId projectId,
        string backupFilePath,
        string currentDatabasePath,
        string backupRootFolder,
        CancellationToken cancellationToken = default)
    {
        // 1. Verify candidate database integrity & schema compatibility
        var verification = await VerifyBackupAsync(backupFilePath, cancellationToken).ConfigureAwait(false);
        if (!verification.IsValid)
        {
            return new RestoreResult(false, currentDatabasePath, null, $"Cannot restore invalid backup: {verification.Error}");
        }

        if (verification.ProjectId is not null &&
            !string.Equals(verification.ProjectId, projectId.Value, StringComparison.OrdinalIgnoreCase))
        {
            return new RestoreResult(false, currentDatabasePath, null, $"Backup belongs to project '{verification.ProjectId}', not expected '{projectId.Value}'.");
        }

        // 2. Validate manifest if available next to backup
        var manifestPath = Path.Combine(
            Path.GetDirectoryName(backupFilePath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(backupFilePath)}.manifest.json");

        if (File.Exists(manifestPath))
        {
            try
            {
                var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<BackupManifest>(manifestBytes);
                if (manifest is not null)
                {
                    if (!string.Equals(manifest.ProjectId, projectId.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        return new RestoreResult(false, currentDatabasePath, null, $"Manifest project ID '{manifest.ProjectId}' does not match expected '{projectId.Value}'.");
                    }

                    var backupBytes = await File.ReadAllBytesAsync(backupFilePath, cancellationToken).ConfigureAwait(false);
                    var computedSha = Convert.ToHexString(SHA256.HashData(backupBytes)).ToLowerInvariant();
                    if (!string.Equals(computedSha, manifest.BackupSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return new RestoreResult(false, currentDatabasePath, null, $"Backup SHA-256 mismatch. Manifest: '{manifest.BackupSha256}', Actual: '{computedSha}'.");
                    }
                }
            }
            catch (Exception ex)
            {
                return new RestoreResult(false, currentDatabasePath, null, $"Manifest verification failed: {ex.Message}");
            }
        }

        // 3. Fail-closed preservation of existing live database
        string? preservedDamagedPath = null;
        if (File.Exists(currentDatabasePath))
        {
            var targetDirectory = Path.Combine(backupRootFolder, ".photo-ai-factory", "backups", projectId.Value);
            Directory.CreateDirectory(targetDirectory);
            var timestampStr = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var uniqueId = Guid.NewGuid().ToString("N")[..8];
            preservedDamagedPath = Path.Combine(targetDirectory, $"damaged_pre_restore_{projectId.Value}_{timestampStr}_{uniqueId}.db");

            try
            {
                SqliteConnection.ClearAllPools();
                File.Copy(currentDatabasePath, preservedDamagedPath, overwrite: false);
                logger.LogInformation("Preserved existing live database before restore at {PreservedPath}", preservedDamagedPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to preserve live database before restore. Aborting restore to prevent data loss.");
                return new RestoreResult(false, currentDatabasePath, null, $"Failed to preserve live database: {ex.Message}");
            }
        }

        // 4. Safe staging and live database replacement
        var tempRestoredPath = $"{currentDatabasePath}.restore.tmp";
        if (File.Exists(tempRestoredPath)) File.Delete(tempRestoredPath);

        try
        {
            SqliteConnection.ClearAllPools();

            // Clear WAL / SHM safely
            var walPath = $"{currentDatabasePath}-wal";
            var shmPath = $"{currentDatabasePath}-shm";
            if (File.Exists(walPath))
            {
                try { File.Delete(walPath); }
                catch (Exception ex)
                {
                    return new RestoreResult(false, currentDatabasePath, preservedDamagedPath, $"Cannot safely remove WAL file '{walPath}': {ex.Message}");
                }
            }

            if (File.Exists(shmPath))
            {
                try { File.Delete(shmPath); }
                catch (Exception ex)
                {
                    return new RestoreResult(false, currentDatabasePath, preservedDamagedPath, $"Cannot safely remove SHM file '{shmPath}': {ex.Message}");
                }
            }

            // Copy to staging and verify
            File.Copy(backupFilePath, tempRestoredPath, overwrite: true);
            var stagedVerification = await VerifyBackupAsync(tempRestoredPath, cancellationToken).ConfigureAwait(false);
            if (!stagedVerification.IsValid)
            {
                File.Delete(tempRestoredPath);
                return new RestoreResult(false, currentDatabasePath, preservedDamagedPath, $"Staged database verification failed: {stagedVerification.Error}");
            }

            SqliteConnection.ClearAllPools();

            // Atomically replace live DB
            File.Move(tempRestoredPath, currentDatabasePath, overwrite: true);

            // Re-open and verify restored live database
            var postCheck = await VerifyBackupAsync(currentDatabasePath, cancellationToken).ConfigureAwait(false);
            if (!postCheck.IsValid)
            {
                return new RestoreResult(false, currentDatabasePath, preservedDamagedPath, $"Post-restore live database check failed: {postCheck.Error}");
            }

            logger.LogInformation("Database successfully restored for project {ProjectId} from {BackupPath}", projectId.Value, backupFilePath);
            return new RestoreResult(true, currentDatabasePath, preservedDamagedPath, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database restore failed for project {ProjectId}", projectId.Value);
            if (File.Exists(tempRestoredPath)) try { File.Delete(tempRestoredPath); } catch { }
            return new RestoreResult(false, currentDatabasePath, preservedDamagedPath, ex.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }
}
