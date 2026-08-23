using PhotoAIFactory.Application.Storage;

namespace PhotoAIFactory.Infrastructure.Storage;

public sealed class DriveInfoStorageSpaceInspector : IStorageSpaceInspector
{
    public long GetAvailableFreeSpaceBytes(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty.", nameof(path));

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Directory.GetCurrentDirectory();
            }

            var driveInfo = new DriveInfo(root);
            return driveInfo.AvailableFreeSpace;
        }
        catch
        {
            // If drive info fails, return conservative 0 to fail closed
            return 0;
        }
    }
}
