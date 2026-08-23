using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Qa;

namespace PhotoAIFactory.Infrastructure.UI;

public sealed class HistoryQueryService(IAppPaths paths) : IHistoryQueryService
{
    public async Task<IReadOnlyList<HistoryItemDto>> GetHistoryAsync(ProjectId projectId, int limit = 200, CancellationToken cancellationToken = default)
    {
        var dbPath = paths.GetProjectDatabasePath(projectId);
        if (!File.Exists(dbPath))
            return [];

        var results = new List<HistoryItemDto>();

        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT j.job_id, j.photo_id, p.association_key, j.state,
                   COALESCE(pr.reveal_mode, 'PRE_AI') AS reveal_mode,
                   j.processing_config_id,
                   pub.destination_path, pub.sha256, pub.size_bytes,
                   qr.decision AS qa_decision,
                   j.created_at_utc, j.updated_at_utc, j.parent_job_id,
                   EXISTS(SELECT 1 FROM jobs WHERE parent_job_id = j.job_id) AS has_reprocess_child
            FROM jobs j
            JOIN photos p ON p.photo_id = j.photo_id
            LEFT JOIN processing_recipes pr ON pr.job_id = j.job_id
            LEFT JOIN publications pub ON pub.job_id = j.job_id
            LEFT JOIN qa_results qr ON qr.job_id = j.job_id
            WHERE j.state IN ('COMPLETED', 'REJECTED_FINAL', 'REJECTED_PRE', 'REVIEW_FINAL', 'ERROR')
            ORDER BY j.updated_at_utc DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var jId = new JobId(reader.GetString(0));
            var pId = new PhotoId(reader.GetString(1));
            var pName = reader.GetString(2);
            var stateStr = reader.GetString(3);
            var jState = DbEnumMapper.ToJobState(stateStr);
            var revModeStr = reader.GetString(4);
            var revMode = DbEnumMapper.ToRevealMode(revModeStr);
            var cfgVer = reader.GetString(5);
            var outPath = reader.IsDBNull(6) ? null : reader.GetString(6);
            var outSha = reader.IsDBNull(7) ? null : reader.GetString(7);
            var outSize = reader.IsDBNull(8) ? 0L : reader.GetInt64(8);
            var decStr = reader.IsDBNull(9) ? null : reader.GetString(9);
            var dec = DbEnumMapper.ToQaDecision(decStr);
            var created = DateTimeOffset.Parse(reader.GetString(10));
            var completed = DateTimeOffset.Parse(reader.GetString(11));
            var duration = completed - created;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            var parentJobId = reader.IsDBNull(12) ? null : reader.GetString(12);
            var hasChild = reader.GetInt32(13) == 1;

            results.Add(new HistoryItemDto(
                jId,
                pId,
                pName,
                jState,
                revMode,
                cfgVer,
                outPath,
                outSha,
                outSize,
                dec,
                created,
                completed,
                duration,
                parentJobId,
                hasChild));
        }

        return results;
    }
}
