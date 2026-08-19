using System.Diagnostics;
using System.Text.Json;
using PhotoAIFactory.Gpu01;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PhotoAIFactory.Gpu01 <project-root> <output-root>");
    return 2;
}

var projectRoot = Path.GetFullPath(args[0]);
var outputRoot = Path.GetFullPath(args[1]);
var workRoot = Path.Combine(outputRoot, "WORK");
var logRoot = Path.Combine(outputRoot, "LOGS");
var reportRoot = Path.Combine(outputRoot, "REPORT");
var resultPath = Path.Combine(workRoot, "gpu01_results.json");
var logPath = Path.Combine(logRoot, "gpu01.jsonl");
var pocRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

foreach (var directory in new[] { outputRoot, workRoot, logRoot, reportRoot }) Directory.CreateDirectory(directory);

var aiPython = @"C:\Users\Pc\AppData\Local\PhotoAIFactory\runtimes\ai-worker\Scripts\python.exe";
var modelRoot = @"C:\Users\Pc\AppData\Local\PhotoAIFactory\models\qwen3-vl-2b-instruct-fp8";
var comfyRoot = @"C:\Users\Pc\AppData\Local\PhotoAIFactory\components\comfyui";
var comfyPython = Path.Combine(comfyRoot, "python_embeded", "python.exe");
var comfySource = Path.Combine(comfyRoot, "ComfyUI");
var comfyMain = Path.Combine(comfySource, "main.py");
var workerPath = Path.Combine(pocRoot, "gpu_worker.py");
var instrumentationSource = Path.Combine(pocRoot, "instrumentation", "paf_gpu01", "__init__.py");
var comfyBase = Path.Combine(workRoot, "COMFY_BASE");
var instrumentationDestination = Path.Combine(comfyBase, "custom_nodes", "paf_gpu01", "__init__.py");

foreach (var required in new[] { aiPython, comfyPython, comfyMain, workerPath, instrumentationSource, Path.Combine(modelRoot, "config.json") })
    if (!File.Exists(required)) throw new FileNotFoundException("GPU-01 prerequisite missing", required);
Directory.CreateDirectory(Path.GetDirectoryName(instrumentationDestination)!);
File.Copy(instrumentationSource, instrumentationDestination, overwrite: true);

using var nvml = new NvmlMonitor();
using var log = new GpuLog(logPath);
using var coordinator = new GpuLeaseCoordinator(nvml, log);
var checks = new List<CheckResult>();
var ownedPids = new List<int>();
GpuWorkerClient? worker = null;
GpuWorkerClient? replacementWorker = null;
ComfyGpuSupervisor? comfy = null;

async Task CheckAsync(string name, Func<Task<object?>> action)
{
    var timer = Stopwatch.StartNew();
    try
    {
        var detail = await action();
        checks.Add(new CheckResult(name, true, timer.ElapsedMilliseconds, detail, null));
        log.Write("GATE", null, name, durationMs: timer.ElapsedMilliseconds, memory: nvml.Snapshot(), details: detail);
        Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)");
    }
    catch (Exception ex)
    {
        var code = ex switch { GpuLeaseException lease => lease.Code, OperationCanceledException => "GPU_LEASE_CANCELLED", _ => ex.GetType().Name.ToUpperInvariant() };
        checks.Add(new CheckResult(name, false, timer.ElapsedMilliseconds, null, new CheckError(code, ex.Message, ex.GetType().Name)));
        log.Write("GATE", null, name, durationMs: timer.ElapsedMilliseconds, memory: nvml.Snapshot(), errorCode: code,
            details: new { exception = ex.GetType().Name, ex.Message });
        Console.WriteLine($"FAIL {name}: {code} {ex.Message}");
    }
}

try
{
    await CheckAsync("nvml_and_clean_baseline", () =>
    {
        var memory = nvml.Snapshot();
        var matching = FindMatchingPids(aiPython, comfyPython);
        Require(matching.Count == 0, $"Heavy runtime processes active before gate: {string.Join(',', matching)}");
        return Task.FromResult<object?>(new { gpu = nvml.DeviceName, driver = nvml.DriverVersion, memory, active_heavy_pids = matching, source = "NVML" });
    });

    await CheckAsync("exclusive_lease_python_then_comfy", async () =>
    {
        var first = await coordinator.AcquireAsync("PYTHON_AI", 0, TimeSpan.FromSeconds(2));
        var waiting = coordinator.AcquireAsync("COMFYUI", 0, TimeSpan.FromSeconds(2));
        await Task.Delay(120);
        Require(!waiting.IsCompleted, "COMFYUI lease did not wait behind PYTHON_AI");
        await first.DisposeAsync();
        await using var second = await waiting;
        Require(coordinator.CurrentOwner == "COMFYUI", "COMFYUI did not become owner");
        return new { first = "PYTHON_AI", second = "COMFYUI", blocked_ms = 120, overlap = false };
    });

    await CheckAsync("exclusive_lease_comfy_then_python", async () =>
    {
        var first = await coordinator.AcquireAsync("COMFYUI", 0, TimeSpan.FromSeconds(2));
        var waiting = coordinator.AcquireAsync("PYTHON_AI", 0, TimeSpan.FromSeconds(2));
        await Task.Delay(120);
        Require(!waiting.IsCompleted, "PYTHON_AI lease did not wait behind COMFYUI");
        await first.DisposeAsync();
        await using var second = await waiting;
        Require(coordinator.CurrentOwner == "PYTHON_AI", "PYTHON_AI did not become owner");
        return new { first = "COMFYUI", second = "PYTHON_AI", blocked_ms = 120, overlap = false };
    });

    await CheckAsync("wait_cancellation_preserves_owner", async () =>
    {
        await using var held = await coordinator.AcquireAsync("PYTHON_AI", 0, TimeSpan.FromSeconds(2));
        using var cancel = new CancellationTokenSource(150);
        var cancelled = false;
        try { await coordinator.AcquireAsync("COMFYUI", 0, TimeSpan.FromSeconds(5), cancellationToken: cancel.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "Waiting request was not cancelled");
        Require(coordinator.CurrentOwner == "PYTHON_AI", "Cancellation disturbed the active owner");
        return new { cancelled, active_owner = coordinator.CurrentOwner };
    });

    await CheckAsync("lease_timeout", async () =>
    {
        await using var held = await coordinator.AcquireAsync("COMFYUI", 0, TimeSpan.FromSeconds(2));
        string? code = null;
        try { await coordinator.AcquireAsync("PYTHON_AI", 0, TimeSpan.FromMilliseconds(250)); }
        catch (GpuLeaseException ex) { code = ex.Code; }
        Require(code == "GPU_LEASE_TIMEOUT", $"Expected GPU_LEASE_TIMEOUT, got {code}");
        Require(coordinator.CurrentOwner == "COMFYUI", "Timeout disturbed the active owner");
        return new { error_code = code, active_owner = coordinator.CurrentOwner };
    });

    await CheckAsync("memory_preflight_rejection", async () =>
    {
        var before = nvml.Snapshot(); string? code = null;
        try { await coordinator.AcquireAsync("PYTHON_AI", before.FreeMb + 512, TimeSpan.FromSeconds(1)); }
        catch (GpuLeaseException ex) { code = ex.Code; }
        var after = nvml.Snapshot();
        Require(code == "GPU_INSUFFICIENT_MEMORY", $"Expected GPU_INSUFFICIENT_MEMORY, got {code}");
        Require(coordinator.CurrentOwner is null, "Rejected memory preflight left a lease owner");
        return new { error_code = code, requested_free_mb = before.FreeMb + 512, before, after, allocation_attempted = false };
    });

    await CheckAsync("darktable_ai_lease_classification", async () =>
    {
        await using var lease = await coordinator.AcquireAsync("DARKTABLE_AI", 0, TimeSpan.FromSeconds(2));
        Require(coordinator.CurrentOwner == "DARKTABLE_AI", "DARKTABLE_AI did not acquire lease");
        return new { lease_architecture = "LEASE_ARCHITECTURE_READY", neural_headless = "NOT_HEADLESS_PROVEN", neural_execution_attempted = false };
    });

    worker = new GpuWorkerClient(aiPython, workerPath, modelRoot, log, allowKernelNetwork: false);
    ownedPids.Add(worker.ProcessId);
    await CheckAsync("isolated_python_health", async () =>
    {
        var response = await worker.CommandAsync("health");
        return new { python_path = aiPython, process_id = worker.ProcessId, response = response.GetProperty("result") };
    });

    await CheckAsync("qwen_fp8_real_load_and_inference", async () =>
    {
        var before = nvml.Snapshot();
        await using var lease = await coordinator.AcquireAsync("PYTHON_AI", 3800, TimeSpan.FromSeconds(5), worker.Process);
        var loaded = await worker.CommandAsync("load_qwen", timeout: TimeSpan.FromMinutes(2));
        var during = nvml.Snapshot();
        var released = await worker.CommandAsync("release_qwen", timeout: TimeSpan.FromSeconds(30));
        await Task.Delay(400);
        var after = nvml.Snapshot();
        return new { model = "Qwen3-VL-2B-Instruct-FP8", before, during, after,
            load_result = loaded.GetProperty("result"), release_result = released.GetProperty("result"), substitute_model_used = false };
    });

    comfy = new ComfyGpuSupervisor(comfyPython, comfyMain, comfySource, comfyBase,
        Path.Combine(workRoot, "COMFY_OUTPUT"), Path.Combine(workRoot, "COMFY_INPUT"),
        Path.Combine(workRoot, "COMFY_TEMP"), Path.Combine(workRoot, "COMFY_USER"), log);
    await CheckAsync("comfy_controlled_startup", async () =>
    {
        await using var lease = await coordinator.AcquireAsync("COMFYUI", 1024, TimeSpan.FromSeconds(5));
        var stats = await comfy.StartAsync(TimeSpan.FromSeconds(90)); ownedPids.Add(comfy.ProcessId);
        using var doc = JsonDocument.Parse(stats);
        var version = doc.RootElement.GetProperty("system").GetProperty("comfyui_version").GetString();
        Require(version == "0.33.1", $"Unexpected ComfyUI version {version}");
        return new { process_id = comfy.ProcessId, port = comfy.Port, bind = "127.0.0.1", version,
            use_shell_execute = false, argument_list = true, custom_node_scope = "GPU01_WORK_ONLY" };
    });

    await CheckAsync("comfy_cuda_pressure_and_release", async () =>
    {
        var before = nvml.Snapshot();
        await using var lease = await coordinator.AcquireAsync("COMFYUI", 1024, TimeSpan.FromSeconds(5), comfy.Process);
        var operation = comfy.RunProbeAsync(512, 900, "pressure", TimeSpan.FromSeconds(30));
        var peak = before;
        while (!operation.IsCompleted)
        {
            var sample = nvml.Snapshot(); if (sample.UsedMb > peak.UsedMb) peak = sample;
            await Task.Delay(20);
        }
        var promptId = await operation;
        await comfy.FreeAsync(); await Task.Delay(500);
        var after = nvml.Snapshot();
        Require(peak.UsedMb >= before.UsedMb + 400, $"Expected >=400 MB VRAM increase, observed {peak.UsedMb - before.UsedMb:F1} MB");
        return new { prompt_id = promptId, requested_mb = 512, before, peak, after, observed_growth_mb = peak.UsedMb - before.UsedMb };
    });

    await CheckAsync("python_reacquire_after_comfy", async () =>
    {
        await using var lease = await coordinator.AcquireAsync("PYTHON_AI", 128, TimeSpan.FromSeconds(5), worker.Process);
        var response = await worker.CommandAsync("cuda_op", new { megabytes = 32 });
        return new { reacquired = true, response = response.GetProperty("result") };
    });

    await CheckAsync("stress_100_alternating_cycles", async () =>
    {
        var before = nvml.Snapshot(); var maxUsed = before.UsedMb; var promptIds = new List<string>();
        var timer = Stopwatch.StartNew();
        for (var cycle = 1; cycle <= 100; cycle++)
        {
            await using (var pythonLease = await coordinator.AcquireAsync("PYTHON_AI", 64, TimeSpan.FromSeconds(5), worker.Process))
                await worker.CommandAsync("cuda_op", new { megabytes = 8 }, TimeSpan.FromSeconds(10));
            await using (var comfyLease = await coordinator.AcquireAsync("COMFYUI", 64, TimeSpan.FromSeconds(5), comfy.Process))
                promptIds.Add(await comfy.RunProbeAsync(8, 0, $"cycle-{cycle:D3}", TimeSpan.FromSeconds(15)));
            if (cycle % 10 == 0)
            {
                var sample = nvml.Snapshot(); maxUsed = Math.Max(maxUsed, sample.UsedMb);
                log.Write("GATE", null, "stress_checkpoint", durationMs: timer.ElapsedMilliseconds, memory: sample,
                    details: new { cycle, current_owner = coordinator.CurrentOwner });
            }
        }
        await comfy.FreeAsync(); await Task.Delay(500);
        var after = nvml.Snapshot();
        Require(promptIds.Distinct(StringComparer.Ordinal).Count() == 100, "Comfy stress prompt IDs were not unique");
        Require(coordinator.CurrentOwner is null, "Stress left a lease active");
        return new { cycles = 100, python_operations = 100, comfy_operations = 100, unique_prompt_ids = 100,
            oom_count = 0, deadlock_count = 0, lost_lease_count = 0, before, after, sampled_max_used_mb = maxUsed,
            final_growth_mb = after.UsedMb - before.UsedMb, duration_ms = timer.ElapsedMilliseconds };
    });

    await CheckAsync("python_owner_crash_recovery", async () =>
    {
        var lease = await coordinator.AcquireAsync("PYTHON_AI", 64, TimeSpan.FromSeconds(5), worker.Process);
        var crashedPid = worker.ProcessId; var exitCode = await worker.CrashAsync();
        await WaitUntilAsync(() => coordinator.CurrentOwner is null, TimeSpan.FromSeconds(3));
        await using var recovered = await coordinator.AcquireAsync("COMFYUI", 64, TimeSpan.FromSeconds(3), comfy.Process);
        var promptId = await comfy.RunProbeAsync(8, 0, "after-python-crash", TimeSpan.FromSeconds(15));
        await lease.DisposeAsync();
        return new { crashed_owner = "PYTHON_AI", crashed_pid = crashedPid, exit_code = exitCode,
            recovered_owner = "COMFYUI", prompt_id = promptId, stale_lease = false };
    });

    replacementWorker = new GpuWorkerClient(aiPython, workerPath, modelRoot, log);
    ownedPids.Add(replacementWorker.ProcessId);
    await replacementWorker.CommandAsync("health");
    await CheckAsync("comfy_owner_crash_recovery", async () =>
    {
        var lease = await coordinator.AcquireAsync("COMFYUI", 64, TimeSpan.FromSeconds(5), comfy.Process);
        var crashedPid = comfy.ProcessId; var exitCode = await comfy.CrashAsync();
        await WaitUntilAsync(() => coordinator.CurrentOwner is null, TimeSpan.FromSeconds(3));
        await using var recovered = await coordinator.AcquireAsync("PYTHON_AI", 64, TimeSpan.FromSeconds(3), replacementWorker.Process);
        var response = await replacementWorker.CommandAsync("cuda_op", new { megabytes = 8 });
        await lease.DisposeAsync();
        return new { crashed_owner = "COMFYUI", crashed_pid = crashedPid, exit_code = exitCode,
            recovered_owner = "PYTHON_AI", response = response.GetProperty("result"), stale_lease = false };
    });

    await replacementWorker.DisposeAsync(); replacementWorker = null;
    await comfy.DisposeAsync(); comfy = null;
    await worker.DisposeAsync(); worker = null;

    await CheckAsync("clean_final_state", () =>
    {
        var memory = nvml.Snapshot(); var aliveOwned = ownedPids.Where(IsAlive).ToArray(); var matching = FindMatchingPids(aiPython, comfyPython);
        Require(aliveOwned.Length == 0, $"Owned processes still alive: {string.Join(',', aliveOwned)}");
        Require(matching.Count == 0, $"Heavy runtime processes remain: {string.Join(',', matching)}");
        Require(coordinator.CurrentOwner is null, "Lease remains active at final state");
        return Task.FromResult<object?>(new { memory, owned_pids = ownedPids, alive_owned_pids = aliveOwned,
            active_heavy_pids = matching, current_owner = coordinator.CurrentOwner });
    });
}
finally
{
    if (replacementWorker is not null) await replacementWorker.DisposeAsync();
    if (comfy is not null) await comfy.DisposeAsync();
    if (worker is not null) await worker.DisposeAsync();
}

var failed = checks.Where(check => !check.Pass).Select(check => check.Name).ToArray();
var gateStatus = failed.Length == 0 ? "PASS_WITH_LIMITATIONS" : "FAIL";
var result = new
{
    gate = "GPU-01", status = gateStatus, generated_at = DateTimeOffset.UtcNow,
    gpu = nvml.DeviceName, driver = nvml.DriverVersion, nvml = "PASS", checks, failed_checks = failed,
    owned_process_ids = ownedPids, log_path = logPath, isolated_python = aiPython,
    qwen_model = "Qwen3-VL-2B-Instruct-FP8", comfyui = "0.33.1",
    darktable_ai = new { lease_architecture = "LEASE_ARCHITECTURE_READY", neural_headless = "NOT_HEADLESS_PROVEN", classification = "HEADLESS_NEURAL_NOT_PROVEN" },
    limitations = new[]
    {
        "Darktable Neural Restore remains NOT_HEADLESS_PROVEN, as inherited from DT-01.",
        "A fresh environment must fetch the pinned finegrained-fp8 kernel snapshot once; the validated local environment is cached and runs offline."
    }
};
await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
Console.WriteLine($"RESULT {result.status} {resultPath}");
return failed.Length == 0 ? 0 : 1;

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
{
    var timer = Stopwatch.StartNew();
    while (!predicate()) { if (timer.Elapsed >= timeout) throw new TimeoutException("Condition timeout"); await Task.Delay(20); }
}

static bool IsAlive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

static List<int> FindMatchingPids(params string[] executablePaths)
{
    var expected = executablePaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var result = new List<int>();
    foreach (var process in Process.GetProcesses())
    {
        using (process)
        {
            try { if (process.MainModule?.FileName is { } path && expected.Contains(Path.GetFullPath(path))) result.Add(process.Id); }
            catch { }
        }
    }
    return result;
}

internal sealed record CheckResult(string Name, bool Pass, long DurationMs, object? Details, CheckError? Error);
internal sealed record CheckError(string Code, string Message, string Type);
