using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Infrastructure;
using PhotoAIFactory.Ipc01;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: PhotoAIFactory.Ipc01 <isolated-python> <ai-worker-root> <output-root>");
    return 2;
}

var pythonPath = Path.GetFullPath(args[0]);
var workerRoot = Path.GetFullPath(args[1]);
var outputRoot = Path.GetFullPath(args[2]);
var workRoot = Path.Combine(outputRoot, "WORK");
var logRoot = Path.Combine(outputRoot, "LOGS");
var resultPath = Path.Combine(workRoot, "ipc01_results.json");
var logPath = Path.Combine(logRoot, "ipc01.jsonl");
var entrypoint = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ipc_worker_entrypoint.py"));

Directory.CreateDirectory(workRoot);
Directory.CreateDirectory(logRoot);
Directory.CreateDirectory(Path.Combine(outputRoot, "REPORT"));

if (!File.Exists(pythonPath)) throw new FileNotFoundException("Isolated Python not found", pythonPath);
if (!File.Exists(Path.Combine(workerRoot, "worker_entrypoint.py"))) throw new DirectoryNotFoundException($"AI Worker root is invalid: {workerRoot}");
if (!File.Exists(entrypoint)) throw new FileNotFoundException("IPC wrapper not found", entrypoint);

using var log = new StructuredLog(logPath);
var checks = new List<CheckResult>();
var allOwnedPids = new List<int>();
WorkerSupervisor? supervisor = null;

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
            WorkerStartException start => start.Code,
            OperationCanceledException => "OPERATION_CANCELLED",
            _ => ex.GetType().Name.ToUpperInvariant()
        };
        checks.Add(new CheckResult(name, false, timer.ElapsedMilliseconds, null, new StructuredError(code, ex.Message, ex.GetType().Name)));
        log.Write(name, durationMs: timer.ElapsedMilliseconds, processId: supervisor?.ProcessId, errorCode: code, details: new { ex.Message, exception = ex.GetType().Name });
    }
}

try
{
    await AddCheckAsync("isolated_python", async () =>
    {
        var runner = new ProcessRunner();
        var version = await runner.RunAsync(pythonPath, ["--version"], TimeSpan.FromSeconds(5));
        Require(version.Success, $"Python --version exit code {version.ExitCode}");
        return new
        {
            path = pythonPath,
            version = (version.StdOut + version.StdErr).Trim(),
            use_shell_execute = false,
            safe_argument_list = true
        };
    });

    supervisor = new WorkerSupervisor(pythonPath, workerRoot, entrypoint, log);
    HealthResponse? initialHealth = null;
    var firstPid = 0;
    var firstPort = 0;

    await AddCheckAsync("controlled_startup", async () =>
    {
        initialHealth = await supervisor.StartAsync(TimeSpan.FromSeconds(30));
        firstPid = supervisor.ProcessId ?? 0;
        firstPort = supervisor.Port;
        allOwnedPids.AddRange(supervisor.OwnedProcessIds);
        Require(firstPid > 0, "Worker PID was not captured");
        Require(initialHealth.Status == "HEALTHY", "Worker did not report HEALTHY");
        return new
        {
            process_id = firstPid,
            port = firstPort,
            host = supervisor.BaseAddress?.Host,
            status = supervisor.Status,
            api_version = initialHealth.ApiVersion,
            worker_version = initialHealth.WorkerVersion,
            session_token_bytes = supervisor.SessionTokenBytes
        };
    });

    await AddCheckAsync("loopback_and_process_security", () =>
    {
        var listeners = supervisor.ListenerAddresses();
        Require(supervisor.BaseAddress?.Host == "127.0.0.1", "Base address is not IPv4 loopback");
        Require(listeners.Count > 0, "Worker listener was not found");
        Require(listeners.All(address => address == "127.0.0.1"), $"Unexpected listener address: {string.Join(',', listeners)}");
        Require(!supervisor.UsesShellExecute, "UseShellExecute must be false");
        Require(supervisor.UsesArgumentList, "ArgumentList must be used");
        return Task.FromResult<object?>(new { listeners, use_shell_execute = false, argument_list = true, paths_with_spaces = pythonPath.Contains(' ') || entrypoint.Contains(' ') });
    });

    await AddCheckAsync("health_authentication", async () =>
    {
        using var unauthenticated = new HttpClient { BaseAddress = supervisor.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        using var noTokenResponse = await unauthenticated.GetAsync("v1/health");
        using var wrongTokenRequest = new HttpRequestMessage(HttpMethod.Get, "v1/health");
        wrongTokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "definitely-wrong-token");
        using var wrongTokenResponse = await unauthenticated.SendAsync(wrongTokenRequest);
        var correct = await supervisor.Client.GetHealthAsync();
        Require(noTokenResponse.StatusCode == HttpStatusCode.Unauthorized, $"No-token status was {(int)noTokenResponse.StatusCode}");
        Require(wrongTokenResponse.StatusCode == HttpStatusCode.Unauthorized, $"Wrong-token status was {(int)wrongTokenResponse.StatusCode}");
        Require(correct.Status == "HEALTHY" && correct.ApiVersion == "v1", "Authenticated health response was invalid");
        return new { no_token_status = (int)noTokenResponse.StatusCode, wrong_token_status = (int)wrongTokenResponse.StatusCode, correct_token_status = 200, correct.Status, correct.ApiVersion, correct.WorkerVersion };
    });

    var fixturePath = Path.Combine(workRoot, "Path With Spaces", "ipc technical fixture.ppm");
    await WritePpmFixtureAsync(fixturePath);
    await AddCheckAsync("real_json_request", async () =>
    {
        var requestId = $"analyze-{Guid.NewGuid():N}";
        var request = WorkerSupervisor.CreateRequest(requestId, "analyze", new { mode = "technical" }, [fixturePath]);
        var response = await supervisor.Client.ExecuteAsync("v1/analyze", request);
        Require(response.ApiVersion == "v1", "Response api_version mismatch");
        Require(response.RequestId == requestId, "Response request_id mismatch");
        Require(response.Success, $"Analyze failed: {response.Error?.Code}");
        Require(response.Result is not null && response.Result.Value.TryGetProperty("technical", out _), "Analyze response lacks technical result");
        Require(response.Error is null, "Successful response contains an error");
        Require(response.Timings is not null && response.Timings.ContainsKey("total_ms"), "Response lacks timings.total_ms");
        return new
        {
            request_api_version = request.ApiVersion,
            request_id_sent = request.RequestId,
            request.JobId,
            request.Operation,
            input_paths = request.InputPaths,
            config = request.Config,
            response.Success,
            request_id_received = response.RequestId,
            response_api_version = response.ApiVersion,
            response.Timings
        };
    });

    await AddCheckAsync("concurrent_light_requests", async () =>
    {
        const int count = 8;
        var expected = Enumerable.Range(0, count).Select(index => $"concurrent-{index:D2}-{Guid.NewGuid():N}").ToArray();
        var tasks = expected.Select(async (requestId, index) =>
        {
            var request = WorkerSupervisor.CreateRequest(requestId, "ipc-echo", new { index });
            return await supervisor.Client.ExecuteAsync("v1/ipc/echo", request);
        }).ToArray();
        var responses = await Task.WhenAll(tasks);
        Require(responses.All(response => response.Success), "At least one concurrent request failed");
        Require(responses.Select(response => response.RequestId).Order().SequenceEqual(expected.Order()), "Concurrent request IDs were mixed or lost");
        Require(responses.Select(response => response.RequestId).Distinct().Count() == count, "Concurrent response IDs were duplicated");
        return new { count, unique_request_ids = count, correlation = "exact" };
    });

    await AddCheckAsync("timeout", async () =>
    {
        var requestId = $"timeout-{Guid.NewGuid():N}";
        var request = WorkerSupervisor.CreateRequest(requestId, "ipc-delay", new { delay_ms = 1500 });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var detected = false;
        try
        {
            await supervisor.Client.ExecuteAsync("v1/ipc/delay", request, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            detected = true;
            log.Write("request_timeout", requestId, 250, supervisor.ProcessId, "IPC_TIMEOUT");
        }
        Require(detected, "Controlled timeout was not detected");
        var health = await supervisor.Client.GetHealthAsync();
        Require(health.Status == "HEALTHY", "Worker was not healthy after timeout");
        return new { request_id = requestId, timeout_ms = 250, error_code = "IPC_TIMEOUT", task_completed = true, post_timeout_health = health.Status };
    });

    await AddCheckAsync("cancellation", async () =>
    {
        var requestId = $"cancel-{Guid.NewGuid():N}";
        var request = WorkerSupervisor.CreateRequest(requestId, "ipc-delay", new { delay_ms = 1200 });
        using var cancellation = new CancellationTokenSource();
        var requestTask = supervisor.Client.ExecuteAsync("v1/ipc/delay", request, cancellation.Token);
        await Task.Delay(200);
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            await requestTask;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancelled = true;
            log.Write("request_cancelled", requestId, 200, supervisor.ProcessId, "IPC_CANCELLED");
        }
        Require(cancelled, "Cancellation was not observed");
        Require(requestTask.IsCompleted, "Cancelled request task was left incomplete");
        var health = await supervisor.Client.GetHealthAsync();
        Require(health.Status == "HEALTHY", "Worker was not healthy after cancellation");
        return new { request_id = requestId, cancel_after_ms = 200, error_code = "IPC_CANCELLED", task_completed = requestTask.IsCompleted, post_cancel_health = health.Status };
    });

    var crashExitCode = 0;
    await AddCheckAsync("crash_detection", async () =>
    {
        crashExitCode = await supervisor.CrashForTestAsync();
        Require(supervisor.Status == "STOPPED", $"Unexpected supervisor status: {supervisor.Status}");
        Require(!WorkerSupervisor.IsProcessAlive(firstPid), "Crashed worker PID is still alive");
        return new { process_id = firstPid, exit_code = crashExitCode, status = supervisor.Status, orphan = false };
    });

    var restartPid = 0;
    var restartPort = 0;
    await AddCheckAsync("controlled_restart", async () =>
    {
        var health = await supervisor.StartAsync(TimeSpan.FromSeconds(30));
        restartPid = supervisor.ProcessId ?? 0;
        restartPort = supervisor.Port;
        foreach (var pid in supervisor.OwnedProcessIds.Where(pid => !allOwnedPids.Contains(pid))) allOwnedPids.Add(pid);
        Require(restartPid > 0 && restartPid != firstPid, "Restart did not produce a new PID");
        Require(health.Status == "HEALTHY", "Restarted worker health failed");
        var requestId = $"restart-{Guid.NewGuid():N}";
        var response = await supervisor.Client.ExecuteAsync("v1/ipc/echo", WorkerSupervisor.CreateRequest(requestId, "ipc-echo", new { after_restart = true }));
        Require(response.Success && response.RequestId == requestId, "JSON request failed after restart");
        return new { old_process_id = firstPid, new_process_id = restartPid, old_port = firstPort, new_port = restartPort, health = health.Status, authenticated_request = "PASS" };
    });

    await AddCheckAsync("controlled_startup_failure", async () =>
    {
        var invalidPython = Path.Combine(outputRoot, "missing-runtime", "python.exe");
        await using var failing = new WorkerSupervisor(invalidPython, workerRoot, entrypoint, log);
        var timer = Stopwatch.StartNew();
        WorkerStartException? observed = null;
        try
        {
            await failing.StartAsync(TimeSpan.FromSeconds(2));
        }
        catch (WorkerStartException ex)
        {
            observed = ex;
        }
        timer.Stop();
        Require(observed is not null, "Invalid Python path did not fail startup");
        Require(failing.OwnedProcessIds.Count == 0, "Failed startup recorded an owned process");
        Require(timer.Elapsed < TimeSpan.FromSeconds(2), "Invalid-path startup failure was not immediate");
        return new { error_code = observed!.Code, exception = observed.InnerException?.GetType().Name ?? observed.GetType().Name, retries = 0, process_created = false, duration_ms = timer.ElapsedMilliseconds };
    });

    await AddCheckAsync("clean_shutdown", async () =>
    {
        var pid = restartPid;
        var port = restartPort;
        var graceful = await supervisor.StopAsync();
        var released = await WaitForPortReleaseAsync(port, TimeSpan.FromSeconds(3));
        Require(graceful, $"Worker did not exit cleanly; exit code {supervisor.LastExitCode}");
        Require(!WorkerSupervisor.IsProcessAlive(pid), "Stopped worker PID is still alive");
        Require(released, "Worker port was not released");
        return new { process_id = pid, exit_code = supervisor.LastExitCode, graceful, port, port_released = released };
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

await Task.Delay(200);
var orphanPids = allOwnedPids.Distinct().Where(WorkerSupervisor.IsProcessAlive).ToArray();
var allPassed = checks.Count == 12 && checks.All(check => check.Pass) && orphanPids.Length == 0;
var summary = new
{
    gate = "IPC-01",
    timestamp = DateTimeOffset.UtcNow,
    conclusion = allPassed ? "PASS" : "FAIL",
    environment = new
    {
        dotnet = Environment.Version.ToString(),
        python_path = pythonPath,
        worker_root = workerRoot,
        bind = "127.0.0.1",
        max_concurrent_heavy_jobs = 1
    },
    checks,
    owned_process_ids = allOwnedPids.Distinct().ToArray(),
    orphan_process_ids = orphanPids,
    orphan_process_count = orphanPids.Length,
    token_logged = false,
    artifacts = new { result_path = resultPath, log_path = logPath }
};
await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(summary, StructuredLog.JsonOptions) + Environment.NewLine, new UTF8Encoding(false));
Console.WriteLine(JsonSerializer.Serialize(summary, StructuredLog.JsonOptions));
return allPassed ? 0 : 1;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task WritePpmFixtureAsync(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    const int width = 32;
    const int height = 32;
    var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
    var pixels = new byte[width * height * 3];
    for (var y = 0; y < height; y++)
    for (var x = 0; x < width; x++)
    {
        var offset = (y * width + x) * 3;
        pixels[offset] = (byte)(x * 8);
        pixels[offset + 1] = (byte)(y * 8);
        pixels[offset + 2] = (byte)((x + y) * 4);
    }

    await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    await stream.WriteAsync(header);
    await stream.WriteAsync(pixels);
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

internal sealed record StructuredError(string Code, string Message, string ExceptionType);
internal sealed record CheckResult(string Name, bool Pass, long DurationMs, object? Details, StructuredError? Error);
