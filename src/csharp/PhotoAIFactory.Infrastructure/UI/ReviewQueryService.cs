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
            SELECT r.review_item_id, r.job_id, j.photo_id, p.association_key, j.state, r.review_kind,
                   COALESCE(ce.output_path, fp.image_path, o.path, j.analysis_representation_path) AS candidate_path,
                   r.created_at_utc, j.quality_reprocess_count,
                   qr.decision AS qa_decision,
                   qr.result_json AS qa_result_json,
                   pres.findings_json AS preselection_findings_json,
                   (SELECT reason FROM job_state_transitions WHERE job_id = j.job_id AND to_state = 'ERROR' ORDER BY occurred_at_utc DESC LIMIT 1) AS error_msg
            FROM review_items r
            JOIN jobs j ON j.job_id = r.job_id
            JOIN photos p ON p.photo_id = j.photo_id
            LEFT JOIN comfy_executions ce ON ce.job_id = j.job_id
            LEFT JOIN feedback_passes fp ON fp.job_id = j.job_id AND fp.pass_number = 2
            LEFT JOIN processing_passes pp ON pp.job_id = j.job_id
            LEFT JOIN outputs o ON o.output_id = pp.output_id
            LEFT JOIN qa_results qr ON qr.job_id = j.job_id
            LEFT JOIN preselection_results pres ON pres.job_id = j.job_id
            WHERE r.status = 'PENDING'
            ORDER BY r.created_at_utc ASC;
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

        return results;
    }
}
