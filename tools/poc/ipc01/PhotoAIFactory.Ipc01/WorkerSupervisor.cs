using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Infrastructure;

namespace PhotoAIFactory.Ipc01;

internal sealed class WorkerStartException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

internal sealed class WorkerSupervisor : IAsyncDisposable
{
    private readonly string _pythonPath;
    private readonly string _workerRoot;
    private readonly string _entrypoint;
    private readonly StructuredLog _log;
    private Process? _process;
    private HttpClient? _http;
    private PythonAiClient? _client;

    public WorkerSupervisor(string pythonPath, string workerRoot, string entrypoint, StructuredLog log)
    {
        _pythonPath = pythonPath;
        _workerRoot = workerRoot;
        _entrypoint = entrypoint;
        _log = log;
    }

    public string Status { get; private set; } = "STOPPED";
    public int? ProcessId { get; private set; }
    public int? LastExitCode { get; private set; }
    public int Port { get; private set; }
    public Uri? BaseAddress { get; private set; }
    public bool UsesShellExecute => false;
    public bool UsesArgumentList => true;
    public int SessionTokenBytes => 32;
    public List<int> OwnedProcessIds { get; } = [];

    public PythonAiClient Client => _client ?? throw new InvalidOperationException("Worker is not running");

    public async Task<HealthResponse> StartAsync(TimeSpan readinessTimeout, CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false }) throw new InvalidOperationException("Worker is already running");

        DisposeTransport();
        _process?.Dispose();
        _process = null;
        LastExitCode = null;
        Status = "STARTING";
        Port = ReserveLoopbackPort();
        BaseAddress = new Uri($"http://127.0.0.1:{Port}/");
        var token = GenerateSessionToken();

        var startInfo = new ProcessStartInfo(_pythonPath)
        {
            WorkingDirectory = _workerRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(_entrypoint);
        startInfo.Environment["PAF_AI_HOST"] = "127.0.0.1";
        startInfo.Environment["PAF_AI_PORT"] = Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["PAF_AI_TOKEN"] = token;
        startInfo.Environment["PAF_AI_LOG_LEVEL"] = "info";
        startInfo.Environment["PYTHONPATH"] = _workerRoot;
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _log.Write("worker_stdout", processId: SafeProcessId(process), details: new { message = e.Data });
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _log.Write("worker_stderr", processId: SafeProcessId(process), details: new { message = e.Data });
        };
        process.Exited += (_, _) =>
        {
            var pid = SafeProcessId(process);
            var exitCode = SafeExitCode(process);
            LastExitCode = exitCode;
            if (ReferenceEquals(_process, process)) Status = "STOPPED";
            _log.Write("worker_exited", processId: pid, errorCode: exitCode == 0 ? null : "WORKER_EXITED", details: new { exit_code = exitCode });
        };

        var started = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) throw new WorkerStartException("PROCESS_START_FALSE", "Process.Start returned false");
            _process = process;
            ProcessId = process.Id;
            OwnedProcessIds.Add(process.Id);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _log.Write(
                "worker_started",
                processId: process.Id,
                details: new
                {
                    host = "127.0.0.1",
                    port = Port,
                    token_generated = true,
                    token_bytes = SessionTokenBytes,
                    use_shell_execute = startInfo.UseShellExecute,
                    argument_list_count = startInfo.ArgumentList.Count
                });
        }
        catch (Exception ex) when (ex is not WorkerStartException)
        {
            Status = "STOPPED";
            process.Dispose();
            _log.Write("worker_start_failed", durationMs: started.ElapsedMilliseconds, errorCode: "PROCESS_START_ERROR", details: new { exception = ex.GetType().Name, ex.Message });
            throw new WorkerStartException("PROCESS_START_ERROR", ex.Message, ex);
        }

        _http = new HttpClient
        {
            BaseAddress = BaseAddress,
            Timeout = TimeSpan.FromSeconds(10)
        };
        _client = new PythonAiClient(_http, token);

        var deadline = Stopwatch.StartNew();
        Exception? lastError = null;
        while (deadline.Elapsed < readinessTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var exitCode = SafeExitCode(process);
                throw new WorkerStartException("WORKER_EXITED_DURING_STARTUP", $"Worker exited during startup with code {exitCode}");
            }

            try
            {
                var health = await _client.GetHealthAsync(cancellationToken);
                if (health.Status == "HEALTHY" && health.ApiVersion == "v1")
                {
                    Status = "HEALTHY";
                    _log.Write("worker_ready", durationMs: deadline.ElapsedMilliseconds, processId: process.Id, details: new { health.Status, health.ApiVersion, health.WorkerVersion });
                    return health;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new WorkerStartException("READINESS_TIMEOUT", $"Worker did not become ready in {readinessTimeout.TotalSeconds:F1}s", lastError);
    }

    public async Task<int> CrashForTestAsync(CancellationToken cancellationToken = default)
    {
        var process = _process ?? throw new InvalidOperationException("Worker is not running");
        if (process.HasExited) return process.ExitCode;
        var pid = process.Id;
        _log.Write("crash_injected", processId: pid);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        LastExitCode = process.ExitCode;
        Status = "STOPPED";
        return process.ExitCode;
    }

    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            Status = "STOPPED";
            DisposeTransport();
            return true;
        }

        var requestId = $"shutdown-{Guid.NewGuid():N}";
        var request = CreateRequest(requestId, "ipc-shutdown", new { });
        var graceful = false;
        try
        {
            var response = await Client.ExecuteAsync("v1/ipc/shutdown", request, cancellationToken);
            if (!response.Success || response.RequestId != requestId)
                throw new InvalidDataException("Invalid shutdown acknowledgement");
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            graceful = process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Write("worker_shutdown_fallback", requestId, processId: process.Id, errorCode: "SHUTDOWN_FALLBACK", details: new { ex.Message });
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }

        LastExitCode = SafeExitCode(process);
        Status = "STOPPED";
        _log.Write("worker_stopped", requestId, processId: process.Id, details: new { graceful, exit_code = LastExitCode });
        DisposeTransport();
        return graceful;
    }

    public IReadOnlyList<string> ListenerAddresses()
    {
        return IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Where(endpoint => endpoint.Port == Port)
            .Select(endpoint => endpoint.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(address => address, StringComparer.Ordinal)
            .ToArray();
    }

    public static AiRequest CreateRequest(string requestId, string operation, object config, IReadOnlyList<string>? inputPaths = null)
    {
        return new AiRequest(
            "v1",
            requestId,
            "ipc01-job",
            operation,
            inputPaths ?? [],
            JsonSerializer.SerializeToElement(config, StructuredLog.JsonOptions));
    }

    public static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false }) await StopAsync();
        }
        finally
        {
            DisposeTransport();
            _process?.Dispose();
            _process = null;
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static string GenerateSessionToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private void DisposeTransport()
    {
        _client = null;
        _http?.Dispose();
        _http = null;
    }

    private static int? SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch { return null; }
    }

    private static int? SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }
}

