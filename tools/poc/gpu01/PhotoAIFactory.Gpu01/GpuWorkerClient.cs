using System.Diagnostics;
using System.Text.Json;

namespace PhotoAIFactory.Gpu01;

internal sealed class GpuWorkerClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly GpuLog _log;

    public GpuWorkerClient(string pythonPath, string workerPath, string modelPath, GpuLog log, bool allowKernelNetwork = false)
    {
        _log = log;
        var start = new ProcessStartInfo(pythonPath)
        {
            WorkingDirectory = Path.GetDirectoryName(workerPath)!, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(workerPath); start.ArgumentList.Add("--model-path"); start.ArgumentList.Add(modelPath);
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["HF_HUB_OFFLINE"] = allowKernelNetwork ? "0" : "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = allowKernelNetwork ? "0" : "1";
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!_process.Start()) throw new InvalidOperationException("GPU worker failed to start");
        _ = PumpErrorsAsync();
        _log.Write("PYTHON_AI", null, "worker_started", processId: _process.Id,
            details: new { python_path = pythonPath, use_shell_execute = false, argument_list = true });
    }

    public Process Process => _process;
    public int ProcessId => _process.Id;
    public bool IsAlive => !_process.HasExited;

    public async Task<JsonElement> CommandAsync(string command, object? parameters = null, TimeSpan? timeout = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new { request_id = id, command, parameters });
        await _process.StandardInput.WriteLineAsync(payload);
        await _process.StandardInput.FlushAsync();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        var line = await _process.StandardOutput.ReadLineAsync(cts.Token) ?? throw new EndOfStreamException("GPU worker stdout closed");
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement.Clone();
        if (root.GetProperty("request_id").GetString() != id) throw new InvalidDataException("GPU worker response id mismatch");
        if (!root.GetProperty("success").GetBoolean())
            throw new InvalidOperationException($"GPU worker {command} failed: {root.GetProperty("error").GetRawText()}");
        return root;
    }

    public async Task<int> CrashAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        _log.Write("PYTHON_AI", null, "worker_crash_injected", processId: _process.Id,
            errorCode: "GPU_OWNER_EXITED", details: new { exit_code = _process.ExitCode, owned_pid_only = true });
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try { await CommandAsync("exit", timeout: TimeSpan.FromSeconds(10)); }
            catch { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        _process.Dispose();
    }

    private async Task PumpErrorsAsync()
    {
        while (await _process.StandardError.ReadLineAsync() is { } line)
            _log.Write("PYTHON_AI", null, "worker_stderr", processId: ProcessId, details: new { message = line });
    }
}
