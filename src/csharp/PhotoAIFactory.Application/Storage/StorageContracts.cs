using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Storage;

public enum StoragePreflightStatus
{
    SufficientSpace,
    InsufficientSpace,
    PathNotFound,
    Error
}

public sealed record StoragePreflightResult(
    StoragePreflightStatus Status,
    string TargetPath,
    long RequiredBytes,
    long AvailableBytes,
    string? Message)
{
    public bool IsSufficient => Status == StoragePreflightStatus.SufficientSpace;
}

public interface IStorageSpaceInspector
{
    long GetAvailableFreeSpaceBytes(string path);
}

public interface IStoragePreflightService
{
    StoragePreflightResult CheckAvailableSpace(string targetPath, long requiredBytes);
    long EstimateRequiredBytes(StageName stage, long inputSizeBytes);
}
