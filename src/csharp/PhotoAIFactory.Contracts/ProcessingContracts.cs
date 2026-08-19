using System.Text.Json.Serialization;

namespace PhotoAIFactory.Contracts;

public sealed record ComfyTask(
    [property: JsonPropertyName("task")] string Task,
    [property: JsonPropertyName("execute")] bool Execute,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("workflow_id")] string? WorkflowId,
    [property: JsonPropertyName("parameters")] IReadOnlyDictionary<string, object?>? Parameters);

public sealed record ComfyPlan(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("execute")] bool Execute,
    [property: JsonPropertyName("tasks")] IReadOnlyList<ComfyTask> Tasks);

public sealed record QaFinding(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("score")] double? Score);

public sealed record QaResult(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("findings")] IReadOnlyList<QaFinding> Findings,
    [property: JsonPropertyName("suggested_correction")] IReadOnlyDictionary<string, object?>? SuggestedCorrection);
