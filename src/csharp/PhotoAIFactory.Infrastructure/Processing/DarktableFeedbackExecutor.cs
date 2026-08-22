using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;
using PhotoAIFactory.Infrastructure;
using PhotoAIFactory.Infrastructure.Analysis;

namespace PhotoAIFactory.Infrastructure.Processing;

public sealed class DarktableFeedbackExecutor(
    IGpuResourceCoordinator gpu,
    IAppPaths paths,
    ProcessRunner runner,
    ComponentLockReader componentLockReader,
    IOptions<AnalysisRuntimeOptions> options) : IDarktableFeedbackExecutor
{
    public async Task<FeedbackImageArtifact> ExportPass1Async(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        FeedbackJobSnapshot job,
        CancellationToken cancellationToken = default)
    {
        ValidateInputPolicy(job);
        var input = await VerifyManagedInputAsync(job, cancellationToken)
            .ConfigureAwait(false);

        var output = FeedbackPath(
            projectId, jobId, attemptId, "pass1.tif");
        EnsureNoCollision(output, "FEEDBACK_PASS1_OUTPUT_COLLISION");

        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(
            directory,
            $"pass1.partial-{Guid.NewGuid():N}.tif");

        try
        {
            var darktable = new DarktableCliAdapter(
                ResolveDarktableCliPath(), runner);
            var version = await darktable.GetVersionAsync(cancellationToken)
                .ConfigureAwait(false);

            ProcessExecutionResult result;
            await using (var lease = await gpu.AcquireAsync(
                $"darktable-feedback-pass1:{jobId.Value}",
                cancellationToken).ConfigureAwait(false))
            {
                var runtime = Path.Combine(directory, "darktable-runtime");
                result = await darktable.ExportAsync(
                    new DarktableExportRequest(
                        input,
                        partial,
                        XmpPath: null,
                        Style: null,
                        MaxWidth: null,
                        MaxHeight: null,
                        HighQuality: true,
                        ApplyCustomPresets: false,
                        JpegQuality: null,
                        ConfigDirectory: Path.Combine(runtime, "config"),
                        CacheDirectory: Path.Combine(runtime, "cache"),
                        LibraryPath: ":memory:",
                        IccType: "SRGB",
                        TiffBitsPerSample: 16,
                        TiffWriteRgb: true),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!result.Success)
            {
                throw new RevealStageException(
                    "DARKTABLE_PASS1_FAILED",
                    "darktable",
                    $"Darktable FEEDBACK Pass 1 failed with exit {result.ExitCode}: {result.StdErr}",
                    true);
            }

            ValidatedTiff16 validated;
            try
            {
                validated = await Tiff16ArtifactValidator.ValidateAsync(
                    partial, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw new RevealStageException(
                    "DARKTABLE_PASS1_OUTPUT_INVALID",
                    "artifact",
                    ex.Message,
                    true,
                    ex);
            }

            await VerifyUnchangedAsync(job, input, cancellationToken)
                .ConfigureAwait(false);
            File.Move(partial, output);

            return new(
                output,
                validated.Sha256,
                validated.SizeBytes,
                validated.Width,
                validated.Height,
                validated.BitsPerSample,
                validated.Channels,
                version,
                result.Duration,
                validated.AuthenticXmp);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RevealStageException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            throw new RevealStageException(
                "DARKTABLE_PASS1_TIMEOUT",
                "darktable",
                ex.Message,
                true,
                ex);
        }
        catch (IOException ex)
        {
            throw new RevealStageException(
                "FEEDBACK_PASS1_IO_ERROR",
                "storage",
                ex.Message,
                true,
                ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new RevealStageException(
                "DARKTABLE_PASS1_RUNTIME_ERROR",
                "darktable",
                ex.Message,
                true,
                ex);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    public async Task<FeedbackImageArtifact> ValidatePersistedPass1Async(
        FeedbackJobSnapshot job,
        FeedbackPassSnapshot pass,
        CancellationToken cancellationToken = default)
    {
        ValidateInputPolicy(job);
        var input = await VerifyManagedInputAsync(job, cancellationToken)
            .ConfigureAwait(false);

        if (!File.Exists(pass.ImagePath))
            throw new RevealStageException(
                "FEEDBACK_PASS1_RECOVERY_MISSING",
                "integrity",
                "DARKTABLE_PASS1_COMPLETE exists but Pass 1 TIFF is missing.",
                false);

        ValidatedTiff16 validated;
        try
        {
            validated = await Tiff16ArtifactValidator.ValidateAsync(
                pass.ImagePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw new RevealStageException(
                "FEEDBACK_PASS1_RECOVERY_INVALID",
                "integrity",
                ex.Message,
                false,
                ex);
        }

        if (!string.Equals(
                validated.Sha256, pass.ImageSha256,
                StringComparison.OrdinalIgnoreCase) ||
            validated.SizeBytes != pass.ImageSizeBytes ||
            validated.Width != pass.ImageWidth ||
            validated.Height != pass.ImageHeight ||
            validated.BitsPerSample != 16)
        {
            throw new RevealStageException(
                "FEEDBACK_PASS1_RECOVERY_MISMATCH",
                "integrity",
                "Persisted Pass 1 TIFF differs from its durable row.",
                false);
        }

        if (!File.Exists(pass.XmpPath))
            throw new RevealStageException(
                "FEEDBACK_PASS1_XMP_MISSING",
                "integrity",
                "Pass 1 authentic XMP is missing.",
                false);

        var xmpHash = await Sha256Async(pass.XmpPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                xmpHash, pass.XmpSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RevealStageException(
                "FEEDBACK_PASS1_XMP_MISMATCH",
                "integrity",
                "Pass 1 XMP SHA-256 mismatch.",
                false);
        }

        var xmpBytes = await File.ReadAllBytesAsync(
            pass.XmpPath, cancellationToken).ConfigureAwait(false);
        DarktableXmpExtractor.ValidateDarktablePacket(xmpBytes);

        var darktable = new DarktableCliAdapter(
            ResolveDarktableCliPath(), runner);
        var currentVersion = await darktable.GetVersionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                currentVersion, pass.DarktableVersion, StringComparison.Ordinal))
        {
            throw new RevealStageException(
                "FEEDBACK_COMPONENT_DRIFT",
                "component",
                "Darktable version changed after Pass 1.",
                false);
        }

        await VerifyUnchangedAsync(job, input, cancellationToken)
            .ConfigureAwait(false);

        return new(
            pass.ImagePath,
            validated.Sha256,
            validated.SizeBytes,
            validated.Width,
            validated.Height,
            validated.BitsPerSample,
            validated.Channels,
            pass.DarktableVersion,
            TimeSpan.Zero,
            xmpBytes);
    }

    public async Task<FeedbackImageArtifact> ExportPass2Async(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        FeedbackJobSnapshot job,
        FeedbackPassSnapshot pass1,
        int jpegQuality,
        CancellationToken cancellationToken = default)
    {
        ValidateInputPolicy(job);
        if (jpegQuality is < 5 or > 100)
            throw new ArgumentOutOfRangeException(nameof(jpegQuality));

        var input = await VerifyManagedInputAsync(job, cancellationToken)
            .ConfigureAwait(false);
        await ValidatePass1XmpAsync(pass1, cancellationToken)
            .ConfigureAwait(false);

        var output = FeedbackPath(
            projectId, jobId, attemptId, "pass2.jpg");
        EnsureNoCollision(output, "FEEDBACK_PASS2_OUTPUT_COLLISION");
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(
            directory,
            $"pass2.partial-{Guid.NewGuid():N}.jpg");

        try
        {
            var darktable = new DarktableCliAdapter(
                ResolveDarktableCliPath(), runner);
            var version = await darktable.GetVersionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    version, pass1.DarktableVersion, StringComparison.Ordinal))
            {
                throw new RevealStageException(
                    "FEEDBACK_COMPONENT_DRIFT",
                    "component",
                    "Pass 2 requires the same Darktable version as Pass 1.",
                    false);
            }

            ProcessExecutionResult result;
            await using (var lease = await gpu.AcquireAsync(
                $"darktable-feedback-pass2:{jobId.Value}",
                cancellationToken).ConfigureAwait(false))
            {
                var runtime = Path.Combine(directory, "darktable-runtime");
                result = await darktable.ExportAsync(
                    new DarktableExportRequest(
                        input,
                        partial,
                        XmpPath: pass1.XmpPath,
                        Style: null,
                        MaxWidth: null,
                        MaxHeight: null,
                        HighQuality: true,
                        ApplyCustomPresets: false,
                        JpegQuality: jpegQuality,
                        ConfigDirectory: Path.Combine(runtime, "config"),
                        CacheDirectory: Path.Combine(runtime, "cache"),
                        LibraryPath: ":memory:"),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!result.Success)
            {
                throw new RevealStageException(
                    "DARKTABLE_PASS2_FAILED",
                    "darktable",
                    $"Darktable FEEDBACK Pass 2 failed with exit {result.ExitCode}: {result.StdErr}",
                    true);
            }

            ValidatedJpeg validated;
            byte[] xmp;
            try
            {
                validated = await JpegArtifactValidator.ValidateAsync(
                    partial, cancellationToken).ConfigureAwait(false);
                xmp = await DarktableXmpExtractor.FromJpegAsync(
                    partial, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                throw new RevealStageException(
                    "DARKTABLE_PASS2_OUTPUT_INVALID",
                    "artifact",
                    ex.Message,
                    true,
                    ex);
            }

            await VerifyUnchangedAsync(job, input, cancellationToken)
                .ConfigureAwait(false);
            File.Move(partial, output);

            return new(
                output,
                validated.Sha256,
                validated.SizeBytes,
                validated.Width,
                validated.Height,
                8,
                3,
                version,
                result.Duration,
                xmp);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RevealStageException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            throw new RevealStageException(
                "DARKTABLE_PASS2_TIMEOUT",
                "darktable",
                ex.Message,
                true,
                ex);
        }
        catch (IOException ex)
        {
            throw new RevealStageException(
                "FEEDBACK_PASS2_IO_ERROR",
                "storage",
                ex.Message,
                true,
                ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new RevealStageException(
                "DARKTABLE_PASS2_RUNTIME_ERROR",
                "darktable",
                ex.Message,
                true,
                ex);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    public async Task<FeedbackImageArtifact> RecoverPass2Async(
        FeedbackJobSnapshot job,
        FeedbackPass2Recovery recovery,
        CancellationToken cancellationToken = default)
    {
        var input = await VerifyManagedInputAsync(job, cancellationToken)
            .ConfigureAwait(false);
        var expectedPath = FeedbackPath(
            job.ProjectId,
            job.Id,
            recovery.AttemptId,
            "pass2.jpg");
        if (!string.Equals(
                Path.GetFullPath(recovery.Artifact.Path),
                expectedPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RevealStageException(
                "FEEDBACK_PASS2_RECOVERY_PATH_INVALID",
                "integrity",
                "Portable FEEDBACK history points outside its attempt-owned Pass 2 path.",
                false);
        }

        if (!File.Exists(expectedPath))
            throw new RevealStageException(
                "FEEDBACK_PASS2_RECOVERY_MISSING",
                "integrity",
                "Portable FEEDBACK history exists but Pass 2 JPEG is missing.",
                false);

        ValidatedJpeg validated;
        byte[] xmp;
        try
        {
            validated = await JpegArtifactValidator.ValidateAsync(
                expectedPath,
                cancellationToken).ConfigureAwait(false);
            xmp = await DarktableXmpExtractor.FromJpegAsync(
                expectedPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw new RevealStageException(
                "FEEDBACK_PASS2_RECOVERY_INVALID",
                "integrity",
                ex.Message,
                false,
                ex);
        }

        var expected = recovery.Artifact;
        if (!string.Equals(
                validated.Sha256, expected.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            validated.SizeBytes != expected.SizeBytes ||
            validated.Width != expected.Width ||
            validated.Height != expected.Height)
        {
            throw new RevealStageException(
                "FEEDBACK_PASS2_RECOVERY_MISMATCH",
                "integrity",
                "Recovered Pass 2 JPEG differs from immutable history.",
                false);
        }

        var embeddedXmpHash = Convert.ToHexString(
                SHA256.HashData(xmp))
            .ToLowerInvariant();
        if (!string.Equals(
                embeddedXmpHash,
                recovery.XmpSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RevealStageException(
                "FEEDBACK_PASS2_XMP_RECOVERY_MISMATCH",
                "integrity",
                "Recovered Pass 2 embedded XMP differs from immutable history.",
                false);
        }

        var currentVersion = await new DarktableCliAdapter(
                ResolveDarktableCliPath(), runner)
            .GetVersionAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                currentVersion, expected.DarktableVersion,
                StringComparison.Ordinal))
        {
            throw new RevealStageException(
                "FEEDBACK_COMPONENT_DRIFT",
                "component",
                "Darktable version changed before FEEDBACK recovery.",
                false);
        }

        await VerifyUnchangedAsync(job, input, cancellationToken)
            .ConfigureAwait(false);

        return expected with { AuthenticXmp = xmp };
    }

    public Task CleanupPass1TemporaryAsync(
        FeedbackPassSnapshot pass1,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var full = Path.GetFullPath(pass1.ImagePath);
        var workRoot = Path.GetFullPath(paths.WorkDirectory);
        if (!workRoot.EndsWith(
                Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal))
        {
            workRoot += Path.DirectorySeparatorChar;
        }

        var expectedSuffix = Path.Combine(
            pass1.JobId.Value,
            pass1.AttemptId,
            "feedback",
            "pass1.tif");
        if (!full.StartsWith(workRoot, StringComparison.OrdinalIgnoreCase) ||
            !full.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Pass 1 cleanup path escaped its attempt-owned FEEDBACK workspace.");
        }

        if (File.Exists(full))
            File.Delete(full);
        return Task.CompletedTask;
    }

    private string FeedbackPath(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        if (!string.Equals(Path.GetFileName(attemptId), attemptId, StringComparison.Ordinal))
            throw new ArgumentException("Unsafe attempt ID.", nameof(attemptId));

        return Path.GetFullPath(
            Path.Combine(
                paths.WorkDirectory,
                projectId.Value,
                jobId.Value,
                attemptId,
                "feedback",
                fileName));
    }

    private static void ValidateInputPolicy(FeedbackJobSnapshot job)
    {
        if (string.Equals(job.InputFormat, "RAW", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(
                    job.RawSupportStatus,
                    "SUPPORTED_FULL_SIZE",
                    StringComparison.Ordinal))
            {
                throw new RevealStageException(
                    "FEEDBACK_RAW_NOT_SUPPORTED",
                    "input",
                    $"FEEDBACK V1 requires SUPPORTED_FULL_SIZE RAW; got {job.RawSupportStatus}.",
                    false);
            }
            return;
        }

        if (string.Equals(job.InputFormat, "JPEG", StringComparison.OrdinalIgnoreCase))
            return;

        throw new RevealStageException(
            "FEEDBACK_INPUT_FORMAT_UNSUPPORTED",
            "input",
            $"FEEDBACK does not support input format {job.InputFormat}.",
            false);
    }

    private async Task<string> VerifyManagedInputAsync(
        FeedbackJobSnapshot job,
        CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(job.InputPath);
        if (!File.Exists(input))
            throw new RevealStageException(
                "MANAGED_ORIGINAL_MISSING",
                "input",
                $"Managed FEEDBACK input is missing: {input}",
                false);

        var hash = await Sha256Async(input, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                hash, job.InputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RevealStageException(
                "MANAGED_ORIGINAL_INTEGRITY",
                "input",
                "Managed original SHA-256 does not match the Asset row.",
                false);
        }

        if (string.Equals(
                job.InputFormat, "JPEG", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await JpegArtifactValidator.ValidateAsync(
                    input, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                throw new RevealStageException(
                    "FEEDBACK_JPEG_INPUT_INVALID",
                    "input",
                    ex.Message,
                    false,
                    ex);
            }
        }

        return input;
    }

    private static async Task VerifyUnchangedAsync(
        FeedbackJobSnapshot job,
        string input,
        CancellationToken cancellationToken)
    {
        var hash = await Sha256Async(input, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                hash, job.InputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RevealStageException(
                "MANAGED_ORIGINAL_MUTATED",
                "integrity",
                "Managed original changed during FEEDBACK execution.",
                false);
        }
    }

    private static async Task ValidatePass1XmpAsync(
        FeedbackPassSnapshot pass1,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(pass1.XmpPath))
            throw new RevealStageException(
                "FEEDBACK_PASS1_XMP_MISSING",
                "integrity",
                "Pass 1 XMP is missing before Pass 2.",
                false);

        var hash = await Sha256Async(pass1.XmpPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                hash, pass1.XmpSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RevealStageException(
                "FEEDBACK_PASS1_XMP_MISMATCH",
                "integrity",
                "Pass 1 XMP changed before Pass 2.",
                false);
        }

        var packet = await File.ReadAllBytesAsync(
            pass1.XmpPath, cancellationToken).ConfigureAwait(false);
        DarktableXmpExtractor.ValidateDarktablePacket(packet);
    }

    private static void EnsureNoCollision(string path, string code)
    {
        if (File.Exists(path))
            throw new RevealStageException(
                code,
                "storage",
                $"Attempt-owned output collision: {path}",
                false);
    }

    private string ResolveDarktableCliPath()
    {
        var configured = options.Value.DarktableCliPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (File.Exists(full))
                return full;
            throw new FileNotFoundException(
                "Configured darktable-cli was not found.", full);
        }

        foreach (var lockPath in new[]
        {
            Path.Combine(paths.RootDirectory, "components.lock.json"),
            Path.Combine(paths.ComponentsDirectory, "components.lock.json")
        })
        {
            var components = componentLockReader.Read(lockPath);
            foreach (var id in new[] { "darktable", "darktable-cli" })
            {
                if (!components.TryGetValue(id, out var component) ||
                    !component.Installed ||
                    string.IsNullOrWhiteSpace(component.LocalPath))
                    continue;

                var local = Path.GetFullPath(component.LocalPath);
                if (File.Exists(local))
                    return local;

                if (Directory.Exists(local))
                {
                    foreach (var candidate in new[]
                    {
                        Path.Combine(local, "bin", "darktable-cli.exe"),
                        Path.Combine(local, "darktable-cli.exe")
                    })
                    {
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
        }

        var programFiles =
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var standardInstall =
            Path.Combine(programFiles, "darktable", "bin", "darktable-cli.exe");
        if (File.Exists(standardInstall))
            return standardInstall;

        throw new FileNotFoundException(
            "darktable-cli was not found. Configure PhotoAIFactory:Analysis:DarktableCliPath " +
            "or repair the local component inventory.");
    }

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
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }
}
