namespace PhotoAIFactory.Domain;

public static class JobStateMachine
{
    private static readonly IReadOnlyDictionary<JobState, HashSet<JobState>> Allowed =
        new Dictionary<JobState, HashSet<JobState>>
        {
            [JobState.Received] = [JobState.Analyzing, JobState.Cancelled, JobState.Error, JobState.Interrupted],
            [JobState.Analyzing] = [JobState.ReviewPre, JobState.RejectedPre, JobState.Queued, JobState.CancelRequested, JobState.Retrying, JobState.Error, JobState.Interrupted],
            [JobState.ReviewPre] = [JobState.Queued, JobState.RejectedPre, JobState.Cancelled],
            [JobState.RejectedPre] = [JobState.Queued],
            [JobState.Queued] = [JobState.Processing, JobState.Cancelled, JobState.Interrupted],
            [JobState.Processing] = [JobState.Qa, JobState.CancelRequested, JobState.Retrying, JobState.Error, JobState.Interrupted],
            [JobState.CancelRequested] = [JobState.Cancelled, JobState.Error],
            [JobState.Retrying] = [JobState.Processing, JobState.Analyzing, JobState.Error, JobState.Interrupted],
            [JobState.Interrupted] = [JobState.Analyzing, JobState.Queued, JobState.Processing, JobState.Qa, JobState.ReviewFinal, JobState.Error, JobState.Cancelled],
            [JobState.Qa] = [JobState.Completed, JobState.ReviewFinal, JobState.Error, JobState.Retrying, JobState.Processing, JobState.Interrupted],
            [JobState.ReviewFinal] = [JobState.Completed, JobState.RejectedFinal],
        };

    public static bool CanTransition(JobState from, JobState to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static void EnsureTransition(JobState from, JobState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Invalid Job transition: {from} -> {to}");
        }
    }

    public static bool IsTerminal(JobState state) => state is
        JobState.Completed or JobState.RejectedFinal or JobState.Error or JobState.Cancelled;
}
