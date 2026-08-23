using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Storage;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Domain.Processing;

namespace PhotoAIFactory.Application.Processing;

public sealed class ComfyOrchestrator(
    IComfyStoreFactory comfyStores,
    IProjectStoreFactory projectStores,
    IPythonAiClient python,
    IComfyWorkflowCatalog catalog,
    IComfyWorkflowExecutor executor,
    IComfyHistoryWriter historyWriter,
    IGpuResourceCoordinator gpu,
    RevealExecutionCoordinator executionCoordinator,
    TimeProvider timeProvider,
    ILogger<ComfyOrchestrator> logger,
    IStoragePreflightService? storagePreflight = null,
    ProjectLifecycleService? lifecycleService = null,
    IComponentHealthTracker? healthTracker = null,
    IGpuExecutionPolicy? gpuPolicy = null)
{
    private const int RetryLimit = 2;
    private static readonly EventId StartedEvent = new(4600, "ComfyStarted");
    private static readonly EventId CompletedEvent = new(4601, "ComfyCompleted");
    private static readonly EventId RetryEvent = new(4602, "ComfyRetry");
    private static readonly EventId FailureEvent = new(4699, "ComfyFailed");

    public async Task<ComfyRunResult> ProcessNextAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await using var executionLease =
            await executionCoordinator.AcquireAsync(cancellationToken).ConfigureAwait(false);

        var project = await projectStores.Open(projectId)
            .GetAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Project {projectId.Value} was not found.");

        if (project.Project.State is
            ProjectState.Paused or
            ProjectState.Stopped or
            ProjectState.BlockedStorage or
            ProjectState.ComponentUnhealthy)
            return new(ComfyWorkStatus.NoWork, null, null);

        if (healthTracker is not null && healthTracker.IsStageBlocked("ComfyUI"))
        {
            logger.LogWarning("ComfyUI processing blocked because ComfyUI component is unhealthy.");
            return new(ComfyWorkStatus.NoWork, null, null);
        }

        var store = comfyStores.Open(projectId);
        var job = await store.GetNextEligibleAsync(
            projectId, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return new(ComfyWorkStatus.NoWork, null, null);

        var configVersion = project.ConfigVersions.SingleOrDefault(
            item => string.Equals(
                item.Id,
                job.ProcessingConfigId,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Processing ConfigVersion {job.ProcessingConfigId} was not found.");
        var config = configVersion.ReadConfig();

        if (storagePreflight is not null)
        {
            var inputLength = job.RevealSizeBytes > 0 ? job.RevealSizeBytes : (File.Exists(job.RevealPath) ? new FileInfo(job.RevealPath).Length : 10_000_000L);
            var requiredBytes = storagePreflight.EstimateRequiredBytes(StageName.ComfyUi, inputLength);
            var preflight = storagePreflight.CheckAvailableSpace(config.OutputFolder, requiredBytes);
            if (!preflight.IsSufficient)
            {
                if (lifecycleService is not null)
                {
                    await lifecycleService.EnterBlockedStorageAsync(
                        projectId,
                        $"comfy-preflight:{job.Id.Value}",
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                return new(ComfyWorkStatus.NoWork, null, null);
            }
        }

        var plan = await EnsurePlanAsync(
            store, job, config, cancellationToken).ConfigureAwait(false);
        var validated = ComfyPlanPolicy.Validate(
            plan.Plan,
            config.ComfyUiMode,
            config.AuthorizedComfyUiTasks);
        IReadOnlyList<ComfyTaskDescriptor> approvedTasks;
        try
        {
            approvedTasks = ComfyPlanPolicy.RequireApproved(validated, catalog);
        }
        catch (ComfyStageException ex)
        {
            await BestEffortErrorAsync(store, job.Id, ex.Code).ConfigureAwait(false);
            throw;
        }
        var taskManifest = JsonSerializer.SerializeToElement(
            validated.Decisions.Select(item => new
            {
                task_id = item.TaskId,
                action = item.Action,
                reason = item.Reason
            }),
            ContractJson.Options);

        if (approvedTasks.Count == 0)
        {
            var attemptId = $"comfy-skip:{plan.PlanSha256[..16]}";
            var historyPath = historyWriter.GetHistoryPath(
                config, job.PhotoId, job.Id);
            var artifact = SameAsInput(job);
            await historyWriter.WriteAsync(
                config,
                job,
                configVersion.Sha256,
                plan,
                attemptId,
                "SKIPPED",
                artifact,
                taskManifest,
                historyPath,
                cancellationToken).ConfigureAwait(false);
            await store.PersistCompleteAsync(
                new(
                    job,
                    attemptId,
                    "SKIPPED",
                    artifact,
                    taskManifest,
                    historyPath,
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            var persisted = await store.GetExecutionAsync(
                job.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "COMFYUI_COMPLETE exists without a Comfy execution row.");
            return new(ComfyWorkStatus.Skipped, job.Id, persisted);
        }

        job = await PrepareProcessingStateAsync(
            store, projectId, job, cancellationToken).ConfigureAwait(false);
        var retryCount = job.ComfyRetryCount;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptId = Guid.NewGuid().ToString("N");
            try
            {
                logger.LogInformation(
                    StartedEvent,
                    "ComfyUI started for Job {JobId}",
                    job.Id.Value);

                var historyPath = historyWriter.GetHistoryPath(
                    config, job.PhotoId, job.Id);
                var recovered = await historyWriter.TryReadRecoveryAsync(
                    job, plan, historyPath, cancellationToken).ConfigureAwait(false);

                ComfyExecutionArtifact artifact;
                if (recovered is not null)
                {
                    attemptId = recovered.AttemptId;
                    artifact = recovered.Artifact;
                }
                else
                {
                    if (gpuPolicy is not null)
                    {
                        artifact = await gpuPolicy.ExecuteWithGpuAsync(
                            $"COMFYUI:{job.Id.Value}",
                            async () => await executor.ExecuteApprovedAsync(
                                job,
                                approvedTasks,
                                attemptId,
                                cancellationToken).ConfigureAwait(false),
                            releaseMemory: async () =>
                            {
                                await ReleasePythonModelsAsync(
                                    job.Id, cancellationToken).ConfigureAwait(false);
                            },
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ReleasePythonModelsAsync(
                            job.Id, cancellationToken).ConfigureAwait(false);
                        await using var gpuLease = await gpu.AcquireAsync(
                            $"COMFYUI:{job.Id.Value}",
                            cancellationToken).ConfigureAwait(false);
                        artifact = await executor.ExecuteApprovedAsync(
                            job,
                            approvedTasks,
                            attemptId,
                            cancellationToken).ConfigureAwait(false);
                    }
                    await historyWriter.WriteAsync(
                        config,
                        job,
                        configVersion.Sha256,
                        plan,
                        attemptId,
                        "COMPLETED",
                        artifact,
                        taskManifest,
                        historyPath,
                        cancellationToken).ConfigureAwait(false);
                }

                await store.PersistCompleteAsync(
                    new(
                        job,
                        attemptId,
                        "COMPLETED",
                        artifact,
                        taskManifest,
                        historyPath,
                        timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);
                var persisted = await store.GetExecutionAsync(
                    job.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "COMFYUI_COMPLETE exists without a Comfy execution row.");

                logger.LogInformation(
                    CompletedEvent,
                    "COMFYUI_COMPLETE persisted for Job {JobId}",
                    job.Id.Value);
                return new(ComfyWorkStatus.Completed, job.Id, persisted);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                await BestEffortInterruptedAsync(store, job.Id).ConfigureAwait(false);
                throw;
            }
            catch (ComfyStageException ex)
            {
                var effectiveRetryLimit = string.Equals(
                    ex.Code,
                    "GPU_OOM",
                    StringComparison.Ordinal)
                    ? 1
                    : RetryLimit;
                if (!ex.Retryable || retryCount >= effectiveRetryLimit)
                {
                    await BestEffortErrorAsync(store, job.Id, ex.Code).ConfigureAwait(false);
                    logger.LogError(
                        FailureEvent,
                        ex,
                        "ComfyUI failed for Job {JobId}; code {Code}",
                        job.Id.Value,
                        ex.Code);
                    throw;
                }

                retryCount = await store.ScheduleRetryAsync(
                    job.Id,
                    $"comfy-retry:{Guid.NewGuid():N}",
                    ex.Code,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                if (retryCount < 0)
                    throw new ComfyStageException(
                        "COMFY_RETRY_EXHAUSTED",
                        "runtime",
                        "ComfyUI technical retry limit was exhausted.",
                        false);

                logger.LogWarning(
                    RetryEvent,
                    "Retrying ComfyUI Job {JobId}; retry {Retry}/2",
                    job.Id.Value,
                    retryCount);
                await Task.Delay(
                    retryCount == 1
                        ? TimeSpan.FromSeconds(1)
                        : TimeSpan.FromSeconds(3),
                    cancellationToken).ConfigureAwait(false);

                var resumed = await store.ResumeRetryAsync(
                    job.Id,
                    $"comfy-retry-resume:{Guid.NewGuid():N}",
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                if (!resumed)
                    throw new InvalidOperationException(
                        "ComfyUI retry could not return Job to PROCESSING.");
                job = await store.GetNextEligibleAsync(
                    projectId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "Retried ComfyUI Job disappeared.");
            }
        }
    }

    private async Task<ComfyPlanSnapshot> EnsurePlanAsync(
        IComfyStore store,
        ComfyJobSnapshot job,
        ProjectConfigV1 config,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetPlanAsync(
            job.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            _ = ComfyPlanPolicy.Validate(
                existing.Plan,
                config.ComfyUiMode,
                config.AuthorizedComfyUiTasks);
            return existing;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var requestConfig = JsonSerializer.SerializeToElement(
            new
            {
                schema_version = ComfyPlanPolicy.SchemaVersion,
                mode = config.ComfyUiMode.ToString().ToUpperInvariant(),
                authorized_tasks = config.AuthorizedComfyUiTasks
                    .Select(item => item.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            },
            ContractJson.Options);

        AiResponse response;
        try
        {
            response = await python.ExecuteAsync(
                "/v1/comfy/plan",
                new AiRequest(
                    "v1",
                    requestId,
                    job.Id.Value,
                    "comfy.plan",
                    [job.RevealPath],
                    requestConfig),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            throw new ComfyStageException(
                "PYTHON_COMFY_PLAN_TRANSPORT",
                "transport",
                ex.Message,
                true,
                ex);
        }

        if (!string.Equals(response.ApiVersion, "v1", StringComparison.Ordinal) ||
            !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            throw new ComfyStageException(
                "PYTHON_COMFY_PLAN_CORRELATION_MISMATCH",
                "contract",
                "ComfyPlan response request_id/api_version mismatch.",
                false);

        if (!response.Success || response.Result is not JsonElement result)
        {
            var error = response.Error
                ?? throw new ComfyStageException(
                    "COMFY_PLAN_RESPONSE_INVALID",
                    "contract",
                    "ComfyPlan failed without a structured error.",
                    false);
            throw new ComfyStageException(
                error.Code,
                error.Category,
                error.Message,
                error.Retryable);
        }

        ComfyValidatedPlan validated;
        try
        {
            validated = ComfyPlanPolicy.Validate(
                result,
                config.ComfyUiMode,
                config.AuthorizedComfyUiTasks);
        }
        catch (InvalidDataException ex)
        {
            throw new ComfyStageException(
                "COMFY_PLAN_NOT_AUTHORIZED",
                "contract",
                ex.Message,
                false,
                ex);
        }

        var hash = Sha256(validated.Raw.GetRawText());
        await store.PersistPlanAsync(
            new(
                job.Id,
                validated.SchemaVersion,
                validated.Mode.ToString().ToUpperInvariant(),
                validated.Raw,
                hash,
                timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        return await store.GetPlanAsync(
            job.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Persisted ComfyPlan could not be reloaded.");
    }

    private async Task<ComfyJobSnapshot> PrepareProcessingStateAsync(
        IComfyStore store,
        ProjectId projectId,
        ComfyJobSnapshot job,
        CancellationToken cancellationToken)
    {
        if (job.State == JobState.Processing)
        {
            await store.MarkInterruptedAsync(
                job.Id,
                $"comfy-recovery-discovered:{Guid.NewGuid():N}",
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            job = await store.GetNextEligibleAsync(
                projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Interrupted ComfyUI Job could not be reloaded.");
        }

        var changed = job.State switch
        {
            JobState.Qa => await store.ClaimFromQaAsync(
                job.Id,
                $"comfy-claim:{Guid.NewGuid():N}",
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false),
            JobState.Retrying => await store.ResumeRetryAsync(
                job.Id,
                $"comfy-retry-recovery:{Guid.NewGuid():N}",
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false),
            JobState.Interrupted => await store.ResumeInterruptedAsync(
                job.Id,
                $"comfy-interrupted-recovery:{Guid.NewGuid():N}",
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false),
            JobState.Processing => true,
            _ => false
        };

        if (!changed)
            throw new InvalidOperationException(
                $"ComfyUI Job in state {job.State} could not enter PROCESSING.");

        return await store.GetNextEligibleAsync(
            projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Claimed ComfyUI Job could not be reloaded.");
    }

    private async Task ReleasePythonModelsAsync(
        JobId jobId,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var empty = JsonSerializer.SerializeToElement(
            new { schema_version = 1 },
            ContractJson.Options);
        var response = await python.ExecuteAsync(
            "/v1/models/release",
            new AiRequest(
                "v1",
                requestId,
                jobId.Value,
                "models.release",
                [],
                empty),
            cancellationToken).ConfigureAwait(false);
        if (!response.Success ||
            !string.Equals(response.RequestId, requestId, StringComparison.Ordinal))
            throw new ComfyStageException(
                "PYTHON_MODEL_RELEASE_FAILED",
                "resource",
                "Python models were not released before ComfyUI GPU ownership.",
                true);
    }

    private static ComfyExecutionArtifact SameAsInput(ComfyJobSnapshot job) =>
        new(
            job.RevealPath,
            job.RevealSha256,
            job.RevealSizeBytes,
            JsonSerializer.SerializeToElement(
                new
                {
                    workflow_id = (string?)null,
                    executed = false,
                    reason = "NO_APPROVED_EXECUTION_REQUESTED"
                },
                ContractJson.Options),
            JsonSerializer.SerializeToElement(Array.Empty<string>(), ContractJson.Options));

    private async Task BestEffortInterruptedAsync(IComfyStore store, JobId jobId)
    {
        try
        {
            await store.MarkInterruptedAsync(
                jobId,
                $"comfy-interrupted:{Guid.NewGuid():N}",
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task BestEffortErrorAsync(
        IComfyStore store,
        JobId jobId,
        string reason)
    {
        try
        {
            await store.MarkErrorAsync(
                jobId,
                $"comfy-error:{Guid.NewGuid():N}",
                reason,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();
}
