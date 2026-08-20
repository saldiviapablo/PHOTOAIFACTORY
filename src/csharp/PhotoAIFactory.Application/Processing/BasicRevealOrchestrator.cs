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

public sealed class BasicRevealOrchestrator(
    IProcessingStoreFactory processingStores,
    IProjectStoreFactory projectStores,
    IPythonAiClient python,
    IDarktableRecipeCompiler recipeCompiler,
    IBasicRevealExecutor executor,
    IProcessingHistoryWriter historyWriter,
    RevealExecutionCoordinator executionCoordinator,
    TimeProvider timeProvider,
    ILogger<BasicRevealOrchestrator> logger)
{
    private const int RecipeSchemaVersion = 1;
    private const int RevealRetryLimit = 2;

    private static readonly EventId StartedEvent = new(4100, "BasicRevealStarted");
    private static readonly EventId CompletedEvent = new(4101, "BasicRevealCompleted");
    private static readonly EventId RetryEvent = new(4102, "BasicRevealRetry");
    private static readonly EventId FailureEvent = new(4199, "BasicRevealFailed");

    public async Task<RevealRunResult> ProcessNextAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await using var executionLease = await executionCoordinator.AcquireAsync(
            cancellationToken).ConfigureAwait(false);

        var projectStore = projectStores.Open(projectId);
        var project = await projectStore.GetAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Project {projectId.Value} was not found.");

        var store = processingStores.Open(projectId);
        var job = await store.GetActiveAsync(projectId, cancellationToken).ConfigureAwait(false);

        if (job is not null && project.Project.State is
            ProjectState.Paused or
            ProjectState.Stopped or
            ProjectState.BlockedStorage or
            ProjectState.ComponentUnhealthy)
        {
            // Do not resume a stale active Job while the project is operationally
            // blocked/stopped. Recovery/start logic can make it eligible later.
            return new(RevealWorkStatus.NoWork, null, job.Id);
        }

        if (job is not null)
        {
            // ProcessNextAsync is itself serialized. Therefore a recoverable Job
            // found at method entry belongs to a previous interrupted invocation.
            // Record that durable boundary before resuming.
            if (job.State == JobState.Processing)
            {
                await store.MarkInterruptedAsync(
                    job.Id,
                    $"basic-reveal-recovery-discovered:{Guid.NewGuid():N}",
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);

                job = await store.GetActiveAsync(
                    projectId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "Interrupted recovery Job could not be reloaded.");
            }

            bool resumed = job.State switch
            {
                JobState.Retrying => await store.ResumeRetryAsync(
                    job.Id,
                    $"basic-reveal-retry-recovery:{Guid.NewGuid():N}",
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false),
                JobState.Interrupted => await store.ResumeInterruptedAsync(
                    job.Id,
                    $"basic-reveal-interrupted-recovery:{Guid.NewGuid():N}",
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false),
                JobState.Processing => true,
                _ => throw new InvalidOperationException(
                    $"Unexpected recoverable reveal state {job.State}.")
            };

            if (!resumed)
            {
                throw new InvalidOperationException(
                    "Recoverable reveal Job could not return to PROCESSING.");
            }

            job = await store.GetActiveAsync(
                projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Resumed reveal Job could not be reloaded.");
        }

        if (job is null)
        {
            // Safe pause/stop: only RUNNING may claim a new queue item.
            if (project.Project.State != ProjectState.Running)
            {
                return new(RevealWorkStatus.NoWork, null, null);
            }

            var next = await store.PeekNextQueuedAsync(
                projectId, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                return new(RevealWorkStatus.NoWork, null, null);
            }

            var nextConfig = RequireConfig(project, next.ProcessingConfigId);
            if (nextConfig.ReadConfig().RevealMode == RevealMode.Feedback)
            {
                // FIFO is not bypassed. Phase 5 must own this head Job.
                return new(RevealWorkStatus.DeferredFeedback, null, next.Id);
            }

            var claimed = await store.TryClaimAsync(
                next.Id,
                $"basic-reveal-claim:{Guid.NewGuid():N}",
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (!claimed)
            {
                return new(RevealWorkStatus.NoWork, null, next.Id);
            }

            job = await store.GetActiveAsync(
                projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Claimed reveal Job could not be reloaded.");
        }

        var persisted = await store.GetBasicRevealPassAsync(
            job.Id, cancellationToken).ConfigureAwait(false);
        var checkpoint = await store.HasBasicRevealCheckpointAsync(
            job.Id, cancellationToken).ConfigureAwait(false);
        if (persisted is not null && checkpoint)
        {
            return new(RevealWorkStatus.Completed, persisted, job.Id);
        }

        var configVersion = RequireConfig(project, job.ProcessingConfigId);
        var config = configVersion.ReadConfig();

        if (config.RevealMode == RevealMode.Feedback)
        {
            throw new RevealStageException(
                "FEEDBACK_DEFERRED_TO_PHASE5",
                "capability",
                "A FEEDBACK Job reached the Phase 4 PROCESSING boundary. Phase 5 owns FEEDBACK.",
                false);
        }

        if (!string.Equals(config.ExportFormat, "JPG", StringComparison.Ordinal) &&
            !string.Equals(config.ExportFormat, "JPEG", StringComparison.Ordinal))
        {
            throw new RevealStageException(
                "UNSUPPORTED_EXPORT_FORMAT",
                "configuration",
                $"Phase 4 V1 supports JPEG export, not {config.ExportFormat}.",
                false);
        }

        var retryCount = job.RevealRetryCount;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptId = Guid.NewGuid().ToString("N");

            try
            {
                logger.LogInformation(
                    StartedEvent,
                    "Basic reveal started for Job {JobId} in mode {RevealMode}",
                    job.Id.Value,
                    config.RevealMode);

                JsonElement? recipe = null;
                string? recipeHash = null;

                if (config.RevealMode == RevealMode.PreAi)
                {
                    recipe = await GenerateRecipeAsync(
                        store, job, config, cancellationToken).ConfigureAwait(false);
                    recipeHash = Sha256(recipe.Value.GetRawText());
                }

                var plan = CompilePlan(config.RevealMode, recipe, config);
                var historyPath = historyWriter.GetHistoryPath(
                    config, job.PhotoId, job.Id);
                var recovery = await ReadHistoryRecoveryAsync(
                    config,
                    job,
                    config.RevealMode,
                    recipe,
                    configVersion.Sha256,
                    plan,
                    historyPath,
                    cancellationToken).ConfigureAwait(false);

                BasicRevealArtifact artifact;
                if (recovery is null)
                {
                    artifact = await executor.ExportAsync(
                        projectId,
                        job.Id,
                        attemptId,
                        job,
                        plan,
                        config.ExportQuality,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    attemptId = recovery.AttemptId;
                    artifact = await executor.RecoverAsync(
                        projectId,
                        job.Id,
                        job,
                        recovery,
                        cancellationToken).ConfigureAwait(false);
                }

                var xmpHistoryPath = await WriteHistoryAsync(
                    config,
                    job,
                    config.RevealMode,
                    recipe,
                    configVersion.Sha256,
                    plan,
                    artifact,
                    attemptId,
                    historyPath,
                    cancellationToken).ConfigureAwait(false);

                await PersistCompleteAsync(
                    store,
                    new BasicRevealPersistRequest(
                        job,
                        attemptId,
                        config.RevealMode,
                        recipe,
                        recipe is null ? null : RecipeSchemaVersion,
                        recipeHash,
                        plan,
                        artifact,
                        historyPath,
                        XmpHistoryPath: xmpHistoryPath,
                        timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);

                var pass = await store.GetBasicRevealPassAsync(
                    job.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "BASIC_REVEAL_COMPLETE persisted without a ProcessingPass.");

                logger.LogInformation(
                    CompletedEvent,
                    "BASIC_REVEAL_COMPLETE persisted for Job {JobId}; output SHA {OutputSha}",
                    job.Id.Value,
                    pass.OutputSha256);

                return new(RevealWorkStatus.Completed, pass, job.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await BestEffortInterruptedAsync(store, job.Id).ConfigureAwait(false);
                throw;
            }
            catch (RevealStageException ex)
            {
                if (!ex.Retryable || retryCount >= RevealRetryLimit)
                {
                    await BestEffortErrorAsync(store, job.Id, ex.Code).ConfigureAwait(false);
                    logger.LogError(
                        FailureEvent,
                        ex,
                        "Basic reveal failed for Job {JobId}; code {ErrorCode}; retryable {Retryable}",
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
                    store, job.Id, "BASIC_REVEAL_UNEXPECTED_ERROR").ConfigureAwait(false);
                logger.LogError(
                    FailureEvent,
                    ex,
                    "Basic reveal failed unexpectedly for Job {JobId}",
                    job.Id.Value);
                throw;
            }

            var resumed = await store.ResumeRetryAsync(
                job.Id,
                $"basic-reveal-retry-resume:{Guid.NewGuid():N}",
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (!resumed)
            {
                throw new InvalidOperationException(
                    "Reveal retry could not return Job to PROCESSING.");
            }

            job = await store.GetActiveAsync(
                projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Retried reveal Job disappeared.");
        }
    }

    private async Task<string> WriteHistoryAsync(
        ProjectConfigV1 config,
        BasicRevealJobSnapshot job,
        RevealMode revealMode,
        JsonElement? recipe,
        string processingConfigSha256,
        DarktableControlPlan plan,
        BasicRevealArtifact artifact,
        string attemptId,
        string historyPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await historyWriter.WriteAsync(
                config,
                job,
                revealMode,
                recipe,
                processingConfigSha256,
                plan,
                artifact,
                attemptId,
                historyPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RevealHistoryCollisionException ex)
        {
            throw new RevealStageException(
                "HISTORY_COLLISION",
                "integrity",
                ex.Message,
                false,
                ex);
        }
        catch (IOException ex)
        {
            throw new RevealStageException(
                "HISTORY_WRITE_FAILED",
                "storage",
                ex.Message,
                true,
                ex);
        }
    }

    private async Task<BasicRevealRecovery?> ReadHistoryRecoveryAsync(
        ProjectConfigV1 config,
        BasicRevealJobSnapshot job,
        RevealMode revealMode,
        JsonElement? recipe,
        string processingConfigSha256,
        DarktableControlPlan plan,
        string historyPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await historyWriter.TryReadRecoveryAsync(
                config,
                job,
                revealMode,
                recipe,
                processingConfigSha256,
                plan,
                historyPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RevealHistoryCollisionException ex)
        {
            throw new RevealStageException(
                "HISTORY_COLLISION", "integrity", ex.Message, false, ex);
        }
        catch (IOException ex)
        {
            throw new RevealStageException(
                "HISTORY_READ_FAILED", "storage", ex.Message, true, ex);
        }
    }

    private static async Task PersistCompleteAsync(
        IProcessingStore store,
        BasicRevealPersistRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.PersistBasicRevealCompleteAsync(
                request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
                "BASIC_REVEAL_PERSIST_FAILED",
                "database",
                ex.Message,
                true,
                ex);
        }
    }

    private async Task<JsonElement> GenerateRecipeAsync(
        IProcessingStore store,
        BasicRevealJobSnapshot job,
        ProjectConfigV1 config,
        CancellationToken cancellationToken)
    {
        var analysis = await store.GetAnalysisResultAsync(
            job.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new RevealStageException(
                "ANALYSIS_REQUIRED",
                "state",
                "PRE_AI requires the persisted Phase 3 Analysis.",
                false);

        var requestId = Guid.NewGuid().ToString("N");
        var requestConfig = JsonSerializer.SerializeToElement(
            new
            {
                schema_version = RecipeSchemaVersion,
                benchmark_status = "NOT_CALIBRATED",
                analysis = analysis.Clone(),
                authorized_preset_profiles = config.PresetProfiles,
                policy = new
                {
                    arbitrary_darktable_parameters = false,
                    xmp_compilation = "WHITELIST_ONLY",
                    creative_thresholds = "BENCHMARK_PENDING"
                }
            },
            ContractJson.Options);

        AiResponse response;
        try
        {
            response = await python.ExecuteAsync(
                "/v1/recipe/pre-ai",
                new AiRequest(
                    "v1",
                    requestId,
                    job.Id.Value,
                    "recipe.pre-ai",
                    [job.InputPath],
                    requestConfig),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            throw new RevealStageException(
                "PYTHON_PRE_AI_TRANSPORT",
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
                "PRE_AI response request_id/api_version mismatch.",
                false);
        }

        if (!response.Success || response.Result is not JsonElement result)
        {
            var error = response.Error
                ?? throw new RevealStageException(
                    "PRE_AI_RESPONSE_INVALID",
                    "contract",
                    "PRE_AI failed without a structured error.",
                    false);

            throw new RevealStageException(
                error.Code,
                error.Category,
                error.Message,
                error.Retryable);
        }

        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new RevealStageException(
                "PRE_AI_RESPONSE_INVALID",
                "contract",
                "PRE_AI result must be a structured JSON object.",
                false);
        }

        return result.Clone();
    }

    private DarktableControlPlan CompilePlan(
        RevealMode revealMode,
        JsonElement? recipe,
        ProjectConfigV1 config)
    {
        try
        {
            return recipeCompiler.Compile(revealMode, recipe, config);
        }
        catch (InvalidDataException ex)
        {
            throw new RevealStageException(
                "RECIPE_NOT_AUTHORIZED",
                "contract",
                ex.Message,
                false,
                ex);
        }
        catch (NotSupportedException ex)
        {
            throw new RevealStageException(
                "REVEAL_MODE_NOT_SUPPORTED",
                "capability",
                ex.Message,
                false,
                ex);
        }
    }

    private async Task<int> ScheduleRetryAsync(
        IProcessingStore store,
        JobId jobId,
        string reason,
        int currentRetryCount,
        CancellationToken cancellationToken)
    {
        var count = await store.ScheduleRevealRetryAsync(
            jobId,
            $"basic-reveal-retry:{Guid.NewGuid():N}",
            reason,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        if (count < 0)
        {
            throw new RevealStageException(
                "BASIC_REVEAL_RETRY_EXHAUSTED",
                "runtime",
                "Reveal retry limit was exhausted.",
                false);
        }

        logger.LogWarning(
            RetryEvent,
            "Retrying basic reveal Job {JobId}; retry {Retry}/2 after {Reason}",
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

    private async Task BestEffortInterruptedAsync(
        IProcessingStore store,
        JobId jobId)
    {
        try
        {
            await store.MarkInterruptedAsync(
                jobId,
                $"basic-reveal-interrupted:{Guid.NewGuid():N}",
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                FailureEvent,
                ex,
                "Failed to persist reveal interruption for Job {JobId}",
                jobId.Value);
        }
    }

    private async Task BestEffortErrorAsync(
        IProcessingStore store,
        JobId jobId,
        string reason)
    {
        try
        {
            await store.MarkErrorAsync(
                jobId,
                $"basic-reveal-error:{Guid.NewGuid():N}",
                reason,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                FailureEvent,
                ex,
                "Failed to persist reveal error for Job {JobId}",
                jobId.Value);
        }
    }

    private static ConfigVersion RequireConfig(
        ProjectSnapshot project,
        string configId) =>
        project.ConfigVersions.SingleOrDefault(
            item => string.Equals(item.Id, configId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"Processing ConfigVersion {configId} was not found.");

    private static string Sha256(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
