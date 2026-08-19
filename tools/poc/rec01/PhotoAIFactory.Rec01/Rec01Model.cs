using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoAIFactory.Rec01;

internal static class Rec01Model
{
    public static readonly string[] Checkpoints =
    [
        "INGEST_COMPLETE",
        "ORIGINAL_ARCHIVED",
        "ANALYSIS_COMPLETE",
        "PRESELECTION_COMPLETE",
        "DARKTABLE_PASS1_COMPLETE",
        "FEEDBACK_INSPECTION_COMPLETE",
        "RAW_DENOISE_COMPLETE",
        "DARKTABLE_PASS2_COMPLETE",
        "COMFYUI_COMPLETE",
        "QA_COMPLETE",
        "OUTPUT_PUBLISHED"
    ];

    public static string UtcNow() => DateTimeOffset.UtcNow.ToString("O");

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static bool ValidateArtifact(string path, long size, string sha256, bool jpeg = false)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length != size || !string.Equals(Sha256(path), sha256, StringComparison.OrdinalIgnoreCase)) return false;
        if (!jpeg) return true;
        using var stream = File.OpenRead(path);
        return stream.Length > 4 && stream.ReadByte() == 0xFF && stream.ReadByte() == 0xD8;
    }

    public static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    public static void AtomicJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Environment.ProcessId;
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}

internal sealed record WorkerOptions(
    string Database,
    string Scenario,
    string Work,
    string Log,
    string Fixture,
    string Crash,
    string Target,
    string Barrier,
    string HelperBarrier,
    int Jobs);

internal sealed record ScenarioResult(
    string Scenario,
    string Status,
    string Expected,
    string Observed,
    IReadOnlyList<string> Evidence);

internal sealed record ProcessOutcome(int ExitCode, int? KilledPid, string StandardOutput, string StandardError);

internal static class Cli
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[index][2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : "true";
            parsed[key] = value;
        }
        return parsed;
    }

    public static string Required(this IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing --{key}");

    public static string Optional(this IReadOnlyDictionary<string, string> values, string key, string fallback = "") =>
        values.TryGetValue(key, out var value) ? value : fallback;
}
