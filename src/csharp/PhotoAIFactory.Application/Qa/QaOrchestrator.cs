using System.Text.Json;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Qa;

namespace PhotoAIFactory.Application.Qa;

public sealed class QaOrchestrator(
    IQaStoreFactory storeFactory,
    IPythonAiClient pythonClient,
    IPublishService publishService)
{
    public async Task<bool> ProcessJobAsync(
        ProjectId projectId,
        JobId jobId,
        string outputRootFolder,
        CancellationToken cancellationToken = default)
    {
        var store = storeFactory.Open(projectId);
        var job = await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return false;

        var claimed = await store.ClaimJobForQaAsync(jobId, "qa-claim", DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        if (!claimed)
            return false;

        var attemptId = $"qa-{Guid.NewGuid().ToString("N")[..8]}";
        QaResultSnapshot qaResult;

        if (await store.HasQaResultAsync(jobId, cancellationToken).ConfigureAwait(false))
        {
            qaResult = (await store.GetQaResultAsync(jobId, cancellationToken).ConfigureAwait(false))!;
        }
        else
        {
            var requestId = Guid.NewGuid().ToString("N");
            var aiRequest = new AiRequest(
                "v1",
                requestId,
                jobId.Value,
                "qa",
                [job.CandidatePath],
                JsonSerializer.SerializeToElement(new
                {
                    thresholds = new
                    {
                        min_laplacian_variance = 35.0,
                        reprocess_laplacian_variance = 15.0,
                        max_clipping_fraction = 0.08
                    }
                }));

            var aiResponse = await pythonClient.ExecuteAsync("/v1/qa", aiRequest, cancellationToken).ConfigureAwait(false);
            var eval = QaPolicy.EvaluateResponse(requestId, aiResponse);

            var req = new PersistQaResultRequest(
                jobId,
                attemptId,
                eval.RawDecision,
                eval.ResultJson,
                job.CandidatePath,
                job.CandidateSha256,
                DateTimeOffset.UtcNow);

            await store.PersistQaResultAsync(req, cancellationToken).ConfigureAwait(false);
            qaResult = (await store.GetQaResultAsync(jobId, cancellationToken).ConfigureAwait(false))!;
        }

        if (!await store.HasCheckpointAsync(jobId, "QA_COMPLETE", cancellationToken).ConfigureAwait(false))
        {
            await store.InsertCheckpointAsync(
                jobId,
                "QA_COMPLETE",
                attemptId,
                job.CandidateSha256,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }

        var decision = qaResult.Decision.ToUpperInvariant() switch
        {
            "QA_PASS" or "PASS" => QaDecision.Pass,
            "QA_REVIEW" or "REVIEW" => QaDecision.Review,
            "QA_REPROCESS" or "REPROCESS" => QaDecision.Reprocess,
            "QA_TECH_RETRY" or "TECH_RETRY" => QaDecision.TechRetry,
            "QA_FATAL" or "FATAL" => QaDecision.Fatal,
            _ => QaDecision.Fatal
        };

        switch (decision)
        {
            case QaDecision.Pass:
            {
                var pubResult = await publishService.PublishAsync(
                    new PublishCandidateRequest(
                        job.JobId,
                        job.ProjectId,
                        job.PhotoId,
                        attemptId,
                        job.CandidatePath,
                        job.CandidateSha256,
                        "FINAL",
                        qaResult,
                        outputRootFolder),
                    cancellationToken).ConfigureAwait(false);

                await store.PersistPublicationAsync(
                    new PersistPublicationRequest(
                        pubResult.PublicationId,
                        job.JobId,
                        attemptId,
                        "FINAL",
                        pubResult.DestinationPath,
                        pubResult.Sha256,
                        pubResult.SizeBytes,
                        pubResult.Width,
                        pubResult.Height,
                        pubResult.HistoryPath,
                        pubResult.PublishedAtUtc),
                    cancellationToken).ConfigureAwait(false);

                await store.InsertCheckpointAsync(
                    job.JobId,
                    "OUTPUT_PUBLISHED",
                    attemptId,
                    pubResult.Sha256,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);

                await store.TransitionJobStateAsync(
                    job.JobId,
                    job.State,
                    JobState.Completed,
                    "QA_PASSED_AND_PUBLISHED",
                    attemptId,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            case QaDecision.Review:
            {
                var reviewItemId = Guid.NewGuid().ToString("N");
                await store.CreateReviewItemAsync(
                    new CreateReviewItemRequest(
                        reviewItemId,
                        job.JobId,
                        "FINAL",
                        DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);

                await store.TransitionJobStateAsync(
                    job.JobId,
                    job.State,
                    JobState.ReviewFinal,
                    "QA_REVIEW_REQUIRED",
                    attemptId,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            case QaDecision.Reprocess:
            {
                if (job.QualityReprocessCount == 0)
                {
                    var childJobId = JobId.New();
                    await store.CreateChildQualityReprocessJobAsync(
                        job.JobId,
                        childJobId,
                        attemptId,
                        DateTimeOffset.UtcNow,
                        cancellationToken).ConfigureAwait(false);

                    await store.TransitionJobStateAsync(
                        job.JobId,
                        job.State,
                        JobState.ReviewFinal,
                        "QA_REPROCESS_CHILD_SPAWNED",
                        attemptId,
                        DateTimeOffset.UtcNow,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var reviewItemId = Guid.NewGuid().ToString("N");
                    await store.CreateReviewItemAsync(
                        new CreateReviewItemRequest(
                            reviewItemId,
                            job.JobId,
                            "FINAL",
                            DateTimeOffset.UtcNow),
                        cancellationToken).ConfigureAwait(false);

                    await store.TransitionJobStateAsync(
                        job.JobId,
                        job.State,
                        JobState.ReviewFinal,
                        "QA_REPROCESS_LIMIT_REACHED_ROUTED_TO_REVIEW",
                        attemptId,
                        DateTimeOffset.UtcNow,
                        cancellationToken).ConfigureAwait(false);
                }
                break;
            }

            case QaDecision.TechRetry:
            {
                await store.ScheduleTechnicalRetryAsync(
                    job.JobId,
                    attemptId,
                    "QA_TECH_RETRY_REQUESTED",
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                break;
            }

            case QaDecision.Fatal:
            default:
            {
                await store.TransitionJobStateAsync(
                    job.JobId,
                    job.State,
                    JobState.Error,
                    "QA_FATAL_ERROR",
                    attemptId,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                break;
            }
        }

        return true;
    }
}
