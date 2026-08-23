using PhotoAIFactory.Application.Storage;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Storage;

public sealed class DefaultStoragePreflightService : IStoragePreflightService
{
    private readonly IStorageSpaceInspector spaceInspector;
    private const long DefaultSafetyHeadroomBytes = 50L * 1024 * 1024; // 50 MB safety margin

    public DefaultStoragePreflightService(IStorageSpaceInspector? spaceInspector = null)
    {
        this.spaceInspector = spaceInspector ?? new DriveInfoStorageSpaceInspector();
    }

    public StoragePreflightResult CheckAvailableSpace(string targetPath, long requiredBytes)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new StoragePreflightResult(
                StoragePreflightStatus.PathNotFound,
                targetPath ?? string.Empty,
                requiredBytes,
                0,
                "Target path cannot be empty.");
        }

        try
        {
            var targetDirectory = targetPath;
            while (!string.IsNullOrWhiteSpace(targetDirectory) && !Directory.Exists(targetDirectory))
            {
                var parent = Path.GetDirectoryName(targetDirectory);
                if (string.Equals(parent, targetDirectory, StringComparison.Ordinal) || parent is null)
                    break;
                targetDirectory = parent;
            }
            if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
            {
                targetDirectory = Path.GetPathRoot(targetPath) ?? Directory.GetCurrentDirectory();
            }

            var available = spaceInspector.GetAvailableFreeSpaceBytes(targetDirectory);
            var totalRequired = requiredBytes + DefaultSafetyHeadroomBytes;

            if (available < totalRequired)
            {
                return new StoragePreflightResult(
                    StoragePreflightStatus.InsufficientSpace,
                    targetPath,
                    totalRequired,
                    available,
                    $"Insufficient storage space on '{targetDirectory}'. Available: {available:N0} bytes, Required: {totalRequired:N0} bytes (including {DefaultSafetyHeadroomBytes:N0} bytes safety headroom).");
            }

            return new StoragePreflightResult(
                StoragePreflightStatus.SufficientSpace,
                targetPath,
                totalRequired,
                available,
                "Sufficient storage space available.");
        }
        catch (Exception ex)
        {
            return new StoragePreflightResult(
                StoragePreflightStatus.Error,
                targetPath,
                requiredBytes,
                0,
                $"Storage preflight check error: {ex.Message}");
        }
    }

    public long EstimateRequiredBytes(StageName stage, long inputSizeBytes)
    {
        var baseSize = Math.Max(inputSizeBytes, 5L * 1024 * 1024); // at least 5 MB
        return stage switch
        {
            StageName.Ingest => baseSize * 2, // temp copy + archive
            StageName.OriginalArchive => baseSize,
            StageName.Analysis => 20L * 1024 * 1024, // 20 MB preview + model embeddings
            StageName.Preselection => 5L * 1024 * 1024,
            StageName.DarktablePass1 => baseSize * 5, // 16-bit TIFF can be 50-100MB
            StageName.FeedbackInspection => 20L * 1024 * 1024,
            StageName.DarktablePass2 => baseSize * 3, // JPEG + XMP + sidecars
            StageName.ComfyUi => baseSize * 4, // input + output + temp masks
            StageName.Qa => 15L * 1024 * 1024,
            StageName.Publish => baseSize * 2 + 5L * 1024 * 1024, // final JPEG + history
            _ => baseSize * 2
        };
    }
}
