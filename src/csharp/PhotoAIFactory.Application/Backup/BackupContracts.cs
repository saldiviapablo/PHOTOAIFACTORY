using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Backup;

public sealed record BackupManifest(
    int SchemaVersion,
    string ProjectId,
    string BackupFileName,
    string BackupSha256,
    long BackupSizeBytes,
    int TotalTables,
    DateTimeOffset CreatedAtUtc,
    string CreatorAppVersion);

public sealed record BackupResult(
    bool Success,
    string BackupPath,
    string ManifestPath,
    BackupManifest? Manifest,
    string? Error);

public sealed record RestoreVerificationResult(
    bool IsValid,
    int SchemaVersion,
    string? ProjectId,
    string? Error);

public sealed record RestoreResult(
    bool Success,
    string RestoredDbPath,
    string? PreservedDamagedDbPath,
    string? Error);

public interface IBackupService
{
    Task<BackupResult> CreateBackupAsync(
        ProjectId projectId,
        string backupRootFolder,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupManifest>> ListVerifiedBackupsAsync(
        ProjectId projectId,
        string backupRootFolder,
        CancellationToken cancellationToken = default);

    Task<int> EnforceRetentionAsync(
        ProjectId projectId,
        string backupRootFolder,
        int keepCount = 5,
        CancellationToken cancellationToken = default);
}

public interface IRestoreService
{
    Task<RestoreVerificationResult> VerifyBackupAsync(
        string backupFilePath,
        CancellationToken cancellationToken = default);

    Task<RestoreResult> RestoreDatabaseAsync(
        ProjectId projectId,
        string backupFilePath,
        string currentDatabasePath,
        string backupRootFolder,
        CancellationToken cancellationToken = default);
}
