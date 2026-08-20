namespace PhotoAIFactory.Infrastructure.Analysis;

public sealed class AnalysisRuntimeOptions
{
    public const string SectionName = "PhotoAIFactory:Analysis";

    public string PythonExecutablePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoAIFactory", "runtimes", "ai-worker", "Scripts", "python.exe");

    // Optional absolute overrides. Product/dev discovery is used when omitted.
    public string? WorkerEntrypointPath { get; set; }
    public string? DarktableCliPath { get; set; }

    public int StartupTimeoutSeconds { get; set; } = 45;
    public int RequestTimeoutSeconds { get; set; } = 180;

    public static bool IsValid(AnalysisRuntimeOptions options) =>
        !string.IsNullOrWhiteSpace(options.PythonExecutablePath) &&
        Path.IsPathFullyQualified(options.PythonExecutablePath) &&
        (string.IsNullOrWhiteSpace(options.WorkerEntrypointPath) ||
         Path.IsPathFullyQualified(options.WorkerEntrypointPath)) &&
        (string.IsNullOrWhiteSpace(options.DarktableCliPath) ||
         Path.IsPathFullyQualified(options.DarktableCliPath)) &&
        options.StartupTimeoutSeconds is >= 5 and <= 180 &&
        options.RequestTimeoutSeconds is >= 10 and <= 600;
}
