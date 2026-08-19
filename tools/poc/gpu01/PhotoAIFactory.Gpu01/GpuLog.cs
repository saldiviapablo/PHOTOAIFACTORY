using System.Text.Json;

namespace PhotoAIFactory.Gpu01;

internal sealed class GpuLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public GpuLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void Write(string owner, string? leaseId, string @event, long waitMs = 0, long durationMs = 0,
        NvmlMemory? memory = null, int? processId = null, string? errorCode = null, object? details = null)
    {
        var row = new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"), owner, lease_id = leaseId, @event,
            wait_ms = waitMs, duration_ms = durationMs,
            vram_used_mb = memory?.UsedMb, vram_free_mb = memory?.FreeMb,
            process_id = processId, error_code = errorCode, details
        };
        lock (_gate) _writer.WriteLine(JsonSerializer.Serialize(row, _json));
    }

    public void Dispose() => _writer.Dispose();
}
