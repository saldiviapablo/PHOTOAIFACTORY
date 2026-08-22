using System.Security.Cryptography;
using System.Text.Json;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.Processing;

public sealed class FeedbackHistoryWriter : IFeedbackHistoryWriter
{
    public string GetHistoryPath(
        ProjectConfigV1 config,
        PhotoId photoId,
        JobId jobId) =>
        Path.Combine(
            config.OutputFolder,
            ".photo-ai-factory",
            "history",
            photoId.Value,
            $"{jobId.Value}.json");

    public string GetXmpPath(
        ProjectConfigV1 config,
        PhotoId photoId,
        JobId jobId,
        int passNumber)
    {
        if (passNumber is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(passNumber));
        return Path.Combine(
            config.OutputFolder,
            ".photo-ai-factory",
            "xmp",
            photoId.Value,
            jobId.Value,
            $"pass{passNumber}.xmp");
    }

    public async Task<string> WriteXmpImmutableAsync(
        ProjectConfigV1 config,
        PhotoId photoId,
        JobId jobId,
        int passNumber,
        byte[] xmp,
        CancellationToken cancellationToken = default)
    {
        DarktableXmpExtractor.ValidateDarktablePacket(xmp);
        var requested = Path.GetFullPath(
            GetXmpPath(config, photoId, jobId, passNumber));
        EnsureSafeMetadataPath(config, requested);

        var sha = Sha256(xmp);
        var actual = requested;

        if (File.Exists(requested))
        {
            var existing = await File.ReadAllBytesAsync(
                requested, cancellationToken).ConfigureAwait(false);
            if (existing.AsSpan().SequenceEqual(xmp))
                return requested;

            // A crash can leave a valid immutable XMP before the SQLite
            // checkpoint. Never overwrite it. Materialize the new packet under
            // a content-addressed sibling and persist that exact path.
            actual = Path.Combine(
                Path.GetDirectoryName(requested)!,
                $"pass{passNumber}-{sha[..16]}.xmp");
            EnsureSafeMetadataPath(config, actual);
            if (File.Exists(actual))
            {
                var sibling = await File.ReadAllBytesAsync(
                    actual, cancellationToken).ConfigureAwait(false);
                if (sibling.AsSpan().SequenceEqual(xmp))
                    return actual;
                throw new RevealHistoryCollisionException(
                    $"FEEDBACK XMP collision at {actual}");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(actual)!);
        var partial = actual + $".partial-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(
                partial, xmp, cancellationToken).ConfigureAwait(false);
            File.Move(partial, actual);
            return actual;
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    public async Task WriteFinalAsync(
        ProjectConfigV1 config,
        FeedbackJobSnapshot job,
        string processingConfigSha256,
        FeedbackPassSnapshot pass1,
        FeedbackInspectionSnapshot inspection,
        FeedbackImageArtifact pass2,
        string pass2AttemptId,
        string pass2XmpPath,
        string historyPath,
        CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(historyPath);
        EnsureSafeMetadataPath(config, target);
        EnsureSafeMetadataPath(config, Path.GetFullPath(pass1.XmpPath));
        EnsureSafeMetadataPath(config, Path.GetFullPath(pass2XmpPath));

        var root = JsonSerializer.SerializeToElement(new
        {
            schema_version = 1,
            phase = "FEEDBACK",
            project_id = job.ProjectId.Value,
            photo_id = job.PhotoId.Value,
            job_id = job.Id.Value,
            processing_config_id = job.ProcessingConfigId,
            processing_config_sha256 = processingConfigSha256,
            input = new
            {
                asset_id = job.InputAssetId,
                sha256 = job.InputSha256,
                format = job.InputFormat,
                raw_support_status = job.RawSupportStatus
            },
            pass1 = new
            {
                attempt_id = pass1.AttemptId,
                role = "FEEDBACK_PASS1_TEMPORARY_TIFF16",
                image_path = pass1.ImagePath,
                image_sha256 = pass1.ImageSha256,
                size_bytes = pass1.ImageSizeBytes,
                width = pass1.ImageWidth,
                height = pass1.ImageHeight,
                bits_per_sample = pass1.BitsPerSample,
                channels = pass1.Channels,
                xmp_path = pass1.XmpPath,
                xmp_sha256 = pass1.XmpSha256,
                darktable_version = pass1.DarktableVersion,
                control = pass1.ControlPlan
            },
            inspection = new
            {
                schema_version = inspection.SchemaVersion,
                recipe_sha256 = inspection.RecipeSha256,
                recipe = inspection.Recipe,
                observations = inspection.Inspection
            },
            neural_restore = new
            {
                raw_denoise = false,
                rgb_denoise = false,
                upscale = false,
                reason = "NOT_HEADLESS_PROVEN_AND_BENCHMARK_PENDING",
                raw_denoise_checkpoint_written = false
            },
            pass2 = new
            {
                attempt_id = pass2AttemptId,
                source = string.Equals(
                    job.InputFormat, "RAW", StringComparison.OrdinalIgnoreCase)
                    ? "MANAGED_RAW_ORIGINAL"
                    : "MANAGED_JPEG_ORIGINAL",
                pass1_derivative_used_as_source = false,
                pass1_xmp_reapplied = true,
                role = "FEEDBACK_PASS2_STAGING",
                image_path = pass2.Path,
                image_sha256 = pass2.Sha256,
                size_bytes = pass2.SizeBytes,
                width = pass2.Width,
                height = pass2.Height,
                bits_per_sample = pass2.BitsPerSample,
                channels = pass2.Channels,
                xmp_path = pass2XmpPath,
                xmp_sha256 = await Sha256Async(
                    pass2XmpPath, cancellationToken).ConfigureAwait(false),
                darktable_version = pass2.DarktableVersion
            },
            durable_checkpoints_before_manifest = new[]
            {
                "DARKTABLE_PASS1_COMPLETE",
                "FEEDBACK_INSPECTION_COMPLETE"
            },
            next_durable_checkpoint = "DARKTABLE_PASS2_COMPLETE",
            darktable_pass2_checkpoint_written_at_manifest_time = false,
            publication = new
            {
                final_published = false,
                output_published_checkpoint = false
            },
            downstream = new
            {
                comfyui_executed = false,
                qa_executed = false
            }
        });

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            root,
            new JsonSerializerOptions { WriteIndented = true });

        if (File.Exists(target))
        {
            var existing = await File.ReadAllBytesAsync(
                target, cancellationToken).ConfigureAwait(false);
            if (existing.AsSpan().SequenceEqual(bytes))
                return;
            throw new RevealHistoryCollisionException(
                $"Immutable FEEDBACK history already exists with different content: {target}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + $".partial-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(
                partial, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(partial, target);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    public async Task<FeedbackPass2Recovery?> TryReadPass2RecoveryAsync(
        ProjectConfigV1 config,
        FeedbackJobSnapshot job,
        string processingConfigSha256,
        FeedbackPassSnapshot pass1,
        FeedbackInspectionSnapshot inspection,
        string historyPath,
        CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(historyPath);
        EnsureSafeMetadataPath(config, target);
        if (!File.Exists(target))
            return null;

        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(
                target, cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;

        RequireString(root, "phase", "FEEDBACK");
        RequireString(root, "project_id", job.ProjectId.Value);
        RequireString(root, "photo_id", job.PhotoId.Value);
        RequireString(root, "job_id", job.Id.Value);
        RequireString(
            root, "processing_config_sha256", processingConfigSha256);

        var input = root.GetProperty("input");
        RequireString(input, "asset_id", job.InputAssetId);
        RequireString(input, "sha256", job.InputSha256);

        var storedPass1 = root.GetProperty("pass1");
        RequireString(storedPass1, "image_sha256", pass1.ImageSha256);
        RequireString(storedPass1, "xmp_sha256", pass1.XmpSha256);

        var storedInspection = root.GetProperty("inspection");
        RequireString(
            storedInspection, "recipe_sha256", inspection.RecipeSha256);

        var pass2 = root.GetProperty("pass2");
        var attemptId = pass2.GetProperty("attempt_id").GetString()
            ?? throw new InvalidDataException(
                "FEEDBACK history pass2 attempt_id is missing.");
        if (!string.Equals(
                Path.GetFileName(attemptId), attemptId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "FEEDBACK history contains an unsafe attempt ID.");
        }

        var imagePath = pass2.GetProperty("image_path").GetString()
            ?? throw new InvalidDataException(
                "FEEDBACK history image_path is missing.");
        var xmpPath = pass2.GetProperty("xmp_path").GetString()
            ?? throw new InvalidDataException(
                "FEEDBACK history xmp_path is missing.");
        EnsureSafeMetadataPath(config, Path.GetFullPath(xmpPath));

        var artifact = new FeedbackImageArtifact(
            imagePath,
            pass2.GetProperty("image_sha256").GetString()
                ?? throw new InvalidDataException("Pass 2 SHA is missing."),
            pass2.GetProperty("size_bytes").GetInt64(),
            pass2.GetProperty("width").GetInt32(),
            pass2.GetProperty("height").GetInt32(),
            pass2.GetProperty("bits_per_sample").GetInt32(),
            pass2.GetProperty("channels").GetInt32(),
            pass2.GetProperty("darktable_version").GetString()
                ?? throw new InvalidDataException("Darktable version is missing."),
            TimeSpan.Zero,
            []);

        var xmpSha = pass2.GetProperty("xmp_sha256").GetString()
            ?? throw new InvalidDataException("Pass 2 XMP SHA is missing.");
        if (!File.Exists(xmpPath) ||
            !string.Equals(
                await Sha256Async(xmpPath, cancellationToken).ConfigureAwait(false),
                xmpSha,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Pass 2 XMP is missing or differs from FEEDBACK history.");
        }

        return new(attemptId, artifact, xmpPath, xmpSha);
    }

    private static void EnsureSafeMetadataPath(
        ProjectConfigV1 config,
        string target)
    {
        var root = Path.GetFullPath(
            Path.Combine(config.OutputFolder, ".photo-ai-factory"));
        if (!root.EndsWith(
                Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal))
        {
            root += Path.DirectorySeparatorChar;
        }

        var full = Path.GetFullPath(target);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Portable FEEDBACK metadata path escaped .photo-ai-factory.");
        }
    }

    private static void RequireString(
        JsonElement obj,
        string name,
        string expected)
    {
        if (!obj.TryGetProperty(name, out var item) ||
            item.ValueKind != JsonValueKind.String ||
            !string.Equals(
                item.GetString(), expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"FEEDBACK history field {name} does not match current durable state.");
        }
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<string> Sha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false))
            .ToLowerInvariant();
    }
}
