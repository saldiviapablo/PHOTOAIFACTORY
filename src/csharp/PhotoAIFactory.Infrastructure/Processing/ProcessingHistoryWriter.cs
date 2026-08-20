using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.Processing;

public sealed class ProcessingHistoryWriter : IProcessingHistoryWriter
{
    public string GetHistoryPath(ProjectConfigV1 config, PhotoId photoId, JobId jobId) =>
        Path.Combine(
            config.OutputFolder, ".photo-ai-factory", "history",
            photoId.Value, $"{jobId.Value}.json");

    public async Task<string> WriteAsync(
        ProjectConfigV1 config,
        BasicRevealJobSnapshot job,
        RevealMode revealMode,
        JsonElement? recipe,
        string processingConfigSha256,
        DarktableControlPlan plan,
        BasicRevealArtifact artifact,
        string attemptId,
        string historyPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        if (!string.Equals(Path.GetFileName(attemptId), attemptId, StringComparison.Ordinal) ||
            attemptId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            attemptId is "." or "..")
        {
            throw new ArgumentException(
                "Attempt ID is not safe for portable history.", nameof(attemptId));
        }
        var target = Path.GetFullPath(historyPath);
        EnsureSafePath(config, target);
        var xmpTarget = Path.Combine(
            Path.GetDirectoryName(target)!,
            $"{Path.GetFileNameWithoutExtension(target)}.{attemptId}.xmp");
        EnsureSafePath(config, xmpTarget);
        var xmpPacket = ExtractAuthenticDarktableXmp(artifact.Path);
        var xmpSha256 = Convert.ToHexString(SHA256.HashData(xmpPacket))
            .ToLowerInvariant();

        var history = JsonSerializer.SerializeToElement(new
        {
            schema_version = 1,
            phase = "BASIC_REVEAL",
            project_id = job.ProjectId.Value,
            photo_id = job.PhotoId.Value,
            job_id = job.Id.Value,
            attempt_id = attemptId,
            reveal_mode = revealMode == RevealMode.PreAi ? "PRE_AI" : "DT_AUTO",
            processing_config_id = job.ProcessingConfigId,
            processing_config_sha256 = processingConfigSha256,
            input = new
            {
                asset_id = job.InputAssetId,
                sha256 = job.InputSha256,
                format = job.InputFormat
            },
            recipe = recipe,
            darktable_control = plan.Details,
            darktable_version = artifact.DarktableVersion,
            output = new
            {
                role = "BASIC_REVEAL_STAGING",
                sha256 = artifact.Sha256,
                size_bytes = artifact.SizeBytes,
                width = artifact.Width,
                height = artifact.Height
            },
            xmp_history = new
            {
                file_name = Path.GetFileName(xmpTarget),
                sha256 = xmpSha256,
                source = "DARKTABLE_EMBEDDED_XMP_EXACT_PACKET"
            },
            publication = new
            {
                final_published = false,
                reason = "QA_AND_PUBLISH_ARE_LATER_PHASES"
            }
        });

        var canonical = JsonSerializer.Serialize(history);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await WriteImmutableBytesAsync(
            xmpTarget, xmpPacket, cancellationToken).ConfigureAwait(false);

        if (File.Exists(target))
        {
            var existing = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existing, canonical, StringComparison.Ordinal))
                throw new RevealHistoryCollisionException(
                    "Portable history collision: existing immutable history differs.");
            return xmpTarget;
        }

        var partial = target + $".partial-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllTextAsync(
                partial, canonical, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(partial, target);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
        return xmpTarget;
    }

    public async Task<BasicRevealRecovery?> TryReadRecoveryAsync(
        ProjectConfigV1 config,
        BasicRevealJobSnapshot job,
        RevealMode revealMode,
        JsonElement? recipe,
        string processingConfigSha256,
        DarktableControlPlan plan,
        string historyPath,
        CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(historyPath);
        EnsureSafePath(config, target);
        if (!File.Exists(target))
            return null;

        try
        {
            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            var input = root.GetProperty("input");
            var output = root.GetProperty("output");
            var xmpHistory = root.GetProperty("xmp_history");
            var publication = root.GetProperty("publication");
            var expectedRecipe = recipe ?? JsonSerializer.SerializeToElement<object?>(null);

            Require(root.GetProperty("schema_version").GetInt32() == 1);
            Require(root.GetProperty("phase").GetString() == "BASIC_REVEAL");
            Require(root.GetProperty("project_id").GetString() == job.ProjectId.Value);
            Require(root.GetProperty("photo_id").GetString() == job.PhotoId.Value);
            Require(root.GetProperty("job_id").GetString() == job.Id.Value);
            Require(root.GetProperty("reveal_mode").GetString() ==
                (revealMode == RevealMode.PreAi ? "PRE_AI" : "DT_AUTO"));
            Require(root.GetProperty("processing_config_id").GetString() ==
                job.ProcessingConfigId);
            Require(root.GetProperty("processing_config_sha256").GetString() ==
                processingConfigSha256);
            Require(input.GetProperty("asset_id").GetString() == job.InputAssetId);
            Require(input.GetProperty("sha256").GetString() == job.InputSha256);
            Require(input.GetProperty("format").GetString() == job.InputFormat);
            Require(JsonElement.DeepEquals(root.GetProperty("recipe"), expectedRecipe));
            Require(JsonElement.DeepEquals(root.GetProperty("darktable_control"), plan.Details));
            Require(publication.GetProperty("final_published").ValueKind == JsonValueKind.False);

            var attemptId = root.GetProperty("attempt_id").GetString();
            var version = root.GetProperty("darktable_version").GetString();
            var sha256 = output.GetProperty("sha256").GetString();
            Require(!string.IsNullOrWhiteSpace(attemptId));
            Require(!string.IsNullOrWhiteSpace(version));
            Require(sha256 is { Length: 64 });

            var xmpFileName = xmpHistory.GetProperty("file_name").GetString();
            Require(!string.IsNullOrWhiteSpace(xmpFileName));
            Require(Path.GetFileName(xmpFileName) == xmpFileName);
            Require(xmpFileName ==
                $"{Path.GetFileNameWithoutExtension(target)}.{attemptId}.xmp");
            var xmpTarget = Path.Combine(Path.GetDirectoryName(target)!, xmpFileName!);
            EnsureSafePath(config, xmpTarget);
            Require(xmpHistory.GetProperty("source").GetString() ==
                "DARKTABLE_EMBEDDED_XMP_EXACT_PACKET");
            Require(File.Exists(xmpTarget));
            Require(xmpHistory.GetProperty("sha256").GetString() ==
                await Sha256Async(xmpTarget, cancellationToken).ConfigureAwait(false));

            return new BasicRevealRecovery(
                attemptId!,
                new BasicRevealArtifact(
                    string.Empty,
                    sha256!,
                    output.GetProperty("size_bytes").GetInt64(),
                    output.GetProperty("width").GetInt32(),
                    output.GetProperty("height").GetInt32(),
                    version!,
                    TimeSpan.Zero));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RevealHistoryCollisionException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new RevealHistoryCollisionException(
                $"Portable history cannot be used for recovery: {ex.Message}");
        }
    }

    private static void EnsureSafePath(ProjectConfigV1 config, string target)
    {
        var expectedRoot = Path.GetFullPath(Path.Combine(
            config.OutputFolder, ".photo-ai-factory", "history"));
        var prefix = expectedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? expectedRoot : expectedRoot + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("History path escaped project metadata storage.");
    }

    private static byte[] ExtractAuthenticDarktableXmp(string jpegPath)
    {
        using var stream = new FileStream(
            jpegPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
            throw new InvalidDataException(
                "Darktable output is not a JPEG while extracting XMP history.");

        var xmpHeader = Encoding.ASCII.GetBytes(
            "http://ns.adobe.com/xap/1.0/\0");
        Span<byte> lengthBytes = stackalloc byte[2];
        while (stream.Position < stream.Length)
        {
            int prefix;
            do { prefix = stream.ReadByte(); } while (prefix >= 0 && prefix != 0xFF);
            if (prefix < 0) break;

            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xFF);
            if (marker < 0 || marker is 0xD9 or 0xDA) break;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;

            stream.ReadExactly(lengthBytes);
            var length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (length < 2)
                throw new InvalidDataException("Invalid JPEG segment while extracting XMP history.");
            var payload = new byte[length - 2];
            stream.ReadExactly(payload);
            if (marker == 0xE1 && payload.AsSpan().StartsWith(xmpHeader))
            {
                var packet = payload[xmpHeader.Length..];
                var text = Encoding.UTF8.GetString(packet);
                if (!text.Contains("http://darktable.sf.net/", StringComparison.Ordinal) ||
                    !text.Contains("darktable:history", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Embedded XMP is not an authentic Darktable history packet.");
                }
                return packet;
            }
        }

        throw new InvalidDataException(
            "Darktable output did not contain an authentic embedded XMP history packet.");
    }

    private static async Task WriteImmutableBytesAsync(
        string target,
        byte[] content,
        CancellationToken cancellationToken)
    {
        if (File.Exists(target))
        {
            var existing = await File.ReadAllBytesAsync(
                target, cancellationToken).ConfigureAwait(false);
            if (!existing.AsSpan().SequenceEqual(content))
                throw new RevealHistoryCollisionException(
                    "Authentic XMP history collision: existing immutable XMP differs.");
            return;
        }

        var partial = target + $".partial-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(
                partial, content, cancellationToken).ConfigureAwait(false);
            File.Move(partial, target);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    private static async Task<string> Sha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static void Require(bool condition)
    {
        if (!condition)
            throw new RevealHistoryCollisionException(
                "Portable history identity differs from the current reveal attempt.");
    }
}
