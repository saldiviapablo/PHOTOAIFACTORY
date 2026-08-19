namespace PhotoAIFactory.Domain.Ingestion;

public sealed record AssetId(string Value)
{
    public static AssetId New() => new(Guid.NewGuid().ToString("N"));
}

public sealed record IngestionSourceId(string Value)
{
    public static IngestionSourceId New() => new(Guid.NewGuid().ToString("N"));
}

public enum AssetFormat
{
    Raw,
    Jpeg
}

public enum AssetRole
{
    RawOriginal,
    JpegPending,
    JpegCamera,
    JpegMaster
}

public enum AssetArchiveState
{
    Archived
}

public enum RawSupportStatus
{
    NotApplicable,
    SupportedFullSize,
    UnsupportedReduced,
    Unknown
}

public enum IngestionPhotoState
{
    WaitingForAssociation,
    ReadyForAnalysis,
    ReviewUnsupportedFormat
}

public sealed record RawSupportInfo(
    RawSupportStatus Status,
    int MaxWidth,
    int MaxHeight,
    string Classification)
{
    public bool ProcessingSupported => Status == RawSupportStatus.SupportedFullSize;

    public static RawSupportInfo NotApplicable { get; } =
        new(RawSupportStatus.NotApplicable, 0, 0, "NOT_APPLICABLE");
}

public sealed record IngestionSourceSnapshot(
    IngestionSourceId Id,
    ProjectId ProjectId,
    string InputRoot,
    bool IncludeSubfolders,
    string ConfigVersionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ClosedAtUtc);

public sealed record AssetSnapshot(
    AssetId Id,
    ProjectId ProjectId,
    PhotoId PhotoId,
    IngestionSourceId SourceId,
    string SourcePath,
    string SourceRelativePath,
    string ManagedPath,
    AssetFormat Format,
    AssetRole Role,
    AssetArchiveState ArchiveState,
    long SizeBytes,
    string Sha256,
    RawSupportInfo RawSupport,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ArchivedAtUtc);

public sealed record PhotoIngestionSnapshot(
    PhotoId Id,
    ProjectId ProjectId,
    IngestionSourceId SourceId,
    string AssociationKey,
    IngestionPhotoState State,
    AssetId? MasterAssetId,
    AssetFormat? MasterFormat,
    DateTimeOffset AssociationDeadlineUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
