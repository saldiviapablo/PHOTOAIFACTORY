namespace PhotoAIFactory.Domain.Projects;

public sealed class ProjectConfigV1
{
    public const int CurrentSchemaVersion = 1;

    public ProjectConfigV1(
        string inputFolder,
        string outputFolder,
        bool includeSubfolders,
        RevealMode revealMode,
        bool preselectionEnabled,
        string preselectionProfile,
        SemanticMode semanticMode,
        ComfyUiMode comfyUiMode,
        IEnumerable<string> authorizedComfyUiTasks,
        IEnumerable<string> presetProfiles,
        string exportFormat,
        int exportQuality,
        int associationWindowSeconds)
    {
        InputFolder = ProjectPathPolicy.Normalize(inputFolder);
        OutputFolder = ProjectPathPolicy.Normalize(outputFolder);
        ProjectPathPolicy.EnsureSafeRelationship(InputFolder, OutputFolder, includeSubfolders);

        if (preselectionEnabled && string.IsNullOrWhiteSpace(preselectionProfile))
        {
            throw new ArgumentException("An enabled preselection needs a profile.", nameof(preselectionProfile));
        }

        if (string.IsNullOrWhiteSpace(exportFormat))
        {
            throw new ArgumentException("Export format is required.", nameof(exportFormat));
        }

        if (exportQuality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(exportQuality), "Export quality must be between 1 and 100.");
        }

        if (associationWindowSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(associationWindowSeconds));
        }

        IncludeSubfolders = includeSubfolders;
        RevealMode = revealMode;
        PreselectionEnabled = preselectionEnabled;
        PreselectionProfile = NormalizeToken(preselectionProfile);
        SemanticMode = semanticMode;
        ComfyUiMode = comfyUiMode;
        AuthorizedComfyUiTasks = NormalizeSet(authorizedComfyUiTasks);
        PresetProfiles = NormalizeSet(presetProfiles);
        ExportFormat = NormalizeToken(exportFormat);
        ExportQuality = exportQuality;
        AssociationWindowSeconds = associationWindowSeconds;
    }

    public int SchemaVersion => CurrentSchemaVersion;
    public string InputFolder { get; }
    public string OutputFolder { get; }
    public bool IncludeSubfolders { get; }
    public RevealMode RevealMode { get; }
    public bool PreselectionEnabled { get; }
    public string PreselectionProfile { get; }
    public SemanticMode SemanticMode { get; }
    public ComfyUiMode ComfyUiMode { get; }
    public IReadOnlyList<string> AuthorizedComfyUiTasks { get; }
    public IReadOnlyList<string> PresetProfiles { get; }
    public string ExportFormat { get; }
    public int ExportQuality { get; }
    public int AssociationWindowSeconds { get; }

    private static IReadOnlyList<string> NormalizeSet(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeToken)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeToken(string value) => value.Trim().ToUpperInvariant();
}

public static class ProjectPathPolicy
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static void EnsureSafeRelationship(string inputFolder, string outputFolder, bool includeSubfolders)
    {
        if (!includeSubfolders)
        {
            return;
        }

        var input = Normalize(inputFolder);
        var output = Normalize(outputFolder);
        var relative = Path.GetRelativePath(input, output);
        var outputIsInsideInput = relative == "." ||
            (!Path.IsPathRooted(relative) &&
             relative != ".." &&
             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
             !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));

        if (outputIsInsideInput)
        {
            throw new ArgumentException(
                "Output folder cannot equal or be contained by input when subfolders are included.",
                nameof(outputFolder));
        }
    }
}
