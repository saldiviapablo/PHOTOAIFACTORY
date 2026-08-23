using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.UI;

public sealed class DashboardQueryService(
    IAppPaths paths,
    IProjectStoreFactory storeFactory,
    IComponentHealthTracker healthTracker) : IDashboardQueryService
{
    public async Task<DashboardSummaryDto?> GetDashboardSummaryAsync(ProjectId projectId, CancellationToken cancellationToken = default)
    {
        var dbPath = paths.GetProjectDatabasePath(projectId);
        if (!File.Exists(dbPath))
            return null;

        var store = storeFactory.Open(projectId);
        var projectWrapper = await store.GetAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (projectWrapper is null)
            return null;

        var project = projectWrapper.Project;
        var config = projectWrapper.LatestConfig.ReadConfig();

        int received = 0, queued = 0, processing = 0, completed = 0, review = 0, rejected = 0, errors = 0;
        double? avgSeconds = null;
        ActiveJobSummaryDto? activeJob = null;

        await using (var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // 1. State counts against real jobs table
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT
                        COALESCE(SUM(CASE WHEN state = 'RECEIVED' THEN 1 ELSE 0 END), 0) AS received_count,
                        COALESCE(SUM(CASE WHEN state = 'QUEUED' THEN 1 ELSE 0 END), 0) AS queued_count,
                        COALESCE(SUM(CASE WHEN state IN ('ANALYZING', 'PROCESSING', 'QA', 'RETRYING') THEN 1 ELSE 0 END), 0) AS processing_count,
                        COALESCE(SUM(CASE WHEN state = 'COMPLETED' THEN 1 ELSE 0 END), 0) AS completed_count,
                        COALESCE(SUM(CASE WHEN state IN ('REVIEW_PRE', 'REVIEW_FINAL') THEN 1 ELSE 0 END), 0) AS review_count,
                        COALESCE(SUM(CASE WHEN state IN ('REJECTED_PRE', 'REJECTED_FINAL') THEN 1 ELSE 0 END), 0) AS rejected_count,
                        COALESCE(SUM(CASE WHEN state = 'ERROR' THEN 1 ELSE 0 END), 0) AS error_count
                    FROM jobs;
                    """;

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    received = reader.GetInt32(0);
                    queued = reader.GetInt32(1);
                    processing = reader.GetInt32(2);
                    completed = reader.GetInt32(3);
                    review = reader.GetInt32(4);
                    rejected = reader.GetInt32(5);
                    errors = reader.GetInt32(6);
                }
            }

            // 2. Average processing time from completed jobs (durations from RECEIVED to COMPLETED transitions)
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT AVG((julianday(t_end.occurred_at_utc) - julianday(t_start.occurred_at_utc)) * 86400.0) AS avg_duration_seconds
                    FROM jobs j
                    JOIN job_state_transitions t_start ON t_start.job_id = j.job_id AND t_start.to_state = 'RECEIVED'
                    JOIN job_state_transitions t_end ON t_end.job_id = j.job_id AND t_end.to_state = 'COMPLETED'
                    WHERE j.state = 'COMPLETED';
                    """;

                var val = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (val is not null and not DBNull)
                {
                    avgSeconds = Convert.ToDouble(val);
                }
            }

            // 3. Active Job (ANALYZING, PROCESSING, QA, RETRYING)
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT j.job_id, j.photo_id, p.association_key, j.state,
                           COALESCE(pr.reveal_mode, 'PRE_AI') AS reveal_mode,
                           j.processing_config_id, j.technical_retry_count, j.quality_reprocess_count,
                           (SELECT occurred_at_utc FROM job_state_transitions WHERE job_id = j.job_id ORDER BY occurred_at_utc DESC LIMIT 1) AS last_transition_utc,
                           COALESCE(ce.output_path, fp.image_path, o.path, j.analysis_representation_path) AS preview_path
                    FROM jobs j
                    JOIN photos p ON p.photo_id = j.photo_id
                    LEFT JOIN processing_recipes pr ON pr.job_id = j.job_id
                    LEFT JOIN comfy_executions ce ON ce.job_id = j.job_id
                    LEFT JOIN feedback_passes fp ON fp.job_id = j.job_id AND fp.pass_number = 2
                    LEFT JOIN processing_passes pp ON pp.job_id = j.job_id
                    LEFT JOIN outputs o ON o.output_id = pp.output_id
                    WHERE j.state IN ('ANALYZING', 'PROCESSING', 'QA', 'RETRYING')
                    ORDER BY j.created_at_utc ASC
                    LIMIT 1;
                    """;

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var jId = new JobId(reader.GetString(0));
                    var pId = new PhotoId(reader.GetString(1));
                    var pName = reader.GetString(2);
                    var stateStr = reader.GetString(3);
                    var jState = DbEnumMapper.ToJobState(stateStr);
                    var revModeStr = reader.GetString(4);
                    var revMode = DbEnumMapper.ToRevealMode(revModeStr);
                    var cfgVer = reader.GetString(5);
                    var retries = reader.GetInt32(6);
                    var reprocesses = reader.GetInt32(7);
                    var elapsed = TimeSpan.Zero;
                    if (!reader.IsDBNull(8) && DateTimeOffset.TryParse(reader.GetString(8), out var transTime))
                    {
                        elapsed = DateTimeOffset.UtcNow - transTime;
                    }
                    var preview = reader.IsDBNull(9) ? null : reader.GetString(9);

                    var stageName = jState switch
                    {
                        JobState.Analyzing => "Analysis & Preselection",
                        JobState.Processing => revMode == RevealMode.Feedback ? "Feedback Iteration" : "Basic Reveal",
                        JobState.Qa => "Quality Assurance",
                        JobState.Retrying => "Retrying Stage",
                        _ => jState.ToString()
                    };

                    activeJob = new ActiveJobSummaryDto(
                        jId,
                        pId,
                        pName,
                        jState,
                        stageName,
                        true,
                        0,
                        elapsed,
                        revMode,
                        1,
                        preview,
                        retries,
                        reprocesses);
                }
            }
        }

        // 4. Component health cards
        var healthStatuses = healthTracker.GetAllStatuses();
        var healthCards = healthStatuses.Select(h => new ComponentHealthCardDto(
            h.ComponentName,
            GetDisplayComponentName(h.ComponentName),
            h.State,
            h.Reason ?? (h.State == ComponentHealthState.Healthy ? "Operational" : h.State.ToString()),
            h.CircuitBreakerOpen,
            h.TotalRestarts,
            h.LastCheckedUtc)).ToList();

        TimeSpan? avgDuration = avgSeconds.HasValue && avgSeconds.Value > 0 ? TimeSpan.FromSeconds(avgSeconds.Value) : null;

        return new DashboardSummaryDto(
            project.Id,
            project.Name,
            project.State,
            config.InputFolder,
            config.OutputFolder,
            received,
            queued,
            processing,
            completed,
            review,
            rejected,
            errors,
            avgDuration,
            avgDuration.HasValue,
            activeJob,
            healthCards);
    }

    private static string GetDisplayComponentName(string name) => name switch
    {
        "PythonWorker" => "Python AI Worker",
        "Darktable" => "Darktable CLI",
        "ComfyUI" => "ComfyUI Runtime",
        "Storage" => "Storage Preflight",
        "GpuCoordinator" => "GPU Coordinator",
        _ => name
    };
}
