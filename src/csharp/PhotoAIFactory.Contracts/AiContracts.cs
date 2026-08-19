using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoAIFactory.Contracts;

public static class ContractJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

public sealed record AiRequest(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("input_paths")] IReadOnlyList<string> InputPaths,
    [property: JsonPropertyName("config")] JsonElement Config);

public sealed record AiError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] JsonElement? Details = null);

public sealed record AiResponse(
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("result")] JsonElement? Result,
    [property: JsonPropertyName("error")] AiError? Error,
    [property: JsonPropertyName("timings")] IReadOnlyDictionary<string,double>? Timings);

public sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("worker_version")] string WorkerVersion,
    [property: JsonPropertyName("device")] string? Device,
    [property: JsonPropertyName("models_loaded")] IReadOnlyList<string> ModelsLoaded);
