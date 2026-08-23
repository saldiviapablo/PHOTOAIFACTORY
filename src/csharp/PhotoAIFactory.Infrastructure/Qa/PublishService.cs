using System.Security.Cryptography;
using PhotoAIFactory.Application.Qa;

namespace PhotoAIFactory.Infrastructure.Qa;

public sealed class PublishService(IFinalHistoryWriter historyWriter) : IPublishService
{
    public async Task<PublishResult> PublishAsync(
        PublishCandidateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourceCandidatePath))
        {
            throw new FileNotFoundException(
                $"Source candidate file not found at '{request.SourceCandidatePath}'.",
                request.SourceCandidatePath);
        }

        var sourceInfo = new FileInfo(request.SourceCandidatePath);
        if (sourceInfo.Length <= 0)
        {
            throw new InvalidDataException($"Source candidate file is empty ({request.SourceCandidatePath}).");
        }

        var sourceBytes = await File.ReadAllBytesAsync(request.SourceCandidatePath, cancellationToken).ConfigureAwait(false);
        var computedSha = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();

        if (!string.Equals(computedSha, request.ExpectedSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Source candidate SHA-256 mismatch. Expected: {request.ExpectedSourceSha256}, Actual: {computedSha}.");
        }

        var ext = Path.GetExtension(request.SourceCandidatePath);
        if (!string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Final publication in V1 only supports JPEG format (.jpg/.jpeg). File '{request.SourceCandidatePath}' has unsupported extension '{ext}'.");
        }

        var dimensions = ImageHeaderInspector.Inspect(request.SourceCandidatePath);

        var destinationFolder = Path.Combine(request.OutputRootFolder, request.DestinationKind);
        Directory.CreateDirectory(destinationFolder);

        var baseFileName = Path.GetFileName(request.SourceCandidatePath);
        if (string.IsNullOrWhiteSpace(baseFileName))
        {
            baseFileName = $"{request.PhotoId.Value}.jpg";
        }

        var targetPath = Path.Combine(destinationFolder, baseFileName);
        var finalDestinationPath = targetPath;

        if (File.Exists(targetPath))
        {
            var existingBytes = await File.ReadAllBytesAsync(targetPath, cancellationToken).ConfigureAwait(false);
            var existingSha = Convert.ToHexString(SHA256.HashData(existingBytes)).ToLowerInvariant();

            if (string.Equals(existingSha, computedSha, StringComparison.OrdinalIgnoreCase))
            {
                // Identical file already published (idempotent replay)
                finalDestinationPath = targetPath;
            }
            else
            {
                // Colliding different file -> deterministic safe disambiguation based on durable Job ID
                var nameWithoutExt = Path.GetFileNameWithoutExtension(baseFileName);
                var disambiguatedName = $"{nameWithoutExt}_{request.JobId.Value}{ext}";
                var disambiguatedPath = Path.Combine(destinationFolder, disambiguatedName);

                if (File.Exists(disambiguatedPath))
                {
                    var disBytes = await File.ReadAllBytesAsync(disambiguatedPath, cancellationToken).ConfigureAwait(false);
                    var disSha = Convert.ToHexString(SHA256.HashData(disBytes)).ToLowerInvariant();
                    if (string.Equals(disSha, computedSha, StringComparison.OrdinalIgnoreCase))
                    {
                        finalDestinationPath = disambiguatedPath;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"Publication collision conflict at '{disambiguatedPath}'. Destination exists with differing content.");
                    }
                }
                else
                {
                    await SafeCopyFileAsync(request.SourceCandidatePath, disambiguatedPath, destinationFolder, cancellationToken).ConfigureAwait(false);
                    finalDestinationPath = disambiguatedPath;
                }
            }
        }
        else
        {
            await SafeCopyFileAsync(request.SourceCandidatePath, targetPath, destinationFolder, cancellationToken).ConfigureAwait(false);
            finalDestinationPath = targetPath;
        }

        var publishedAtUtc = DateTimeOffset.UtcNow;

        var historyPath = await historyWriter.WriteFinalHistoryAsync(
            request.ProjectId,
            request.PhotoId,
            request.JobId,
            request.AttemptId,
            finalDestinationPath,
            computedSha,
            sourceInfo.Length,
            dimensions.Width,
            dimensions.Height,
            request.QaResult,
            request.OutputRootFolder,
            publishedAtUtc,
            cancellationToken).ConfigureAwait(false);

        return new PublishResult(
            Guid.NewGuid().ToString("N"),
            finalDestinationPath,
            computedSha,
            sourceInfo.Length,
            dimensions.Width,
            dimensions.Height,
            historyPath,
            publishedAtUtc);
    }

    private static async Task SafeCopyFileAsync(
        string sourcePath,
        string destinationPath,
        string destinationFolder,
        CancellationToken cancellationToken)
    {
        var stagingPath = Path.Combine(destinationFolder, $".staging_{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var sourceStream = File.OpenRead(sourcePath))
            await using (var stagingStream = File.Create(stagingPath))
            {
                await sourceStream.CopyToAsync(stagingStream, cancellationToken).ConfigureAwait(false);
            }

            File.Move(stagingPath, destinationPath, overwrite: false);
        }
        catch
        {
            if (File.Exists(stagingPath))
            {
                try { File.Delete(stagingPath); } catch { /* ignore cleanup error */ }
            }
            throw;
        }
    }
}
