using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.UI;

namespace PhotoAIFactory.Infrastructure.UI;

public sealed partial class ErrorLogQueryService(IAppPaths paths) : IErrorLogQueryService
{
    private static readonly Regex BearerHeaderRegex = new(@"Authorization:\s*Bearer\s+[A-Za-z0-9_\-\.]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BearerTokenRegex = new(@"Bearer\s+[A-Za-z0-9_\-\.]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GenericSecretRegex = new(@"(session[_-]?token|api[_-]?key|secret|password|auth_token)\s*[:=]\s*[""']?[A-Za-z0-9_\-\.\/+=]{6,}[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<IReadOnlyList<ErrorLogEntryDto>> GetErrorLogsAsync(
        Domain.ProjectId? projectId = null,
        Domain.JobId? jobId = null,
        LogLevel? minLevel = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var logsDir = paths.LogsDirectory;
        if (!Directory.Exists(logsDir))
            return [];

        var results = new List<ErrorLogEntryDto>();
        var logFiles = Directory.GetFiles(logsDir, "*.jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(5);

        foreach (var file in logFiles)
        {
            if (results.Count >= limit)
                break;

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        // Parse Level: support "level" (JsonLinesLoggerProvider standard) and "LogLevel"
                        var levelStr = root.TryGetProperty("level", out var lProp) ? lProp.GetString() :
                                       (root.TryGetProperty("LogLevel", out var lProp2) ? lProp2.GetString() : "Information");
                        if (!Enum.TryParse<LogLevel>(levelStr, out var level))
                        {
                            level = LogLevel.Information;
                        }

                        if (minLevel.HasValue && level < minLevel.Value)
                            continue;

                        // Parse Timestamp: support "timestamp_utc", "TimestampUtc", "timestamp"
                        var timestampStr = root.TryGetProperty("timestamp_utc", out var tProp) ? tProp.GetString() :
                                           (root.TryGetProperty("TimestampUtc", out var tProp2) ? tProp2.GetString() :
                                           (root.TryGetProperty("timestamp", out var tProp3) ? tProp3.GetString() : null));
                        var timestamp = DateTimeOffset.TryParse(timestampStr, out var ts) ? ts : DateTimeOffset.UtcNow;

                        // Parse Category / Component
                        var category = root.TryGetProperty("component", out var compProp) && !string.IsNullOrWhiteSpace(compProp.GetString())
                            ? compProp.GetString()!
                            : (root.TryGetProperty("category", out var cProp) ? cProp.GetString() ?? "General"
                            : (root.TryGetProperty("Category", out var cProp2) ? cProp2.GetString() ?? "General" : "General"));

                        // Parse Message
                        var rawMessage = root.TryGetProperty("message", out var mProp) ? mProp.GetString() ?? string.Empty :
                                         (root.TryGetProperty("Message", out var mProp2) ? mProp2.GetString() ?? string.Empty : string.Empty);

                        // Parse Correlation Identifiers: project_id, job_id, photo_id
                        string? pId = root.TryGetProperty("project_id", out var pjProp) ? pjProp.GetString() : null;
                        string? jId = root.TryGetProperty("job_id", out var jbProp) ? jbProp.GetString() : null;

                        // Fallback to nested State if present
                        if (pId is null && root.TryGetProperty("State", out var stateProp) && stateProp.ValueKind == JsonValueKind.Object)
                        {
                            if (stateProp.TryGetProperty("project_id", out var sPjProp)) pId = sPjProp.GetString();
                            if (stateProp.TryGetProperty("job_id", out var sJbProp)) jId = sJbProp.GetString();
                        }

                        // Project filtering: include if projectId matches OR if entry is a system-level log (pId == null)
                        if (projectId is not null && pId is not null && !string.Equals(pId, projectId.Value, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (jobId is not null && jId is not null && !string.Equals(jId, jobId.Value, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Parse Exception / Technical details
                        string? rawTechnical = null;
                        if (root.TryGetProperty("exception", out var exProp))
                        {
                            if (exProp.ValueKind == JsonValueKind.Object)
                            {
                                var exType = exProp.TryGetProperty("type", out var et) ? et.GetString() : null;
                                var exMsg = exProp.TryGetProperty("message", out var em) ? em.GetString() : null;
                                var exSt = exProp.TryGetProperty("stack_trace", out var es) ? es.GetString() : null;
                                rawTechnical = string.IsNullOrWhiteSpace(exSt)
                                    ? $"{exType}: {exMsg}"
                                    : $"{exType}: {exMsg}\n{exSt}";
                            }
                            else if (exProp.ValueKind == JsonValueKind.String)
                            {
                                rawTechnical = exProp.GetString();
                            }
                        }
                        else if (root.TryGetProperty("Exception", out var exProp2))
                        {
                            rawTechnical = exProp2.GetString();
                        }

                        var logId = Guid.NewGuid().ToString("N")[..8];

                        results.Add(new ErrorLogEntryDto(
                            logId,
                            timestamp,
                            level,
                            category,
                            Sanitize(rawMessage),
                            pId,
                            jId,
                            level == LogLevel.Warning,
                            rawTechnical is null ? null : Sanitize(rawTechnical)));

                        if (results.Count >= limit)
                            break;
                    }
                    catch
                    {
                        // Ignore individual malformed log lines
                    }
                }
            }
            catch
            {
                // Ignore file read errors
            }
        }

        return results.OrderByDescending(r => r.TimestampUtc).Take(limit).ToList();
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var sanitized = BearerHeaderRegex.Replace(text, "Authorization: Bearer [REDACTED]");
        sanitized = BearerTokenRegex.Replace(sanitized, "Bearer [REDACTED]");
        sanitized = GenericSecretRegex.Replace(sanitized, "$1: [REDACTED]");
        return sanitized;
    }
}
