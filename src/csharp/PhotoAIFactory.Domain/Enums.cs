namespace PhotoAIFactory.Domain;

public enum JobState
{
    Received, Analyzing, ReviewPre, RejectedPre, Queued, Processing, Qa,
    ReviewFinal, RejectedFinal, Completed, Error, CancelRequested, Cancelled,
    Retrying, Interrupted
}

public enum ProjectState
{
    Running, PauseRequested, Paused, StopRequested, Stopped,
    BlockedStorage, ComponentUnhealthy
}

public enum RevealMode { PreAi, DtAuto, Feedback }
public enum SemanticMode { Off, Standard, Full }
public enum ComfyUiMode { Off, On, Auto }
public enum QaDecision { Pass, Review, Reprocess, TechRetry, Fatal }
public enum PreselectionDecision { Approved, ReviewPre, RejectedPre }

public enum StageName
{
    Ingest, OriginalArchive, Analysis, Preselection, DarktablePass1,
    FeedbackInspection, RawDenoise, DarktablePass2, ComfyUi, Qa, Publish
}
