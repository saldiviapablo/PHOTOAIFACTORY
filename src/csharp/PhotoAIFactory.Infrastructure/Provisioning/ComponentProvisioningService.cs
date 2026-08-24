using System.Diagnostics;
using System.Security.Cryptography;
using PhotoAIFactory.Application.Provisioning;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.Storage;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.Provisioning;

public sealed class ComponentProvisioningService : IComponentProvisioner
{
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
        "huggingface.co",
        "raw.githubusercontent.com",
        "download.photoaifactory.internal"
    };

    private readonly IReleaseManifestService _manifestService;
    private readonly IStorageSpaceInspector _storageInspector;
    private readonly IAppPaths _appPaths;
    private readonly HttpClient _httpClient;
    private readonly string _offlinePayloadDir;

    public ComponentProvisioningService(
        IReleaseManifestService manifestService,
        IStorageSpaceInspector storageInspector,
        IAppPaths appPaths,
        HttpClient? httpClient = null,
        string? offlinePayloadDir = null)
    {
        _manifestService = manifestService;
        _storageInspector = storageInspector;
        _appPaths = appPaths;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _offlinePayloadDir = offlinePayloadDir ?? Path.Combine(AppContext.BaseDirectory, "payloads");
    }

    public async Task<IReadOnlyList<ComponentState>> InspectAllAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = await _manifestService.LoadComponentDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<ComponentState>();

        foreach (var desc in descriptors)
        {
            var state = await InspectDescriptorAsync(desc, cancellationToken).ConfigureAwait(false);
            list.Add(state);
        }

        return list;
    }

    public async Task<ComponentState> InspectAsync(string componentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        var descriptors = await _manifestService.LoadComponentDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        var desc = descriptors.FirstOrDefault(d => string.Equals(d.ComponentId, componentId, StringComparison.OrdinalIgnoreCase));

        if (desc is null)
        {
            throw new ArgumentException($"Unknown component ID '{componentId}'.", nameof(componentId));
        }

        return await InspectDescriptorAsync(desc, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComponentState> ProvisionAsync(
        string componentId,
        IProgress<ComponentProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var desc = await GetDescriptorAsync(componentId, cancellationToken).ConfigureAwait(false);
        var currentState = await InspectDescriptorAsync(desc, cancellationToken).ConfigureAwait(false);

        if (currentState.Status == ComponentStatus.Installed)
        {
            return currentState;
        }

        return await ExecuteProvisioningAsync(desc, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComponentState> RepairAsync(
        string componentId,
        IProgress<ComponentProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var desc = await GetDescriptorAsync(componentId, cancellationToken).ConfigureAwait(false);
        return await ExecuteProvisioningAsync(desc, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ComponentState>> ProvisionRequiredAsync(
        IProgress<ComponentProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var descriptors = await _manifestService.LoadComponentDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<ComponentState>();

        foreach (var desc in descriptors.Where(d => d.IsRequired))
        {
            var state = await ProvisionAsync(desc.ComponentId, progress, cancellationToken).ConfigureAwait(false);
            list.Add(state);
        }

        return list;
    }

    private async Task<ComponentDescriptor> GetDescriptorAsync(string componentId, CancellationToken cancellationToken)
    {
        var descriptors = await _manifestService.LoadComponentDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        var desc = descriptors.FirstOrDefault(d => string.Equals(d.ComponentId, componentId, StringComparison.OrdinalIgnoreCase));
        if (desc is null)
        {
            throw new ArgumentException($"Component '{componentId}' not found in manifest.", nameof(componentId));
        }

        return desc;
    }

    private async Task<ComponentState> InspectDescriptorAsync(ComponentDescriptor desc, CancellationToken cancellationToken)
    {
        var targetDir = ResolveTargetDirectory(desc);
        if (!Directory.Exists(targetDir))
        {
            return new ComponentState(desc, ComponentStatus.Missing, null, null, null, "Directory does not exist.");
        }

        // Special handling for ModelFileset
        if (desc.Format == PayloadFormat.ModelFileset && desc.Fileset != null && desc.Fileset.Count > 0)
        {
            foreach (var entry in desc.Fileset)
            {
                var filePath = Path.Combine(targetDir, entry.RelativePath);
                if (!File.Exists(filePath))
                {
                    return new ComponentState(desc, ComponentStatus.Missing, targetDir, null, null, $"Fileset member missing: '{entry.RelativePath}'");
                }

                var actualFileSha = await ComputeSha256Async(filePath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualFileSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new ComponentState(desc, ComponentStatus.Corrupted, targetDir, actualFileSha, DateTimeOffset.UtcNow.ToString("O"), $"Fileset member '{entry.RelativePath}' SHA mismatch: expected {entry.Sha256}, got {actualFileSha}");
                }
            }

            return new ComponentState(desc, ComponentStatus.Installed, targetDir, desc.InstalledArtifactSha256, DateTimeOffset.UtcNow.ToString("O"), null);
        }

        var mainFile = ResolveMainFile(desc, targetDir);
        if (!File.Exists(mainFile))
        {
            return new ComponentState(desc, ComponentStatus.Missing, targetDir, null, null, "Main executable/artifact file missing.");
        }

        try
        {
            var actualSha = await ComputeSha256Async(mainFile, cancellationToken).ConfigureAwait(false);
            var matches = string.Equals(actualSha, desc.InstalledArtifactSha256, StringComparison.OrdinalIgnoreCase);

            if (matches)
            {
                return new ComponentState(desc, ComponentStatus.Installed, targetDir, actualSha, DateTimeOffset.UtcNow.ToString("O"), null);
            }
            else
            {
                return new ComponentState(desc, ComponentStatus.Corrupted, targetDir, actualSha, DateTimeOffset.UtcNow.ToString("O"), $"SHA-256 mismatch: expected {desc.InstalledArtifactSha256}, got {actualSha}");
            }
        }
        catch (Exception ex)
        {
            return new ComponentState(desc, ComponentStatus.Failed, targetDir, null, null, ex.Message);
        }
    }

    private async Task<ComponentState> ExecuteProvisioningAsync(
        ComponentDescriptor desc,
        IProgress<ComponentProvisionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var targetDir = ResolveTargetDirectory(desc);
        var tempDir = Path.Combine(Path.GetTempPath(), "PAF_Provisioning_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Storage Preflight
            var requiredBytes = desc.PayloadSizeBytes + (50L * 1024L * 1024L); // 50MB safety margin
            var targetRoot = Path.GetPathRoot(targetDir) ?? "C:\\";
            var freeBytes = _storageInspector.GetAvailableFreeSpaceBytes(targetRoot);

            if (freeBytes < requiredBytes)
            {
                throw new IOException($"Insufficient disk space on {targetRoot}. Required: {requiredBytes} bytes, Available: {freeBytes} bytes.");
            }

            // 2. Special provisioning for ModelFileset
            if (desc.Format == PayloadFormat.ModelFileset && desc.Fileset != null && desc.Fileset.Count > 0)
            {
                var filesetStagingDir = Path.Combine(tempDir, "fileset_staging");
                Directory.CreateDirectory(filesetStagingDir);

                for (int i = 0; i < desc.Fileset.Count; i++)
                {
                    var entry = desc.Fileset[i];
                    var stagedFile = Path.Combine(filesetStagingDir, entry.RelativePath);
                    var stagedFileDir = Path.GetDirectoryName(stagedFile);
                    if (!string.IsNullOrEmpty(stagedFileDir)) Directory.CreateDirectory(stagedFileDir);

                    var offlineFile = Path.Combine(_offlinePayloadDir, desc.ComponentId, entry.RelativePath);
                    if (File.Exists(offlineFile))
                    {
                        File.Copy(offlineFile, stagedFile, overwrite: true);
                    }
                    else if (!string.IsNullOrWhiteSpace(entry.SourceUrl))
                    {
                        await DownloadSingleHttpsAsync(entry.SourceUrl, stagedFile, cancellationToken).ConfigureAwait(false);
                    }
                    else if (!string.IsNullOrWhiteSpace(desc.SourceUrl) && entry.RelativePath == "model.safetensors")
                    {
                        await DownloadSingleHttpsAsync(desc.SourceUrl, stagedFile, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Source not found for fileset member '{entry.RelativePath}' of '{desc.ComponentId}'.");
                    }

                    // Strict verification of individual member
                    var memberSha = await ComputeSha256Async(stagedFile, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(memberSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Fileset member '{entry.RelativePath}' SHA-256 mismatch: expected {entry.Sha256}, got {memberSha}.");
                    }

                    progress?.Report(new ComponentProvisionProgress(desc.ComponentId, "Verifying", i + 1, desc.Fileset.Count, (double)(i + 1) / desc.Fileset.Count * 90.0, $"Verified {entry.RelativePath}..."));
                }

                // Promote entire fileset atomically
                Directory.CreateDirectory(targetDir);
                CopyDirectoryRecursive(filesetStagingDir, targetDir);

                // Write durable manifest.lock.json
                var manifestLockPath = Path.Combine(targetDir, "manifest.lock.json");
                var manifestLockJson = System.Text.Json.JsonSerializer.Serialize(desc.Fileset, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(manifestLockPath, manifestLockJson, cancellationToken).ConfigureAwait(false);

                var state = await InspectDescriptorAsync(desc, cancellationToken).ConfigureAwait(false);
                if (state.Status != ComponentStatus.Installed)
                {
                    throw new InvalidOperationException($"Post-provisioning verification failed for fileset '{desc.ComponentId}': {state.ErrorMessage}");
                }
                return state;
            }

            progress?.Report(new ComponentProvisionProgress(desc.ComponentId, "Acquiring", 0, desc.PayloadSizeBytes, 0.0, "Acquiring component payload..."));

            var ext = !string.IsNullOrWhiteSpace(desc.SourceUrl) ? Path.GetExtension(new Uri(desc.SourceUrl).AbsolutePath) : ".partial";
            if (string.IsNullOrWhiteSpace(ext)) ext = ".partial";
            var stagedArchiveOrFile = Path.Combine(tempDir, $"payload{ext}");

            // 3. Acquire from local offline payload or HTTPS download
            var localPayload = Path.Combine(_offlinePayloadDir, $"{desc.ComponentId}-{desc.Version}.zip");
            var localSingleFile = Path.Combine(_offlinePayloadDir, $"{desc.ComponentId}-{desc.Version}.bin");

            if (File.Exists(localPayload))
            {
                File.Copy(localPayload, stagedArchiveOrFile, overwrite: true);
            }
            else if (File.Exists(localSingleFile))
            {
                File.Copy(localSingleFile, stagedArchiveOrFile, overwrite: true);
            }
            else if (!string.IsNullOrWhiteSpace(desc.SourceUrl))
            {
                await DownloadHttpsAsync(desc, stagedArchiveOrFile, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException($"No local offline payload or source URL available for '{desc.ComponentId}'.");
            }

            // 4. STRICT PRE-ACTIVATION VERIFICATION: Verify Payload SHA-256 BEFORE extraction or promotion
            progress?.Report(new ComponentProvisionProgress(desc.ComponentId, "Verifying", desc.PayloadSizeBytes, desc.PayloadSizeBytes, 90.0, "Verifying cryptographic SHA-256 payload integrity..."));

            var payloadSha = await ComputeSha256Async(stagedArchiveOrFile, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(payloadSha, desc.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { if (File.Exists(stagedArchiveOrFile)) File.Delete(stagedArchiveOrFile); } catch { }
                throw new InvalidOperationException($"Payload SHA-256 mismatch for '{desc.ComponentId}': expected {desc.PayloadSha256}, got {payloadSha}. Extraction strictly aborted.");
            }

            // 5. Promote / Extract based on explicit PayloadFormat install strategy
            Directory.CreateDirectory(targetDir);

            switch (desc.Format)
            {
                case PayloadFormat.TarGzArchive:
                    ArchiveExtractionHelper.ExtractTarGzSafely(stagedArchiveOrFile, targetDir, null, cancellationToken);
                    break;

                case PayloadFormat.ZipArchive:
                    ArchiveExtractionHelper.ExtractZipSafely(stagedArchiveOrFile, targetDir, null, cancellationToken);
                    break;

                case PayloadFormat.ExeInstaller:
                    if (OperatingSystem.IsWindows() && stagedArchiveOrFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        var psi = new ProcessStartInfo(stagedArchiveOrFile, $"/S /D={targetDir}")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var installerProc = Process.Start(psi);
                        if (installerProc != null)
                        {
                            installerProc.WaitForExit();
                        }
                    }
                    else
                    {
                        var destExe = ResolveMainFile(desc, targetDir);
                        var destDir = Path.GetDirectoryName(destExe);
                        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                        File.Copy(stagedArchiveOrFile, destExe, overwrite: true);
                    }
                    break;

                case PayloadFormat.DirectoryBundle:
                    if (Directory.Exists(stagedArchiveOrFile))
                    {
                        CopyDirectoryRecursive(stagedArchiveOrFile, targetDir);
                    }
                    else
                    {
                        var destBundleFile = ResolveMainFile(desc, targetDir);
                        var destBundleDir = Path.GetDirectoryName(destBundleFile);
                        if (!string.IsNullOrEmpty(destBundleDir)) Directory.CreateDirectory(destBundleDir);
                        File.Copy(stagedArchiveOrFile, destBundleFile, overwrite: true);
                    }
                    break;

                case PayloadFormat.DirectFile:
                default:
                    var destinationFile = ResolveMainFile(desc, targetDir);
                    var destFileDir = Path.GetDirectoryName(destinationFile);
                    if (!string.IsNullOrEmpty(destFileDir))
                    {
                        Directory.CreateDirectory(destFileDir);
                    }
                    File.Copy(stagedArchiveOrFile, destinationFile, overwrite: true);
                    break;
            }

            // 5. Post-Promotion Verification of the installed main file
            var verifiedState = await InspectDescriptorAsync(desc, cancellationToken).ConfigureAwait(false);
            if (verifiedState.Status != ComponentStatus.Installed)
            {
                throw new InvalidOperationException($"Post-provisioning verification failed for '{desc.ComponentId}': {verifiedState.ErrorMessage}");
            }

            progress?.Report(new ComponentProvisionProgress(desc.ComponentId, "Completed", desc.PayloadSizeBytes, desc.PayloadSizeBytes, 100.0, "Component provisioned successfully."));
            return verifiedState;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private async Task DownloadHttpsAsync(
        ComponentDescriptor desc,
        string destinationPartialFile,
        IProgress<ComponentProvisionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(desc.SourceUrl!);
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Insecure download URL rejected: '{desc.SourceUrl}'. HTTPS is mandatory.");
        }

        if (!AllowedDownloadHosts.Contains(uri.Host))
        {
            throw new InvalidOperationException($"Unauthorized download host: '{uri.Host}'. Must be an allowlisted host.");
        }

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? desc.PayloadSizeBytes;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(destinationPartialFile, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[65536];
        long totalRead = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;
            if (totalBytes > 0 && progress is not null)
            {
                var pct = Math.Min(89.0, (double)totalRead / totalBytes * 89.0);
                progress.Report(new ComponentProvisionProgress(desc.ComponentId, "Downloading", totalRead, totalBytes, pct, $"Downloading {desc.DisplayName}... ({totalRead / 1024 / 1024} MB / {totalBytes / 1024 / 1024} MB)"));
            }
        }
    }

    private async Task DownloadSingleHttpsAsync(string sourceUrl, string destinationFile, CancellationToken cancellationToken)
    {
        var uri = new Uri(sourceUrl);
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Insecure download URL rejected: '{sourceUrl}'. HTTPS is mandatory.");
        }

        if (!AllowedDownloadHosts.Contains(uri.Host))
        {
            throw new InvalidOperationException($"Unauthorized download host: '{uri.Host}'. Must be an allowlisted host.");
        }

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(destinationFile, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveTargetDirectory(ComponentDescriptor desc)
    {
        var root = !string.IsNullOrWhiteSpace(_appPaths.RootDirectory)
            ? _appPaths.RootDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoAIFactory");
        var baseDir = Path.Combine(root, desc.InstallRoot, desc.ComponentId, desc.Version);
        return Path.GetFullPath(baseDir);
    }

    private string ResolveMainFile(ComponentDescriptor desc, string targetDir)
    {
        if (!string.IsNullOrWhiteSpace(desc.ExecutableRelativePath))
        {
            return Path.GetFullPath(Path.Combine(targetDir, desc.ExecutableRelativePath));
        }

        return Path.GetFullPath(Path.Combine(targetDir, desc.ComponentId + ".bin"));
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static bool IsZipArchive(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            if (stream.Length < 4) return false;
            var b1 = stream.ReadByte();
            var b2 = stream.ReadByte();
            return b1 == 'P' && b2 == 'K';
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSub = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, destSub);
        }
    }
}
