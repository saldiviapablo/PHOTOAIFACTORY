using System.Security.Cryptography;
using System.Text.Json;
using PhotoAIFactory.Application.Provisioning;

namespace PhotoAIFactory.Infrastructure.Provisioning;

public sealed class ReleaseManifestVerifier : IReleaseManifestService
{
    private readonly string _releaseDir;

    public ReleaseManifestVerifier(string? releaseDir = null)
    {
        _releaseDir = releaseDir ?? Path.Combine(AppContext.BaseDirectory, "release");
    }

    public async Task<ReleaseManifest> LoadReleaseManifestAsync(CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(_releaseDir, "release-manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Release manifest not found.", manifestPath);
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var version = root.GetProperty("version").GetString()!;
        var name = root.GetProperty("name").GetString()!;
        var commit = root.GetProperty("commit").GetString()!;
        var builtAt = root.GetProperty("built_at_utc").GetString()!;
        var targetOs = root.GetProperty("target_os").GetString()!;
        var targetArch = root.GetProperty("target_architecture").GetString()!;
        var signing = root.GetProperty("signing_status").GetString()!;
        var lockSha = root.GetProperty("components_lock_sha256").GetString()!;
        var isProdReady = root.GetProperty("is_production_ready").GetBoolean();

        var included = new List<string>();
        if (root.TryGetProperty("included_components", out var compArray))
        {
            foreach (var item in compArray.EnumerateArray())
            {
                if (item.GetString() is { } s)
                {
                    included.Add(s);
                }
            }
        }

        return new ReleaseManifest(version, name, commit, builtAt, targetOs, targetArch, signing, lockSha, included, isProdReady, null);
    }

    public async Task<IReadOnlyList<ComponentDescriptor>> LoadComponentDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        var lockPath = Path.Combine(_releaseDir, "components.lock.json");
        if (!File.Exists(lockPath))
        {
            throw new FileNotFoundException("Components lock file not found.", lockPath);
        }

        var json = await File.ReadAllTextAsync(lockPath, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var list = new List<ComponentDescriptor>();
        if (!root.TryGetProperty("components", out var comps))
        {
            return list;
        }

        foreach (var c in comps.EnumerateArray())
        {
            var id = c.GetProperty("component_id").GetString()!;
            var name = c.GetProperty("display_name").GetString()!;
            var kindStr = c.GetProperty("kind").GetString()!;
            var kind = Enum.Parse<ComponentKind>(kindStr, ignoreCase: true);
            var formatStr = c.TryGetProperty("payload_format", out var pf) ? pf.GetString() : null;
            var format = formatStr != null ? Enum.Parse<PayloadFormat>(formatStr, ignoreCase: true) : PayloadFormat.DirectFile;
            var ver = c.GetProperty("version").GetString()!;
            var url = c.TryGetProperty("source_url", out var u) ? u.GetString() : null;
            var commit = c.TryGetProperty("source_commit", out var sc) ? sc.GetString() : null;
            var payloadSha = c.GetProperty("payload_sha256").GetString()!;
            var installedSha = c.GetProperty("installed_artifact_sha256").GetString()!;
            var size = c.TryGetProperty("payload_size_bytes", out var sz) ? sz.GetInt64() : 0L;
            var lic = c.GetProperty("license_id").GetString()!;
            var licPath = c.GetProperty("license_path").GetString()!;
            var redistStr = c.GetProperty("redistribution_status").GetString()!;
            var redist = Enum.Parse<RedistributionStatus>(redistStr, ignoreCase: true);
            var rootPath = c.GetProperty("install_root").GetString()!;
            var exe = c.TryGetProperty("executable_relative_path", out var ep) ? ep.GetString() : null;
            var probe = c.TryGetProperty("health_probe_endpoint", out var hp) ? hp.GetString() : null;
            var required = c.GetProperty("is_required").GetBoolean();
            var notes = c.TryGetProperty("notes", out var n) ? n.GetString() : null;

            List<ModelFileEntry>? fileset = null;
            if (c.TryGetProperty("fileset", out var fsElem) && fsElem.ValueKind == JsonValueKind.Array)
            {
                fileset = new List<ModelFileEntry>();
                foreach (var f in fsElem.EnumerateArray())
                {
                    var rPath = f.GetProperty("relative_path").GetString()!;
                    var fUrl = f.TryGetProperty("source_url", out var fu) ? fu.GetString() : null;
                    var fSize = f.GetProperty("payload_size_bytes").GetInt64();
                    var fSha = f.GetProperty("sha256").GetString()!;
                    fileset.Add(new ModelFileEntry(rPath, fUrl, fSize, fSha));
                }
            }

            list.Add(new ComponentDescriptor(
                id, name, kind, format, ver, url, commit, payloadSha, installedSha, size, lic, licPath, redist, rootPath, exe, probe, required, notes, fileset));
        }

        return list;
    }

    public async Task<bool> ValidateProductionGuardsAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = await LoadComponentDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await LoadReleaseManifestAsync(cancellationToken).ConfigureAwait(false);

        // Guard 1: Verify components.lock.json SHA-256 matches release manifest
        var lockPath = Path.Combine(_releaseDir, "components.lock.json");
        var lockBytes = await File.ReadAllBytesAsync(lockPath, cancellationToken).ConfigureAwait(false);
        var calculatedLockSha = Convert.ToHexString(SHA256.HashData(lockBytes)).ToLowerInvariant();

        if (!string.Equals(calculatedLockSha, manifest.ComponentsLockSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Components lock SHA-256 does not match release manifest.");
        }

        // Guard 2: Reject placeholder hashes (e.g. repeated patterns, obvious placeholders)
        foreach (var d in descriptors)
        {
            if (IsPlaceholderSha256(d.PayloadSha256) || IsPlaceholderSha256(d.InstalledArtifactSha256))
            {
                throw new InvalidOperationException($"Component '{d.ComponentId}' contains placeholder SHA-256 hash.");
            }
        }

        // Guard 3: Reject any bundled component marked REVIEW_REQUIRED or RESTRICTED
        foreach (var d in descriptors)
        {
            if (d.Redistribution == RedistributionStatus.ReviewRequired || d.Redistribution == RedistributionStatus.Restricted)
            {
                if (manifest.IncludedComponentIds.Contains(d.ComponentId, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Component '{d.ComponentId}' is marked {d.Redistribution} and cannot be bundled in release.");
                }
            }
        }

        // Guard 4: Assert no forbidden dev test flags in release configuration
        if (Environment.GetEnvironmentVariable("PAF_ALLOW_TEST_FORCE_DECISION") == "1" && manifest.IsProductionReady)
        {
            throw new InvalidOperationException("Forbidden development flag PAF_ALLOW_TEST_FORCE_DECISION is set in production configuration.");
        }

        return true;
    }

    public static bool IsPlaceholderSha256(string sha)
    {
        if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64) return true;
        if (sha.Equals("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", StringComparison.OrdinalIgnoreCase)) return false; // empty file sha
        if (sha.StartsWith("12a34b56c78d90ef", StringComparison.OrdinalIgnoreCase)) return true;
        if (sha.StartsWith("f8a91b2c3d4e5f60", StringComparison.OrdinalIgnoreCase)) return true;
        if (sha.StartsWith("a1b2c3d4e5f60718", StringComparison.OrdinalIgnoreCase)) return true;
        if (sha.StartsWith("b2c3d4e5f6a7b8c9", StringComparison.OrdinalIgnoreCase)) return true;
        if (sha.StartsWith("3c4d5e6f7a8b9c0d", StringComparison.OrdinalIgnoreCase)) return true;
        if (sha.StartsWith("0000000000", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
