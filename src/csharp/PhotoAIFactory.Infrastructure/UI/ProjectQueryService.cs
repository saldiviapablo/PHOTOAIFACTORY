using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.UI;

public sealed class ProjectQueryService(
    IAppPaths paths,
    IProjectStoreFactory storeFactory) : IProjectQueryService
{
    public async Task<IReadOnlyList<ProjectSummaryDto>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ProjectSummaryDto>();
        var projectsDir = paths.ProjectsDirectory;
        if (!Directory.Exists(projectsDir))
        {
            return results;
        }

        foreach (var dir in Directory.GetDirectories(projectsDir))
        {
            var folderName = Path.GetFileName(dir);
            var dbPath = paths.GetProjectDatabasePath(new ProjectId(folderName));
            if (!File.Exists(dbPath))
                continue;

            try
            {
                var summary = await GetProjectSummaryAsync(new ProjectId(folderName), cancellationToken).ConfigureAwait(false);
                if (summary is not null)
                {
                    results.Add(summary);
                }
            }
            catch
            {
                // Skip unreadable or corrupted project folders gracefully
            }
        }

        return results.OrderByDescending(p => p.LastActivityUtc ?? p.CreatedAtUtc).ToList();
    }

    public async Task<ProjectSummaryDto?> GetProjectSummaryAsync(ProjectId projectId, CancellationToken cancellationToken = default)
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

        var totalPhotos = 0;
        var completedJobs = 0;
        var pendingReviews = 0;
        var activeErrors = 0;
        DateTimeOffset? lastActivity = null;

        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM photos) AS total_photos,
                (SELECT COUNT(*) FROM jobs WHERE state = 'COMPLETED') AS completed_jobs,
                (SELECT COUNT(*) FROM review_items WHERE status = 'PENDING') AS pending_reviews,
                (SELECT COUNT(*) FROM jobs WHERE state = 'ERROR') AS active_errors,
                (SELECT MAX(activity_time) FROM (
                    SELECT occurred_at_utc AS activity_time FROM job_state_transitions
                    UNION ALL
                    SELECT occurred_at_utc AS activity_time FROM project_state_transitions
                    UNION ALL
                    SELECT created_at_utc AS activity_time FROM photos
                )) AS last_activity;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            totalPhotos = reader.GetInt32(0);
            completedJobs = reader.GetInt32(1);
            pendingReviews = reader.GetInt32(2);
            activeErrors = reader.GetInt32(3);
            if (!reader.IsDBNull(4))
            {
                if (DateTimeOffset.TryParse(reader.GetString(4), out var dt))
                {
                    lastActivity = dt;
                }
            }
        }

        return new ProjectSummaryDto(
            project.Id,
            project.Name,
            project.State,
            project.StateRevision,
            config.InputFolder,
            config.OutputFolder,
            config.RevealMode,
            project.CreatedAtUtc,
            lastActivity,
            totalPhotos,
            completedJobs,
            pendingReviews,
            activeErrors);
    }
}
