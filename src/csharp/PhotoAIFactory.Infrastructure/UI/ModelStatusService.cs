using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.UI;

namespace PhotoAIFactory.Infrastructure.UI;

public sealed class ModelStatusService(
    IComponentHealthTracker healthTracker,
    IAppPaths paths) : IModelStatusService
{
    public Task<IReadOnlyList<ComponentHealthCardDto>> GetComponentStatusesAsync(CancellationToken cancellationToken = default)
    {
        var statuses = healthTracker.GetAllStatuses();
        var cards = statuses.Select(s => new ComponentHealthCardDto(
            s.ComponentName,
            GetDisplayName(s.ComponentName),
            s.State,
            s.Reason ?? (s.State == ComponentHealthState.Healthy ? "Operational" : s.State.ToString()),
            s.CircuitBreakerOpen,
            s.TotalRestarts,
            s.LastCheckedUtc)).ToList();

        return Task.FromResult<IReadOnlyList<ComponentHealthCardDto>>(cards);
    }

    public Task<IReadOnlyList<ModelDescriptorDto>> GetModelDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        var modelsDir = paths.ModelsDirectory;
        var list = new List<ModelDescriptorDto>
        {
            new(
                "florence-2-large",
                "Florence-2 Large (Vision-Language)",
                "revision: 4271c66",
                "BASELINE",
                "Image captioning, dense region detection, and visual analysis",
                Directory.Exists(Path.Combine(modelsDir, "florence-2-large")),
                "7715423d6549bf1e71188bdd84f4ac960cc0597886af24a5ef7b66f128660685",
                "MIT / Florence-2 Community License"),
            new(
                "darktable-neural-restore",
                "Darktable Neural Restore",
                "v1.0",
                "NOT_HEADLESS_PROVEN",
                "Raw neural restoration (Requires interactive Darktable GUI; disabled in headless V1)",
                false,
                null,
                "GPLv3 / Darktable"),
            new(
                "comfy-core-roundtrip",
                "ComfyUI Core Workflow Engine",
                "v0.33.1 (commit 72865f4)",
                "APPROVED",
                "Model-free execution pipeline and custom node graph runtime",
                true,
                null,
                "GPLv3 / ComfyUI"),
            new(
                "comfy-enhancement-pack",
                "ComfyUI Enhancement Workflows",
                "v1.0-draft",
                "BENCHMARK_AND_LICENSE_REQUIRED",
                "Upscaling, face retouching, and low-light enhancement",
                false,
                null,
                "License review required prior to production promotion")
        };

        return Task.FromResult<IReadOnlyList<ModelDescriptorDto>>(list);
    }

    private static string GetDisplayName(string component) => component switch
    {
        "PythonWorker" => "Python AI Worker",
        "Darktable" => "Darktable CLI",
        "ComfyUI" => "ComfyUI Runtime",
        "Storage" => "Storage Preflight",
        "GpuCoordinator" => "GPU Resource Coordinator",
        _ => component
    };
}
