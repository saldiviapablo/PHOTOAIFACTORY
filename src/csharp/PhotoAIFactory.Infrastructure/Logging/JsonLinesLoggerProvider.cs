using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Infrastructure.Hosting;

namespace PhotoAIFactory.Infrastructure.Logging;

public sealed class JsonLinesLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private static readonly ConcurrentDictionary<string, JsonLinesLoggerProvider> ActiveDestinations =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> CorrelationFields =
    [
        "session_id", "project_id", "photo_id", "job_id", "attempt_id",
        "stage", "component", "request_id"
    ];

    private readonly object sync = new();
    private readonly IRuntimeSession session;
    private IExternalScopeProvider scopeProvider = new LoggerExternalScopeProvider();
    private StreamWriter? writer;
    private bool active;
    private bool disposed;

    public JsonLinesLoggerProvider(
        IAppPaths paths,
        IRuntimeSession session,
        IOptions<PhotoAIFactoryRuntimeOptions> options)
    {
        this.session = session;
        DestinationPath = Path.Combine(paths.LogsDirectory, options.Value.LogFileName);
    }

    public string DestinationPath { get; }
    public bool IsActive { get { lock (sync) return active; } }
    public bool IsDisposed { get { lock (sync) return disposed; } }

    public void Activate()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active)
            {
                return;
            }

            if (!ActiveDestinations.TryAdd(DestinationPath, this))
            {
                throw new IOException($"A JSONL logger already owns '{DestinationPath}'.");
            }

            try
            {
                var stream = new FileStream(
                    DestinationPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan);
                writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
                active = true;
            }
            catch
            {
                ActiveDestinations.TryRemove(DestinationPath, out _);
                throw;
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new JsonLinesLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        this.scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));

    public void Flush()
    {
        lock (sync)
        {
            writer?.Flush();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            active = false;
            writer?.Flush();
            writer?.Dispose();
            writer = null;
            ActiveDestinations.TryRemove(DestinationPath, out _);
        }
    }

    private void Write<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Dictionary<string, string> correlations = new(StringComparer.Ordinal)
        {
            ["session_id"] = session.SessionId
        };
        scopeProvider.ForEachScope(
            static (scope, values) => CollectScope(scope, values),
            correlations);

        string line;
        using (var stream = new MemoryStream())
        {
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteString("timestamp_utc", DateTimeOffset.UtcNow);
                json.WriteString("level", level.ToString());
                json.WriteString("category", category);
                json.WriteNumber("event_id", eventId.Id);
                if (!string.IsNullOrWhiteSpace(eventId.Name))
                {
                    json.WriteString("event_name", eventId.Name);
                }
                json.WriteString("message", formatter(state, exception));
                foreach (var field in correlations.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    json.WriteString(field.Key, field.Value);
                }
                if (exception is not null)
                {
                    json.WriteStartObject("exception");
                    json.WriteString("type", exception.GetType().FullName);
                    json.WriteString("message", exception.Message);
                    if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                    {
                        json.WriteString("stack_trace", exception.StackTrace);
                    }
                    json.WriteEndObject();
                }
                json.WriteEndObject();
            }
            line = Encoding.UTF8.GetString(stream.ToArray());
        }

        lock (sync)
        {
            if (!active || disposed)
            {
                return;
            }
            writer!.WriteLine(line);
        }
    }

    private static void CollectScope(object? scope, Dictionary<string, string> values)
    {
        if (scope is not IEnumerable<KeyValuePair<string, object?>> properties)
        {
            return;
        }

        foreach (var property in properties)
        {
            if (CorrelationFields.Contains(property.Key) && property.Value is not null)
            {
                values[property.Key] = Convert.ToString(property.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
    }

    private sealed class JsonLinesLogger(JsonLinesLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            provider.scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && provider.IsActive;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
