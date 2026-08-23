using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.UI;

public static class DbEnumMapper
{
    public static JobState ToJobState(string dbState) => dbState.ToUpperInvariant() switch
    {
        "RECEIVED" => JobState.Received,
        "ANALYZING" => JobState.Analyzing,
        "REVIEW_PRE" => JobState.ReviewPre,
        "REJECTED_PRE" => JobState.RejectedPre,
        "QUEUED" => JobState.Queued,
        "PROCESSING" => JobState.Processing,
        "QA" => JobState.Qa,
        "REVIEW_FINAL" => JobState.ReviewFinal,
        "REJECTED_FINAL" => JobState.RejectedFinal,
        "COMPLETED" => JobState.Completed,
        "ERROR" => JobState.Error,
        "CANCEL_REQUESTED" => JobState.CancelRequested,
        "CANCELLED" => JobState.Cancelled,
        "RETRYING" => JobState.Retrying,
        "INTERRUPTED" => JobState.Interrupted,
        _ => JobState.Error
    };

    public static string FromJobState(JobState state) => state switch
    {
        JobState.Received => "RECEIVED",
        JobState.Analyzing => "ANALYZING",
        JobState.ReviewPre => "REVIEW_PRE",
        JobState.RejectedPre => "REJECTED_PRE",
        JobState.Queued => "QUEUED",
        JobState.Processing => "PROCESSING",
        JobState.Qa => "QA",
        JobState.ReviewFinal => "REVIEW_FINAL",
        JobState.RejectedFinal => "REJECTED_FINAL",
        JobState.Completed => "COMPLETED",
        JobState.Error => "ERROR",
        JobState.CancelRequested => "CANCEL_REQUESTED",
        JobState.Cancelled => "CANCELLED",
        JobState.Retrying => "RETRYING",
        JobState.Interrupted => "INTERRUPTED",
        _ => "ERROR"
    };

    public static ProjectState ToProjectState(string dbState) => dbState.ToUpperInvariant() switch
    {
        "RUNNING" => ProjectState.Running,
        "PAUSE_REQUESTED" => ProjectState.PauseRequested,
        "PAUSED" => ProjectState.Paused,
        "STOP_REQUESTED" => ProjectState.StopRequested,
        "STOPPED" => ProjectState.Stopped,
        "BLOCKED_STORAGE" => ProjectState.BlockedStorage,
        "COMPONENT_UNHEALTHY" => ProjectState.ComponentUnhealthy,
        _ => ProjectState.Stopped
    };

    public static string FromProjectState(ProjectState state) => state switch
    {
        ProjectState.Running => "RUNNING",
        ProjectState.PauseRequested => "PAUSE_REQUESTED",
        ProjectState.Paused => "PAUSED",
        ProjectState.StopRequested => "STOP_REQUESTED",
        ProjectState.Stopped => "STOPPED",
        ProjectState.BlockedStorage => "BLOCKED_STORAGE",
        ProjectState.ComponentUnhealthy => "COMPONENT_UNHEALTHY",
        _ => "STOPPED"
    };

    public static RevealMode ToRevealMode(string? dbRevealMode) => (dbRevealMode ?? string.Empty).ToUpperInvariant() switch
    {
        "PRE_AI" or "PREAI" => RevealMode.PreAi,
        "DT_AUTO" or "DTAUTO" => RevealMode.DtAuto,
        "FEEDBACK" => RevealMode.Feedback,
        _ => RevealMode.PreAi
    };

    public static QaDecision? ToQaDecision(string? dbDecision)
    {
        if (string.IsNullOrWhiteSpace(dbDecision))
            return null;

        return dbDecision.ToUpperInvariant() switch
        {
            "PASS" or "QA_PASS" => QaDecision.Pass,
            "REVIEW" or "QA_REVIEW" => QaDecision.Review,
            "REPROCESS" or "QA_REPROCESS" => QaDecision.Reprocess,
            "TECH_RETRY" or "QA_TECH_RETRY" => QaDecision.TechRetry,
            "FATAL" or "QA_FATAL" => QaDecision.Fatal,
            _ => null
        };
    }
}
