using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoAIFactory.Application.Cleanup;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Cleanup;

public sealed class SafeCleanupService : ICleanupService
{
    private readonly IAppPaths appPaths;
    private readonly ILogger<SafeCleanupService> logger;

    public SafeCleanupService(
        IAppPaths appPaths,
        ILogger<SafeCleanupService>? logger = null)
    {
        this.appPaths = appPaths;
        this.logger = logger ?? NullLogger<SafeCleanupService>.Instance;
    }

    public async Task<CleanupResult> CleanupStaleTemporaryArtifactsAsync(
        ProjectId projectId,
        string stagingFolder,
        CleanupOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CleanupOptions(TimeSpan.FromHours(1));

        if (string.IsNullOrWhiteSpace(stagingFolder) || !Directory.Exists(stagingFolder))
        {
            return new CleanupResult(0, 0, 0, options.DryRun, [], []);
        }

        // 1. Resolve and canonicalize managed project root and candidate path
        var candidateCanonical = Path.GetFullPath(stagingFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var managedWorkRoot = Path.GetFullPath(Path.Combine(appPaths.WorkDirectory, projectId.Value)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var managedProjectTemp = Path.GetFullPath(Path.Combine(appPaths.ProjectsDirectory, projectId.Value, "temp")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Security Guard: Candidate must be strictly within managedWorkRoot or managedProjectTemp
        var isWithinManagedWork = candidateCanonical.Equals(managedWorkRoot, StringComparison.OrdinalIgnoreCase) ||
                                  candidateCanonical.StartsWith(managedWorkRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        var isWithinProjectTemp = candidateCanonical.Equals(managedProjectTemp, StringComparison.OrdinalIgnoreCase) ||
                                  candidateCanonical.StartsWith(managedProjectTemp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (!isWithinManagedWork && !isWithinProjectTemp)
        {
            logger.LogWarning(
                "SafeCleanupService security check rejected unmanaged candidate path '{CandidatePath}' for project '{ProjectId}'. Must be inside '{ManagedWorkRoot}'.",
                candidateCanonical, projectId.Value, managedWorkRoot);

            return new CleanupResult(
                0, 0, 0, options.DryRun, [],
                [$"Security rejection: path '{candidateCanonical}' is outside managed project root '{managedWorkRoot}'."]);
        }

        // 2. Safe directory enumeration with ReparsePoint / Junction guard
        var cutoffUtc = DateTimeOffset.UtcNow - options.MinimumAge;
        var items = new List<DeletedItemRecord>();
        var errors = new List<string>();
        long totalBytesReclaimed = 0;
        var totalCandidates = 0;
        var totalDeleted = 0;

        try
        {
            var rootDirInfo = new DirectoryInfo(candidateCanonical);
            if (rootDirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new CleanupResult(0, 0, 0, options.DryRun, [], ["Refused to traverse root reparse point/junction."]);
            }

            var dirsToVisit = new Stack<DirectoryInfo>();
            dirsToVisit.Push(rootDirInfo);

            while (dirsToVisit.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentDir = dirsToVisit.Pop();

                // Check subdirectories
                DirectoryInfo[] subDirs;
                try
                {
                    subDirs = currentDir.GetDirectories();
                }
                catch (Exception ex)
                {
                    errors.Add($"Could not enumerate directories in '{currentDir.FullName}': {ex.Message}");
                    continue;
                }

                foreach (var subDir in subDirs)
                {
                    // Guard against symlink/junction/reparse point escaping the managed root
                    if (subDir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        logger.LogWarning("SafeCleanupService skipping reparse point / junction '{Path}'", subDir.FullName);
                        continue;
                    }

                    // Strict protection against durable output / originals folders
                    var subName = subDir.Name.ToLowerInvariant();
                    if (subName is "originals" or "final" or "revisar" or "descartadas" or "history" or "backups")
                    {
                        logger.LogWarning("SafeCleanupService refusing to enter protected directory '{Path}'", subDir.FullName);
                        continue;
                    }

                    dirsToVisit.Push(subDir);
                }

                // Check files
                FileInfo[] files;
                try
                {
                    files = currentDir.GetFiles();
                }
                catch (Exception ex)
                {
                    errors.Add($"Could not enumerate files in '{currentDir.FullName}': {ex.Message}");
                    continue;
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            continue; // Skip reparse point files
                        }

                        var ext = file.Extension.ToLowerInvariant();
                        var name = file.Name.ToLowerInvariant();

                        // Protected extensions and durable file patterns
                        if (ext is ".db" or ".xmp" or ".lock" or ".json" ||
                            name.Contains("manifest") || name.Contains("history") ||
                            name.Contains("journal") || name.Contains("-wal") || name.Contains("-shm"))
                        {
                            continue;
                        }

                        if (file.LastWriteTimeUtc > cutoffUtc.UtcDateTime)
                        {
                            continue; // File is younger than MinimumAge (active temp)
                        }

                        totalCandidates++;
                        var size = file.Length;

                        if (!options.DryRun)
                        {
                            if (totalBytesReclaimed + size > options.MaxBytesToDelete)
                            {
                                break;
                            }

                            file.Delete();
                            totalDeleted++;
                            totalBytesReclaimed += size;
                        }
                        else
                        {
                            totalBytesReclaimed += size;
                        }

                        items.Add(new DeletedItemRecord(
                            file.FullName,
                            size,
                            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                            "TemporaryStagingFile"));
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Failed to delete file '{file.FullName}': {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("SafeCleanupService canceled for project {ProjectId}.", projectId.Value);
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"Cleanup traversal error: {ex.Message}");
        }

        logger.LogInformation(
            "SafeCleanup completed for project {ProjectId}. Candidates: {Candidates}, Deleted: {Deleted}, BytesReclaimed: {Bytes:N0}, DryRun: {DryRun}",
            projectId.Value, totalCandidates, totalDeleted, totalBytesReclaimed, options.DryRun);

        return new CleanupResult(
            totalCandidates,
            totalDeleted,
            totalBytesReclaimed,
            options.DryRun,
            items,
            errors);
    }
}
