using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PhotoAIFactory.Infrastructure;

namespace PhotoAIFactory.Gpu01;

internal sealed class ComfyGpuSupervisor : IAsyncDisposable
{
    private readonly string _pythonPath, _mainPath, _sourceRoot, _baseDirectory, _outputDirectory, _inputDirectory, _tempDirectory, _userDirectory;
    private readonly GpuLog _log;
    private Process? _process;
    private HttpClient? _http;
    private ComfyUiClient? _client;

    public ComfyGpuSupervisor(string pythonPath, string mainPath, string sourceRoot, string baseDirectory,
        string outputDirectory, string inputDirectory, string tempDirectory, string userDirectory, GpuLog log)
    {
        _pythonPath = pythonPath; _mainPath = mainPath; _sourceRoot = sourceRoot; _baseDirectory = baseDirectory;
        _outputDirectory = outputDirectory; _inputDirectory = inputDirectory; _tempDirectory = tempDirectory; _userDirectory = userDirectory; _log = log;
    }

    public Process Process => _process ?? throw new InvalidOperationException("ComfyUI has not started");
    public int ProcessId => Process.Id;
    public int Port { get; private set; }
    public bool IsAlive => _process is { HasExited: false };
    public ComfyUiClient Client => _client ?? throw new InvalidOperationException("ComfyUI is not ready");

    public async Task<string> StartAsync(TimeSpan timeout)
    {
        foreach (var path in new[] { _baseDirectory, _outputDirectory, _inputDirectory, _tempDirectory, _userDirectory }) Directory.CreateDirectory(path);
        Port = ReservePort();
        var arguments = new[] { "-s", _mainPath, "--listen", "127.0.0.1", "--port", Port.ToString(),
            "--disable-auto-launch", "--preview-method", "none", "--base-directory", _baseDirectory,
            "--output-directory", _outputDirectory, "--input-directory", _inputDirectory, "--temp-directory", _tempDirectory,
            "--user-directory", _userDirectory, "--disable-all-custom-nodes", "--whitelist-custom-nodes", "paf_gpu01" };
        var start = new ProcessStartInfo(_pythonPath) { WorkingDirectory = _sourceRoot, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PYTHONUNBUFFERED"] = "1";
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!_process.Start()) throw new InvalidOperationException("ComfyUI Process.Start returned false");
        _ = PumpAsync(_process.StandardOutput, "comfy_stdout"); _ = PumpAsync(_process.StandardError, "comfy_stderr");
        _log.Write("COMFYUI", null, "process_started", processId: _process.Id,
            details: new { port = Port, bind = "127.0.0.1", use_shell_execute = false, argument_list = true, disable_auto_launch = true });
        var baseUri = new Uri($"http://127.0.0.1:{Port}/");
        _http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
        _client = new ComfyUiClient(_http, new Uri($"ws://127.0.0.1:{Port}/"));
        var timer = Stopwatch.StartNew(); Exception? last = null;
        while (timer.Elapsed < timeout)
        {
            if (_process.HasExited) throw new InvalidOperationException($"ComfyUI exited during startup: {_process.ExitCode}");
            try { var stats = await Client.GetSystemStatsAsync(); _log.Write("COMFYUI", null, "ready", durationMs: timer.ElapsedMilliseconds, processId: ProcessId); return stats; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { last = ex; }
            await Task.Delay(150);
        }
        throw new TimeoutException($"ComfyUI readiness timed out: {last?.Message}");
    }

    public async Task<string> RunProbeAsync(int megabytes, int holdMs, string nonce, TimeSpan timeout)
    {
        var clientId = $"gpu01-{Guid.NewGuid():N}";
        var workflow = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["1"] = new { class_type = "PafGpu01CudaProbe", inputs = new { megabytes, hold_ms = holdMs, nonce } }
        });
        var promptId = await Client.SubmitPromptAsync(workflow, clientId);
        await Client.WaitForCompletionAsync(promptId, clientId, timeout);
        var history = await Client.GetHistoryAsync(promptId);
        using var document = JsonDocument.Parse(history);
        if (!document.RootElement.TryGetProperty(promptId, out var item)) throw new InvalidDataException("Comfy history missing prompt");
        if (!item.GetProperty("status").GetProperty("completed").GetBoolean()) throw new InvalidDataException("Comfy prompt not completed");
        return promptId;
    }

    public async Task FreeAsync()
    {
        using var response = await (_http ?? throw new InvalidOperationException()).PostAsync("free", new StringContent("{}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task<int> CrashAsync()
    {
        var process = Process;
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        _log.Write("COMFYUI", null, "process_crash_injected", processId: process.Id, errorCode: "GPU_OWNER_EXITED",
            details: new { exit_code = process.ExitCode, owned_pid_only = true });
        return process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false } process)
        {
            try { await Client.InterruptAsync(); await FreeAsync(); } catch { }
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            _log.Write("COMFYUI", null, "process_stopped", processId: process.Id, details: new { owned_pid_only = true, exit_code = process.ExitCode });
        }
        _http?.Dispose(); _process?.Dispose();
    }

    private async Task PumpAsync(StreamReader reader, string eventName)
    {
        while (await reader.ReadLineAsync() is { } line) _log.Write("COMFYUI", null, eventName, processId: ProcessId, details: new { message = line });
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
    }
}
