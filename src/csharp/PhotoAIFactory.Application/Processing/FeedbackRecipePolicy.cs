using System.Text.Json;

namespace PhotoAIFactory.Application.Processing;

public static class FeedbackRecipePolicy
{
    public const int SchemaVersion = 1;
    public const string RecipeVersion = "phase5-feedback-v1";
    public const string Strategy = "CONSERVATIVE_REUSE_PASS1";
    public const string BenchmarkStatus = "NOT_CALIBRATED";
    public const string Pass2Mode = "REUSE_PASS1_XMP";
    public const string NeuralRestoreDisabledReason =
        "NOT_HEADLESS_PROVEN_AND_BENCHMARK_PENDING";

    public static void Validate(JsonElement recipe)
    {
        if (recipe.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Feedback recipe must be an object.");

        RequireInt(recipe, "schema_version", SchemaVersion);
        RequireString(recipe, "recipe_version", RecipeVersion);
        RequireString(recipe, "strategy", Strategy);
        RequireString(recipe, "benchmark_status", BenchmarkStatus);

        if (!recipe.TryGetProperty("operations", out var operations) ||
            operations.ValueKind != JsonValueKind.Array ||
            operations.GetArrayLength() != 0)
        {
            throw new InvalidDataException(
                "Phase 5 baseline forbids unbenchmarked creative operations.");
        }

        if (!recipe.TryGetProperty("pass2_control", out var pass2) ||
            pass2.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("pass2_control is required.");
        }

        RequireString(pass2, "mode", Pass2Mode);
        RequireFalse(pass2, "arbitrary_xmp_compilation");
        RequireTrue(pass2, "restart_from_managed_original");
        RequireFalse(pass2, "pass1_derivative_as_source");

        if (!recipe.TryGetProperty("darktable_ai", out var ai) ||
            ai.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("darktable_ai policy is required.");
        }

        foreach (var name in new[] { "raw_denoise", "rgb_denoise", "upscale" })
        {
            if (!ai.TryGetProperty(name, out var task) ||
                task.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"darktable_ai.{name} is required.");
            }

            RequireFalse(task, "enabled");
            RequireString(task, "reason", NeuralRestoreDisabledReason);
        }
    }

    private static void RequireInt(JsonElement obj, string name, int expected)
    {
        if (!obj.TryGetProperty(name, out var item) ||
            item.ValueKind != JsonValueKind.Number ||
            !item.TryGetInt32(out var actual) ||
            actual != expected)
        {
            throw new InvalidDataException($"{name} must equal {expected}.");
        }
    }

    private static void RequireString(JsonElement obj, string name, string expected)
    {
        if (!obj.TryGetProperty(name, out var item) ||
            item.ValueKind != JsonValueKind.String ||
            !string.Equals(item.GetString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{name} must equal {expected}.");
        }
    }

    private static void RequireTrue(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var item) ||
            item.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException($"{name} must be true.");
        }
    }

    private static void RequireFalse(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var item) ||
            item.ValueKind != JsonValueKind.False)
        {
            throw new InvalidDataException($"{name} must be false.");
        }
    }
}
