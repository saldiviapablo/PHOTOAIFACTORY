using System.Text.Json;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Domain.Processing;

public sealed record BasicRevealJobSnapshot(
    JobId Id,
    ProjectId ProjectId,
    PhotoId PhotoId,
    JobState State,
    string ProcessingConfigId,
    string InputAssetId,
    string InputPath,
    string InputSha256,
    string InputFormat,
    int RevealRetryCount,
    long QueueSequence,
    bool ProcessNext);

public sealed record ProcessingRecipeSnapshot(
    string RecipeId,
    JobId JobId,
    int SchemaVersion,
    RevealMode RevealMode,
    string RecipeSha256,
    JsonElement Recipe,
    DateTimeOffset CreatedAtUtc);

public sealed record BasicRevealPassSnapshot(
    string ProcessingPassId,
    JobId JobId,
    string AttemptId,
    RevealMode RevealMode,
    string InputAssetId,
    string InputSha256,
    string? RecipeId,
    string DarktableVersion,
    JsonElement ControlPlan,
    string OutputId,
    string OutputPath,
    string OutputSha256,
    long OutputSizeBytes,
    int OutputWidth,
    int OutputHeight,
    string HistoryPath,
    string? XmpHistoryPath,
    DateTimeOffset CompletedAtUtc);
