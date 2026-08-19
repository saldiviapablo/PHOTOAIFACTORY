using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using PhotoAIFactory.Infrastructure;

namespace PhotoAIFactory.Cui01;

internal sealed class ComfyStartException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

internal sealed record ControlledStopResult(bool ApiPreparationSucceeded, bool ProcessTerminated, int? ExitCode);

internal sealed class ComfySupervisor : IAsyncDisposable
{
    private readonly string _pythonPath;
    private readonly string _mainPath;
    private readonly string _sourceRoot;
    private readonly string _baseDirectory;
    private readonly string _internalOutput;
    private readonly string _inputDirectory;
    private readonly string _tempDirectory;
    private readonly string _userDirectory;
    private readonly StructuredLog _log;
    private Process? _process;
    private HttpClient? _http;
    private ComfyUiClient? _client;

    public ComfySupervisor(
        string pythonPath,
        string mainPath,
        string sourceRoot,
        string baseDirectory,
        string internalOutput,
        string inputDirectory,
        string tempDirectory,
        string userDirectory,
        StructuredLog log)
    {
        _pythonPath = pythonPath;
        _mainPath = mainPath;
        _sourceRoot = sourceRoot;
        _baseDirectory = baseDirectory;
        _internalOutput = internalOutput;
        _inputDirectory = inputDirectory;
        _tempDirectory = tempDirectory;
        _userDirectory = userDirectory;
        _log = log;
    }

    public string Status { get; private set; } = "STOPPED";
    public int? ProcessId { get; private set; }
    public int? LastExitCode { get; private set; }
    public int Port { get; private set; }
    public Uri? BaseAddress { get; private set; }
    public Uri? WebSocketBaseAddress { get; private set; }
    public bool UsesShellExecute => false;
    public bool UsesArgumentList => true;
    public bool AutoLaunchDisabled => true;
    public IReadOnlyList<string> LastArguments { get; private set; } = [];
    public List<int> OwnedProcessIds { get; } = [];
    public ComfyUiClient Client => _client ?? throw new InvalidOperationException("ComfyUI is not running");
    public HttpClient Http => _http ?? throw new InvalidOperationException("ComfyUI is not running");

    public async Task<string> StartAsync(TimeSpan readinessTimeout, CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false }) throw new InvalidOperationException("ComfyUI is already running");

        DisposeTransport();
        _process?.Dispose();
        _process = null;
        LastExitCode = null;
        Status = "STARTING";
        Port = ReserveLoopbackPort();
        BaseAddress = new Uri($"http://127.0.0.1:{Port}/");
        WebSocketBaseAddress = new Uri($"ws://127.0.0.1:{Port}/");

        foreach (var directory in new[] { _baseDirectory, _internalOutput, _inputDirectory, _tempDirectory, _userDirectory })
            Directory.CreateDirectory(directory);

        var arguments = new[]
        {
            "-s", _mainPath,
            "--listen", "127.0.0.1",
            "--port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--disable-auto-launch",
            "--preview-method", "none",
            "--base-directory", _baseDirectory,
            "--output-directory", _internalOutput,
            "--input-directory", _inputDirectory,
            "--temp-directory", _tempDirectory,
            "--user-directory", _userDirectory,
            "--disable-all-custom-nodes",
            "--whitelist-custom-nodes", "paf_cui01"
        };
        LastArguments = arguments;

        var startInfo = new ProcessStartInfo(_pythonPath)
        {
            WorkingDirectory = _sourceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                _log.Write("comfy_stdout", processId: SafeProcessId(process), details: new { message = eventArgs.Data });
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
                _log.Write("comfy_stderr", processId: SafeProcessId(process), details: new { message = eventArgs.Data });
        };
        process.Exited += (_, _) =>
        {
            var exitCode = SafeExitCode(process);
            LastExitCode = exitCode;
            if (ReferenceEquals(_process, process)) Status = "STOPPED";
            _log.Write("comfy_exited", processId: SafeProcessId(process), errorCode: exitCode == 0 ? null : "COMFY_EXITED", details: new { exit_code = exitCode });
        };

        var startup = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) throw new ComfyStartException("PROCESS_START_FALSE", "Process.Start returned false");
            _process = process;
            ProcessId = process.Id;
            OwnedProcessIds.Add(process.Id);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _log.Write("comfy_started", processId: process.Id, details: new
            {
                host = "127.0.0.1",
                port = Port,
                use_shell_execute = startInfo.UseShellExecute,
                argument_list_count = startInfo.ArgumentList.Count,
                disable_auto_launch = true
            });
        }
        catch (Exception ex) when (ex is not ComfyStartException)
        {
            Status = "STOPPED";
            process.Dispose();
            _log.Write("comfy_start_failed", durationMs: startup.ElapsedMilliseconds, errorCode: "PROCESS_START_ERROR", details: new { exception = ex.GetType().Name, ex.Message });
            throw new ComfyStartException("PROCESS_START_ERROR", ex.Message, ex);
        }

        _http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(10) };
        _client = new ComfyUiClient(_http, WebSocketBaseAddress);
        Exception? lastError = null;

        while (startup.Elapsed < readinessTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var exitCode = SafeExitCode(process);
                throw new ComfyStartException("COMFY_EXITED_DURING_STARTUP", $"ComfyUI exited during startup with code {exitCode}");
            }

            try
            {
                var stats = await _client.GetSystemStatsAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(stats))
                {
                    Status = "READY";
                    _log.Write("comfy_ready", durationMs: startup.ElapsedMilliseconds, processId: process.Id, details: new { port = Port });
                    return stats;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
            }

            await Task.Delay(150, cancellationToken);
        }

        throw new ComfyStartException("READINESS_TIMEOUT", $"ComfyUI did not become ready in {readinessTimeout.TotalSeconds:F1}s", lastError);
    }

    public async Task<int> CrashForTestAsync(CancellationToken cancellationToken = default)
    {
        var process = _process ?? throw new InvalidOperationException("ComfyUI is not running");
        if (process.HasExited) return process.ExitCode;
        var pid = process.Id;
        _log.Write("crash_injected", processId: pid);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        LastExitCode = process.ExitCode;
        Status = "STOPPED";
        return process.ExitCode;
    }

    public async Task<ControlledStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            Status = "STOPPED";
            DisposeTransport();
            return new ControlledStopResult(true, true, process is null ? null : SafeExitCode(process));
        }

        var apiPrepared = true;
        try
        {
            await Client.InterruptAsync(cancellationToken);
            using var freeResponse = await Http.PostAsync("free", new StringContent("{}", Encoding.UTF8, "application/json"), cancellationToken);
            freeResponse.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            apiPrepared = false;
            _log.Write("comfy_stop_api_warning", processId: process.Id, errorCode: "STOP_API_WARNING", details: new { ex.Message });
        }

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        LastExitCode = process.ExitCode;
        Status = "STOPPED";
        _log.Write("comfy_stopped", processId: process.Id, details: new { api_prepared = apiPrepared, exit_code = LastExitCode, owned_pid_only = true });
        DisposeTransport();
        return new ControlledStopResult(apiPrepared, true, LastExitCode);
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

