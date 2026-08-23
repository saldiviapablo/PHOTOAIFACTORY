using System.Security.Cryptography;
using System.Text.Json;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain.Processing;

namespace PhotoAIFactory.Infrastructure.Processing;

public sealed class ComfyWorkflowExecutor(
    IComfyUiRuntime runtime,
    IComfyWorkflowCatalog catalog) : IComfyWorkflowExecutor
{
    public Task<ComfyExecutionArtifact> ExecuteApprovedAsync(
        ComfyJobSnapshot job,
        IReadOnlyList<ComfyTaskDescriptor> tasks,
        string attemptId,
        CancellationToken cancellationToken = default)
    {
        if (tasks.Count == 0)
            throw new ArgumentException(
                "At least one approved ComfyUI task is required.",
                nameof(tasks));

        throw new ComfyStageException(
            "COMFY_WORKFLOW_MATERIALIZER_NOT_APPROVED",
            "capability",
            "A task was marked approved without an audited Phase 6 workflow materializer.",
            false);
    }

    public async Task<ComfyExecutionArtifact> ValidateCoreRoundTripAsync(
        CancellationToken cancellationToken = default)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var clientId = $"paf-phase6-{Guid.NewGuid():N}";
        string promptId;
        try
        {
            promptId = await runtime.Client.SubmitPromptAsync(
                catalog.ValidationWorkflowJson,
                clientId,
                cancellationToken).ConfigureAwait(false);
            await runtime.Client.WaitForCompletionAsync(
                promptId,
                clientId,
                TimeSpan.FromSeconds(60),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await runtime.Client.InterruptAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
        catch (Exception ex)
        {
            throw new ComfyStageException(
                "COMFY_VALIDATION_EXECUTION_FAILED",
                "component",
                ex.Message,
                true,
                ex);
        }

        var historyRaw = await runtime.Client.GetHistoryAsync(
            promptId, cancellationToken).ConfigureAwait(false);
        using var history = JsonDocument.Parse(historyRaw);
        var output = ResolveFirstOutput(
            history.RootElement,
            promptId,
            runtime.OutputDirectory);

        if (!File.Exists(output))
            throw new ComfyStageException(
                "COMFY_OUTPUT_MISSING",
                "integrity",
                $"ComfyUI history referenced missing output {output}.",
                false);

        var info = new FileInfo(output);
        if (info.Length <= 0)
            throw new ComfyStageException(
                "COMFY_OUTPUT_EMPTY",
                "integrity",
                "ComfyUI produced an empty output.",
                false);

        var hash = await Sha256Async(output, cancellationToken).ConfigureAwait(false);
        var workflowSha = Convert.ToHexString(
            SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    catalog.ValidationWorkflowJson)))
            .ToLowerInvariant();
        var workflowManifest = JsonSerializer.SerializeToElement(
            new
            {
                workflow_id = catalog.ValidationWorkflowId,
                workflow_sha256 = workflowSha,
                validation_only = true,
                model_weights = false,
                core_nodes = new[] { "EmptyImage", "SaveImage" }
            },
            ContractJson.Options);
        var promptIds = JsonSerializer.SerializeToElement(
            new[] { promptId },
            ContractJson.Options);
        return new(
            output,
            hash,
            info.Length,
            workflowManifest,
            promptIds);
    }

    private static string ResolveFirstOutput(
        JsonElement history,
        string promptId,
        string outputRoot)
    {
        if (!history.TryGetProperty(promptId, out var prompt) ||
            !prompt.TryGetProperty("outputs", out var outputs) ||
            outputs.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "ComfyUI history does not contain prompt outputs.");

        foreach (var node in outputs.EnumerateObject())
        {
            if (!node.Value.TryGetProperty("images", out var images) ||
                images.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var image in images.EnumerateArray())
            {
                if (!image.TryGetProperty("filename", out var filenameElement))
                    continue;
                var filename = filenameElement.GetString();
                var subfolder = image.TryGetProperty("subfolder", out var subfolderElement)
                    ? subfolderElement.GetString()
                    : string.Empty;
                var type = image.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString()
                    : "output";
                if (!string.Equals(type, "output", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(filename))
                    continue;

                var root = Path.GetFullPath(outputRoot);
                var candidate = Path.GetFullPath(
                    Path.Combine(
                        root,
                        subfolder ?? string.Empty,
                        Path.GetFileName(filename)));
                var prefix = root.EndsWith(Path.DirectorySeparatorChar)
                    ? root
                    : root + Path.DirectorySeparatorChar;
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "ComfyUI output escaped the owned output directory.");
                return candidate;
            }
        }

        throw new InvalidDataException(
            "ComfyUI history contains no validated output image.");
    }

    private static async Task<string> Sha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
