using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.Processing;

public sealed class ComfyHistoryWriter : IComfyHistoryWriter
{
    public string GetHistoryPath(
        ProjectConfigV1 config,
        PhotoId photoId,
        JobId jobId) =>
        Path.Combine(
            Path.GetFullPath(config.OutputFolder),
            ".photo-ai-factory",
            "history",
            photoId.Value,
            jobId.Value,
            "comfyui.json");

    public async Task WriteAsync(
        ProjectConfigV1 config,
        ComfyJobSnapshot job,
        string configSha256,
        ComfyPlanSnapshot plan,
        string attemptId,
        string status,
        ComfyExecutionArtifact artifact,
        JsonElement taskManifest,
        string historyPath,
        CancellationToken cancellationToken = default)
    {
        var expected = GetHistoryPath(config, job.PhotoId, job.Id);
        if (!string.Equals(
                Path.GetFullPath(historyPath),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "ComfyUI history path is outside the deterministic Job history location.");

        var payload = JsonSerializer.Serialize(
            new
            {
                schema_version = 1,
                stage = "COMFYUI",
                job_id = job.Id.Value,
                photo_id = job.PhotoId.Value,
                config_sha256 = configSha256,
                attempt_id = attemptId,
                status,
                input = new
                {
                    reveal_stage = job.RevealStage,
                    path = job.RevealPath,
                    sha256 = job.RevealSha256,
                    size_bytes = job.RevealSizeBytes
                },
                plan = new
                {
                    plan_id = plan.PlanId,
                    sha256 = plan.PlanSha256,
                    value = plan.Plan
                },
                task_manifest = taskManifest,
                output = new
                {
                    path = artifact.Path,
                    sha256 = artifact.Sha256,
                    size_bytes = artifact.SizeBytes
                },
                workflow_manifest = artifact.WorkflowManifest,
                prompt_ids = artifact.PromptIds,
                checkpoint = "COMFYUI_COMPLETE",
                phase7_qa_executed = false,
                output_published = false
            },
            ContractJson.Options);

        var bytes = new UTF8Encoding(false).GetBytes(payload + Environment.NewLine);
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);

        if (File.Exists(expected))
        {
            var current = await File.ReadAllBytesAsync(
                expected, cancellationToken).ConfigureAwait(false);
            if (!current.AsSpan().SequenceEqual(bytes))
                throw new IOException(
                    "Immutable ComfyUI history already exists with different content.");
            return;
        }

        var temp = expected + $".partial.{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(
                temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, expected);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public async Task<ComfyHistoryRecovery?> TryReadRecoveryAsync(
        ComfyJobSnapshot job,
        ComfyPlanSnapshot plan,
        string historyPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(historyPath))
            return null;
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                historyPath, cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;

        if (ReadString(root, "job_id") != job.Id.Value)
            throw new InvalidDataException("ComfyUI history Job ID mismatch.");

        var input = root.GetProperty("input");
        if (!string.Equals(
                ReadString(input, "sha256"),
                job.RevealSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "ComfyUI recovery history input SHA mismatch.");

        var planNode = root.GetProperty("plan");
        if (!string.Equals(
                ReadString(planNode, "sha256"),
                plan.PlanSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "ComfyUI recovery history plan SHA mismatch.");

        var output = root.GetProperty("output");
        var path = ReadString(output, "path");
        var expectedSha = ReadString(output, "sha256");
        var expectedSize = output.GetProperty("size_bytes").GetInt64();
        if (!File.Exists(path))
            throw new InvalidDataException(
                "ComfyUI recovery output is missing.");
        var info = new FileInfo(path);
        if (info.Length != expectedSize)
            throw new InvalidDataException(
                "ComfyUI recovery output size mismatch.");

        var actualSha = await Sha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                actualSha,
                expectedSha,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "ComfyUI recovery output SHA mismatch.");

        return new(
            ReadString(root, "attempt_id"),
            new ComfyExecutionArtifact(
                path,
                actualSha,
                info.Length,
                root.GetProperty("workflow_manifest").Clone(),
                root.GetProperty("prompt_ids").Clone()));
    }

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException(
                $"ComfyUI history field {name} is invalid.");

    private static async Task<string> Sha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
