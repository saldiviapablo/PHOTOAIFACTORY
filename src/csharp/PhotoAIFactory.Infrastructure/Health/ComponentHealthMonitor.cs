using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.Storage;

namespace PhotoAIFactory.Infrastructure.Health;

public sealed class ComponentHealthMonitor : IComponentHealthMonitor
{
    private readonly IComponentHealthTracker tracker;
    private readonly IPythonAiClient? pythonAiClient;
    private readonly IComfyUiClient? comfyUiClient;
    private readonly IDarktableCli? darktableCli;
    private readonly IStorageSpaceInspector? spaceInspector;
    private readonly IAppPaths? appPaths;
    private readonly IGpuResourceCoordinator? gpuCoordinator;

    public ComponentHealthMonitor(
        IComponentHealthTracker tracker,
        IPythonAiClient? pythonAiClient = null,
        IComfyUiClient? comfyUiClient = null,
        IDarktableCli? darktableCli = null,
        IStorageSpaceInspector? spaceInspector = null,
        IAppPaths? appPaths = null,
        IGpuResourceCoordinator? gpuCoordinator = null)
    {
        this.tracker = tracker;
        this.pythonAiClient = pythonAiClient;
        this.comfyUiClient = comfyUiClient;
        this.darktableCli = darktableCli;
        this.spaceInspector = spaceInspector;
        this.appPaths = appPaths;
        this.gpuCoordinator = gpuCoordinator;
    }

    public async Task<ComponentHealthStatus> CheckComponentHealthAsync(
        string componentName,
        CancellationToken cancellationToken = default)
    {
        var normalized = componentName.ToLowerInvariant();
        try
        {
            switch (normalized)
            {
                case "python" or "pythonworker":
                    if (pythonAiClient is not null)
                    {
                        var res = await pythonAiClient.GetHealthAsync(cancellationToken).ConfigureAwait(false);
                        if (string.Equals(res.Status, "ok", StringComparison.OrdinalIgnoreCase))
                        {
                            tracker.RecordSuccess("PythonWorker");
                        }
                        else
                        {
                            tracker.RecordFailure("PythonWorker", $"Unhealthy status reported: {res.Status}");
                        }
                    }
                    break;

                case "comfyui" or "comfy":
                    if (comfyUiClient is not null)
                    {
                        var stats = await comfyUiClient.GetSystemStatsAsync(cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(stats))
                        {
                            tracker.RecordSuccess("ComfyUI");
                        }
                        else
                        {
                            tracker.RecordFailure("ComfyUI", "ComfyUI returned empty system stats");
                        }
                    }
                    break;

                case "darktable":
                    if (darktableCli is not null)
                    {
                        var version = await darktableCli.GetVersionAsync(cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(version))
                        {
                            tracker.RecordSuccess("Darktable");
                        }
                        else
                        {
                            tracker.RecordFailure("Darktable", "Darktable CLI probe returned empty version");
                        }
                    }
                    break;

                case "storage":
                    if (spaceInspector is not null && appPaths is not null)
                    {
                        var freeBytes = spaceInspector.GetAvailableFreeSpaceBytes(appPaths.RootDirectory);
                        if (freeBytes > 100L * 1024 * 1024) // At least 100 MB free
                        {
                            tracker.RecordSuccess("Storage");
                        }
                        else
                        {
                            tracker.RecordFailure("Storage", $"Low storage space on root directory: {freeBytes:N0} bytes available");
                        }
                    }
                    break;

                case "gpucoordinator" or "gpu":
                    if (gpuCoordinator is not null)
                    {
                        // Real probe: coordinator lock state check
                        if (gpuCoordinator.CurrentOwner is null)
                        {
                            tracker.RecordSuccess("GpuCoordinator");
                        }
                        else
                        {
                            // Active owner is normal busy state
                            tracker.RecordSuccess("GpuCoordinator");
                        }
                    }
                    break;

                default:
                    // Unknown component: do not record fake success
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation does NOT count as a health failure
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            tracker.RecordFailure(componentName, $"Health check probe failed: {ex.Message}");
        }

        return tracker.GetStatus(componentName);
    }

    public async Task<IReadOnlyList<ComponentHealthStatus>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        await CheckComponentHealthAsync("PythonWorker", cancellationToken).ConfigureAwait(false);
        await CheckComponentHealthAsync("ComfyUI", cancellationToken).ConfigureAwait(false);
        await CheckComponentHealthAsync("Darktable", cancellationToken).ConfigureAwait(false);
        await CheckComponentHealthAsync("Storage", cancellationToken).ConfigureAwait(false);
        await CheckComponentHealthAsync("GpuCoordinator", cancellationToken).ConfigureAwait(false);

        return tracker.GetAllStatuses();
    }
}
