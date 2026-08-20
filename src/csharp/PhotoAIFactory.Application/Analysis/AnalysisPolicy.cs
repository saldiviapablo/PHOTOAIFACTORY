using System.Text.Json;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Analysis;

public static class AnalysisPolicy
{
    public static PreselectionDecision ValidateSuggestedDecision(
        JsonElement preselectionResult,
        bool allowAutomaticReject)
    {
        if (preselectionResult.ValueKind != JsonValueKind.Object ||
            !preselectionResult.TryGetProperty("decision", out var decisionElement) ||
            decisionElement.ValueKind != JsonValueKind.String)
        {
            return PreselectionDecision.ReviewPre;
        }

        var decision = decisionElement.GetString();
        return decision switch
        {
            "APPROVED" => PreselectionDecision.Approved,
            "REVIEW_PRE" => PreselectionDecision.ReviewPre,
            "REJECTED_PRE" when allowAutomaticReject => PreselectionDecision.RejectedPre,
            "REJECTED_PRE" => PreselectionDecision.ReviewPre,
            _ => PreselectionDecision.ReviewPre
        };
    }

    public static JsonElement ExtractFindings(JsonElement preselectionResult)
    {
        if (preselectionResult.ValueKind == JsonValueKind.Object &&
            preselectionResult.TryGetProperty("findings", out var findings))
        {
            return findings.Clone();
        }

        return JsonSerializer.SerializeToElement(Array.Empty<object>());
    }
}
