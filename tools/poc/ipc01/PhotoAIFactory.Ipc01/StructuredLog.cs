using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoAIFactory.Ipc01;

internal sealed class StructuredLog : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;

    public StructuredLog(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public void Write(
        string eventName,
        string? requestId = null,
        long? durationMs = null,
        int? processId = null,
        string? errorCode = null,
        object? details = null)
    {
        var entry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            component = "ipc01-csharp",
            job_id = "ipc01-job",
            request_id = requestId,
            @event = eventName,
            duration_ms = durationMs,
            process_id = processId,
            error_code = errorCode,
            details
        };

        var json = JsonSerializer.Serialize(entry, JsonOptions);
        lock (_sync) _writer.WriteLine(json);
    }

    public void Dispose()
    {
        lock (_sync) _writer.Dispose();
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
