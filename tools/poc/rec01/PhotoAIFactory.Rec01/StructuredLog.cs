using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhotoAIFactory.Rec01;

internal sealed class StructuredLog(string path)
{
    private readonly string _path = path;
    private readonly string _mutexName = "Local\\PAF_REC01_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..20];

    public void Write(
        string level,
        string component,
        string eventName,
        string projectId = "",
        string photoId = "",
        string jobId = "",
        string attemptId = "",
        string stage = "",
        IReadOnlyDictionary<string, object?>? extra = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["timestamp"] = Rec01Model.UtcNow(),
            ["level"] = level,
            ["component"] = component,
            ["project_id"] = projectId,
            ["photo_id"] = photoId,
            ["job_id"] = jobId,
            ["attempt_id"] = attemptId,
            ["stage"] = stage,
            ["event"] = eventName
        };
        if (extra is not null)
        {
            foreach (var pair in extra) payload[pair.Key] = pair.Value;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var mutex = new Mutex(false, _mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired) throw new TimeoutException("Timed out acquiring the structured-log mutex.");
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(JsonSerializer.Serialize(payload));
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }
}
