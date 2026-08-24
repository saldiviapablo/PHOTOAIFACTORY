namespace PhotoAIFactory.Application.Provisioning;

public enum ComponentKind
{
    Application,
    Runtime,
    ExternalEngine,
    ModelWeights,
    WorkflowBundle
}

public enum ComponentStatus
{
    Installed,
    Missing,
    Corrupted,
    Outdated,
    ReviewRequired,
    Downloading,
    Staged,
    Failed
}

public enum RedistributionStatus
{
    Approved,
    AutomatedDownloadOnly,
    ReviewRequired,
    Restricted
}

public enum PayloadFormat
{
    DirectFile,
    ZipArchive,
    TarGzArchive,
    ExeInstaller,
    DirectoryBundle,
    ModelFileset
}

public sealed record ModelFileEntry(
    string RelativePath,
    string? SourceUrl,
    long PayloadSizeBytes,
    string Sha256);

public sealed record ComponentDescriptor(
    string ComponentId,
    string DisplayName,
    ComponentKind Kind,
    PayloadFormat Format,
    string Version,
    string? SourceUrl,
    string? SourceCommit,
    string PayloadSha256,
    string InstalledArtifactSha256,
    long PayloadSizeBytes,
    string LicenseId,
    string LicensePath,
    RedistributionStatus Redistribution,
    string InstallRoot,
    string? ExecutableRelativePath,
    string? HealthProbeEndpoint,
    bool IsRequired,
    string? Notes,
    IReadOnlyList<ModelFileEntry>? Fileset = null);

public sealed record ComponentState(
    ComponentDescriptor Descriptor,
    ComponentStatus Status,
    string? InstalledPath,
    string? ActualSha256,
    string? LastVerifiedAtUtc,
    string? ErrorMessage);

public sealed record ComponentProvisionProgress(
    string ComponentId,
    string Phase,
    long BytesTransferred,
    long TotalBytes,
    double Percentage,
    string StatusMessage);

public sealed record ReleaseManifest(
    string ReleaseVersion,
    string ReleaseName,
    string CommitSha,
    string BuiltAtUtc,
    string TargetOs,
    string TargetArchitecture,
    string SigningStatus,
    string ComponentsLockSha256,
    IReadOnlyList<string> IncludedComponentIds,
    bool IsProductionReady,
    IReadOnlyDictionary<string, string>? Metadata);
