using PhotoAIFactory.Contracts;

namespace PhotoAIFactory.Application;

public interface IPythonAiClient
{
    Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<AiResponse> ExecuteAsync(string route, AiRequest request, CancellationToken cancellationToken = default);
}

public interface IDarktableCli
{
    Task<string> GetVersionAsync(CancellationToken cancellationToken = default);
    Task<ProcessExecutionResult> ExportAsync(
        DarktableExportRequest request,
        CancellationToken cancellationToken = default);
}

public interface IComfyUiClient
{
    Task<string> GetSystemStatsAsync(CancellationToken cancellationToken = default);
    Task<string> SubmitPromptAsync(string workflowJson, string clientId, CancellationToken cancellationToken = default);
    Task WaitForCompletionAsync(string promptId, string clientId, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<string> GetHistoryAsync(string promptId, CancellationToken cancellationToken = default);
    Task CancelPendingAsync(string promptId, CancellationToken cancellationToken = default);
    Task InterruptAsync(CancellationToken cancellationToken = default);
}

public sealed record DarktableExportRequest(
    string InputPath,
    string OutputPath,
    string? XmpPath = null,
    string? Style = null,
    int? MaxWidth = null,
    int? MaxHeight = null,
    bool HighQuality = true,
    bool? ApplyCustomPresets = null,
    int? JpegQuality = null,
    string? ConfigDirectory = null,
    string? CacheDirectory = null,
    string? LibraryPath = null,
    string? IccType = null,
    int? TiffBitsPerSample = null,
    bool? TiffWriteRgb = null);

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan Duration)
{
    public bool Success => ExitCode == 0;
}

public interface IGpuResourceCoordinator
{
    Task<IAsyncDisposable> AcquireAsync(
        string owner,
        CancellationToken cancellationToken = default);
    string? CurrentOwner { get; }
}
