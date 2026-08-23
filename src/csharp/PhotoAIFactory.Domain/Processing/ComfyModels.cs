using System.Text.Json;

namespace PhotoAIFactory.Domain.Processing;

public sealed record ComfyJobSnapshot(
    JobId Id,
    ProjectId ProjectId,
    PhotoId PhotoId,
    JobState State,
    string ProcessingConfigId,
    string RevealStage,
    string RevealPath,
    string RevealSha256,
    long RevealSizeBytes,
    int ComfyRetryCount);

public sealed record ComfyPlanSnapshot(
    string PlanId,
    JobId JobId,
    int SchemaVersion,
    string Mode,
    string PlanSha256,
    JsonElement Plan,
    DateTimeOffset CreatedAtUtc);

public sealed record ComfyExecutionSnapshot(
    string ExecutionId,
    JobId JobId,
    string AttemptId,
    string Status,
    string InputPath,
    string InputSha256,
    string OutputPath,
    string OutputSha256,
    long OutputSizeBytes,
    JsonElement TaskManifest,
    JsonElement WorkflowManifest,
    JsonElement PromptIds,
    string HistoryPath,
    DateTimeOffset CompletedAtUtc);
