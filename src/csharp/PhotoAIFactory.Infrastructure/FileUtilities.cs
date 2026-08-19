using System.Security.Cryptography;

namespace PhotoAIFactory.Infrastructure;

public static class FileUtilities
{
    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<bool> WaitForStableFileAsync(
        string path,
        TimeSpan stableFor,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        long? previousLength = null;
        DateTime stableSince = DateTime.UtcNow;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) { await Task.Delay(250, cancellationToken); continue; }

            long length;
            try
            {
                var info = new FileInfo(path);
                length = info.Length;
                using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                _ = probe.Length;
            }
            catch (IOException)
            {
                previousLength = null;
                stableSince = DateTime.UtcNow;
                await Task.Delay(500, cancellationToken);
                continue;
            }

            if (previousLength == length)
            {
                if (DateTime.UtcNow - stableSince >= stableFor) return true;
            }
            else
            {
                previousLength = length;
                stableSince = DateTime.UtcNow;
            }
            await Task.Delay(500, cancellationToken);
        }
        return false;
    }
}
