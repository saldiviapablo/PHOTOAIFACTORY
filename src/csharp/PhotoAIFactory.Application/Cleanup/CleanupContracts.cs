using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Cleanup;

public sealed record CleanupOptions(
    TimeSpan MinimumAge,
    bool DryRun = false,
    long MaxBytesToDelete = 10L * 1024 * 1024 * 1024); // 10 GB limit per run

public sealed record DeletedItemRecord(
    string Path,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string Category);

public sealed record CleanupResult(
    int TotalCandidatesFound,
    int TotalDeleted,
    long TotalBytesReclaimed,
    bool DryRun,
    IReadOnlyList<DeletedItemRecord> Items,
    IReadOnlyList<string> Errors);

public interface ICleanupService
{
    Task<CleanupResult> CleanupStaleTemporaryArtifactsAsync(
        ProjectId projectId,
        string stagingFolder,
        CleanupOptions? options = null,
        CancellationToken cancellationToken = default);
}
