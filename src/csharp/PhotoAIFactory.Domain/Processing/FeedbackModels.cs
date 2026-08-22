using System.Text.Json;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Domain.Processing;

public sealed record FeedbackJobSnapshot(
    JobId Id,
    ProjectId ProjectId,
    PhotoId PhotoId,
    JobState State,
    string ProcessingConfigId,
    string InputAssetId,
    string InputPath,
    string InputSha256,
    string InputFormat,
    string RawSupportStatus,
    int RevealRetryCount,
    long QueueSequence,
    bool ProcessNext);

public sealed record FeedbackPassSnapshot(
    string FeedbackPassId,
    JobId JobId,
    int PassNumber,
    string AttemptId,
    string InputAssetId,
    string InputSha256,
    string InputKind,
    string DarktableVersion,
    JsonElement ControlPlan,
    string ImagePath,
    string ImageSha256,
    long ImageSizeBytes,
    int ImageWidth,
    int ImageHeight,
    int BitsPerSample,
    int Channels,
    string XmpPath,
    string XmpSha256,
    string? HistoryPath,
    DateTimeOffset CompletedAtUtc);

public sealed record FeedbackInspectionSnapshot(
    string FeedbackInspectionId,
    JobId JobId,
    int SchemaVersion,
    string RecipeSha256,
    JsonElement Recipe,
    JsonElement Inspection,
    DateTimeOffset CompletedAtUtc);
