using System.Text.Json;

namespace PhotoAIFactory.Ing01;

internal sealed class IngestLog : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public IngestLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public void Write(string @event, string? path = null, string? photoId = null, string? assetId = null,
        string? kind = null, string? state = null, long? size = null, string? sha256 = null,
        string? association = null, long durationMs = 0, string? errorCode = null, object? details = null)
    {
        var row = new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"), @event, path, photo_id = photoId, asset_id = assetId,
            kind, state, size, sha256, association, duration_ms = durationMs, error_code = errorCode, details
        };
        lock (_gate) _writer.WriteLine(JsonSerializer.Serialize(row, _json));
    }

    public void Dispose() => _writer.Dispose();
}
