using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Qa;

namespace PhotoAIFactory.Infrastructure.Qa;

public sealed class FinalHistoryWriter : IFinalHistoryWriter
{
    public async Task<string> WriteFinalHistoryAsync(
        ProjectId projectId,
        PhotoId photoId,
        JobId jobId,
        string attemptId,
        string destinationPath,
        string destinationSha256,
        long destinationSizeBytes,
        int width,
        int height,
        QaResultSnapshot qaResult,
        string outputRootFolder,
        DateTimeOffset publishedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var historyDir = Path.Combine(
            outputRootFolder,
            ".photo-ai-factory",
            "history",
            photoId.Value,
            jobId.Value);

        Directory.CreateDirectory(historyDir);

        var finalHistoryPath = Path.Combine(historyDir, "final_history.json");
        var nowUtc = publishedAtUtc.ToString("O", CultureInfo.InvariantCulture);

        var payload = new
        {
            schema_version = 1,
            project_id = projectId.Value,
            photo_id = photoId.Value,
            job_id = jobId.Value,
            attempt_id = attemptId,
            published_at_utc = nowUtc,
            publication = new
            {
                destination_path = destinationPath,
                sha256 = destinationSha256,
                size_bytes = destinationSizeBytes,
                width,
                height
            },
            qa_result = new
            {
                decision = qaResult.Decision,
                attempt_id = qaResult.AttemptId,
                input_sha256 = qaResult.InputSha256,
                created_at_utc = qaResult.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                details = qaResult.ResultJson
            }
        };

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            new JsonSerializerOptions { WriteIndented = true });

        if (File.Exists(finalHistoryPath))
        {
            var existingBytes = await File.ReadAllBytesAsync(finalHistoryPath, cancellationToken).ConfigureAwait(false);
            var existingSha = Convert.ToHexString(SHA256.HashData(existingBytes)).ToLowerInvariant();
            var newSha = Convert.ToHexString(SHA256.HashData(jsonBytes)).ToLowerInvariant();

            if (string.Equals(existingSha, newSha, StringComparison.Ordinal))
            {
                return finalHistoryPath;
            }

            try
            {
                using var doc = JsonDocument.Parse(existingBytes);
                var root = doc.RootElement;
                if (root.TryGetProperty("publication", out var pub) &&
                    pub.TryGetProperty("sha256", out var existingPubSha) &&
                    string.Equals(existingPubSha.GetString(), destinationSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return finalHistoryPath;
                }
            }
            catch
            {
                // Non-matching unparseable
            }

            throw new InvalidOperationException(
                $"Final history conflict at '{finalHistoryPath}'. Existing SHA-256 '{existingSha}' differs from generated SHA-256 '{newSha}'.");
        }

        var tempPath = Path.Combine(historyDir, $"final_history_{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(tempPath, jsonBytes, cancellationToken).ConfigureAwait(false);

        try
        {
            File.Move(tempPath, finalHistoryPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(finalHistoryPath))
        {
            File.Delete(tempPath);
            var existingBytes = await File.ReadAllBytesAsync(finalHistoryPath, cancellationToken).ConfigureAwait(false);
            var existingSha = Convert.ToHexString(SHA256.HashData(existingBytes)).ToLowerInvariant();
            var newSha = Convert.ToHexString(SHA256.HashData(jsonBytes)).ToLowerInvariant();

            if (string.Equals(existingSha, newSha, StringComparison.Ordinal))
            {
                return finalHistoryPath;
            }

            try
            {
                using var doc = JsonDocument.Parse(existingBytes);
                var root = doc.RootElement;
                if (root.TryGetProperty("publication", out var pub) &&
                    pub.TryGetProperty("sha256", out var existingPubSha) &&
                    string.Equals(existingPubSha.GetString(), destinationSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return finalHistoryPath;
                }
            }
            catch
            {
                // Non-matching unparseable
            }

            throw new InvalidOperationException(
                $"Final history conflict at '{finalHistoryPath}'. Concurrently written SHA-256 '{existingSha}' differs from expected '{newSha}'.");
        }

        return finalHistoryPath;
    }
}
