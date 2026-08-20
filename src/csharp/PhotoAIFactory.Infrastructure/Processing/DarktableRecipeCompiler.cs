using System.Text.Json;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.Processing;

public sealed class DarktableRecipeCompiler : IDarktableRecipeCompiler
{
    public DarktableControlPlan Compile(
        RevealMode revealMode,
        JsonElement? recipe,
        ProjectConfigV1 config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (revealMode == RevealMode.Feedback)
        {
            throw new NotSupportedException("FEEDBACK belongs to Phase 5.");
        }

        if (revealMode == RevealMode.DtAuto)
        {
            return DefaultPlan("DT_AUTO_DEFAULT_PIPELINE");
        }

        if (recipe is not JsonElement value || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("PRE_AI requires a structured recipe object.");
        }

        RequireInt(value, "schema_version", 1);
        RequireString(value, "recipe_version", "phase4-pre-ai-v1");
        RequireString(value, "strategy", "CONSERVATIVE_BASELINE");
        RequireString(value, "benchmark_status", "NOT_CALIBRATED");

        if (!value.TryGetProperty("operations", out var operations) ||
            operations.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("PRE_AI recipe operations must be an array.");
        }

        // DT-01 proved authentic XMP application and a bounded control subset,
        // but not a generic XMP compiler. Never fabricate Darktable internals.
        if (operations.GetArrayLength() != 0)
        {
            throw new InvalidDataException(
                "PRE_AI recipe contains unvalidated Darktable operations. " +
                "Phase 4 baseline permits only the conservative no-op recipe.");
        }

        if (!value.TryGetProperty("darktable_control", out var control) ||
            control.ValueKind != JsonValueKind.Object ||
            !control.TryGetProperty("mode", out var mode) ||
            mode.ValueKind != JsonValueKind.String ||
            !string.Equals(mode.GetString(), "DEFAULT_PIPELINE", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "PRE_AI baseline must explicitly request the validated Darktable default pipeline.");
        }

        return DefaultPlan("PRE_AI_CONSERVATIVE_DEFAULT_PIPELINE");
    }

    private static DarktableControlPlan DefaultPlan(string policyId) =>
        new(
            policyId,
            XmpPath: null,
            XmpSha256: null,
            Style: null,
            ApplyCustomPresets: false,
            JsonSerializer.SerializeToElement(new
            {
                policy_id = policyId,
                xmp = (string?)null,
                style = (string?)null,
                apply_custom_presets = false,
                arbitrary_xmp_compilation = false
            }));

    private static void RequireInt(JsonElement root, string name, int expected)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            value.GetInt32() != expected)
        {
            throw new InvalidDataException($"Recipe field {name} must equal {expected}.");
        }
    }

    private static void RequireString(JsonElement root, string name, string expected)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Recipe field {name} must equal {expected}.");
        }
    }
}
