namespace PhotoAIFactory.Domain;

public sealed record ProjectId(string Value)
{
    public static ProjectId New() => new(Guid.NewGuid().ToString("N"));
}

public sealed record PhotoId(string Value)
{
    public static PhotoId New() => new(Guid.NewGuid().ToString("N"));
}

public sealed record JobId(string Value)
{
    public static JobId New() => new(Guid.NewGuid().ToString("N"));
}

public sealed record JobSnapshot(
    JobId Id,
    PhotoId PhotoId,
    JobState State,
    RevealMode RevealMode,
    string ProcessingConfigId,
    string? PreselectionConfigId,
    int TechnicalRetryCount,
    int QualityReprocessCount);
