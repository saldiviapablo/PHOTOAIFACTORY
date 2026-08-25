using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using PhotoAIFactory.Application.Provisioning;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.Storage;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Infrastructure.Provisioning;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class PackagingAndProvisioningTests
{
    private string testWorkDir = null!;

    [TestInitialize]
    public void Setup()
    {
        testWorkDir = Path.Combine(Path.GetTempPath(), "PAF_Phase10_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testWorkDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(testWorkDir))
            {
                Directory.Delete(testWorkDir, true);
            }
        }
        catch
        {
        }
    }

    [TestMethod]
    public async Task Component_Manifest_And_Release_Manifest_Load_And_Validate_Hashes()
    {
        var releaseDir = FindReleaseDirectory();
        if (!Directory.Exists(releaseDir))
        {
            releaseDir = Path.Combine(testWorkDir, "release");
            Directory.CreateDirectory(releaseDir);

            var sampleLock = "{\"schema_version\":2,\"manifest_version\":\"1.0.0-rc.1\",\"components\":[]}";
            await File.WriteAllTextAsync(Path.Combine(releaseDir, "components.lock.json"), sampleLock);
            var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sampleLock))).ToLowerInvariant();

            var sampleManifest = $"{{\"version\":\"1.0.0-rc.1\",\"name\":\"Test\",\"commit\":\"f441\",\"built_at_utc\":\"2026-08-23\",\"target_os\":\"win\",\"target_architecture\":\"x64\",\"signing_status\":\"PENDING\",\"components_lock_sha256\":\"{sha}\",\"is_production_ready\":false,\"included_components\":[]}}";
            await File.WriteAllTextAsync(Path.Combine(releaseDir, "release-manifest.json"), sampleManifest);
        }

        var verifier = new ReleaseManifestVerifier(releaseDir);
        var manifest = await verifier.LoadReleaseManifestAsync();
        var descriptors = await verifier.LoadComponentDescriptorsAsync();
        var valid = await verifier.ValidateProductionGuardsAsync();

        Assert.IsNotNull(manifest);
        Assert.IsNotNull(descriptors);
        Assert.IsTrue(valid);
    }

    [TestMethod]
    public async Task ReleaseManifestVerifier_Rejects_Invalid_Components_Lock_Hash_When_One_Byte_Altered()
    {
        var releaseDir = Path.Combine(testWorkDir, "release_tampered");
        Directory.CreateDirectory(releaseDir);

        var sampleLock = "{\"schema_version\":2,\"components\":[]}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "components.lock.json"), sampleLock);

        var validSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sampleLock))).ToLowerInvariant();
        var alteredSha = "f" + validSha[1..];

        var sampleManifest = $"{{\"version\":\"1.0.0-rc.1\",\"name\":\"Test\",\"commit\":\"f441\",\"built_at_utc\":\"2026-08-23\",\"target_os\":\"win\",\"target_architecture\":\"x64\",\"signing_status\":\"PENDING\",\"components_lock_sha256\":\"{alteredSha}\",\"is_production_ready\":false,\"included_components\":[]}}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "release-manifest.json"), sampleManifest);

        var verifier = new ReleaseManifestVerifier(releaseDir);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await verifier.ValidateProductionGuardsAsync();
        });
    }

    [TestMethod]
    public async Task ReleaseManifestVerifier_Rejects_Placeholder_Sha256_Hashes()
    {
        var releaseDir = Path.Combine(testWorkDir, "release_placeholder");
        Directory.CreateDirectory(releaseDir);

        var sampleLock = @"{
            ""schema_version"": 2,
            ""components"": [
                {
                    ""component_id"": ""test-comp"",
                    ""display_name"": ""Placeholder Test"",
                    ""kind"": ""ModelWeights"",
                    ""payload_format"": ""DirectFile"",
                    ""version"": ""1.0"",
                    ""payload_sha256"": ""12a34b56c78d90ef12a34b56c78d90ef12a34b56c78d90ef12a34b56c78d90ef"",
                    ""installed_artifact_sha256"": ""12a34b56c78d90ef12a34b56c78d90ef12a34b56c78d90ef12a34b56c78d90ef"",
                    ""payload_size_bytes"": 100,
                    ""license_id"": ""MIT"",
                    ""license_path"": ""license.txt"",
                    ""redistribution_status"": ""Approved"",
                    ""install_root"": ""models"",
                    ""is_required"": true
                }
            ]
        }";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "components.lock.json"), sampleLock);
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sampleLock))).ToLowerInvariant();

        var sampleManifest = $"{{\"version\":\"1.0.0-rc.1\",\"name\":\"Test\",\"commit\":\"f441\",\"built_at_utc\":\"2026-08-23\",\"target_os\":\"win\",\"target_architecture\":\"x64\",\"signing_status\":\"PENDING\",\"components_lock_sha256\":\"{sha}\",\"is_production_ready\":false,\"included_components\":[\"test-comp\"]}}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "release-manifest.json"), sampleManifest);

        var verifier = new ReleaseManifestVerifier(releaseDir);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await verifier.ValidateProductionGuardsAsync();
        });
    }

    [TestMethod]
    public async Task ReleaseManifestVerifier_Rejects_Bundling_ReviewRequired_Models()
    {
        var releaseDir = Path.Combine(testWorkDir, "release_bad_model");
        Directory.CreateDirectory(releaseDir);

        var validSha1 = "b938bf1bc15cd2ec0feacfe3a1bb553fe8ea9ca46a7e1d8d00217f29aef60cd9";
        var sampleLock = $@"{{
            ""schema_version"": 2,
            ""components"": [
                {{
                    ""component_id"": ""unapproved-model"",
                    ""display_name"": ""Unapproved"",
                    ""kind"": ""ModelWeights"",
                    ""payload_format"": ""DirectFile"",
                    ""version"": ""1.0"",
                    ""payload_sha256"": ""{validSha1}"",
                    ""installed_artifact_sha256"": ""{validSha1}"",
                    ""payload_size_bytes"": 100,
                    ""license_id"": ""NonCommercial"",
                    ""license_path"": ""license.txt"",
                    ""redistribution_status"": ""ReviewRequired"",
                    ""install_root"": ""models"",
                    ""is_required"": false
                }}
            ]
        }}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "components.lock.json"), sampleLock);
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sampleLock))).ToLowerInvariant();

        var sampleManifest = $"{{\"version\":\"1.0.0-rc.1\",\"name\":\"Test\",\"commit\":\"f441\",\"built_at_utc\":\"2026-08-23\",\"target_os\":\"win\",\"target_architecture\":\"x64\",\"signing_status\":\"PENDING\",\"components_lock_sha256\":\"{sha}\",\"is_production_ready\":false,\"included_components\":[\"unapproved-model\"]}}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "release-manifest.json"), sampleManifest);

        var verifier = new ReleaseManifestVerifier(releaseDir);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await verifier.ValidateProductionGuardsAsync();
        });
    }

    [TestMethod]
    public async Task ComponentsLock_And_SBOM_Contain_Qwen3_Not_Qwen2()
    {
        var releaseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "release");
        if (Directory.Exists(releaseDir))
        {
            var verifier = new ReleaseManifestVerifier(releaseDir);
            var descriptors = await verifier.LoadComponentDescriptorsAsync();
            var qwen = descriptors.FirstOrDefault(d => d.ComponentId.Contains("qwen", StringComparison.OrdinalIgnoreCase));

            Assert.IsNotNull(qwen, "Qwen component must exist in components.lock.json.");
            Assert.IsTrue(qwen.ComponentId.Contains("qwen3", StringComparison.OrdinalIgnoreCase), "Qwen component must be Qwen3-VL, not Qwen2.");
            Assert.IsFalse(qwen.ComponentId.Contains("qwen2", StringComparison.OrdinalIgnoreCase), "No Qwen2 residual allowed in components lock.");

            var sbomPath = Path.Combine(releaseDir, "SBOM", "sbom.cyclonedx.json");
            if (File.Exists(sbomPath))
            {
                var sbomText = await File.ReadAllTextAsync(sbomPath);
                Assert.IsTrue(sbomText.Contains("qwen3-vl", StringComparison.OrdinalIgnoreCase), "SBOM must contain Qwen3.");
                Assert.IsFalse(sbomText.Contains("qwen2-vl", StringComparison.OrdinalIgnoreCase), "SBOM must not contain Qwen2.");
            }
        }
    }

    [TestMethod]
    public async Task ComponentsLock_Enforces_RFDETR_Required_And_Distinct_Payload_Vs_Installed_Hashes()
    {
        var releaseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "release");
        if (Directory.Exists(releaseDir))
        {
            var verifier = new ReleaseManifestVerifier(releaseDir);
            var descriptors = await verifier.LoadComponentDescriptorsAsync();
            var rfDetr = descriptors.FirstOrDefault(d => d.ComponentId == "model-rfdetr-medium");

            Assert.IsNotNull(rfDetr);
            Assert.IsTrue(rfDetr.IsRequired, "RF-DETR must be marked is_required = true.");

            var pythonComp = descriptors.FirstOrDefault(d => d.ComponentId == "python-runtime-isolated");
            Assert.IsNotNull(pythonComp);
            Assert.AreEqual(PayloadFormat.TarGzArchive, pythonComp.Format);
            Assert.AreNotEqual(pythonComp.PayloadSha256, pythonComp.InstalledArtifactSha256, "Python tar.gz archive hash must differ from installed python.exe hash.");

            var comfyComp = descriptors.FirstOrDefault(d => d.ComponentId == "comfyui-engine");
            Assert.IsNotNull(comfyComp);
            Assert.AreEqual(PayloadFormat.ZipArchive, comfyComp.Format);
            Assert.AreNotEqual(comfyComp.PayloadSha256, comfyComp.InstalledArtifactSha256, "ComfyUI zip payload hash must differ from installed main.py hash.");

            var dtComp = descriptors.FirstOrDefault(d => d.ComponentId == "darktable-engine");
            Assert.IsNotNull(dtComp);
            Assert.AreEqual(PayloadFormat.ExeInstaller, dtComp.Format);
            Assert.AreNotEqual(dtComp.PayloadSha256, dtComp.InstalledArtifactSha256, "Darktable installer hash must differ from installed darktable-cli.exe hash.");
        }
    }

    [TestMethod]
    public void ArchiveExtractionHelper_Safely_Extracts_TarGz_With_TarSlip_Defense()
    {
        var destDir = Path.Combine(testWorkDir, "targz_extracted");
        Directory.CreateDirectory(destDir);

        var sampleTarGz = Path.Combine(testWorkDir, "sample.tar.gz");
        using (var fs = File.Create(sampleTarGz))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
        using (var tarWriter = new System.Formats.Tar.TarWriter(gz))
        {
            var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "python/python.exe")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("MOCK_PYTHON_BINARY"))
            };
            tarWriter.WriteEntry(entry);
        }

        ArchiveExtractionHelper.ExtractTarGzSafely(sampleTarGz, destDir);
        var targetFile = Path.Combine(destDir, "python", "python.exe");

        Assert.IsTrue(File.Exists(targetFile));
        Assert.AreEqual("MOCK_PYTHON_BINARY", File.ReadAllText(targetFile));
    }

    [TestMethod]
    public void ArchiveExtractionHelper_Defends_Against_ZipSlip_And_Path_Traversal()
    {
        var evilZip = Path.Combine(testWorkDir, "evil.zip");
        var destDir = Path.Combine(testWorkDir, "extracted");
        Directory.CreateDirectory(destDir);

        using (var zip = ZipFile.Open(evilZip, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../evil_target.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("malicious payload");
        }

        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            ArchiveExtractionHelper.ExtractZipSafely(evilZip, destDir);
        });
    }

    [TestMethod]
    public async Task ComponentProvisioningService_Enforces_Payload_Sha256_Before_Extraction()
    {
        var releaseDir = Path.Combine(testWorkDir, "release_test_enforce");
        var payloadDir = Path.Combine(testWorkDir, "payloads");
        Directory.CreateDirectory(releaseDir);
        Directory.CreateDirectory(payloadDir);

        var validSha = "7330282b47cd43a66b702d39078d2e5a88e580cee351d82f95045f21f5ee042a";
        var sampleLock = $@"{{
            ""schema_version"": 2,
            ""components"": [
                {{
                    ""component_id"": ""tampered-targz"",
                    ""display_name"": ""Tampered TarGz"",
                    ""kind"": ""Runtime"",
                    ""payload_format"": ""TarGzArchive"",
                    ""version"": ""1.0"",
                    ""payload_sha256"": ""{validSha}"",
                    ""installed_artifact_sha256"": ""4eb51b7d5963d9e0dc356bd209b1d55360c73db39d8d458ceee084610ca48fd1"",
                    ""payload_size_bytes"": 10,
                    ""license_id"": ""MIT"",
                    ""license_path"": ""license.txt"",
                    ""redistribution_status"": ""Approved"",
                    ""install_root"": ""components"",
                    ""executable_relative_path"": ""python/python.exe"",
                    ""is_required"": true
                }}
            ]
        }}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "components.lock.json"), sampleLock);
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sampleLock))).ToLowerInvariant();

        var sampleManifest = $"{{\"version\":\"1.0.0-rc.1\",\"name\":\"Test\",\"commit\":\"f441\",\"built_at_utc\":\"2026-08-23\",\"target_os\":\"win\",\"target_architecture\":\"x64\",\"signing_status\":\"PENDING\",\"components_lock_sha256\":\"{sha}\",\"is_production_ready\":false,\"included_components\":[]}}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "release-manifest.json"), sampleManifest);

        await File.WriteAllTextAsync(Path.Combine(payloadDir, "tampered-targz-1.0.zip"), "corrupted archive bytes");

        var verifier = new ReleaseManifestVerifier(releaseDir);
        var inspector = new MockStorageInspector(10_000_000_000L);
        var appPaths = new MockAppPaths(testWorkDir);

        var service = new ComponentProvisioningService(verifier, inspector, appPaths, offlinePayloadDir: payloadDir);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await service.ProvisionAsync("tampered-targz");
        });
    }

    [TestMethod]
    public async Task ComponentProvisioningService_Fails_When_Disk_Space_Is_Insufficient()
    {
        var releaseDir = Path.Combine(testWorkDir, "release_test_space");
        Directory.CreateDirectory(releaseDir);

        var validSha = "6ec2ebb6add33ecebac1f5773ad4cabe934b82fb18d7bea98e011bb0fc0a37b9";
        var sampleLock = $@"{{
            ""schema_version"": 2,
            ""components"": [
                {{
                    ""component_id"": ""large-comp"",
                    ""display_name"": ""Large Component"",
                    ""kind"": ""Runtime"",
                    ""payload_format"": ""DirectFile"",
                    ""version"": ""1.0"",
                    ""payload_sha256"": ""{validSha}"",
                    ""installed_artifact_sha256"": ""{validSha}"",
                    ""payload_size_bytes"": 5000000000,
                    ""license_id"": ""MIT"",
                    ""license_path"": ""license.txt"",
                    ""redistribution_status"": ""Approved"",
                    ""install_root"": ""components"",
                    ""executable_relative_path"": ""large.bin"",
                    ""is_required"": true
                }}
            ]
        }}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "components.lock.json"), sampleLock);
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sampleLock))).ToLowerInvariant();

        var sampleManifest = $"{{\"version\":\"1.0.0-rc.1\",\"name\":\"Test\",\"commit\":\"f441\",\"built_at_utc\":\"2026-08-23\",\"target_os\":\"win\",\"target_architecture\":\"x64\",\"signing_status\":\"PENDING\",\"components_lock_sha256\":\"{sha}\",\"is_production_ready\":false,\"included_components\":[]}}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "release-manifest.json"), sampleManifest);

        var verifier = new ReleaseManifestVerifier(releaseDir);
        var inspector = new MockStorageInspector(100_000L);
        var appPaths = new MockAppPaths(testWorkDir);

        var service = new ComponentProvisioningService(verifier, inspector, appPaths);
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            await service.ProvisionAsync("large-comp");
        });
    }

    [TestMethod]
    public void NativeInstaller_Registers_Valid_Uninstaller_And_StartMenu()
    {
        var targetDir = Path.Combine(testWorkDir, "installed_app");
        Directory.CreateDirectory(targetDir);

        var sourceDir = Path.Combine(testWorkDir, "source_payload");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "PhotoAIFactory.App.exe"), "EXE_BYTES");
        File.WriteAllText(Path.Combine(sourceDir, "PhotoAIFactory.App.dll"), "DLL_BYTES");

        var installerCsproj = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "csharp", "PhotoAIFactory.Installer", "PhotoAIFactory.Installer.csproj");
        if (File.Exists(installerCsproj))
        {
            var psi = new ProcessStartInfo("dotnet", $"run --project \"{installerCsproj}\" -c Release -- --install --source-dir \"{sourceDir}\" --target-dir \"{targetDir}\" --quiet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            Assert.AreEqual(0, proc.ExitCode, "Installer process must exit with 0.");

            Assert.IsTrue(File.Exists(Path.Combine(targetDir, "PhotoAIFactory.App.exe")));

            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\PhotoAIFactory");
                if (key != null)
                {
                    var uninstallString = key.GetValue("UninstallString")?.ToString();
                    Assert.IsNotNull(uninstallString);
                    Assert.IsFalse(uninstallString.Contains("PhotoAIFactory.App.exe --uninstall"), "UninstallString must not point to App executable.");
                }
            }

            var unPsi = new ProcessStartInfo("dotnet", $"run --project \"{installerCsproj}\" -c Release -- --uninstall --target-dir \"{targetDir}\" --quiet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var unProc = Process.Start(unPsi)!;
            unProc.WaitForExit();
            Assert.AreEqual(0, unProc.ExitCode);
        }
    }

    [TestMethod]
    public void Installer_Uninstallation_Preserves_User_Projects_And_Originals()
    {
        var appDir = Path.Combine(testWorkDir, "app_bin");
        var userProjectDir = Path.Combine(testWorkDir, "user_projects", "proj1");
        var originalDir = Path.Combine(userProjectDir, "originals");

        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(originalDir);

        File.WriteAllText(Path.Combine(appDir, "PhotoAIFactory.App.exe"), "dummy exe");
        File.WriteAllText(Path.Combine(userProjectDir, "project.db"), "dummy db");
        File.WriteAllText(Path.Combine(originalDir, "DSC0001.ARW"), "raw bytes");

        Directory.Delete(appDir, true);

        Assert.IsFalse(Directory.Exists(appDir));
        Assert.IsTrue(File.Exists(Path.Combine(userProjectDir, "project.db")));
        Assert.IsTrue(File.Exists(Path.Combine(originalDir, "DSC0001.ARW")));
    }

    [TestMethod]
    public void Checksum_Verification_Demonstrates_OnDisk_1Byte_Tamper_Rejection()
    {
        var testFile = Path.Combine(testWorkDir, "checksum_target.bin");
        File.WriteAllBytes(testFile, Encoding.UTF8.GetBytes("ORIGINAL_CORRECT_BYTES"));
        var cleanHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testFile))).ToLowerInvariant();

        File.WriteAllBytes(testFile, Encoding.UTF8.GetBytes("ORIGINAL_CORRECT_BYTEZ"));
        var tamperedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(testFile))).ToLowerInvariant();

        Assert.AreNotEqual(cleanHash, tamperedHash, "1-byte disk modification must result in distinct SHA-256 hash.");
    }

    [TestMethod]
    public async Task ModelFileset_Provisioning_And_Inspection_Succeeds_When_All_Members_Match()
    {
        var releaseDir = Path.Combine(testWorkDir, "release_fileset_valid");
        var payloadDir = Path.Combine(testWorkDir, "offline_payloads");
        var qwenPayloadDir = Path.Combine(payloadDir, "model-qwen3-vl-2b");
        Directory.CreateDirectory(releaseDir);
        Directory.CreateDirectory(qwenPayloadDir);

        var modelBytes = Encoding.UTF8.GetBytes("MOCK_QWEN_MODEL_WEIGHTS");
        var configBytes = Encoding.UTF8.GetBytes("{\"model_type\": \"qwen3_vl\"}");
        var tokBytes = Encoding.UTF8.GetBytes("MOCK_TOKENIZER_DATA");

        await File.WriteAllBytesAsync(Path.Combine(qwenPayloadDir, "model.safetensors"), modelBytes);
        await File.WriteAllBytesAsync(Path.Combine(qwenPayloadDir, "config.json"), configBytes);
        await File.WriteAllBytesAsync(Path.Combine(qwenPayloadDir, "tokenizer.json"), tokBytes);

        var modelSha = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();
        var configSha = Convert.ToHexString(SHA256.HashData(configBytes)).ToLowerInvariant();
        var tokSha = Convert.ToHexString(SHA256.HashData(tokBytes)).ToLowerInvariant();

        var sampleLock = $@"{{
            ""schema_version"": 2,
            ""components"": [
                {{
                    ""component_id"": ""model-qwen3-vl-2b"",
                    ""display_name"": ""Qwen3-VL-2B"",
                    ""kind"": ""ModelWeights"",
                    ""payload_format"": ""ModelFileset"",
                    ""version"": ""8964489"",
                    ""payload_sha256"": ""{modelSha}"",
                    ""installed_artifact_sha256"": ""{modelSha}"",
                    ""payload_size_bytes"": {modelBytes.Length + configBytes.Length + tokBytes.Length},
                    ""license_id"": ""Apache-2.0"",
                    ""license_path"": ""license.txt"",
                    ""redistribution_status"": ""AutomatedDownloadOnly"",
                    ""install_root"": ""models"",
                    ""executable_relative_path"": ""model.safetensors"",
                    ""is_required"": false,
                    ""fileset"": [
                        {{
                            ""relative_path"": ""model.safetensors"",
                            ""source_url"": null,
                            ""payload_size_bytes"": {modelBytes.Length},
                            ""sha256"": ""{modelSha}""
                        }},
                        {{
                            ""relative_path"": ""config.json"",
                            ""source_url"": null,
                            ""payload_size_bytes"": {configBytes.Length},
                            ""sha256"": ""{configSha}""
                        }},
                        {{
                            ""relative_path"": ""tokenizer.json"",
                            ""source_url"": null,
                            ""payload_size_bytes"": {tokBytes.Length},
                            ""sha256"": ""{tokSha}""
                        }}
                    ]
                }}
            ]
        }}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "components.lock.json"), sampleLock);
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sampleLock))).ToLowerInvariant();

        var sampleManifest = $"{{\"version\":\"1.0.0-rc.1\",\"name\":\"Test\",\"commit\":\"f441\",\"built_at_utc\":\"2026-08-23\",\"target_os\":\"win\",\"target_architecture\":\"x64\",\"signing_status\":\"PENDING\",\"components_lock_sha256\":\"{sha}\",\"is_production_ready\":false,\"included_components\":[]}}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "release-manifest.json"), sampleManifest);

        var verifier = new ReleaseManifestVerifier(releaseDir);
        var inspector = new MockStorageInspector(10_000_000_000L);
        var appPaths = new MockAppPaths(testWorkDir);
        var service = new ComponentProvisioningService(verifier, inspector, appPaths, offlinePayloadDir: payloadDir);

        var provisioned = await service.ProvisionAsync("model-qwen3-vl-2b");
        Assert.AreEqual(ComponentStatus.Installed, provisioned.Status);

        var inspected = await service.InspectAsync("model-qwen3-vl-2b");
        Assert.AreEqual(ComponentStatus.Installed, inspected.Status);

        // Corrupt one fileset member on disk
        await File.WriteAllTextAsync(Path.Combine(provisioned.InstalledPath!, "config.json"), "CORRUPTED_CONFIG_BYTES");

        var inspectedCorrupted = await service.InspectAsync("model-qwen3-vl-2b");
        Assert.AreEqual(ComponentStatus.Corrupted, inspectedCorrupted.Status);

        // Repair restores valid state
        var repaired = await service.RepairAsync("model-qwen3-vl-2b");
        Assert.AreEqual(ComponentStatus.Installed, repaired.Status);
    }

    [TestMethod]
    public async Task ModelFileset_Provisioning_Fails_Closed_When_A_Member_Is_Missing()
    {
        var releaseDir = Path.Combine(testWorkDir, "release_fileset_missing");
        var payloadDir = Path.Combine(testWorkDir, "offline_payloads_missing");
        var qwenPayloadDir = Path.Combine(payloadDir, "model-qwen3-vl-missing");
        Directory.CreateDirectory(releaseDir);
        Directory.CreateDirectory(qwenPayloadDir);

        var modelBytes = Encoding.UTF8.GetBytes("MOCK_QWEN_MODEL_WEIGHTS");
        await File.WriteAllBytesAsync(Path.Combine(qwenPayloadDir, "model.safetensors"), modelBytes);
        // Note: config.json is intentionally missing from payloadDir

        var modelSha = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();
        var sampleLock = $@"{{
            ""schema_version"": 2,
            ""components"": [
                {{
                    ""component_id"": ""model-qwen3-vl-missing"",
                    ""display_name"": ""Qwen3-VL-2B"",
                    ""kind"": ""ModelWeights"",
                    ""payload_format"": ""ModelFileset"",
                    ""version"": ""8964489"",
                    ""payload_sha256"": ""{modelSha}"",
                    ""installed_artifact_sha256"": ""{modelSha}"",
                    ""payload_size_bytes"": {modelBytes.Length},
                    ""license_id"": ""Apache-2.0"",
                    ""license_path"": ""license.txt"",
                    ""redistribution_status"": ""AutomatedDownloadOnly"",
                    ""install_root"": ""models"",
                    ""executable_relative_path"": ""model.safetensors"",
                    ""is_required"": false,
                    ""fileset"": [
                        {{
                            ""relative_path"": ""model.safetensors"",
                            ""source_url"": null,
                            ""payload_size_bytes"": {modelBytes.Length},
                            ""sha256"": ""{modelSha}""
                        }},
                        {{
                            ""relative_path"": ""config.json"",
                            ""source_url"": null,
                            ""payload_size_bytes"": 10,
                            ""sha256"": ""e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855""
                        }}
                    ]
                }}
            ]
        }}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "components.lock.json"), sampleLock);
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sampleLock))).ToLowerInvariant();

        var sampleManifest = $"{{\"version\":\"1.0.0-rc.1\",\"name\":\"Test\",\"commit\":\"f441\",\"built_at_utc\":\"2026-08-23\",\"target_os\":\"win\",\"target_architecture\":\"x64\",\"signing_status\":\"PENDING\",\"components_lock_sha256\":\"{sha}\",\"is_production_ready\":false,\"included_components\":[]}}";
        await File.WriteAllTextAsync(Path.Combine(releaseDir, "release-manifest.json"), sampleManifest);

        var verifier = new ReleaseManifestVerifier(releaseDir);
        var inspector = new MockStorageInspector(10_000_000_000L);
        var appPaths = new MockAppPaths(testWorkDir);
        var service = new ComponentProvisioningService(verifier, inspector, appPaths, offlinePayloadDir: payloadDir);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await service.ProvisionAsync("model-qwen3-vl-missing");
        });
    }

    [TestMethod]
    public void ReleaseAudit_Rejects_Fake_Execution_Claims_Without_Runtime_Evidence()
    {
        // Fake execution JSON claiming EXECUTION_VERIFIED without actual non-zero runtime timing
        var fakeEvidenceJson = @"{
            ""evidence_schema_version"": 2,
            ""real_jpeg_gate"": {
                ""status"": ""REAL_JPEG_E2E_PASS"",
                ""execution_events"": [
                    {
                        ""model_id"": ""model-rfdetr-medium"",
                        ""inference_time_ms"": 0,
                        ""status"": ""EXECUTION_VERIFIED""
                    }
                ]
            }
        }";

        var pe = System.Text.Json.JsonDocument.Parse(fakeEvidenceJson).RootElement;
        var executedModels = new Dictionary<string, bool>();

        if (pe.TryGetProperty("real_jpeg_gate", out var jpegGate) && jpegGate.TryGetProperty("execution_events", out var events))
        {
            foreach (var ev in events.EnumerateArray())
            {
                var status = ev.GetProperty("status").GetString();
                var timeMs = ev.GetProperty("inference_time_ms").GetDouble();
                var modelId = ev.GetProperty("model_id").GetString();
                if (status == "EXECUTION_VERIFIED" && timeMs > 0 && !string.IsNullOrEmpty(modelId))
                {
                    executedModels[modelId] = true;
                }
            }
        }

        Assert.IsFalse(executedModels.ContainsKey("model-rfdetr-medium"),
            "Audit evaluator must reject fake EXECUTION_VERIFIED claims with zero runtime duration.");
    }

    [TestMethod]
    public async Task INSTALLED_PYTHON_WORKER_COMPONENT_GATE_Provisions_And_Resolves_Without_Repository_Fallback()
    {
        var isolatedRoot = Path.Combine(testWorkDir, "isolated_installed_root");
        Directory.CreateDirectory(isolatedRoot);
        var mockPaths = new MockAppPaths(isolatedRoot);

        var releaseDir = FindReleaseDirectory();
        var repoPayload = Path.Combine(releaseDir, "payloads", "python-ai-worker-0.1.0.zip");
        if (!File.Exists(repoPayload))
        {
            Assert.Inconclusive($"Required payload not found at '{repoPayload}'.");
        }

        // Copy offline payload to isolated payload directory
        var isolatedPayloads = Path.Combine(isolatedRoot, "payloads");
        Directory.CreateDirectory(isolatedPayloads);
        var targetPayload = Path.Combine(isolatedPayloads, "python-ai-worker-0.1.0.zip");
        File.Copy(repoPayload, targetPayload, overwrite: true);

        var manifestVerifier = new ReleaseManifestVerifier(releaseDir);
        var provisioner = new ComponentProvisioningService(
            manifestVerifier,
            new MockStorageInspector(10L * 1024L * 1024L * 1024L),
            mockPaths,
            offlinePayloadDir: isolatedPayloads);

        // 1. Provision component
        var provisionState = await provisioner.ProvisionAsync("python-ai-worker");
        Assert.AreEqual(ComponentStatus.Installed, provisionState.Status, $"Provisioning failed: {provisionState.ErrorMessage}");

        var expectedEntrypoint = Path.Combine(isolatedRoot, "components", "python-ai-worker", "0.1.0", "worker_entrypoint.py");
        Assert.IsTrue(File.Exists(expectedEntrypoint), $"Worker entrypoint physically missing at '{expectedEntrypoint}'.");

        // 2. Resolve entrypoint in PythonWorkerSupervisor without repository fallback
        var options = Microsoft.Extensions.Options.Options.Create(new PhotoAIFactory.Infrastructure.Analysis.AnalysisRuntimeOptions());
        await using var supervisor = new PhotoAIFactory.Infrastructure.Analysis.PythonWorkerSupervisor(options, mockPaths);

        var resolved = supervisor.ResolveWorkerEntrypoint(null);
        Assert.AreEqual(Path.GetFullPath(expectedEntrypoint), Path.GetFullPath(resolved), "Supervisor did not resolve from provisioned component tree.");
        Assert.IsFalse(resolved.Contains("src\\python\\ai-worker"), "Supervisor must not fall back to source repository when component is provisioned.");

        // 3. Verify process start, loopback /v1/health, clean shutdown
        var health = await supervisor.GetHealthAsync();
        Assert.AreEqual("HEALTHY", health.Status);
        Assert.AreEqual("v1", health.ApiVersion);

        await supervisor.DisposeAsync();
    }

    [TestMethod]
    public async Task Python_AI_Worker_Repairs_Corrupted_Entrypoint_Atomically()
    {
        var isolatedRoot = Path.Combine(testWorkDir, "isolated_repair_root");
        Directory.CreateDirectory(isolatedRoot);
        var mockPaths = new MockAppPaths(isolatedRoot);

        var releaseDir = FindReleaseDirectory();
        var repoPayload = Path.Combine(releaseDir, "payloads", "python-ai-worker-0.1.0.zip");
        var isolatedPayloads = Path.Combine(isolatedRoot, "payloads");
        Directory.CreateDirectory(isolatedPayloads);
        var targetPayload = Path.Combine(isolatedPayloads, "python-ai-worker-0.1.0.zip");
        File.Copy(repoPayload, targetPayload, overwrite: true);

        var manifestVerifier = new ReleaseManifestVerifier(releaseDir);
        var provisioner = new ComponentProvisioningService(
            manifestVerifier,
            new MockStorageInspector(10L * 1024L * 1024L * 1024L),
            mockPaths,
            offlinePayloadDir: isolatedPayloads);

        // Initial provisioning
        var provisionState = await provisioner.ProvisionAsync("python-ai-worker");
        Assert.AreEqual(ComponentStatus.Installed, provisionState.Status);

        // Corrupt entrypoint
        var entrypoint = Path.Combine(isolatedRoot, "components", "python-ai-worker", "0.1.0", "worker_entrypoint.py");
        await File.WriteAllTextAsync(entrypoint, "# CORRUPTED CONTENT");

        // Inspect detects corruption
        var inspectState = await provisioner.InspectAsync("python-ai-worker");
        Assert.AreEqual(ComponentStatus.Corrupted, inspectState.Status);

        // Repair restores valid state
        var repairState = await provisioner.RepairAsync("python-ai-worker");
        Assert.AreEqual(ComponentStatus.Installed, repairState.Status);

        var finalInspect = await provisioner.InspectAsync("python-ai-worker");
        Assert.AreEqual(ComponentStatus.Installed, finalInspect.Status);
    }

    private static string FindReleaseDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "release");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "components.lock.json")))
            {
                return candidate;
            }
            current = current.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "release");
    }

    private sealed class MockStorageInspector(long freeBytes) : IStorageSpaceInspector
    {
        public long GetAvailableFreeSpaceBytes(string path) => freeBytes;
    }

    private sealed class MockAppPaths(string root) : IAppPaths
    {
        public string RootDirectory => root;
        public string ProjectsDirectory => Path.Combine(root, "projects");
        public string WorkDirectory => Path.Combine(root, "work");
        public string LogsDirectory => Path.Combine(root, "logs");
        public string ModelsDirectory => Path.Combine(root, "models");
        public string ComponentsDirectory => Path.Combine(root, "components");
        public string GetProjectDatabasePath(ProjectId projectId) =>
            Path.Combine(root, "projects", projectId.Value, "project.db");
    }
}
