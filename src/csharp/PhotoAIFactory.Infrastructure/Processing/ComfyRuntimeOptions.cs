namespace PhotoAIFactory.Infrastructure.Processing;

public sealed class ComfyRuntimeOptions
{
    public const string SectionName = "PhotoAIFactory:ComfyUI";

    private static string LocalRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoAIFactory");

    public string ComponentRoot { get; set; } =
        Path.Combine(LocalRoot, "components", "comfyui");

    public string RuntimeRoot { get; set; } =
        Path.Combine(LocalRoot, "runtimes", "comfyui");

    public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public static bool IsValid(ComfyRuntimeOptions value) =>
        Path.IsPathFullyQualified(value.ComponentRoot) &&
        Path.IsPathFullyQualified(value.RuntimeRoot) &&
        value.ReadinessTimeout >= TimeSpan.FromSeconds(5) &&
        value.ReadinessTimeout <= TimeSpan.FromMinutes(5);
}
