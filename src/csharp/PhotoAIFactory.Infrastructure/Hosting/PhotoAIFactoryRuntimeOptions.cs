namespace PhotoAIFactory.Infrastructure.Hosting;

public sealed class PhotoAIFactoryRuntimeOptions
{
    public const string SectionName = "PhotoAIFactory:Runtime";

    public string? RootPath { get; set; }
    public string LogFileName { get; set; } = "photo-ai-factory.jsonl";

    internal static bool IsValid(PhotoAIFactoryRuntimeOptions options)
    {
        if (options.RootPath is not null &&
            (string.IsNullOrWhiteSpace(options.RootPath) || !Path.IsPathFullyQualified(options.RootPath)))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(options.LogFileName) &&
               string.Equals(Path.GetFileName(options.LogFileName), options.LogFileName, StringComparison.Ordinal) &&
               options.LogFileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
    }
}
