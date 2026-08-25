using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Qa;

namespace PhotoAIFactory.Infrastructure.UI;

public sealed class ReviewQueryService(IAppPaths paths) : IReviewQueryService
{
    public async Task<IReadOnlyList<ReviewItemDto>> GetPendingReviewsAsync(ProjectId projectId, CancellationToken cancellationToken = default)
    {
        var dbPath = paths.GetProjectDatabasePath(projectId);
        if (!File.Exists(dbPath))
            return [];

        var results = new List<ReviewItemDto>();

        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(r.review_item_id, 'job:' || j.job_id) AS review_item_id,
                   j.job_id,
                   j.photo_id,
                   p.association_key,
                   j.state,
                   COALESCE(r.review_kind, CASE WHEN j.state = 'REVIEW_PRE' THEN 'PRE' ELSE 'FINAL' END) AS review_kind,
                   COALESCE(ce.output_path, fp.image_path, o.path, j.analysis_representation_path) AS candidate_path,
                   COALESCE(r.created_at_utc, j.updated_at_utc) AS created_at_utc,
                   j.quality_reprocess_count,
                   qr.decision AS qa_decision,
                   qr.result_json AS qa_result_json,
                   pres.findings_json AS preselection_findings_json,
                   (SELECT reason FROM job_state_transitions WHERE job_id = j.job_id AND to_state = 'ERROR' ORDER BY occurred_at_utc DESC LIMIT 1) AS error_msg
            FROM jobs j
            JOIN photos p ON p.photo_id = j.photo_id
            LEFT JOIN review_items r ON r.job_id = j.job_id AND r.status = 'PENDING'
            LEFT JOIN comfy_executions ce ON ce.job_id = j.job_id
            LEFT JOIN feedback_passes fp ON fp.job_id = j.job_id AND fp.pass_number = 2
            LEFT JOIN processing_passes pp ON pp.job_id = j.job_id
            LEFT JOIN outputs o ON o.output_id = pp.output_id
            LEFT JOIN qa_results qr ON qr.job_id = j.job_id
            LEFT JOIN preselection_results pres ON pres.job_id = j.job_id
            WHERE j.state IN ('REVIEW_PRE', 'REVIEW_FINAL') OR r.status = 'PENDING'
            ORDER BY created_at_utc ASC;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rId = reader.GetString(0);
            var jId = new JobId(reader.GetString(1));
            var pId = new PhotoId(reader.GetString(2));
            var pName = reader.GetString(3);
            var stateStr = reader.GetString(4);
            var jState = DbEnumMapper.ToJobState(stateStr);
            var reviewKind = reader.GetString(5);
            var stage = reviewKind == "PRE" ? "Preselection Review" : "Final Quality Review";
            var candPath = reader.IsDBNull(6) ? null : reader.GetString(6);
            var created = DateTimeOffset.Parse(reader.GetString(7));
            var reprocessCount = reader.GetInt32(8);

            var qaDecisionStr = reader.IsDBNull(9) ? null : reader.GetString(9);
            var qaDecision = DbEnumMapper.ToQaDecision(qaDecisionStr);

            JsonElement findingsElement = default;
            if (reviewKind == "FINAL" && !reader.IsDBNull(10))
            {
                try
                {
                    using var doc = JsonDocument.Parse(reader.GetString(10));
                    if (doc.RootElement.TryGetProperty("findings", out var f))
                    {
                        findingsElement = f.Clone();
                    }
                }
                catch
                {
                }
            }
            else if (reviewKind == "PRE" && !reader.IsDBNull(11))
            {
                try
                {
                    using var doc = JsonDocument.Parse(reader.GetString(11));
                    findingsElement = doc.RootElement.Clone();
                }
                catch
                {
                }
            }

            var errorMsg = reader.IsDBNull(12) ? null : reader.GetString(12);
            var previewPath = candPath;

            results.Add(new ReviewItemDto(
                rId,
                projectId,
                jId,
                pId,
                pName,
                jState,
                stage,
                candPath,
                previewPath,
                qaDecision,
                findingsElement,
                errorMsg,
                reprocessCount,
                created));
        }

        // 2. Unsupported format photos in REVIEW_UNSUPPORTED_FORMAT state
        await using var unsuppCmd = connection.CreateCommand();
        unsuppCmd.CommandText = """
            SELECT p.photo_id, p.association_key, a.managed_path, a.source_path, p.created_at_utc, a.raw_classification, a.raw_support_status
            FROM photos p
            JOIN assets a ON a.asset_id = p.master_asset_id
            WHERE p.state = 'REVIEW_UNSUPPORTED_FORMAT'
            ORDER BY p.created_at_utc ASC;
            """;

        await using var unsuppReader = await unsuppCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await unsuppReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var pIdStr = unsuppReader.GetString(0);
            var pId = new PhotoId(pIdStr);
            var pName = unsuppReader.GetString(1);
            var managedPath = unsuppReader.IsDBNull(2) ? null : unsuppReader.GetString(2);
            var sourcePath = unsuppReader.IsDBNull(3) ? null : unsuppReader.GetString(3);
            var created = DateTimeOffset.Parse(unsuppReader.GetString(4));
            var rawClass = unsuppReader.IsDBNull(5) ? "Unsupported RAW" : unsuppReader.GetString(5);
            var rawSupport = unsuppReader.IsDBNull(6) ? "" : unsuppReader.GetString(6);

            var candPath = managedPath ?? sourcePath;
            var rId = $"unsupported:{pIdStr}";
            JobId? jId = null;

            results.Add(new ReviewItemDto(
                rId,
                projectId,
                jId,
                pId,
                pName,
                JobState.RejectedPre,
                "Unsupported Format",
                candPath,
                candPath,
                QaDecision.Fatal,
                default,
                $"Unsupported RAW format: {rawClass} ({rawSupport})",
                0,
                created));
        }

        return results;
    }
}
