namespace PhotoAIFactory.Infrastructure.Ingestion;

public sealed class IngestionRuntimeOptions
{
    public const string SectionName = "PhotoAIFactory:Ingestion";

    public int StableForMilliseconds { get; init; } = 750;
    public int StabilityTimeoutSeconds { get; init; } = 120;
    public int ReconciliationIntervalSeconds { get; init; } = 30;
    public int ChannelCapacity { get; init; } = 1024;
    public int WatcherInternalBufferKilobytes { get; init; } = 32;
    public bool EnableWatcher { get; init; } = true;

    public TimeSpan StableFor => TimeSpan.FromMilliseconds(StableForMilliseconds);
    public TimeSpan StabilityTimeout => TimeSpan.FromSeconds(StabilityTimeoutSeconds);
    public TimeSpan ReconciliationInterval => TimeSpan.FromSeconds(ReconciliationIntervalSeconds);

    public static bool IsValid(IngestionRuntimeOptions value) =>
        value.StableForMilliseconds is >= 100 and <= 10_000 &&
        value.StabilityTimeoutSeconds is >= 5 and <= 600 &&
        value.ReconciliationIntervalSeconds is >= 1 and <= 3600 &&
        value.ChannelCapacity is >= 16 and <= 65_536 &&
        value.WatcherInternalBufferKilobytes is >= 4 and <= 64;
}
