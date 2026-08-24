using System.IO.Compression;

namespace PhotoAIFactory.Infrastructure.Provisioning;

public static class ArchiveExtractionHelper
{
    public static void ExtractZipSafely(
        string zipFilePath,
        string destinationDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!File.Exists(zipFilePath))
        {
            throw new FileNotFoundException("Archive not found.", zipFilePath);
        }

        var fullDestDir = Path.GetFullPath(destinationDirectory);
        if (!fullDestDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            fullDestDir += Path.DirectorySeparatorChar;
        }

        Directory.CreateDirectory(fullDestDir);

        using var archive = ZipFile.OpenRead(zipFilePath);
        var totalEntries = archive.Entries.Count;
        var processed = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Zip-Slip defense: canonicalize target path and assert prefix match
            var targetPath = Path.GetFullPath(Path.Combine(fullDestDir, entry.FullName));
            if (!targetPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Zip-Slip / Path Traversal detected in archive entry: '{entry.FullName}'");
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(targetPath);
            }
            else
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                entry.ExtractToFile(targetPath, overwrite: true);
            }

            processed++;
            if (totalEntries > 0 && progress is not null)
            {
                progress.Report((double)processed / totalEntries * 100.0);
            }
        }
    }

    public static void ExtractTarGzSafely(
        string tarGzFilePath,
        string destinationDirectory,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tarGzFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (!File.Exists(tarGzFilePath))
        {
            throw new FileNotFoundException("Archive not found.", tarGzFilePath);
        }

        var fullDestDir = Path.GetFullPath(destinationDirectory);
        if (!fullDestDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            fullDestDir += Path.DirectorySeparatorChar;
        }

        Directory.CreateDirectory(fullDestDir);

        using var fs = File.OpenRead(tarGzFilePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tarReader = new System.Formats.Tar.TarReader(gz);

        while (tarReader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetPath = Path.GetFullPath(Path.Combine(fullDestDir, entry.Name));
            if (!targetPath.StartsWith(fullDestDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Tar-Slip / Path Traversal detected in entry: '{entry.Name}'");
            }

            if (entry.EntryType == System.Formats.Tar.TarEntryType.Directory)
            {
                Directory.CreateDirectory(targetPath);
            }
            else
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                entry.ExtractToFile(targetPath, overwrite: true);
            }
        }
    }
}
