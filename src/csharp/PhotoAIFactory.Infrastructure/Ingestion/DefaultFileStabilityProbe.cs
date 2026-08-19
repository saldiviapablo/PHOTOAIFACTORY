using PhotoAIFactory.Application.Ingestion;

namespace PhotoAIFactory.Infrastructure.Ingestion;

public sealed class DefaultFileStabilityProbe : IFileStabilityProbe
{
    public async Task<StableFileInfo> WaitUntilStableAsync(
        string path,
        TimeSpan stableFor,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (stableFor <= TimeSpan.Zero || timeout <= stableFor)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Stability timeout must be greater than the required stable interval.");
        }

        var fullPath = Path.GetFullPath(path);
        var started = System.Diagnostics.Stopwatch.StartNew();
        long? previousLength = null;
        DateTime? previousWriteUtc = null;
        var stableSince = System.Diagnostics.Stopwatch.StartNew();

        while (started.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var probe = new FileStream(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    1,
                    FileOptions.SequentialScan);

                var info = new FileInfo(fullPath);
                info.Refresh();
                var length = probe.Length;
                var writeUtc = info.LastWriteTimeUtc;

                if (previousLength == length && previousWriteUtc == writeUtc)
                {
                    if (stableSince.Elapsed >= stableFor)
                    {
                        return new(
                            fullPath,
                            length,
                            new DateTimeOffset(DateTime.SpecifyKind(writeUtc, DateTimeKind.Utc)));
                    }
                }
                else
                {
                    previousLength = length;
                    previousWriteUtc = writeUtc;
                    stableSince.Restart();
                }
            }
            catch (IOException)
            {
                previousLength = null;
                previousWriteUtc = null;
                stableSince.Restart();
            }
            catch (UnauthorizedAccessException)
            {
                previousLength = null;
                previousWriteUtc = null;
                stableSince.Restart();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"File did not become stable within {timeout}: {fullPath}");
    }
}
