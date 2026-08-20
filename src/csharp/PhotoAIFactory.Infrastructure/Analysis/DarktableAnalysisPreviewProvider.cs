using Microsoft.Extensions.Options;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Ingestion;

namespace PhotoAIFactory.Infrastructure.Analysis;

public sealed class DarktableAnalysisPreviewProvider(
    IGpuResourceCoordinator gpu,
    IAppPaths paths,
    ProcessRunner runner,
    ComponentLockReader componentLockReader,
    IOptions<AnalysisRuntimeOptions> options) : IAnalysisPreviewProvider
{
    public string GetPreviewPath(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        AssetSnapshot rawAsset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        if (string.IsNullOrWhiteSpace(rawAsset.Sha256) || rawAsset.Sha256.Length < 16)
        {
            throw new ArgumentException("RAW Asset requires a valid SHA-256.", nameof(rawAsset));
        }

        var workspace = Path.Combine(
            paths.WorkDirectory, projectId.Value, jobId.Value, attemptId, "analysis");
        return Path.Combine(workspace, $"raw-preview-{rawAsset.Sha256[..16]}.jpg");
    }

    public async Task EnsurePreviewAsync(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        AssetSnapshot rawAsset,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var requested = Path.GetFullPath(destinationPath);
        var jobRoot = Path.GetFullPath(Path.Combine(paths.WorkDirectory, projectId.Value, jobId.Value));
        var jobRootPrefix = jobRoot.EndsWith(Path.DirectorySeparatorChar)
            ? jobRoot
            : jobRoot + Path.DirectorySeparatorChar;
        var expectedName = $"raw-preview-{rawAsset.Sha256[..16]}.jpg";
        if (!requested.StartsWith(jobRootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(requested), expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Frozen RAW preview path is outside the stage-owned Job workspace.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(requested)!);
        if (File.Exists(requested) && new FileInfo(requested).Length > 0)
        {
            return;
        }

        var partialPath = Path.Combine(
            Path.GetDirectoryName(requested)!,
            $"{Path.GetFileNameWithoutExtension(requested)}.partial-{Guid.NewGuid():N}.jpg");

        try
        {
            await using var lease = await gpu.AcquireAsync(
                $"darktable-analysis-preview:{jobId.Value}", cancellationToken).ConfigureAwait(false);

            var darktable = new DarktableCliAdapter(ResolveDarktableCliPath(), runner);
            var result = await darktable.ExportAsync(
                new DarktableExportRequest(
                    rawAsset.ManagedPath,
                    partialPath,
                    XmpPath: null,
                    Style: null,
                    MaxWidth: 2048,
                    MaxHeight: 2048,
                    HighQuality: false),
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"Darktable analysis preview failed with exit {result.ExitCode}: {result.StdErr}");
            }

            if (!File.Exists(partialPath) || new FileInfo(partialPath).Length == 0)
            {
                throw new InvalidDataException(
                    "Darktable returned success without a non-empty preview.");
            }

            if (File.Exists(requested))
            {
                throw new IOException($"Analysis preview collision: {requested}");
            }

            File.Move(partialPath, requested);
        }
        finally
        {
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }
        }
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

            throw new FileNotFoundException("Configured darktable-cli was not found.", full);
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

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var standardInstall = Path.Combine(programFiles, "darktable", "bin", "darktable-cli.exe");
        if (File.Exists(standardInstall))
        {
            return standardInstall;
        }

        throw new FileNotFoundException(
            "darktable-cli was not found. Configure PhotoAIFactory:Analysis:DarktableCliPath " +
            "or repair the local component inventory.");
    }
}
