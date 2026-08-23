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

                        var levelStr = root.TryGetProperty("LogLevel", out var lProp) ? lProp.GetString() : "Information";
                        if (!Enum.TryParse<LogLevel>(levelStr, out var level))
                        {
                            level = LogLevel.Information;
                        }

                        if (minLevel.HasValue && level < minLevel.Value)
                            continue;

                        var timestampStr = root.TryGetProperty("TimestampUtc", out var tProp) ? tProp.GetString() : null;
                        var timestamp = DateTimeOffset.TryParse(timestampStr, out var ts) ? ts : DateTimeOffset.UtcNow;
                        var category = root.TryGetProperty("Category", out var cProp) ? cProp.GetString() ?? "General" : "General";
                        var rawMessage = root.TryGetProperty("Message", out var mProp) ? mProp.GetString() ?? string.Empty : string.Empty;

                        string? pId = null;
                        string? jId = null;
                        if (root.TryGetProperty("State", out var stateProp) && stateProp.ValueKind == JsonValueKind.Object)
                        {
                            if (stateProp.TryGetProperty("project_id", out var pjProp)) pId = pjProp.GetString();
                            if (stateProp.TryGetProperty("job_id", out var jbProp)) jId = jbProp.GetString();
                        }

                        if (projectId is not null && !string.Equals(pId, projectId.Value, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (jobId is not null && !string.Equals(jId, jobId.Value, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var rawTechnical = root.TryGetProperty("Exception", out var exProp) ? exProp.GetString() : null;
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
