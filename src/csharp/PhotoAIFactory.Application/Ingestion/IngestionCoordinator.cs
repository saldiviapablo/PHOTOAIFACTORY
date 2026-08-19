using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Ingestion;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Ingestion;

public sealed class IngestionCoordinator
{
    private static readonly EventId IgnoredEvent = new(3000, "IngestionIgnored");
    private static readonly EventId ArchivedEvent = new(3001, "OriginalArchived");
    private static readonly EventId DuplicateEvent = new(3002, "DuplicateExact");
    private static readonly EventId LateRawEvent = new(3003, "LateRawAttached");

    private readonly ProjectConfigV1 config;
    private readonly IngestionSourceSnapshot source;
    private readonly IIngestionStore store;
    private readonly IFileStabilityProbe stability;
    private readonly IManagedOriginalArchive archive;
    private readonly IRawSupportClassifier rawClassifier;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<IngestionCoordinator> logger;

    public IngestionCoordinator(
        ProjectConfigV1 config,
        IngestionSourceSnapshot source,
        IIngestionStore store,
        IFileStabilityProbe stability,
        IManagedOriginalArchive archive,
        IRawSupportClassifier rawClassifier,
        TimeProvider timeProvider,
        ILogger<IngestionCoordinator>? logger = null)
    {
        this.config = config;
        this.source = source;
        this.store = store;
        this.stability = stability;
        this.archive = archive;
        this.rawClassifier = rawClassifier;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<IngestionCoordinator>.Instance;
    }

    public async Task<IngestAssetResult?> IngestPathAsync(
        string path,
        TimeSpan stableFor,
        TimeSpan stabilityTimeout,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!TryDescribePath(fullPath, out var relativePath, out var format))
        {
            logger.LogDebug(IgnoredEvent, "Ignored ingestion path {Path}", fullPath);
            return null;
        }

        var stable = await stability.WaitUntilStableAsync(
            fullPath, stableFor, stabilityTimeout, cancellationToken).ConfigureAwait(false);

        var firstHash = await InfrastructureHash.Sha256Async(fullPath, cancellationToken).ConfigureAwait(false);
        var duplicate = await store.FindAssetByHashAsync(
            source.ProjectId, firstHash, cancellationToken).ConfigureAwait(false);
        if (duplicate is not null)
        {
            var duplicatePhoto = (await store.ListPhotosAsync(source.ProjectId, cancellationToken).ConfigureAwait(false))
                .Single(item => item.Id == duplicate.PhotoId);
            logger.LogInformation(DuplicateEvent,
                "Exact duplicate ignored; sha256={Sha256}; existing_asset={AssetId}",
                firstHash, duplicate.Id.Value);
            return new(IngestAssetStatus.DuplicateExact, duplicatePhoto, duplicate, duplicate.Id);
        }

        var rawSupport = format == AssetFormat.Raw
            ? await rawClassifier.ClassifyAsync(fullPath, cancellationToken).ConfigureAwait(false)
            : RawSupportInfo.NotApplicable;

        var archived = await archive.ArchiveAsync(
            fullPath, config.OutputFolder, format, stable.SizeBytes, firstHash, cancellationToken).ConfigureAwait(false);

        var infoAfterArchive = new FileInfo(fullPath);
        if (!infoAfterArchive.Exists || infoAfterArchive.Length != stable.SizeBytes)
        {
            throw new IOException("Source file changed while the managed original was being protected.");
        }

        var secondHash = await InfrastructureHash.Sha256Async(fullPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(firstHash, secondHash, StringComparison.Ordinal))
        {
            throw new IOException("Source file changed while the managed original was being protected.");
        }

        var now = timeProvider.GetUtcNow();
        var command = new IngestAssetCommand(
            source.ProjectId,
            source.Id,
            AssociationKey(relativePath),
            fullPath,
            relativePath,
            archived.ManagedPath,
            format,
            stable.SizeBytes,
            firstHash,
            rawSupport,
            now,
            archived.ArchivedAtUtc,
            TimeSpan.FromSeconds(config.AssociationWindowSeconds));

        var result = await store.IngestArchivedAsync(command, cancellationToken).ConfigureAwait(false);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["project_id"] = source.ProjectId.Value,
            ["photo_id"] = result.Photo.Id.Value
        });

        var eventId = result.Status == IngestAssetStatus.LateRawAttached ? LateRawEvent : ArchivedEvent;
        logger.LogInformation(eventId,
            "Ingestion result {Status}; format={Format}; sha256={Sha256}; state={PhotoState}",
            result.Status, format, firstHash, result.Photo.State);
        return result;
    }

    public bool IsSupportedPath(string path) =>
        IsSupportedExtension(Path.GetExtension(path));

    public static bool IsSupportedExtension(string extension) =>
        extension.Equals(".arw", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    public static string AssociationKey(string sourceRelativePath)
    {
        var normalized = sourceRelativePath.Normalize(NormalizationForm.FormC);
        var directory = Path.GetDirectoryName(normalized) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(normalized);
        return $"{directory.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)}|{stem}"
            .ToUpperInvariant();
    }

    private bool TryDescribePath(
        string fullPath,
        out string relativePath,
        out AssetFormat format)
    {
        relativePath = string.Empty;
        format = default;

        var extension = Path.GetExtension(fullPath);
        if (!IsSupportedExtension(extension))
        {
            return false;
        }

        var relative = Path.GetRelativePath(config.InputFolder, fullPath);
        if (relative == "." ||
            Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }

        if (!config.IncludeSubfolders && Path.GetDirectoryName(relative) is { Length: > 0 })
        {
            return false;
        }

        relativePath = relative;
        format = extension.Equals(".arw", StringComparison.OrdinalIgnoreCase)
            ? AssetFormat.Raw
            : AssetFormat.Jpeg;
        return true;
    }

    private static class InfrastructureHash
    {
        public static async Task<string> Sha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(
                stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
