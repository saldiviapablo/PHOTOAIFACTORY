using System.Text.Json;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Processing;

public static class ComfyPlanPolicy
{
    public const int SchemaVersion = 1;
    public const string PlanVersion = "phase6-comfy-v1";

    private static readonly HashSet<string> SupportedTasks =
        new(StringComparer.Ordinal)
        {
            "DENOISE_RGB",
            "COLOR",
            "FACE_RETOUCH",
            "FACE_MASKS",
            "LOW_LIGHT",
            "UPSCALE",
            "SHARPNESS"
        };

    public static ComfyValidatedPlan Validate(
        JsonElement plan,
        ComfyUiMode configuredMode,
        IReadOnlyList<string> authorizedTasks)
    {
        if (plan.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("ComfyPlan must be a JSON object.");

        var schema = RequireInt(plan, "schema_version");
        if (schema != SchemaVersion)
            throw new InvalidDataException($"Unsupported ComfyPlan schema {schema}.");

        var version = RequireString(plan, "plan_version");
        if (!string.Equals(version, PlanVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported ComfyPlan version {version}.");

        var modeText = RequireString(plan, "mode").ToUpperInvariant();
        var expectedMode = configuredMode.ToString().ToUpperInvariant();
        if (!string.Equals(modeText, expectedMode, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"ComfyPlan mode {modeText} does not match ConfigVersion mode {expectedMode}.");

        var benchmark = RequireString(plan, "benchmark_status");
        if (!plan.TryGetProperty("decisions", out var decisionsElement) ||
            decisionsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("ComfyPlan decisions must be an array.");
        if (!plan.TryGetProperty("execution_order", out var orderElement) ||
            orderElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("ComfyPlan execution_order must be an array.");

        var authorized = authorizedTasks
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        if (authorized.Any(item => !SupportedTasks.Contains(item)))
            throw new InvalidDataException("ConfigVersion contains an unsupported ComfyUI task.");

        var decisions = new List<ComfyTaskDecision>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in decisionsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Each ComfyPlan decision must be an object.");

            var taskId = RequireString(item, "task_id").ToUpperInvariant();
            var action = RequireString(item, "action").ToUpperInvariant();
            var reason = RequireString(item, "reason");

            if (!SupportedTasks.Contains(taskId) || !authorized.Contains(taskId))
                throw new InvalidDataException($"ComfyPlan task {taskId} is not authorized.");
            if (!seen.Add(taskId))
                throw new InvalidDataException($"ComfyPlan duplicates task {taskId}.");
            if (action is not ("EXECUTE" or "SKIP"))
                throw new InvalidDataException($"Invalid ComfyPlan action {action}.");

            if (configuredMode == ComfyUiMode.Off && action != "SKIP")
                throw new InvalidDataException("OFF mode cannot execute ComfyUI tasks.");

            decisions.Add(new(taskId, action, reason));
        }

        if (!seen.SetEquals(authorized))
            throw new InvalidDataException(
                "ComfyPlan must record a decision for every authorized task.");

        var order = orderElement.EnumerateArray()
            .Select(item => item.GetString()?.Trim().ToUpperInvariant()
                ?? throw new InvalidDataException("execution_order contains a non-string value."))
            .ToArray();

        if (order.Distinct(StringComparer.Ordinal).Count() != order.Length)
            throw new InvalidDataException("ComfyPlan execution_order contains duplicates.");

        var expectedOrder = decisions
            .Where(item => item.Action == "EXECUTE")
            .Select(item => item.TaskId)
            .ToArray();
        if (!order.SequenceEqual(expectedOrder, StringComparer.Ordinal))
            throw new InvalidDataException(
                "ComfyPlan execution_order must match EXECUTE decisions exactly.");

        return new(
            schema,
            version,
            configuredMode,
            benchmark,
            decisions,
            order,
            plan.Clone());
    }

    public static IReadOnlyList<ComfyTaskDescriptor> RequireApproved(
        ComfyValidatedPlan plan,
        IComfyWorkflowCatalog catalog)
    {
        var result = new List<ComfyTaskDescriptor>();
        foreach (var taskId in plan.ExecutionOrder)
        {
            var descriptor = catalog.Require(taskId);
            if (!descriptor.ProductionApproved)
            {
                throw new ComfyStageException(
                    "COMFY_TASK_NOT_APPROVED",
                    "capability",
                    $"ComfyUI task {taskId} is not production-approved: " +
                    $"{descriptor.ApprovalStatus}. {descriptor.Reason}",
                    false);
            }
            if (string.IsNullOrWhiteSpace(descriptor.WorkflowId))
            {
                throw new ComfyStageException(
                    "COMFY_WORKFLOW_MISSING",
                    "capability",
                    $"Approved task {taskId} has no versioned workflow.",
                    false);
            }
            result.Add(descriptor);
        }
        return result;
    }

    private static string RequireString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"ComfyPlan.{name} is required.");
        return value.GetString()!;
    }

    private static int RequireInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) ||
            !value.TryGetInt32(out var result))
            throw new InvalidDataException($"ComfyPlan.{name} must be an integer.");
        return result;
    }
}
