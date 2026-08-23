using System.Text.Json;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Qa;

public sealed record QaFindingSnapshot(
    string Code,
    string Severity,
    string Message,
    double? Score);

public sealed record QaEvaluationResult(
    QaDecision Decision,
    string RawDecision,
    JsonElement ResultJson,
    IReadOnlyList<QaFindingSnapshot> Findings,
    string CalibrationStatus);

public static class QaPolicy
{
    public static QaEvaluationResult EvaluateResponse(string expectedRequestId, AiResponse response)
    {
        if (!string.Equals(response.RequestId, expectedRequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Correlation ID mismatch in QA response: expected '{expectedRequestId}', received '{response.RequestId}'.");
        }

        if (!response.Success)
        {
            var isRetryable = response.Error?.Retryable ?? false;
            var category = response.Error?.Category ?? "unknown";

            if (isRetryable || string.Equals(category, "runtime", StringComparison.OrdinalIgnoreCase))
            {
                return new QaEvaluationResult(
                    QaDecision.TechRetry,
                    "QA_TECH_RETRY",
                    JsonSerializer.SerializeToElement(new { error = response.Error?.Message ?? "Unspecified error", category }),
                    [new QaFindingSnapshot(response.Error?.Code ?? "AI_WORKER_ERROR", "error", response.Error?.Message ?? "QA execution failed", null)],
                    "UNAVAILABLE");
            }

            return new QaEvaluationResult(
                QaDecision.Fatal,
                "QA_FATAL",
                JsonSerializer.SerializeToElement(new { error = response.Error?.Message ?? "Fatal error", category }),
                [new QaFindingSnapshot(response.Error?.Code ?? "AI_FATAL_ERROR", "fatal", response.Error?.Message ?? "QA fatal error", null)],
                "UNAVAILABLE");
        }

        if (!response.Result.HasValue)
        {
            throw new InvalidOperationException("QA response indicated success but contained no result body.");
        }

        var result = response.Result.Value;
        if (!result.TryGetProperty("schema_version", out var versionProp) || versionProp.GetInt32() != 1)
        {
            throw new InvalidOperationException("Unsupported QA result schema version (expected 1).");
        }

        if (!result.TryGetProperty("decision", out var decProp) || string.IsNullOrWhiteSpace(decProp.GetString()))
        {
            throw new InvalidOperationException("QA result missing required 'decision' property.");
        }

        var rawDecision = decProp.GetString()!;
        var domainDecision = rawDecision.ToUpperInvariant() switch
        {
            "QA_PASS" or "PASS" => QaDecision.Pass,
            "QA_REVIEW" or "REVIEW" => QaDecision.Review,
            "QA_REPROCESS" or "REPROCESS" => QaDecision.Reprocess,
            "QA_TECH_RETRY" or "TECH_RETRY" => QaDecision.TechRetry,
            "QA_FATAL" or "FATAL" => QaDecision.Fatal,
            _ => throw new InvalidOperationException($"Unknown QA decision '{rawDecision}'.")
        };

        var findings = new List<QaFindingSnapshot>();
        if (result.TryGetProperty("findings", out var findingsProp) && findingsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in findingsProp.EnumerateArray())
            {
                var code = item.TryGetProperty("code", out var c) ? c.GetString() ?? "UNKNOWN" : "UNKNOWN";
                var severity = item.TryGetProperty("severity", out var s) ? s.GetString() ?? "info" : "info";
                var message = item.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                double? score = item.TryGetProperty("score", out var sc) && sc.TryGetDouble(out var scoreVal) ? scoreVal : null;
                findings.Add(new QaFindingSnapshot(code, severity, message, score));
            }
        }

        var calibration = result.TryGetProperty("calibration_status", out var calProp)
            ? calProp.GetString() ?? "BASELINE_NOT_CALIBRATED"
            : "BASELINE_NOT_CALIBRATED";

        return new QaEvaluationResult(
            domainDecision,
            rawDecision,
            result.Clone(),
            findings,
            calibration);
    }
}
