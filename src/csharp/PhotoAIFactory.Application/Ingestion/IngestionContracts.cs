using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Ingestion;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Ingestion;

public sealed record StableFileInfo(
    string FullPath,
    long SizeBytes,
    DateTimeOffset LastWriteAtUtc);

public sealed record ArchivedOriginal(
    string ManagedPath,
    long SizeBytes,
    string Sha256,
    DateTimeOffset ArchivedAtUtc);

public sealed record IngestAssetCommand(
    ProjectId ProjectId,
    IngestionSourceId SourceId,
    string AssociationKey,
    string SourcePath,
    string SourceRelativePath,
    string ManagedPath,
    AssetFormat Format,
    long SizeBytes,
    string Sha256,
    RawSupportInfo RawSupport,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ArchivedAtUtc,
    TimeSpan AssociationWindow);

public enum IngestAssetStatus
{
    Created,
    DuplicateExact,
    LateRawAttached
}

public sealed record IngestAssetResult(
    IngestAssetStatus Status,
    PhotoIngestionSnapshot Photo,
    AssetSnapshot Asset,
    AssetId? DuplicateAssetId = null);

public enum PrepareIngestionSourceStatus
{
    Ready,
    PendingAssociationsRequireResolution
}

public sealed record PrepareIngestionSourceResult(
    PrepareIngestionSourceStatus Status,
    IngestionSourceSnapshot Source,
    int PendingAssociationCount);

public interface IIngestionStore
{
    Task<PrepareIngestionSourceResult> PrepareSourceAsync(
        ProjectId projectId,
        string configVersionId,
        string inputRoot,
        bool includeSubfolders,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);

    Task<int> CountPendingAsync(
        ProjectId projectId,
        IngestionSourceId sourceId,
        CancellationToken cancellationToken = default);

    Task<int> FinalizeAssociationsAsync(
        ProjectId projectId,
        IngestionSourceId sourceId,
        DateTimeOffset nowUtc,
        bool force,
        CancellationToken cancellationToken = default);

    Task<AssetSnapshot?> FindAssetByHashAsync(
        ProjectId projectId,
        string sha256,
        CancellationToken cancellationToken = default);

    Task<IngestAssetResult> IngestArchivedAsync(
        IngestAssetCommand command,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PhotoIngestionSnapshot>> ListPhotosAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetSnapshot>> ListAssetsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);

    Task<IngestionSourceSnapshot?> GetLatestSourceAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default);
}

public interface IIngestionStoreFactory
{
    IIngestionStore Open(ProjectId projectId);
}

public interface IFileStabilityProbe
{
    Task<StableFileInfo> WaitUntilStableAsync(
        string path,
        TimeSpan stableFor,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public interface IManagedOriginalArchive
{
    Task<ArchivedOriginal> ArchiveAsync(
        string sourcePath,
        string outputRoot,
        AssetFormat format,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken = default);
}

public interface IRawSupportClassifier
{
    Task<RawSupportInfo> ClassifyAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public interface IIngestionSession : IAsyncDisposable
{
    ProjectId ProjectId { get; }
    IngestionSourceSnapshot Source { get; }

    Task StartAsync(CancellationToken cancellationToken = default);
    Task ReconcileAsync(string reason, CancellationToken cancellationToken = default);
    Task WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IIngestionSessionFactory
{
    IIngestionSession Create(
        ProjectId projectId,
        ProjectConfigV1 config,
        IngestionSourceSnapshot source);
}
