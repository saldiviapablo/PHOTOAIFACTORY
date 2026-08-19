using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoAIFactory.Domain.Projects;

public sealed class ConfigVersion
{
    private ConfigVersion(
        string id,
        ProjectId projectId,
        int versionNumber,
        int schemaVersion,
        string canonicalJson,
        string sha256,
        string operationKey,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(projectId.Value))
        {
            throw new ArgumentException("Config and project IDs are required.");
        }

        if (versionNumber <= 0 || schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(versionNumber));
        }

        if (string.IsNullOrWhiteSpace(canonicalJson) || string.IsNullOrWhiteSpace(operationKey))
        {
            throw new ArgumentException("Canonical payload and operation key are required.");
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(createdAtUtc));
        }

        var calculated = ProjectConfigCanonicalizer.ComputeSha256(canonicalJson);
        if (!string.Equals(calculated, sha256, StringComparison.Ordinal))
        {
            throw new ConfigIntegrityException("Stored ConfigVersion SHA-256 does not match its canonical payload.");
        }

        Id = id;
        ProjectId = projectId;
        VersionNumber = versionNumber;
        SchemaVersion = schemaVersion;
        CanonicalJson = canonicalJson;
        Sha256 = sha256;
        OperationKey = operationKey;
        CreatedAtUtc = createdAtUtc;
    }

    public string Id { get; }
    public ProjectId ProjectId { get; }
    public int VersionNumber { get; }
    public int SchemaVersion { get; }
    public string CanonicalJson { get; }
    public string Sha256 { get; }
    public string OperationKey { get; }
    public DateTimeOffset CreatedAtUtc { get; }

    public static ConfigVersion Create(
        ProjectId projectId,
        int versionNumber,
        ProjectConfigV1 config,
        string operationKey,
        DateTimeOffset createdAtUtc)
    {
        var canonicalJson = ProjectConfigCanonicalizer.Serialize(config);
        return new ConfigVersion(
            Guid.NewGuid().ToString("N"),
            projectId,
            versionNumber,
            config.SchemaVersion,
            canonicalJson,
            ProjectConfigCanonicalizer.ComputeSha256(canonicalJson),
            operationKey,
            createdAtUtc);
    }

    public static ConfigVersion Restore(
        string id,
        ProjectId projectId,
        int versionNumber,
        int schemaVersion,
        string canonicalJson,
        string sha256,
        string operationKey,
        DateTimeOffset createdAtUtc) =>
        new(id, projectId, versionNumber, schemaVersion, canonicalJson, sha256, operationKey, createdAtUtc);

    public ProjectConfigV1 ReadConfig() => ProjectConfigCanonicalizer.Deserialize(CanonicalJson, Sha256);
}

public sealed class ConfigIntegrityException(string message) : IOException(message);

public static class ProjectConfigCanonicalizer
{
    public static string Serialize(ProjectConfigV1 config)
    {
        ArgumentNullException.ThrowIfNull(config);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", config.SchemaVersion);
            writer.WriteString("input_folder", config.InputFolder);
            writer.WriteString("output_folder", config.OutputFolder);
            writer.WriteBoolean("include_subfolders", config.IncludeSubfolders);
            writer.WriteString("reveal_mode", RevealModeToken(config.RevealMode));
            writer.WriteStartObject("preselection");
            writer.WriteBoolean("enabled", config.PreselectionEnabled);
            writer.WriteString("profile", config.PreselectionProfile);
            writer.WriteEndObject();
            writer.WriteString("semantic_mode", SemanticModeToken(config.SemanticMode));
            writer.WriteStartObject("comfyui");
            writer.WriteString("mode", ComfyUiModeToken(config.ComfyUiMode));
            writer.WriteStartArray("allowed_tasks");
            foreach (var task in config.AuthorizedComfyUiTasks)
            {
                writer.WriteStringValue(task);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartArray("preset_profiles");
            foreach (var preset in config.PresetProfiles)
            {
                writer.WriteStringValue(preset);
            }
            writer.WriteEndArray();
            writer.WriteStartObject("export");
            writer.WriteString("format", config.ExportFormat);
            writer.WriteNumber("quality", config.ExportQuality);
            writer.WriteEndObject();
            writer.WriteNumber("association_window_seconds", config.AssociationWindowSeconds);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string ComputeSha256(string canonicalJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();

    public static ProjectConfigV1 Deserialize(string canonicalJson, string expectedSha256)
    {
        if (!string.Equals(ComputeSha256(canonicalJson), expectedSha256, StringComparison.Ordinal))
        {
            throw new ConfigIntegrityException("ConfigVersion SHA-256 validation failed.");
        }

        using var document = JsonDocument.Parse(canonicalJson);
        var root = document.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != ProjectConfigV1.CurrentSchemaVersion)
        {
            throw new ConfigIntegrityException("Unsupported project config schema version.");
        }

        var preselection = root.GetProperty("preselection");
        var comfyUi = root.GetProperty("comfyui");
        var export = root.GetProperty("export");
        return new ProjectConfigV1(
            root.GetProperty("input_folder").GetString()!,
            root.GetProperty("output_folder").GetString()!,
            root.GetProperty("include_subfolders").GetBoolean(),
            ParseRevealMode(root.GetProperty("reveal_mode").GetString()!),
            preselection.GetProperty("enabled").GetBoolean(),
            preselection.GetProperty("profile").GetString()!,
            ParseSemanticMode(root.GetProperty("semantic_mode").GetString()!),
            ParseComfyUiMode(comfyUi.GetProperty("mode").GetString()!),
            comfyUi.GetProperty("allowed_tasks").EnumerateArray().Select(item => item.GetString()!),
            root.GetProperty("preset_profiles").EnumerateArray().Select(item => item.GetString()!),
            export.GetProperty("format").GetString()!,
            export.GetProperty("quality").GetInt32(),
            root.GetProperty("association_window_seconds").GetInt32());
    }

    private static string RevealModeToken(RevealMode value) => value switch
    {
        RevealMode.PreAi => "PRE_AI",
        RevealMode.DtAuto => "DT_AUTO",
        RevealMode.Feedback => "FEEDBACK",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string SemanticModeToken(SemanticMode value) => value.ToString().ToUpperInvariant();
    private static string ComfyUiModeToken(ComfyUiMode value) => value.ToString().ToUpperInvariant();

    private static RevealMode ParseRevealMode(string value) => value switch
    {
        "PRE_AI" => RevealMode.PreAi,
        "DT_AUTO" => RevealMode.DtAuto,
        "FEEDBACK" => RevealMode.Feedback,
        _ => throw new ConfigIntegrityException("Invalid reveal mode in canonical config.")
    };

    private static SemanticMode ParseSemanticMode(string value) => Enum.Parse<SemanticMode>(value, true);
    private static ComfyUiMode ParseComfyUiMode(string value) => Enum.Parse<ComfyUiMode>(value, true);
}
