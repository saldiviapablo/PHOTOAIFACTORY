using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Infrastructure;
using PhotoAIFactory.Domain.Ingestion;

namespace PhotoAIFactory.Infrastructure.Ingestion;

public sealed class ManagedOriginalArchive(TimeProvider timeProvider) : IManagedOriginalArchive
{
    public async Task<ArchivedOriginal> ArchiveAsync(
        string sourcePath,
        string outputRoot,
        AssetFormat format,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (expectedSize < 0 || expectedSha256.Length != 64)
        {
            throw new ArgumentException("Expected size/hash is invalid.");
        }

        var source = Path.GetFullPath(sourcePath);
        var managedDirectory = Path.Combine(
            Path.GetFullPath(outputRoot),
            ".photo-ai-factory",
            "originals",
            format == AssetFormat.Raw ? "RAW" : "JPEG_CAMERA");
        Directory.CreateDirectory(managedDirectory);

        var extension = format == AssetFormat.Raw ? ".arw" : ".jpg";
        var destination = Path.Combine(managedDirectory, expectedSha256.ToLowerInvariant() + extension);

        if (File.Exists(destination))
        {
            await ValidateAsync(destination, expectedSize, expectedSha256, cancellationToken).ConfigureAwait(false);
            return new(destination, expectedSize, expectedSha256.ToLowerInvariant(), timeProvider.GetUtcNow());
        }

        var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
        var createdDestination = false;
        try
        {
            await using (var input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            await ValidateAsync(partial, expectedSize, expectedSha256, cancellationToken).ConfigureAwait(false);

            try
            {
                File.Move(partial, destination, overwrite: false);
                createdDestination = true;
            }
            catch (IOException) when (File.Exists(destination))
            {
                await ValidateAsync(destination, expectedSize, expectedSha256, cancellationToken).ConfigureAwait(false);
            }

            await ValidateAsync(destination, expectedSize, expectedSha256, cancellationToken).ConfigureAwait(false);
            return new(destination, expectedSize, expectedSha256.ToLowerInvariant(), timeProvider.GetUtcNow());
        }
        catch
        {
            if (createdDestination && File.Exists(destination))
            {
                try { File.Delete(destination); } catch { }
            }
            throw;
        }
        finally
        {
            if (File.Exists(partial))
            {
                try { File.Delete(partial); } catch { }
            }
        }
    }

    private static async Task ValidateAsync(
        string path,
        long expectedSize,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedSize)
        {
            throw new IOException("Managed original size validation failed.");
        }

        await using (var readable = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1, FileOptions.SequentialScan))
        {
            _ = readable.ReadByte();
        }

        var hash = await FileUtilities.Sha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Managed original SHA-256 validation failed.");
        }
    }
}
