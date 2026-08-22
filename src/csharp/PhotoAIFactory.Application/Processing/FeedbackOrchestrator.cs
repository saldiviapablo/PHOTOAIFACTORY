using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Processing;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.Processing;

public sealed class FeedbackOrchestrator(
    IFeedbackStoreFactory feedbackStores,
    IProjectStoreFactory projectStores,
    IPythonAiClient python,
    IDarktableFeedbackExecutor executor,
    IFeedbackHistoryWriter historyWriter,
    RevealExecutionCoordinator executionCoordinator,
    TimeProvider timeProvider,
    ILogger<FeedbackOrchestrator> logger)
{
    private const int RetryLimit = 2;
    private static readonly EventId StartedEvent =
        new(4300, "FeedbackStarted");
    private static readonly EventId CompletedEvent =
        new(4301, "FeedbackCompleted");
    private static readonly EventId RetryEvent =
        new(4302, "FeedbackRetry");
    private static readonly EventId CleanupEvent =
        new(4303, "FeedbackCleanupDeferred");
    private static readonly EventId FailureEvent =
        new(4399, "FeedbackFailed");

    public async Task<FeedbackRunResult> ProcessNextAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await using var executionLease =
            await executionCoordinator.AcquireAsync(cancellationToken)
                .ConfigureAwait(false);

        var projectStore = projectStores.Open(projectId);
        var project = await projectStore.GetAsync(projectId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Project {projectId.Value} was not found.");

        var store = feedbackStores.Open(projectId);
        var job = await store.GetActiveAsync(projectId, cancellationToken)
            .ConfigureAwait(false);

        if (job is not null)
        {
            var activeConfig = RequireConfig(
                project, job.ProcessingConfigId).ReadConfig();
            if (activeConfig.RevealMode != RevealMode.Feedback)
                return new(FeedbackWorkStatus.NoWork, null, job.Id);

            if (project.Project.State is
                ProjectState.Paused or
                ProjectState.Stopped or
                ProjectState.BlockedStorage or
                ProjectState.ComponentUnhealthy)
            {
                return new(FeedbackWorkStatus.NoWork, null, job.Id);
            }

            if (job.State == JobState.Processing)
            {
                await store.MarkInterruptedAsync(
                    job.Id,
                    $"feedback-recovery-discovered:{Guid.NewGuid():N}",
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);

                job = await store.GetActiveAsync(projectId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "Interrupted FEEDBACK Job could not be reloaded.");
            }

            var resumed = job.State switch
            {
                JobState.Retrying => await store.ResumeRetryAsync(
                    job.Id,
                    $"feedback-retry-recovery:{Guid.NewGuid():N}",
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false),
                JobState.Interrupted => await store.ResumeInterruptedAsync(
                    job.Id,
                    $"feedback-interrupted-recovery:{Guid.NewGuid():N}",
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false),
                JobState.Processing => true,
                _ => throw new InvalidOperationException(
                    $"Unexpected recoverable FEEDBACK state {job.State}.")
            };

            if (!resumed)
                throw new InvalidOperationException(
                    "Recoverable FEEDBACK Job could not return to PROCESSING.");

            job = await store.GetActiveAsync(projectId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Resumed FEEDBACK Job could not be reloaded.");
        }

        if (job is null)
        {
            if (project.Project.State != ProjectState.Running)
                return new(FeedbackWorkStatus.NoWork, null, null);

            var next = await store.PeekNextQueuedAsync(
                projectId, cancellationToken).ConfigureAwait(false);
            if (next is null)
                return new(FeedbackWorkStatus.NoWork, null, null);

            var nextConfig = RequireConfig(
                project, next.ProcessingConfigId).ReadConfig();
            if (nextConfig.RevealMode != RevealMode.Feedback)
                return new(FeedbackWorkStatus.NoWork, null, next.Id);

            if (!await store.TryClaimAsync(
                    next.Id,
                    $"feedback-claim:{Guid.NewGuid():N}",
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false))
            {
                return new(FeedbackWorkStatus.NoWork, null, next.Id);
            }

            job = await store.GetActiveAsync(projectId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Claimed FEEDBACK Job could not be reloaded.");
        }

        var configVersion = RequireConfig(project, job.ProcessingConfigId);
        var config = configVersion.ReadConfig();
        if (config.RevealMode != RevealMode.Feedback)
            return new(FeedbackWorkStatus.NoWork, null, job.Id);

        if (!string.Equals(config.ExportFormat, "JPG", StringComparison.Ordinal) &&
            !string.Equals(config.ExportFormat, "JPEG", StringComparison.Ordinal))
        {
            await BestEffortErrorAsync(
                store,
                job.Id,
                "UNSUPPORTED_EXPORT_FORMAT").ConfigureAwait(false);
            throw new RevealStageException(
                "UNSUPPORTED_EXPORT_FORMAT",
                "configuration",
                $"FEEDBACK V1 supports JPEG final staging, not {config.ExportFormat}.",
                false);
        }

        var retryCount = job.RevealRetryCount;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                logger.LogInformation(
                    StartedEvent,
                    "FEEDBACK started for Job {JobId}",
                    job.Id.Value);

                var pass1 = await EnsurePass1Async(
                    store,
                    projectId,
                    job,
                    config,
                    cancellationToken).ConfigureAwait(false);

                var inspection = await EnsureInspectionAsync(
                    store,
                    job,
                    pass1,
                    cancellationToken).ConfigureAwait(false);

                FeedbackRecipePolicy.Validate(inspection.Recipe);

                var existingPass2 = await store.GetPassAsync(
                    job.Id, 2, cancellationToken).ConfigureAwait(false);
                if (existingPass2 is not null &&
                    await store.HasCheckpointAsync(
                        job.Id,
                        "DARKTABLE_PASS2_COMPLETE",
                        cancellationToken).ConfigureAwait(false))
                {
                    await BestEffortPass1CleanupAsync(pass1).ConfigureAwait(false);
                    return new(
                        FeedbackWorkStatus.Completed,
                        existingPass2,
                        job.Id);
                }

                var historyPath = historyWriter.GetHistoryPath(
                    config, job.PhotoId, job.Id);
                var recovery = await ReadRecoveryAsync(
                    config,
                    job,
                    configVersion.Sha256,
                    pass1,
                    inspection,
                    historyPath,
                    cancellationToken).ConfigureAwait(false);

                string pass2AttemptId;
                FeedbackImageArtifact pass2Artifact;
                string pass2XmpPath;

                if (recovery is null)
                {
                    pass2AttemptId = Guid.NewGuid().ToString("N");
                    pass2Artifact = await executor.ExportPass2Async(
                        projectId,
                        job.Id,
                        pass2AttemptId,
                        job,
                        pass1,
                        config.ExportQuality,
                        cancellationToken).ConfigureAwait(false);

                    pass2XmpPath = await WriteXmpAsync(
                        config,
                        job,
                        2,
                        pass2Artifact.AuthenticXmp,
                        cancellationToken).ConfigureAwait(false);

                    await WriteFinalHistoryAsync(
                        config,
                        job,
                        configVersion.Sha256,
                        pass1,
                        inspection,
                        pass2Artifact,
                        pass2AttemptId,
                        pass2XmpPath,
                        historyPath,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    pass2AttemptId = recovery.AttemptId;
                    pass2XmpPath = recovery.XmpPath;
                    pass2Artifact = await executor.RecoverPass2Async(
                        job,
                        recovery,
                        cancellationToken).ConfigureAwait(false);
                }

                var pass2Control = JsonSerializer.SerializeToElement(
                    new
                    {
                        policy_id = "phase5-feedback-pass2-v1",
                        source = string.Equals(
                            job.InputFormat,
                            "RAW",
                            StringComparison.OrdinalIgnoreCase)
                            ? "MANAGED_RAW_ORIGINAL"
                            : "MANAGED_JPEG_ORIGINAL",
                        xmp_path = pass1.XmpPath,
                        xmp_sha256 = pass1.XmpSha256,
                        restart_from_managed_original = true,
                        pass1_derivative_as_source = false,
                        apply_custom_presets = false,
                        neural_restore =
                            FeedbackRecipePolicy.NeuralRestoreDisabledReason
                    },
                    ContractJson.Options);

                await PersistPass2Async(
                    store,
                    new FeedbackPersistPass2Request(
                        job,
                        pass2AttemptId,
                        pass2Artifact,
                        pass2XmpPath,
                        Sha256(pass2Artifact.AuthenticXmp),
                        historyPath,
                        pass2Control,
                        timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);

                var persisted = await store.GetPassAsync(
                    job.Id, 2, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "DARKTABLE_PASS2_COMPLETE persisted without a FEEDBACK Pass 2 row.");

                await BestEffortPass1CleanupAsync(pass1).ConfigureAwait(false);

                logger.LogInformation(
                    CompletedEvent,
                    "DARKTABLE_PASS2_COMPLETE persisted for FEEDBACK Job {JobId}; output SHA {OutputSha}",
                    job.Id.Value,
                    persisted.ImageSha256);

                return new(
                    FeedbackWorkStatus.Completed,
                    persisted,
                    job.Id);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                await BestEffortInterruptedAsync(store, job.Id)
                    .ConfigureAwait(false);
                throw;
            }
            catch (RevealStageException ex)
            {
                if (!ex.Retryable || retryCount >= RetryLimit)
                {
                    await BestEffortErrorAsync(store, job.Id, ex.Code)
                        .ConfigureAwait(false);
                    logger.LogError(
                        FailureEvent,
                        ex,
                        "FEEDBACK failed for Job {JobId}; code {ErrorCode}; retryable {Retryable}",
                        job.Id.Value,
                        ex.Code,
                        ex.Retryable);
                    throw;
                }

                retryCount = await ScheduleRetryAsync(
                    store,
                    job.Id,
                    ex.Code,
                    retryCount,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await BestEffortErrorAsync(
                    store,
                    job.Id,
                    "FEEDBACK_UNEXPECTED_ERROR").ConfigureAwait(false);
                logger.LogError(
                    FailureEvent,
                    ex,
                    "FEEDBACK failed unexpectedly for Job {JobId}",
                    job.Id.Value);
                throw;
            }

            var resumed = await store.ResumeRetryAsync(
                job.Id,
                $"feedback-retry-resume:{Guid.NewGuid():N}",
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (!resumed)
                throw new InvalidOperationException(
                    "FEEDBACK retry could not return Job to PROCESSING.");

            job = await store.GetActiveAsync(
                projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Retried FEEDBACK Job disappeared.");
        }
    }

    private async Task<FeedbackPassSnapshot> EnsurePass1Async(
        IFeedbackStore store,
        ProjectId projectId,
        FeedbackJobSnapshot job,
        ProjectConfigV1 config,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetPassAsync(
            job.Id, 1, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!await store.HasCheckpointAsync(
                    job.Id,
                    "DARKTABLE_PASS1_COMPLETE",
                    cancellationToken).ConfigureAwait(false))
            {
                throw new RevealStageException(
                    "FEEDBACK_PASS1_ROW_WITHOUT_CHECKPOINT",
                    "integrity",
                    "Pass 1 row exists without its durable checkpoint.",
                    false);
            }

            await executor.ValidatePersistedPass1Async(
                job, existing, cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var attemptId = Guid.NewGuid().ToString("N");
        var artifact = await executor.ExportPass1Async(
            projectId,
            job.Id,
            attemptId,
            job,
            cancellationToken).ConfigureAwait(false);

        var xmpPath = await WriteXmpAsync(
            config,
            job,
            1,
            artifact.AuthenticXmp,
            cancellationToken).ConfigureAwait(false);
        var xmpSha = Sha256(artifact.AuthenticXmp);

        var plan = JsonSerializer.SerializeToElement(
            new
            {
                policy_id = "phase5-feedback-pass1-v1",
                mode = "DEFAULT_PIPELINE_PROPOSAL",
                output = "TIFF_RGB_16",
                icc_type = "SRGB",
                high_quality = true,
                apply_custom_presets = false,
                style = (string?)null,
                neural_restore =
                    FeedbackRecipePolicy.NeuralRestoreDisabledReason,
                arbitrary_xmp_compilation = false
            },
            ContractJson.Options);

        await PersistPass1Async(
            store,
            new FeedbackPersistPass1Request(
                job,
                attemptId,
                artifact,
                xmpPath,
                xmpSha,
                plan,
                timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        return await store.GetPassAsync(
            job.Id, 1, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "DARKTABLE_PASS1_COMPLETE persisted without a FEEDBACK Pass 1 row.");
    }

    private async Task<FeedbackInspectionSnapshot> EnsureInspectionAsync(
        IFeedbackStore store,
        FeedbackJobSnapshot job,
        FeedbackPassSnapshot pass1,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetInspectionAsync(
            job.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!await store.HasCheckpointAsync(
                    job.Id,
                    "FEEDBACK_INSPECTION_COMPLETE",
                    cancellationToken).ConfigureAwait(false))
            {
                throw new RevealStageException(
                    "FEEDBACK_INSPECTION_WITHOUT_CHECKPOINT",
                    "integrity",
                    "FEEDBACK inspection row exists without its durable checkpoint.",
                    false);
            }

            FeedbackRecipePolicy.Validate(existing.Recipe);
            return existing;
        }

        var analysis = await store.GetAnalysisResultAsync(
            job.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new RevealStageException(
                "ANALYSIS_REQUIRED",
                "state",
                "FEEDBACK inspection requires persisted Phase 3 Analysis.",
                false);

        var requestId = Guid.NewGuid().ToString("N");
        var inputKind = string.Equals(
            job.InputFormat, "RAW", StringComparison.OrdinalIgnoreCase)
            ? "RAW"
            : "JPEG";

        var requestConfig = JsonSerializer.SerializeToElement(
            new
            {
                schema_version = FeedbackRecipePolicy.SchemaVersion,
                analysis = analysis.Clone(),
                input_kind = inputKind,
                raw_support_status = job.RawSupportStatus,
                pass1 = new
                {
                    darktable_version = pass1.DarktableVersion,
                    image_sha256 = pass1.ImageSha256,
                    xmp_sha256 = pass1.XmpSha256,
                    width = pass1.ImageWidth,
                    height = pass1.ImageHeight,
                    bits_per_sample = pass1.BitsPerSample,
                    channels = pass1.Channels
                },
                policy = new
                {
                    creative_thresholds = "BENCHMARK_PENDING",
                    arbitrary_xmp_compilation = false,
                    darktable_neural_restore =
                        "NOT_HEADLESS_PROVEN_AND_BENCHMARK_PENDING",
                    pass2_source =
                        "RESTART_FROM_IMMUTABLE_MANAGED_ORIGINAL"
                }
            },
            ContractJson.Options);

        AiResponse response;
        try
        {
            response = await python.ExecuteAsync(
                "/v1/feedback/inspect",
                new AiRequest(
                    "v1",
                    requestId,
                    job.Id.Value,
                    "feedback.inspect",
                    [pass1.ImagePath],
                    requestConfig),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex is
                HttpRequestException or
                TimeoutException or
                TaskCanceledException)
        {
            throw new RevealStageException(
                "PYTHON_FEEDBACK_TRANSPORT",
                "transport",
                ex.Message,
                true,
                ex);
        }

        if (!string.Equals(response.ApiVersion, "v1", StringComparison.Ordinal) ||
            !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
        {
            throw new RevealStageException(
                "PYTHON_CORRELATION_MISMATCH",
                "contract",
                "FEEDBACK response request_id/api_version mismatch.",
                false);
        }

        if (!response.Success || response.Result is not JsonElement result)
        {
            var error = response.Error
                ?? throw new RevealStageException(
                    "FEEDBACK_RESPONSE_INVALID",
                    "contract",
                    "FEEDBACK failed without a structured error.",
                    false);
            throw new RevealStageException(
                error.Code,
                error.Category,
                error.Message,
                error.Retryable);
        }

        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("recipe", out var recipe) ||
            !result.TryGetProperty("inspection", out var inspection) ||
            recipe.ValueKind != JsonValueKind.Object ||
            inspection.ValueKind != JsonValueKind.Object)
        {
            throw new RevealStageException(
                "FEEDBACK_RESPONSE_INVALID",
                "contract",
                "FEEDBACK result must contain recipe and inspection objects.",
                false);
        }

        var recipeClone = recipe.Clone();
        var inspectionClone = inspection.Clone();
        try
        {
            FeedbackRecipePolicy.Validate(recipeClone);
        }
        catch (InvalidDataException ex)
        {
            throw new RevealStageException(
                "FEEDBACK_RECIPE_NOT_AUTHORIZED",
                "contract",
                ex.Message,
                false,
                ex);
        }

        var recipeHash = Sha256(recipeClone.GetRawText());
        await PersistInspectionAsync(
            store,
            new FeedbackPersistInspectionRequest(
                job,
                FeedbackRecipePolicy.SchemaVersion,
                recipeClone,
                recipeHash,
                inspectionClone,
                timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        return await store.GetInspectionAsync(
            job.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "FEEDBACK_INSPECTION_COMPLETE persisted without an inspection row.");
    }

    private async Task<string> WriteXmpAsync(
        ProjectConfigV1 config,
        FeedbackJobSnapshot job,
        int passNumber,
        byte[] packet,
        CancellationToken cancellationToken)
    {
        try
        {
            return await historyWriter.WriteXmpImmutableAsync(
                config,
                job.PhotoId,
                job.Id,
                passNumber,
                packet,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RevealHistoryCollisionException ex)
        {
            throw new RevealStageException(
                "FEEDBACK_XMP_COLLISION",
                "integrity",
                ex.Message,
                false,
                ex);
        }
        catch (IOException ex)
        {
            throw new RevealStageException(
                "FEEDBACK_XMP_WRITE_FAILED",
                "storage",
                ex.Message,
                true,
                ex);
        }
    }

    private async Task WriteFinalHistoryAsync(
        ProjectConfigV1 config,
        FeedbackJobSnapshot job,
        string processingConfigSha256,
        FeedbackPassSnapshot pass1,
        FeedbackInspectionSnapshot inspection,
        FeedbackImageArtifact pass2,
        string pass2AttemptId,
        string pass2XmpPath,
        string historyPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await historyWriter.WriteFinalAsync(
                config,
                job,
                processingConfigSha256,
                pass1,
                inspection,
                pass2,
                pass2AttemptId,
                pass2XmpPath,
                historyPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RevealHistoryCollisionException ex)
        {
            throw new RevealStageException(
                "FEEDBACK_HISTORY_COLLISION",
                "integrity",
                ex.Message,
                false,
                ex);
        }
        catch (IOException ex)
        {
            throw new RevealStageException(
                "FEEDBACK_HISTORY_WRITE_FAILED",
                "storage",
                ex.Message,
                true,
                ex);
        }
    }

    private async Task<FeedbackPass2Recovery?> ReadRecoveryAsync(
        ProjectConfigV1 config,
        FeedbackJobSnapshot job,
        string processingConfigSha256,
        FeedbackPassSnapshot pass1,
        FeedbackInspectionSnapshot inspection,
        string historyPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await historyWriter.TryReadPass2RecoveryAsync(
                config,
                job,
                processingConfigSha256,
                pass1,
                inspection,
                historyPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException)
        {
            throw new RevealStageException(
                "FEEDBACK_HISTORY_READ_FAILED",
                "integrity",
                ex.Message,
                false,
                ex);
        }
    }

    private static async Task PersistPass2Async(
        IFeedbackStore store,
        FeedbackPersistPass2Request request,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.PersistPass2CompleteAsync(
                request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RevealStageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RevealStageException(
                "FEEDBACK_PASS2_PERSIST_FAILED",
                "database",
                ex.Message,
                true,
                ex);
        }
    }

    private static async Task PersistPass1Async(
        IFeedbackStore store,
        FeedbackPersistPass1Request request,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.PersistPass1CompleteAsync(
                request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RevealStageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RevealStageException(
                "FEEDBACK_PASS1_PERSIST_FAILED",
                "database",
                ex.Message,
                true,
                ex);
        }
    }

    private static async Task PersistInspectionAsync(
        IFeedbackStore store,
        FeedbackPersistInspectionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.PersistInspectionCompleteAsync(
                request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RevealStageException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RevealStageException(
                "FEEDBACK_INSPECTION_PERSIST_FAILED",
                "database",
                ex.Message,
                true,
                ex);
        }
    }

    private async Task<int> ScheduleRetryAsync(
        IFeedbackStore store,
        JobId jobId,
        string reason,
        int currentRetryCount,
        CancellationToken cancellationToken)
    {
        var count = await store.ScheduleRetryAsync(
            jobId,
            $"feedback-retry:{Guid.NewGuid():N}",
            reason,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        if (count < 0)
        {
            throw new RevealStageException(
                "FEEDBACK_RETRY_EXHAUSTED",
                "runtime",
                "FEEDBACK retry limit was exhausted.",
                false);
        }

        logger.LogWarning(
            RetryEvent,
            "Retrying FEEDBACK Job {JobId}; retry {Retry}/2 after {Reason}",
            jobId.Value,
            count,
            reason);

        await Task.Delay(
            currentRetryCount == 0
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);

        return count;
    }

    private async Task BestEffortPass1CleanupAsync(
        FeedbackPassSnapshot pass1)
    {
        try
        {
            await executor.CleanupPass1TemporaryAsync(
                pass1, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                CleanupEvent,
                ex,
                "Pass 1 TIFF cleanup deferred for Job {JobId}; durable history remains intact",
                pass1.JobId.Value);
        }
    }

    private async Task BestEffortInterruptedAsync(
        IFeedbackStore store,
        JobId jobId)
    {
        try
        {
            await store.MarkInterruptedAsync(
                jobId,
                $"feedback-interrupted:{Guid.NewGuid():N}",
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                FailureEvent,
                ex,
                "Failed to persist FEEDBACK interruption for Job {JobId}",
                jobId.Value);
        }
    }

    private async Task BestEffortErrorAsync(
        IFeedbackStore store,
        JobId jobId,
        string reason)
    {
        try
        {
            await store.MarkErrorAsync(
                jobId,
                $"feedback-error:{Guid.NewGuid():N}",
                reason,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                FailureEvent,
                ex,
                "Failed to persist FEEDBACK error for Job {JobId}",
                jobId.Value);
        }
    }

    private static ConfigVersion RequireConfig(
        ProjectSnapshot project,
        string configId) =>
        project.ConfigVersions.SingleOrDefault(
            item => string.Equals(
                item.Id, configId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"Processing ConfigVersion {configId} was not found.");

    private static string Sha256(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Sha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value))
            .ToLowerInvariant();
}
