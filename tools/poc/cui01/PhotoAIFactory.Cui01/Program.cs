using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PhotoAIFactory.Cui01;
using PhotoAIFactory.Infrastructure;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PhotoAIFactory.Cui01 <project-root> <output-root>");
    return 2;
}

var projectRoot = Path.GetFullPath(args[0]);
var outputRoot = Path.GetFullPath(args[1]);
var pocRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var workflowRoot = Path.Combine(pocRoot, "workflows");
var workRoot = Path.Combine(outputRoot, "WORK");
var gateOutput = Path.Combine(outputRoot, "OUTPUT");
var logRoot = Path.Combine(outputRoot, "LOGS");
var reportRoot = Path.Combine(outputRoot, "REPORT");
var historyRoot = Path.Combine(workRoot, "HISTORY");
var comfyBase = Path.Combine(workRoot, "COMFY_BASE");
var internalOutput = Path.Combine(workRoot, "COMFY_INTERNAL_OUTPUT");
var comfyInput = Path.Combine(workRoot, "COMFY_INPUT");
var comfyTemp = Path.Combine(workRoot, "COMFY_TEMP");
var comfyUser = Path.Combine(workRoot, "COMFY_USER");
var resultPath = Path.Combine(workRoot, "cui01_results.json");
var logPath = Path.Combine(logRoot, "cui01.jsonl");

foreach (var directory in new[] { outputRoot, workRoot, gateOutput, logRoot, reportRoot, historyRoot, comfyBase, internalOutput, comfyInput, comfyTemp, comfyUser })
    Directory.CreateDirectory(directory);

var lockPath = Path.Combine(projectRoot, "config", "components.lock.local.json");
var comfyComponent = ReadComfyComponent(lockPath);
var installRoot = Path.GetFullPath(comfyComponent.LocalPath);
var sourceRoot = Path.Combine(installRoot, "ComfyUI");
var pythonPath = Path.Combine(installRoot, "python_embeded", "python.exe");
var mainPath = Path.Combine(sourceRoot, "main.py");
var versionFile = Path.Combine(sourceRoot, "comfyui_version.py");
var coreWorkflowPath = Path.Combine(workflowRoot, "minimal_core.json");
var delayWorkflowPath = Path.Combine(workflowRoot, "delay_test.json");
var instrumentationSource = Path.Combine(pocRoot, "instrumentation", "paf_cui01", "__init__.py");
var instrumentationDestination = Path.Combine(comfyBase, "custom_nodes", "paf_cui01", "__init__.py");

foreach (var required in new[] { pythonPath, mainPath, versionFile, coreWorkflowPath, delayWorkflowPath, instrumentationSource })
    if (!File.Exists(required)) throw new FileNotFoundException("Required CUI-01 file not found", required);

Directory.CreateDirectory(Path.GetDirectoryName(instrumentationDestination)!);
File.Copy(instrumentationSource, instrumentationDestination, overwrite: true);

using var log = new StructuredLog(logPath);
var checks = new List<CheckResult>();
var materializedOutputs = new List<OutputRecord>();
var sequentialPromptIds = new List<string>();
var allOwnedPids = new List<int>();
ComfySupervisor? supervisor = null;

async Task AddCheckAsync(string name, Func<Task<object?>> action)
{
    var timer = Stopwatch.StartNew();
    try
    {
        var details = await action();
        timer.Stop();
        checks.Add(new CheckResult(name, true, timer.ElapsedMilliseconds, details, null));
        log.Write(name, durationMs: timer.ElapsedMilliseconds, processId: supervisor?.ProcessId, details: details);
    }
    catch (Exception ex)
    {
        timer.Stop();
        var code = ex switch
        {
            ComfyStartException start => start.Code,
            OperationCanceledException => "OPERATION_CANCELLED",
            _ => ex.GetType().Name.ToUpperInvariant()
        };
        checks.Add(new CheckResult(name, false, timer.ElapsedMilliseconds, null, new StructuredError(code, ex.Message, ex.GetType().Name)));
        log.Write(name, durationMs: timer.ElapsedMilliseconds, processId: supervisor?.ProcessId, errorCode: code, details: new { ex.Message, exception = ex.GetType().Name });
    }
}

var initialPid = 0;
var initialPort = 0;
var restartPid = 0;
var restartPort = 0;
var activePromptId = "";
var pendingPromptId = "";
Task? activeWaitTask = null;

try
{
    await AddCheckAsync("exact_comfyui", async () =>
    {
        var versionText = await File.ReadAllTextAsync(versionFile);
        Require(versionText.Contains("0.33.1", StringComparison.Ordinal), "Installed comfyui_version.py is not 0.33.1");
        var runner = new ProcessRunner();
        var git = await runner.RunAsync("git", ["-C", sourceRoot, "rev-parse", "HEAD"], TimeSpan.FromSeconds(10));
        Require(git.Success, $"git rev-parse failed: {git.StdErr}");
        var commit = git.StdOut.Trim();
        Require(commit == "72865f4f27eaf5396f8f36370e0a2be3a9a090ee", $"Unexpected ComfyUI commit: {commit}");
        var python = await runner.RunAsync(pythonPath, ["--version"], TimeSpan.FromSeconds(10));
        Require(python.Success, "Embedded Python --version failed");
        return new { lock_version = comfyComponent.Version, install_root = installRoot, comfyui_version = "0.33.1", commit, embedded_python = (python.StdOut + python.StdErr).Trim() };
    });

    supervisor = CreateSupervisor(mainPath);
    string? initialStats = null;
    await AddCheckAsync("controlled_startup", async () =>
    {
        initialStats = await supervisor.StartAsync(TimeSpan.FromSeconds(60));
        initialPid = supervisor.ProcessId ?? 0;
        initialPort = supervisor.Port;
        allOwnedPids.AddRange(supervisor.OwnedProcessIds);
        Require(initialPid > 0, "ComfyUI PID was not captured");
        Require(supervisor.Status == "READY", $"Unexpected status {supervisor.Status}");
        return new { process_id = initialPid, port = initialPort, host = supervisor.BaseAddress?.Host, status = supervisor.Status, startup_timeout_seconds = 60 };
    });

    await AddCheckAsync("loopback_headless_security", () =>
    {
        var listeners = supervisor.ListenerAddresses();
        Require(supervisor.BaseAddress?.Host == "127.0.0.1", "ComfyUI base address is not loopback");
        Require(listeners.Count > 0 && listeners.All(address => address == "127.0.0.1"), $"Unexpected listener: {string.Join(',', listeners)}");
        Require(!supervisor.UsesShellExecute, "UseShellExecute must be false");
        Require(supervisor.UsesArgumentList, "ProcessStartInfo.ArgumentList was not used");
        Require(supervisor.AutoLaunchDisabled && supervisor.LastArguments.Contains("--disable-auto-launch"), "Browser auto-launch was not disabled");
        Require(!supervisor.LastArguments.Contains("0.0.0.0"), "Public bind argument detected");
        return Task.FromResult<object?>(new
        {
            listeners,
            use_shell_execute = false,
            argument_list = true,
            disable_auto_launch = true,
            create_no_window = true,
            manual_ui_interactions = 0,
            paths_with_spaces = outputRoot.Contains(' ')
        });
    });

    await AddCheckAsync("system_stats", async () =>
    {
        var stats = initialStats ?? await supervisor.Client.GetSystemStatsAsync();
        using var document = JsonDocument.Parse(stats);
        var root = document.RootElement;
        Require(root.TryGetProperty("system", out var system), "system_stats lacks system");
        Require(root.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array && devices.GetArrayLength() > 0, "system_stats lacks devices");
        var reportedVersion = GetOptionalString(system, "comfyui_version");
        Require(reportedVersion == "0.33.1", $"system_stats version mismatch: {reportedVersion}");
        var firstDevice = devices[0];
        return new
        {
            http_status = 200,
            comfyui_version = reportedVersion,
            os = GetOptionalString(system, "os"),
            python_version = GetOptionalString(system, "python_version"),
            pytorch_version = GetOptionalString(system, "pytorch_version"),
            device_name = GetOptionalString(firstDevice, "name"),
            device_type = GetOptionalString(firstDevice, "type"),
            vram_total = GetOptionalInt64(firstDevice, "vram_total"),
            vram_free = GetOptionalInt64(firstDevice, "vram_free")
        };
    });

    await AddCheckAsync("prompt_websocket_history_output", async () =>
    {
        var clientId = $"cui01-{Guid.NewGuid():N}";
        using var socket = new ClientWebSocket();
        var wsUri = new Uri(supervisor.WebSocketBaseAddress!, $"ws?clientId={Uri.EscapeDataString(clientId)}");
        await socket.ConnectAsync(wsUri, CancellationToken.None);
        Require(socket.State == WebSocketState.Open, "WebSocket did not open");

        var workflow = CreateCoreWorkflow(coreWorkflowPath, "CUI01/core_01", 0x336699);
        var promptId = await supervisor.Client.SubmitPromptAsync(workflow, clientId);
        Require(!string.IsNullOrWhiteSpace(promptId), "POST /prompt returned an empty prompt_id");
        log.Write("prompt_submitted", promptId, processId: supervisor.ProcessId);
        var observation = await ObservePromptAsync(socket, promptId, TimeSpan.FromSeconds(20));
        Require(observation.Events.Contains("executing"), "WebSocket did not report execution");
        Require(observation.Events.Contains("execution_success"), "WebSocket did not report successful completion");
        var history = await supervisor.Client.GetHistoryAsync(promptId);
        var output = await ValidateAndMaterializeAsync(history, promptId, "core01", internalOutput, gateOutput, historyRoot, 64, 48);
        sequentialPromptIds.Add(promptId);
        materializedOutputs.Add(output);
        log.Write("output_materialized", promptId, processId: supervisor.ProcessId, details: output);
        return new { prompt_id = promptId, websocket_events = observation.Events, websocket_duration_ms = observation.DurationMs, history_exact = true, output };
    });

    await AddCheckAsync("five_sequential_prompts", async () =>
    {
        for (var index = 2; index <= 5; index++)
        {
            var clientId = $"cui01-{Guid.NewGuid():N}";
            var workflow = CreateCoreWorkflow(coreWorkflowPath, $"CUI01/core_{index:D2}", 0x223344 + (index * 0x111111));
            var promptId = await supervisor.Client.SubmitPromptAsync(workflow, clientId);
            log.Write("prompt_submitted", promptId, processId: supervisor.ProcessId, details: new { sequence = index });
            await supervisor.Client.WaitForCompletionAsync(promptId, clientId, TimeSpan.FromSeconds(20));
            var history = await supervisor.Client.GetHistoryAsync(promptId);
            var output = await ValidateAndMaterializeAsync(history, promptId, $"core{index:D2}", internalOutput, gateOutput, historyRoot, 64, 48);
            sequentialPromptIds.Add(promptId);
            materializedOutputs.Add(output);
            log.Write("output_materialized", promptId, processId: supervisor.ProcessId, details: output);
        }

        Require(sequentialPromptIds.Count == 5, $"Expected 5 sequential prompts, got {sequentialPromptIds.Count}");
        Require(sequentialPromptIds.Distinct(StringComparer.Ordinal).Count() == 5, "Sequential prompt IDs are not unique");
        Require(materializedOutputs.Take(5).All(output => output.Width == 64 && output.Height == 48 && output.Format == "PNG"), "At least one sequential output is invalid");
        return new { count = sequentialPromptIds.Count, unique_prompt_ids = sequentialPromptIds.Count, prompt_ids = sequentialPromptIds.ToArray(), valid_outputs = materializedOutputs.Take(5).Count() };
    });

    await AddCheckAsync("queue_cancel", async () =>
    {
        var activeClientId = $"cui01-active-{Guid.NewGuid():N}";
        var activeWorkflow = CreateDelayWorkflow(delayWorkflowPath, "CUI01/queue_active", 5000, Guid.NewGuid().ToString("N"));
        activePromptId = await supervisor.Client.SubmitPromptAsync(activeWorkflow, activeClientId);
        log.Write("prompt_submitted", activePromptId, processId: supervisor.ProcessId, details: new { purpose = "queue-holder" });
        activeWaitTask = supervisor.Client.WaitForCompletionAsync(activePromptId, activeClientId, TimeSpan.FromSeconds(15));
        Require(await WaitForQueueStateAsync(supervisor.Http, "queue_running", activePromptId, present: true, TimeSpan.FromSeconds(5)), "Delay prompt did not enter running queue");

        var pendingWorkflow = CreateCoreWorkflow(coreWorkflowPath, "CUI01/queue_pending", 0x884422);
        pendingPromptId = await supervisor.Client.SubmitPromptAsync(pendingWorkflow, $"cui01-pending-{Guid.NewGuid():N}");
        log.Write("prompt_submitted", pendingPromptId, processId: supervisor.ProcessId, details: new { purpose = "pending-cancel" });
        Require(await WaitForQueueStateAsync(supervisor.Http, "queue_pending", pendingPromptId, present: true, TimeSpan.FromSeconds(5)), "Second prompt did not enter pending queue");
        await supervisor.Client.CancelPendingAsync(pendingPromptId);
        Require(await WaitForQueueStateAsync(supervisor.Http, "queue_pending", pendingPromptId, present: false, TimeSpan.FromSeconds(5)), "Cancelled prompt remained pending");
        var pendingHistory = await supervisor.Client.GetHistoryAsync(pendingPromptId);
        Require(!HistoryContainsPrompt(pendingHistory, pendingPromptId), "Cancelled pending prompt unexpectedly entered history");
        log.Write("pending_prompt_cancelled", pendingPromptId, processId: supervisor.ProcessId);
        return new { active_prompt_id = activePromptId, cancelled_prompt_id = pendingPromptId, pending_before_cancel = true, pending_after_cancel = false, history_created = false };
    });

    await AddCheckAsync("interrupt", async () =>
    {
        Require(activeWaitTask is not null && !string.IsNullOrWhiteSpace(activePromptId), "Active delay prompt was not prepared");
        await supervisor.Client.InterruptAsync();
        var interrupted = false;
        string? observedError = null;
        try
        {
            await activeWaitTask!;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("interrupted", StringComparison.OrdinalIgnoreCase))
        {
            interrupted = true;
            observedError = ex.Message;
        }
        Require(interrupted, "Active prompt did not report controlled interruption");
        Require(await WaitForQueueEmptyAsync(supervisor.Http, TimeSpan.FromSeconds(5)), "Queue was not empty after interrupt");
        var history = await supervisor.Client.GetHistoryAsync(activePromptId);
        Require(HistoryIsInterrupted(history, activePromptId), "History does not identify the prompt as interrupted");
        var pendingHistory = await supervisor.Client.GetHistoryAsync(pendingPromptId);
        Require(!HistoryContainsPrompt(pendingHistory, pendingPromptId), "Cancelled pending prompt executed after interrupt");
        Require(!Directory.EnumerateFiles(internalOutput, "*queue_pending*", SearchOption.AllDirectories).Any(), "Cancelled prompt produced an output");
        var stats = await supervisor.Client.GetSystemStatsAsync();
        Require(!string.IsNullOrWhiteSpace(stats), "ComfyUI was unhealthy after interrupt");
        log.Write("prompt_interrupted", activePromptId, processId: supervisor.ProcessId, errorCode: "EXECUTION_INTERRUPTED");
        return new { prompt_id = activePromptId, interrupted = true, queue_empty = true, pending_prompt_executed = false, post_interrupt_health = "READY", observed_error = observedError };
    });

    await AddCheckAsync("post_interrupt_workflow", async () =>
    {
        var clientId = $"cui01-post-{Guid.NewGuid():N}";
        var workflow = CreateCoreWorkflow(coreWorkflowPath, "CUI01/post_interrupt", 0x228844);
        var promptId = await supervisor.Client.SubmitPromptAsync(workflow, clientId);
        await supervisor.Client.WaitForCompletionAsync(promptId, clientId, TimeSpan.FromSeconds(20));
        var history = await supervisor.Client.GetHistoryAsync(promptId);
        var output = await ValidateAndMaterializeAsync(history, promptId, "post_interrupt", internalOutput, gateOutput, historyRoot, 64, 48);
        materializedOutputs.Add(output);
        return new { prompt_id = promptId, success = true, output };
    });

    await AddCheckAsync("crash_detection", async () =>
    {
        var exitCode = await supervisor.CrashForTestAsync();
        Require(supervisor.Status == "STOPPED", $"Unexpected status after crash: {supervisor.Status}");
        Require(!ComfySupervisor.IsProcessAlive(initialPid), "Crashed ComfyUI PID is still alive");
        return new { process_id = initialPid, exit_code = exitCode, status = supervisor.Status, orphan = false };
    });

    await AddCheckAsync("controlled_restart", async () =>
    {
        var stats = await supervisor.StartAsync(TimeSpan.FromSeconds(60));
        restartPid = supervisor.ProcessId ?? 0;
        restartPort = supervisor.Port;
        foreach (var pid in supervisor.OwnedProcessIds.Where(pid => !allOwnedPids.Contains(pid))) allOwnedPids.Add(pid);
        Require(restartPid > 0 && restartPid != initialPid, "Restart did not create a new PID");
        Require(!string.IsNullOrWhiteSpace(stats) && supervisor.Status == "READY", "Restarted ComfyUI did not become ready");
        var clientId = $"cui01-restart-{Guid.NewGuid():N}";
        var promptId = await supervisor.Client.SubmitPromptAsync(CreateCoreWorkflow(coreWorkflowPath, "CUI01/restart", 0x662288), clientId);
        await supervisor.Client.WaitForCompletionAsync(promptId, clientId, TimeSpan.FromSeconds(20));
        var history = await supervisor.Client.GetHistoryAsync(promptId);
        var output = await ValidateAndMaterializeAsync(history, promptId, "restart", internalOutput, gateOutput, historyRoot, 64, 48);
        materializedOutputs.Add(output);
        return new { old_process_id = initialPid, new_process_id = restartPid, old_port = initialPort, new_port = restartPort, status = supervisor.Status, prompt_id = promptId, output };
    });

    await AddCheckAsync("controlled_startup_failure", async () =>
    {
        var invalidMain = Path.Combine(workRoot, "missing-comfyui", "main.py");
        await using var failing = CreateSupervisor(invalidMain);
        ComfyStartException? observed = null;
        var timer = Stopwatch.StartNew();
        try
        {
            await failing.StartAsync(TimeSpan.FromSeconds(5));
        }
        catch (ComfyStartException ex)
        {
            observed = ex;
        }
        timer.Stop();
        foreach (var pid in failing.OwnedProcessIds.Where(pid => !allOwnedPids.Contains(pid))) allOwnedPids.Add(pid);
        Require(observed is not null, "Invalid main.py did not produce a startup failure");
        Require(failing.OwnedProcessIds.All(pid => !ComfySupervisor.IsProcessAlive(pid)), "Invalid startup left a process alive");
        Require(timer.Elapsed < TimeSpan.FromSeconds(5), "Invalid startup did not fail within its bound");
        return new { error_code = observed!.Code, retries = 0, owned_process_ids = failing.OwnedProcessIds.ToArray(), processes_alive = 0, duration_ms = timer.ElapsedMilliseconds };
    });

    await AddCheckAsync("clean_shutdown", async () =>
    {
        var pid = restartPid;
        var port = restartPort;
        var stop = await supervisor.StopAsync();
        var released = await WaitForPortReleaseAsync(port, TimeSpan.FromSeconds(5));
        Require(stop.ApiPreparationSucceeded, "Interrupt/free preparation failed during controlled stop");
        Require(stop.ProcessTerminated && !ComfySupervisor.IsProcessAlive(pid), "Owned ComfyUI PID did not terminate");
        Require(released, "ComfyUI port was not released");
        return new { process_id = pid, exit_code = stop.ExitCode, api_prepared = stop.ApiPreparationSucceeded, process_terminated = true, port, port_released = true, owned_pid_only = true };
    });
}
finally
{
    if (supervisor is not null)
    {
        foreach (var pid in supervisor.OwnedProcessIds.Where(pid => !allOwnedPids.Contains(pid))) allOwnedPids.Add(pid);
        await supervisor.DisposeAsync();
    }
}

await Task.Delay(300);
var orphanPids = allOwnedPids.Distinct().Where(ComfySupervisor.IsProcessAlive).ToArray();
var requiredChecks = new[]
{
    "exact_comfyui", "controlled_startup", "loopback_headless_security", "system_stats",
    "prompt_websocket_history_output", "five_sequential_prompts", "queue_cancel", "interrupt",
    "post_interrupt_workflow", "crash_detection", "controlled_restart", "controlled_startup_failure", "clean_shutdown"
};
var allPassed = requiredChecks.All(name => checks.Any(check => check.Name == name && check.Pass)) && orphanPids.Length == 0;
var prettyJson = new JsonSerializerOptions(StructuredLog.JsonOptions) { WriteIndented = true };
var summary = new
{
    gate = "CUI-01",
    timestamp = DateTimeOffset.UtcNow,
    conclusion = allPassed ? "PASS" : "FAIL",
    environment = new
    {
        comfyui_version = "0.33.1",
        comfyui_commit = "72865f4f27eaf5396f8f36370e0a2be3a9a090ee",
        install_root = installRoot,
        python_path = pythonPath,
        bind = "127.0.0.1",
        browser_auto_launch = false,
        workflows_use_user_photos = false
    },
    checks,
    sequential_prompt_ids = sequentialPromptIds,
    outputs = materializedOutputs,
    owned_process_ids = allOwnedPids.Distinct().ToArray(),
    orphan_process_ids = orphanPids,
    orphan_process_count = orphanPids.Length,
    artifacts = new { result_path = resultPath, log_path = logPath, output_directory = gateOutput, history_directory = historyRoot }
};
await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(summary, prettyJson) + Environment.NewLine, new UTF8Encoding(false));
Console.WriteLine(JsonSerializer.Serialize(summary, StructuredLog.JsonOptions));
return allPassed ? 0 : 1;

ComfySupervisor CreateSupervisor(string selectedMainPath) => new(
    pythonPath,
    selectedMainPath,
    sourceRoot,
    comfyBase,
    internalOutput,
    comfyInput,
    comfyTemp,
    comfyUser,
    log);

static ComfyComponent ReadComfyComponent(string lockPath)
{
    using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
    foreach (var component in document.RootElement.GetProperty("components").EnumerateArray())
    {
        if (component.GetProperty("id").GetString() != "comfyui") continue;
        return new ComfyComponent(
            component.GetProperty("version").GetString() ?? "",
            component.GetProperty("local_path").GetString() ?? "");
    }
    throw new InvalidDataException("components.lock.local.json lacks comfyui");
}

static string CreateCoreWorkflow(string templatePath, string prefix, int color)
{
    var root = JsonNode.Parse(File.ReadAllText(templatePath))?.AsObject() ?? throw new InvalidDataException("Invalid core workflow JSON");
    root["1"]!["inputs"]!["color"] = color;
    root["2"]!["inputs"]!["filename_prefix"] = prefix;
    return root.ToJsonString();
}

static string CreateDelayWorkflow(string templatePath, string prefix, int delayMs, string nonce)
{
    var root = JsonNode.Parse(File.ReadAllText(templatePath))?.AsObject() ?? throw new InvalidDataException("Invalid delay workflow JSON");
    root["1"]!["inputs"]!["delay_ms"] = delayMs;
    root["1"]!["inputs"]!["nonce"] = nonce;
    root["2"]!["inputs"]!["filename_prefix"] = prefix;
    return root.ToJsonString();
}

static async Task<WebSocketObservation> ObservePromptAsync(ClientWebSocket socket, string promptId, TimeSpan timeout)
{
    using var timeoutCts = new CancellationTokenSource(timeout);
    var events = new List<string>();
    var timer = Stopwatch.StartNew();
    var buffer = new byte[64 * 1024];
    while (socket.State == WebSocketState.Open)
    {
        using var message = new MemoryStream();
        WebSocketReceiveResult receive;
        do
        {
            receive = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeoutCts.Token);
            if (receive.MessageType == WebSocketMessageType.Close)
                throw new IOException("ComfyUI WebSocket closed before prompt completion");
            message.Write(buffer, 0, receive.Count);
        } while (!receive.EndOfMessage);

        if (receive.MessageType != WebSocketMessageType.Text) continue;
        using var document = JsonDocument.Parse(message.ToArray());
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement) || !root.TryGetProperty("data", out var data)) continue;
        var messagePromptId = data.TryGetProperty("prompt_id", out var promptElement) ? promptElement.GetString() : null;
        if (!string.Equals(messagePromptId, promptId, StringComparison.Ordinal)) continue;
        var eventName = typeElement.GetString() ?? "unknown";
        events.Add(eventName);
        if (eventName == "execution_success")
        {
            timer.Stop();
            return new WebSocketObservation(events.Distinct(StringComparer.Ordinal).ToArray(), timer.ElapsedMilliseconds);
        }
        if (eventName is "execution_error" or "execution_interrupted")
            throw new InvalidOperationException($"ComfyUI {eventName} for prompt {promptId}");
    }
    throw new IOException("ComfyUI WebSocket ended unexpectedly");
}

static async Task<OutputRecord> ValidateAndMaterializeAsync(
    string historyJson,
    string promptId,
    string label,
    string internalOutput,
    string gateOutput,
    string historyRoot,
    int expectedWidth,
    int expectedHeight)
{
    var historyPath = Path.Combine(historyRoot, $"{promptId}.json");
    await File.WriteAllTextAsync(historyPath, historyJson + Environment.NewLine, new UTF8Encoding(false));
    using var document = JsonDocument.Parse(historyJson);
    Require(document.RootElement.EnumerateObject().Count() == 1, "Prompt-specific history contains another prompt");
    Require(document.RootElement.TryGetProperty(promptId, out var item), "History lacks requested prompt_id");
    Require(item.TryGetProperty("status", out var status), "History lacks status");
    Require(status.TryGetProperty("completed", out var completed) && completed.ValueKind == JsonValueKind.True, "History is not completed");
    Require(status.TryGetProperty("status_str", out var statusText) && statusText.GetString() == "success", "History status is not success");
    Require(item.TryGetProperty("outputs", out var outputs), "History lacks outputs");

    JsonElement? image = null;
    foreach (var node in outputs.EnumerateObject())
    {
        if (!node.Value.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() == 0) continue;
        image = images[0];
        break;
    }
    var imageValue = image ?? throw new InvalidDataException("History contains no identifiable image output");
    var filename = imageValue.GetProperty("filename").GetString() ?? throw new InvalidDataException("Output filename is empty");
    var subfolder = imageValue.TryGetProperty("subfolder", out var folder) ? folder.GetString() ?? "" : "";
    var type = imageValue.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
    Require(type == "output", $"Unexpected ComfyUI output type: {type}");

    var outputRootFull = Path.GetFullPath(internalOutput) + Path.DirectorySeparatorChar;
    var source = Path.GetFullPath(Path.Combine(internalOutput, subfolder, filename));
    Require(source.StartsWith(outputRootFull, StringComparison.OrdinalIgnoreCase), "History output escaped the isolated output root");
    Require(File.Exists(source), $"History output does not exist: {source}");
    var info = new FileInfo(source);
    Require(info.Length > 0, "History output is empty");
    var (format, width, height) = ReadPngHeader(source);
    Require(format == "PNG", $"Unexpected output format: {format}");
    Require(width == expectedWidth && height == expectedHeight, $"Unexpected output dimensions {width}x{height}");

    var destination = Path.Combine(gateOutput, $"{label}_{promptId[..8]}_{Path.GetFileName(filename)}");
    File.Copy(source, destination, overwrite: false);
    await using var destinationStream = File.OpenRead(destination);
    var hash = Convert.ToHexString(await SHA256.HashDataAsync(destinationStream)).ToLowerInvariant();
    return new OutputRecord(promptId, source, destination, new FileInfo(destination).Length, format, width, height, hash, historyPath);
}

static (string Format, int Width, int Height) ReadPngHeader(string path)
{
    Span<byte> header = stackalloc byte[24];
    using var stream = File.OpenRead(path);
    var read = stream.Read(header);
    Require(read == header.Length, "PNG header is truncated");
    ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
    Require(header[..8].SequenceEqual(signature), "Output is not a PNG");
    Require(Encoding.ASCII.GetString(header[12..16]) == "IHDR", "PNG lacks IHDR at expected position");
    return ("PNG", BinaryPrimitives.ReadInt32BigEndian(header[16..20]), BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
}

static bool HistoryContainsPrompt(string json, string promptId)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.TryGetProperty(promptId, out _);
}

static bool HistoryIsInterrupted(string json, string promptId)
{
    using var document = JsonDocument.Parse(json);
    if (!document.RootElement.TryGetProperty(promptId, out var item)) return false;
    if (item.TryGetProperty("status", out var status))
    {
        if (status.TryGetProperty("status_str", out var statusText) && statusText.GetString() == "error") return true;
        if (status.TryGetProperty("messages", out var messages) && messages.GetRawText().Contains("execution_interrupted", StringComparison.OrdinalIgnoreCase)) return true;
    }
    return item.GetRawText().Contains("execution_interrupted", StringComparison.OrdinalIgnoreCase);
}

static async Task<bool> WaitForQueueStateAsync(HttpClient http, string queueName, string promptId, bool present, TimeSpan timeout)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout)
    {
        var json = await http.GetStringAsync("queue");
        using var document = JsonDocument.Parse(json);
        var found = document.RootElement.TryGetProperty(queueName, out var queue) && queue.GetRawText().Contains(promptId, StringComparison.Ordinal);
        if (found == present) return true;
        await Task.Delay(50);
    }
    return false;
}

static async Task<bool> WaitForQueueEmptyAsync(HttpClient http, TimeSpan timeout)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout)
    {
        var json = await http.GetStringAsync("queue");
        using var document = JsonDocument.Parse(json);
        var running = document.RootElement.GetProperty("queue_running");
        var pending = document.RootElement.GetProperty("queue_pending");
        if (running.GetArrayLength() == 0 && pending.GetArrayLength() == 0) return true;
        await Task.Delay(50);
    }
    return false;
}

static async Task<bool> WaitForPortReleaseAsync(int port, TimeSpan timeout)
{
    var timer = Stopwatch.StartNew();
    while (timer.Elapsed < timeout)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            await Task.Delay(100);
        }
        finally
        {
            listener.Stop();
        }
    }
    return false;
}

static string? GetOptionalString(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

static long? GetOptionalInt64(JsonElement element, string name) =>
    element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed record ComfyComponent(string Version, string LocalPath);
internal sealed record StructuredError(string Code, string Message, string ExceptionType);
internal sealed record CheckResult(string Name, bool Pass, long DurationMs, object? Details, StructuredError? Error);
internal sealed record WebSocketObservation(IReadOnlyList<string> Events, long DurationMs);
internal sealed record OutputRecord(string PromptId, string SourcePath, string GatePath, long SizeBytes, string Format, int Width, int Height, string Sha256, string HistoryPath);
