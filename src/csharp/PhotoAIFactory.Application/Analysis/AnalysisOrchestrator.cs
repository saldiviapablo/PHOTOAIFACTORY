using System.Text.Json;
using Microsoft.Extensions.Logging;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Analysis;

namespace PhotoAIFactory.Application.Analysis;

public sealed class AnalysisOrchestrator(
    IAnalysisInputResolver inputResolver,
    IAnalysisStoreFactory stores,
    IPythonAiClient python,
    IGpuResourceCoordinator gpu,
    TimeProvider timeProvider,
    ILogger<AnalysisOrchestrator> logger,
    IComponentHealthTracker? healthTracker = null,
    IGpuExecutionPolicy? gpuPolicy = null)
{
    private const int AnalysisSchemaVersion = 1;
    private static readonly EventId StartedEvent = new(3100, "AnalysisStarted");
    private static readonly EventId CompletedEvent = new(3101, "AnalysisCompleted");
    private static readonly EventId RetryEvent = new(3102, "AnalysisRetry");
    private static readonly EventId PreselectionEvent = new(3103, "PreselectionCompleted");
    private static readonly EventId FailureEvent = new(3199, "AnalysisFailed");

    public async Task<AnalysisRunResult> ProcessPhotoAsync(
        ProjectId projectId,
        PhotoId photoId,
        string preselectionConfigId,
        string processingConfigId,
        SemanticMode semanticMode,
        bool preselectionEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preselectionConfigId);
        ArgumentException.ThrowIfNullOrWhiteSpace(processingConfigId);

        var store = stores.Open(projectId);
        var attemptId = Guid.NewGuid().ToString("N");
        var job = await store.GetInitialJobByPhotoAsync(
            projectId, photoId, cancellationToken).ConfigureAwait(false);

        ResolvedAnalysisInput input;
        if (job is null)
        {
            var proposedJobId = JobId.New();
            input = await inputResolver.ResolveAsync(
                projectId, photoId, proposedJobId, attemptId, cancellationToken).ConfigureAwait(false);
            job = await store.GetOrCreateInitialJobAsync(
                proposedJobId,
                projectId,
                photoId,
                preselectionConfigId,
                processingConfigId,
                input,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }

        input = FrozenInput(job);

        // Idempotent re-entry: once PRESELECTION_COMPLETE exists, a repeated
        // reconciliation call must not reopen or mutate the Job.
        var persistedAnalysis = await store.GetAnalysisAsync(job.Id, cancellationToken).ConfigureAwait(false);
        var persistedPreselection = await store.GetPreselectionAsync(job.Id, cancellationToken).ConfigureAwait(false);
        var analysisCheckpoint = await store.HasCheckpointAsync(
            job.Id, "ANALYSIS_COMPLETE", cancellationToken).ConfigureAwait(false);
        var preselectionCheckpoint = await store.HasCheckpointAsync(
            job.Id, "PRESELECTION_COMPLETE", cancellationToken).ConfigureAwait(false);
        if (persistedAnalysis is not null && persistedPreselection is not null &&
            analysisCheckpoint && preselectionCheckpoint)
        {
            return new(
                job,
                persistedAnalysis,
                persistedPreselection,
                await store.ListQueueAsync(projectId, cancellationToken).ConfigureAwait(false));
        }

        try
        {
            if (job.State != JobState.Analyzing)
            {
                await store.MarkAnalyzingAsync(
                    job.Id,
                    $"analysis-start:{Guid.NewGuid():N}",
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation(
                StartedEvent,
                "Analysis started for Project {ProjectId}, Photo {PhotoId}, Job {JobId}, input {InputKind}",
                projectId.Value,
                photoId.Value,
                job.Id.Value,
                input.Kind);

            // Materialization happens only after the durable Job freezes its source Asset/hash/path.
            await inputResolver.EnsureRepresentationAsync(
                projectId, job.Id, attemptId, input, cancellationToken).ConfigureAwait(false);

            job = await RequireJobAsync(store, job.Id, cancellationToken).ConfigureAwait(false);

            var analysis = await store.GetAnalysisAsync(job.Id, cancellationToken).ConfigureAwait(false);
            var hasAnalysisCheckpoint = await store.HasCheckpointAsync(
                job.Id, "ANALYSIS_COMPLETE", cancellationToken).ConfigureAwait(false);
            if (analysis is null || !hasAnalysisCheckpoint)
            {
                analysis = await ExecuteAnalysisWithRetriesAsync(
                    store, job, input, semanticMode, attemptId, cancellationToken).ConfigureAwait(false);
            }

            var preselection = await store.GetPreselectionAsync(job.Id, cancellationToken).ConfigureAwait(false);
            var hasPreselectionCheckpoint = await store.HasCheckpointAsync(
                job.Id, "PRESELECTION_COMPLETE", cancellationToken).ConfigureAwait(false);
            if (preselection is null || !hasPreselectionCheckpoint)
            {
                // Recovery may arrive with ANALYSIS_COMPLETE and state INTERRUPTED.
                job = await RequireJobAsync(store, job.Id, cancellationToken).ConfigureAwait(false);
                if (job.State == JobState.Interrupted)
                {
                    await store.MarkAnalyzingAsync(
                        job.Id,
                        $"analysis-resume-preselection:{Guid.NewGuid():N}",
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    job = await RequireJobAsync(store, job.Id, cancellationToken).ConfigureAwait(false);
                }

                preselection = await ExecutePreselectionAsync(
                    store,
                    job,
                    input,
                    analysis.Result,
                    preselectionEnabled,
                    attemptId,
                    cancellationToken).ConfigureAwait(false);
            }

            var currentJob = await RequireJobAsync(store, job.Id, cancellationToken).ConfigureAwait(false);
            var queue = await store.ListQueueAsync(projectId, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                PreselectionEvent,
                "Preselection completed for Job {JobId} with decision {Decision}; queue count {QueueCount}",
                job.Id.Value,
                preselection.Decision,
                queue.Count);

            return new(currentJob, analysis, preselection, queue);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryMarkInterruptedAsync(store, job.Id).ConfigureAwait(false);
            throw;
        }
        catch (AnalysisStageException ex)
        {
            await TryMarkErrorAsync(store, job.Id, ex.Code).ConfigureAwait(false);
            logger.LogError(
                FailureEvent,
                ex,
                "Analysis failed for Job {JobId}; code {ErrorCode}; retryable {Retryable}",
                job.Id.Value,
                ex.Code,
                ex.Retryable);
            throw;
        }
        catch (Exception ex)
        {
            await TryMarkErrorAsync(store, job.Id, "ANALYSIS_UNEXPECTED_ERROR").ConfigureAwait(false);
            logger.LogError(FailureEvent, ex, "Analysis failed unexpectedly for Job {JobId}", job.Id.Value);
            throw;
        }
    }

    private async Task<AnalysisResultSnapshot> ExecuteAnalysisWithRetriesAsync(
        IAnalysisStore store,
        AnalysisJobSnapshot job,
        ResolvedAnalysisInput input,
        SemanticMode semanticMode,
        string firstAttemptId,
        CancellationToken cancellationToken)
    {
        var attemptNumber = job.TechnicalRetryCount;
        var attemptId = firstAttemptId;

        if (healthTracker is not null && healthTracker.IsStageBlocked("PythonWorker"))
        {
            logger.LogWarning("Analysis stage blocked because Python worker is unhealthy.");
            throw new AnalysisStageException(
                "PYTHON_WORKER_UNHEALTHY",
                "ai_worker",
                "Python worker component is unhealthy",
                false);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            job = await RequireJobAsync(store, job.Id, cancellationToken).ConfigureAwait(false);
            if (job.State == JobState.Retrying)
            {
                await store.MarkAnalyzingAsync(
                    job.Id,
                    $"analysis-retry-start:{attemptId}",
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                job = await RequireJobAsync(store, job.Id, cancellationToken).ConfigureAwait(false);
            }

            var config = JsonSerializer.SerializeToElement(new
            {
                schema_version = AnalysisSchemaVersion,
                semantic_mode = SemanticModeName(semanticMode),
                analysis_input_kind = InputKindName(input.Kind),
                offline_only = true
            }, ContractJson.Options);

            var requestId = Guid.NewGuid().ToString("N");
            var request = new AiRequest(
                "v1",
                requestId,
                job.Id.Value,
                "analyze",
                [input.RepresentationPath],
                config);

            AiResponse response;
            try
            {
                if (gpuPolicy is not null)
                {
                    response = await gpuPolicy.ExecuteWithGpuAsync(
                        $"analysis:{job.Id.Value}",
                        async () =>
                        {
                            try
                            {
                                return await python.ExecuteAsync(
                                    "/v1/analyze", request, cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                await BestEffortReleaseModelsAsync(job.Id).ConfigureAwait(false);
                            }
                        },
                        releaseMemory: async () =>
                        {
                            await BestEffortReleaseModelsAsync(job.Id).ConfigureAwait(false);
                        },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await using var lease = await gpu.AcquireAsync(
                        $"analysis:{job.Id.Value}",
                        cancellationToken).ConfigureAwait(false);

                    try
                    {
                        response = await python.ExecuteAsync(
                            "/v1/analyze", request, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await BestEffortReleaseModelsAsync(job.Id).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (GpuOutOfMemoryException)
            {
                throw;
            }
            catch (AnalysisStageException ex)
            {
                if (!await TryScheduleRetryAsync(
                        store, job.Id, ex.Code, ex.Retryable, attemptNumber, attemptId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    throw;
                }

                attemptNumber++;
                attemptId = Guid.NewGuid().ToString("N");
                continue;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException)
            {
                if (!await TryScheduleRetryAsync(
                        store, job.Id, "PYTHON_TRANSPORT", true, attemptNumber, attemptId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    throw new AnalysisStageException(
                        "PYTHON_TRANSPORT",
                        "transport",
                        ex.Message,
                        true);
                }

                attemptNumber++;
                attemptId = Guid.NewGuid().ToString("N");
                continue;
            }

            ValidateCorrelation(requestId, response);
            if (response.Success && response.Result is JsonElement result)
            {
                ValidateAnalysisEnvelope(result);
                var models = ParseModelExecutions(result);
                var fingerprint = $"{input.SourceSha256}:{InputKindName(input.Kind)}";

                await store.PersistAnalysisCompleteAsync(
                    job,
                    attemptId,
                    AnalysisSchemaVersion,
                    result.Clone(),
                    models,
                    fingerprint,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);

                logger.LogInformation(
                    CompletedEvent,
                    "ANALYSIS_COMPLETE persisted for Job {JobId} with {ModelCount} model executions",
                    job.Id.Value,
                    models.Count);

                return await store.GetAnalysisAsync(job.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "ANALYSIS_COMPLETE was persisted without an Analysis row.");
            }

            var error = response.Error
                ?? throw new InvalidDataException(
                    "Python returned success=false without structured error.");

            if (!await TryScheduleRetryAsync(
                    store, job.Id, error.Code, error.Retryable, attemptNumber, attemptId, cancellationToken)
                .ConfigureAwait(false))
            {
                throw new AnalysisStageException(
                    error.Code, error.Category, error.Message, error.Retryable);
            }

            attemptNumber++;
            attemptId = Guid.NewGuid().ToString("N");
        }
    }


    private async Task BestEffortReleaseModelsAsync(JobId jobId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var releaseRequestId = Guid.NewGuid().ToString("N");
            var releaseRequest = new AiRequest(
                "v1",
                releaseRequestId,
                jobId.Value,
                "models.release",
                [],
                JsonSerializer.SerializeToElement(
                    new { scope = "analysis" }, ContractJson.Options));
            var response = await python.ExecuteAsync(
                "/v1/models/release", releaseRequest, timeout.Token).ConfigureAwait(false);
            ValidateCorrelation(releaseRequestId, response);
            if (!response.Success)
            {
                logger.LogWarning(
                    FailureEvent,
                    "Python model release returned {ErrorCode} for Job {JobId}",
                    response.Error?.Code ?? "MODEL_RELEASE_FAILED",
                    jobId.Value);
            }
        }
        catch (Exception ex)
        {
            // Analyze/preselection retains its primary failure. Sequential adapter
            // release in the worker is the first safety layer; this is the final
            // lease-bound cleanup attempt.
            logger.LogWarning(
                FailureEvent,
                ex,
                "Best-effort Python model release failed for Job {JobId}",
                jobId.Value);
        }
    }

    private async Task<bool> TryScheduleRetryAsync(
        IAnalysisStore store,
        JobId jobId,
        string errorCode,
        bool retryable,
        int retriesAlreadyUsed,
        string attemptId,
        CancellationToken cancellationToken)
    {
        var retryLimit = string.Equals(
            errorCode, "GPU_OOM", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        if (!retryable || retriesAlreadyUsed >= retryLimit)
        {
            return false;
        }

        await store.IncrementTechnicalRetryAsync(
            jobId,
            $"analysis-retry:{attemptId}:{retriesAlreadyUsed + 1}",
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        var delay = retriesAlreadyUsed == 0
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromSeconds(3);

        logger.LogWarning(
            RetryEvent,
            "Retrying Job {JobId} after {ErrorCode}; retry {RetryNumber}/{RetryLimit}",
            jobId.Value,
            errorCode,
            retriesAlreadyUsed + 1,
            retryLimit);

        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<PreselectionResultSnapshot> ExecutePreselectionAsync(
        IAnalysisStore store,
        AnalysisJobSnapshot job,
        ResolvedAnalysisInput input,
        JsonElement analysis,
        bool preselectionEnabled,
        string attemptId,
        CancellationToken cancellationToken)
    {
        var config = JsonSerializer.SerializeToElement(new
        {
            enabled = preselectionEnabled,
            allow_auto_reject = false,
            policy = new
            {
                benchmark_status = "NOT_CALIBRATED",
                thresholds = new { }
            },
            analysis = analysis.Clone()
        }, ContractJson.Options);

        var requestId = Guid.NewGuid().ToString("N");
        var request = new AiRequest(
            "v1",
            requestId,
            job.Id.Value,
            "preselect",
            [input.RepresentationPath],
            config);

        var response = await python.ExecuteAsync(
            "/v1/preselect", request, cancellationToken).ConfigureAwait(false);
        ValidateCorrelation(requestId, response);

        if (!response.Success || response.Result is not JsonElement result)
        {
            var error = response.Error
                ?? throw new InvalidDataException(
                    "Python preselection failed without structured error.");
            throw new AnalysisStageException(
                error.Code, error.Category, error.Message, error.Retryable);
        }

        var decision = AnalysisPolicy.ValidateSuggestedDecision(
            result, allowAutomaticReject: false);
        var findings = AnalysisPolicy.ExtractFindings(result);

        await store.PersistPreselectionCompleteAsync(
            job,
            attemptId,
            decision,
            findings,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        return await store.GetPreselectionAsync(job.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "PRESELECTION_COMPLETE was persisted without a Preselection row.");
    }

    private async Task TryMarkInterruptedAsync(IAnalysisStore store, JobId jobId)
    {
        try
        {
            await store.MarkInterruptedAsync(
                jobId,
                $"analysis-interrupted:{Guid.NewGuid():N}",
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A concurrent durable completion may already have moved the Job out of ANALYZING.
        }
    }

    private async Task TryMarkErrorAsync(IAnalysisStore store, JobId jobId, string reason)
    {
        try
        {
            await store.MarkErrorAsync(
                jobId,
                $"analysis-error:{Guid.NewGuid():N}",
                reason,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Preserve any state that already advanced durably.
        }
    }

    private static ResolvedAnalysisInput FrozenInput(AnalysisJobSnapshot job) =>
        new(
            job.AnalysisSourceAssetId,
            job.AnalysisSourceSha256,
            job.AnalysisInputKind,
            job.AnalysisRepresentationPath,
            job.AnalysisInputKind == AnalysisInputKind.RawPreview);

    private static async Task<AnalysisJobSnapshot> RequireJobAsync(
        IAnalysisStore store,
        JobId jobId,
        CancellationToken cancellationToken) =>
        await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Analysis Job {jobId.Value} disappeared.");

    private static void ValidateAnalysisEnvelope(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("schema_version", out var schema) ||
            schema.ValueKind != JsonValueKind.Number ||
            schema.GetInt32() != AnalysisSchemaVersion ||
            !result.TryGetProperty("technical", out var technical) ||
            technical.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("model_executions", out var executions) ||
            executions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Python analysis response does not satisfy Phase 3 schema v1.");
        }
    }

    private static IReadOnlyList<AnalysisModelExecution> ParseModelExecutions(
        JsonElement result)
    {
        var executions = result.GetProperty("model_executions");
        var rows = new List<AnalysisModelExecution>();

        foreach (var item in executions.EnumerateArray())
        {
            var id = item.TryGetProperty("model_id", out var idElement)
                ? idElement.GetString()
                : null;
            var version = item.TryGetProperty("model_version", out var versionElement)
                ? versionElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            {
                throw new InvalidDataException(
                    "Model execution is missing model_id/model_version.");
            }

            string? hash = null;
            if (item.TryGetProperty("artifact_set_sha256", out var hashElement) &&
                hashElement.ValueKind == JsonValueKind.String)
            {
                hash = hashElement.GetString();
                if (!string.IsNullOrWhiteSpace(hash) && hash.Length != 64)
                {
                    throw new InvalidDataException(
                        $"Invalid artifact_set_sha256 for model {id}.");
                }
            }

            var parameters = item.TryGetProperty("parameters", out var parametersElement)
                ? parametersElement.Clone()
                : JsonSerializer.SerializeToElement(new { });
            var timings = item.TryGetProperty("timings", out var timingsElement)
                ? timingsElement.Clone()
                : JsonSerializer.SerializeToElement(new { });

            rows.Add(new(id, version, hash, parameters, timings));
        }

        return rows;
    }

    private static void ValidateCorrelation(string requestId, AiResponse response)
    {
        if (!string.Equals(response.ApiVersion, "v1", StringComparison.Ordinal) ||
            !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Python response correlation mismatch. Expected request_id={requestId}, api_version=v1.");
        }
    }

    private static string SemanticModeName(SemanticMode mode) => mode switch
    {
        SemanticMode.Off => "OFF",
        SemanticMode.Standard => "STANDARD",
        SemanticMode.Full => "FULL",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static string InputKindName(AnalysisInputKind kind) => kind switch
    {
        AnalysisInputKind.JpegCamera => "JPEG_CAMERA",
        AnalysisInputKind.JpegMaster => "JPEG_MASTER",
        AnalysisInputKind.RawPreview => "RAW_PREVIEW",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public sealed class AnalysisStageException(
    string code,
    string category,
    string message,
    bool retryable) : Exception($"{code} [{category}]: {message}")
{
    public string Code { get; } = code;
    public string Category { get; } = category;
    public bool Retryable { get; } = retryable;
}
