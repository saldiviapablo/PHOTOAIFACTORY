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

public sealed class DarktableBasicRevealExecutor(
    IGpuResourceCoordinator gpu,
    IAppPaths paths,
    ProcessRunner runner,
    ComponentLockReader componentLockReader,
    IOptions<AnalysisRuntimeOptions> options) : IBasicRevealExecutor
{
    public string GetOutputPath(ProjectId projectId, JobId jobId, string attemptId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        return Path.Combine(
            paths.WorkDirectory,
            projectId.Value,
            jobId.Value,
            attemptId,
            "reveal",
            "basic-reveal.jpg");
    }

    public async Task<BasicRevealArtifact> ExportAsync(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        BasicRevealJobSnapshot job,
        DarktableControlPlan plan,
        int jpegQuality,
        CancellationToken cancellationToken = default)
    {
        if (jpegQuality is < 5 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jpegQuality),
                "Darktable JPEG quality must be between 5 and 100.");
        }

        var input = Path.GetFullPath(job.InputPath);
        if (!File.Exists(input))
        {
            throw new RevealStageException(
                "MANAGED_ORIGINAL_MISSING",
                "input",
                $"Managed reveal input is missing: {input}",
                false);
        }

        var beforeHash = await Sha256Async(input, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(beforeHash, job.InputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RevealStageException(
                "MANAGED_ORIGINAL_INTEGRITY",
                "input",
                "Managed original SHA-256 does not match the Asset row.",
                false);
        }

        string? validatedXmp = null;
        if (!string.IsNullOrWhiteSpace(plan.XmpPath))
        {
            validatedXmp = Path.GetFullPath(plan.XmpPath);
            if (!File.Exists(validatedXmp))
            {
                throw new RevealStageException(
                    "VALIDATED_XMP_MISSING",
                    "input",
                    $"Validated recipe XMP was not found: {validatedXmp}",
                    false);
            }

            if (!string.IsNullOrWhiteSpace(plan.XmpSha256))
            {
                var xmpHash = await Sha256Async(
                    validatedXmp, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(
                        xmpHash, plan.XmpSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RevealStageException(
                        "VALIDATED_XMP_INTEGRITY",
                        "input",
                        "Validated recipe XMP SHA-256 mismatch.",
                        false);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(plan.Style))
        {
            throw new RevealStageException(
                "STYLE_CATALOG_NOT_VALIDATED",
                "configuration",
                "Phase 4 baseline does not enable Darktable styles without a separately validated catalog/configdir.",
                false);
        }

        var output = Path.GetFullPath(GetOutputPath(projectId, jobId, attemptId));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (File.Exists(output))
        {
            throw new RevealStageException(
                "REVEAL_OUTPUT_COLLISION",
                "storage",
                $"Reveal output collision: {output}",
                false);
        }

        var partial = Path.Combine(
            Path.GetDirectoryName(output)!,
            $"basic-reveal.partial-{Guid.NewGuid():N}.jpg");

        string darktablePath;
        try
        {
            darktablePath = ResolveDarktableCliPath();
        }
        catch (FileNotFoundException ex)
        {
            throw new RevealStageException(
                "DARKTABLE_COMPONENT_MISSING", "component", ex.Message, false, ex);
        }

        try
        {
            var darktable = new DarktableCliAdapter(darktablePath, runner);
            var version = await darktable.GetVersionAsync(cancellationToken).ConfigureAwait(false);

            ProcessExecutionResult result;
            await using (var lease = await gpu.AcquireAsync(
                $"darktable-basic-reveal:{jobId.Value}",
                cancellationToken).ConfigureAwait(false))
            {
                var darktableRunRoot = Path.Combine(
                    Path.GetDirectoryName(output)!, "darktable-runtime");
                result = await darktable.ExportAsync(
                    new DarktableExportRequest(
                        input,
                        partial,
                        XmpPath: validatedXmp,
                        Style: null,
                        MaxWidth: null,
                        MaxHeight: null,
                        HighQuality: true,
                        ApplyCustomPresets: plan.ApplyCustomPresets,
                        JpegQuality: jpegQuality,
                        ConfigDirectory: Path.Combine(darktableRunRoot, "config"),
                        CacheDirectory: Path.Combine(darktableRunRoot, "cache"),
                        LibraryPath: ":memory:"),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!result.Success)
            {
                throw new RevealStageException(
                    "DARKTABLE_EXPORT_FAILED",
                    "darktable",
                    $"Darktable basic reveal failed with exit {result.ExitCode}: {result.StdErr}",
                    true);
            }

            ValidatedJpeg validated;
            try
            {
                validated = await JpegArtifactValidator.ValidateAsync(
                    partial, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                throw new RevealStageException(
                    "DARKTABLE_OUTPUT_INVALID",
                    "artifact",
                    ex.Message,
                    true,
                    ex);
            }

            var afterHash = await Sha256Async(input, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(afterHash, job.InputSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new RevealStageException(
                    "MANAGED_ORIGINAL_MUTATED",
                    "integrity",
                    "Managed original changed during Darktable execution.",
                    false);
            }

            File.Move(partial, output);
            return new(
                output,
                validated.Sha256,
                validated.SizeBytes,
                validated.Width,
                validated.Height,
                version,
                result.Duration);
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
                "DARKTABLE_TIMEOUT", "darktable", ex.Message, true, ex);
        }
        catch (IOException ex)
        {
            throw new RevealStageException(
                "REVEAL_IO_ERROR", "storage", ex.Message, true, ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new RevealStageException(
                "DARKTABLE_RUNTIME_ERROR", "darktable", ex.Message, true, ex);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    public async Task<BasicRevealArtifact> RecoverAsync(
        ProjectId projectId,
        JobId jobId,
        BasicRevealJobSnapshot job,
        BasicRevealRecovery recovery,
        CancellationToken cancellationToken = default)
    {
        var input = Path.GetFullPath(job.InputPath);
        if (!File.Exists(input))
        {
            throw new RevealStageException(
                "MANAGED_ORIGINAL_MISSING", "input",
                $"Managed reveal input is missing: {input}", false);
        }

        var inputHash = await Sha256Async(input, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(inputHash, job.InputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RevealStageException(
                "MANAGED_ORIGINAL_INTEGRITY", "input",
                "Managed original SHA-256 does not match the Asset row.", false);
        }

        var output = Path.GetFullPath(
            GetOutputPath(projectId, jobId, recovery.AttemptId));
        if (!File.Exists(output))
        {
            throw new RevealStageException(
                "REVEAL_RECOVERY_OUTPUT_MISSING", "integrity",
                "Portable history exists but its attempt-owned JPEG is missing.", false);
        }

        ValidatedJpeg validated;
        try
        {
            validated = await JpegArtifactValidator.ValidateAsync(
                output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw new RevealStageException(
                "REVEAL_RECOVERY_OUTPUT_INVALID", "integrity",
                ex.Message, false, ex);
        }

        var expected = recovery.Artifact;
        if (!string.Equals(validated.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase) ||
            validated.SizeBytes != expected.SizeBytes ||
            validated.Width != expected.Width ||
            validated.Height != expected.Height)
        {
            throw new RevealStageException(
                "REVEAL_RECOVERY_OUTPUT_MISMATCH", "integrity",
                "Attempt-owned JPEG differs from immutable portable history.", false);
        }

        var darktable = new DarktableCliAdapter(ResolveDarktableCliPath(), runner);
        var version = await darktable.GetVersionAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(version, expected.DarktableVersion, StringComparison.Ordinal))
        {
            throw new RevealStageException(
                "REVEAL_RECOVERY_COMPONENT_DRIFT", "component",
                "Darktable version differs from immutable portable history.", false);
        }

        var afterHash = await Sha256Async(input, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(afterHash, job.InputSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RevealStageException(
                "MANAGED_ORIGINAL_MUTATED", "integrity",
                "Managed original changed while recovering the reveal artifact.", false);
        }

        return expected with { Path = output };
    }

    private string ResolveDarktableCliPath()
    {
        var configured = options.Value.DarktableCliPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            if (File.Exists(full))
            {
                return full;
            }

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
                {
                    continue;
                }

                var local = Path.GetFullPath(component.LocalPath);
                if (File.Exists(local))
                {
                    return local;
                }

                if (Directory.Exists(local))
                {
                    foreach (var candidate in new[]
                    {
                        Path.Combine(local, "bin", "darktable-cli.exe"),
                        Path.Combine(local, "darktable-cli.exe")
                    })
                    {
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }
        }

        var programFiles =
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var standardInstall =
            Path.Combine(programFiles, "darktable", "bin", "darktable-cli.exe");
        if (File.Exists(standardInstall))
        {
            return standardInstall;
        }

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
