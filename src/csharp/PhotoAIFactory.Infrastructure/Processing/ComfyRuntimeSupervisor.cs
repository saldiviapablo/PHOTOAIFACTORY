using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Processing;

namespace PhotoAIFactory.Infrastructure.Processing;

public sealed class ComfyRuntimeSupervisor(
    IOptions<ComfyRuntimeOptions> options,
    ILogger<ComfyRuntimeSupervisor> logger,
    IComponentHealthTracker? healthTracker = null)
    : IComfyUiRuntime, IHostedService, IAsyncDisposable
{
    private readonly ComfyRuntimeOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private HttpClient? _http;
    private ComfyUiClient? _client;
    private int _disposed;

    public string InputDirectory =>
        Path.Combine(_options.RuntimeRoot, "input");
    public string OutputDirectory =>
        Path.Combine(_options.RuntimeRoot, "output");
    public int? ProcessId =>
        _process is { HasExited: false } value ? value.Id : null;
    public IComfyUiClient Client =>
        _client ?? throw new InvalidOperationException("ComfyUI is not running.");

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task EnsureReadyAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _client is not null)
            {
                _ = await _client.GetSystemStatsAsync(cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (_process is not null && _process.HasExited && healthTracker is not null)
            {
                if (!healthTracker.TryRequestRestart("ComfyUI", out var attempt))
                {
                    healthTracker.RecordFailure("ComfyUI", "Maximum automatic restart attempts exceeded.");
                    throw new ComfyStageException(
                        "COMFY_RESTART_EXHAUSTED",
                        "runtime",
                        "Maximum automatic restart attempts exceeded for ComfyUI.",
                        false);
                }
            }

            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);

            var pythonPath = Path.Combine(
                _options.ComponentRoot, "python_embeded", "python.exe");
            var sourceRoot = Path.Combine(
                _options.ComponentRoot, "ComfyUI");
            var mainPath = Path.Combine(sourceRoot, "main.py");
            if (!File.Exists(pythonPath) || !File.Exists(mainPath))
            {
                throw new ComfyStageException(
                    "COMFY_COMPONENT_MISSING",
                    "component",
                    $"Pinned ComfyUI runtime is missing. Expected {pythonPath} and {mainPath}.",
                    false);
            }

            var baseDirectory = Path.Combine(_options.RuntimeRoot, "base");
            var tempDirectory = Path.Combine(_options.RuntimeRoot, "temp");
            var userDirectory = Path.Combine(_options.RuntimeRoot, "user");
            foreach (var directory in new[]
                     {
                         _options.RuntimeRoot,
                         baseDirectory,
                         InputDirectory,
                         OutputDirectory,
                         tempDirectory,
                         userDirectory
                     })
                Directory.CreateDirectory(directory);

            var port = ReserveLoopbackPort();
            var baseAddress = new Uri($"http://127.0.0.1:{port}/");
            var wsAddress = new Uri($"ws://127.0.0.1:{port}/");

            var startInfo = new ProcessStartInfo(pythonPath)
            {
                WorkingDirectory = sourceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in new[]
                     {
                         "-s", mainPath,
                         "--listen", "127.0.0.1",
                         "--port", port.ToString(
                             System.Globalization.CultureInfo.InvariantCulture),
                         "--disable-auto-launch",
                         "--preview-method", "none",
                         "--base-directory", baseDirectory,
                         "--output-directory", OutputDirectory,
                         "--input-directory", InputDirectory,
                         "--temp-directory", tempDirectory,
                         "--user-directory", userDirectory,
                         "--disable-all-custom-nodes"
                     })
                startInfo.ArgumentList.Add(argument);
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    logger.LogDebug(
                        "ComfyUI[{Pid}] stdout: {Line}",
                        SafeProcessId(process),
                        args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    logger.LogDebug(
                        "ComfyUI[{Pid}] stderr: {Line}",
                        SafeProcessId(process),
                        args.Data);
            };

            if (!process.Start())
                throw new ComfyStageException(
                    "COMFY_PROCESS_START_FALSE",
                    "component",
                    "Process.Start returned false for ComfyUI.",
                    true);

            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _http = new HttpClient
            {
                BaseAddress = baseAddress,
                Timeout = TimeSpan.FromSeconds(10)
            };
            _client = new ComfyUiClient(_http, wsAddress);

            var deadline = Stopwatch.StartNew();
            Exception? lastError = null;
            while (deadline.Elapsed < _options.ReadinessTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new ComfyStageException(
                        "COMFY_EXITED_DURING_STARTUP",
                        "component",
                        $"ComfyUI exited during startup with code {process.ExitCode}.",
                        true);
                try
                {
                    var stats = await _client.GetSystemStatsAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(stats))
                    {
                        logger.LogInformation(
                            "ComfyUI ready on loopback port {Port}; PID {Pid}",
                            port,
                            process.Id);
                        healthTracker?.RecordSuccess("ComfyUI");
                        return;
                    }
                }
                catch (Exception ex)
                    when (ex is HttpRequestException or TaskCanceledException)
                {
                    lastError = ex;
                }
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }

            healthTracker?.RecordFailure("ComfyUI", "Startup timeout waiting for ComfyUI stats.");
            throw new ComfyStageException(
                "COMFY_READINESS_TIMEOUT",
                "component",
                $"ComfyUI did not become ready within {_options.ReadinessTimeout.TotalSeconds:F0}s.",
                true,
                lastError);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                healthTracker?.RecordFailure("ComfyUI", ex.Message);
            }
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task IHostedService.StopAsync(CancellationToken cancellationToken) =>
        await StopAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is { HasExited: false })
        {
            try
            {
                if (_client is not null)
                    await _client.InterruptAsync(cancellationToken)
                        .ConfigureAwait(false);
                if (_http is not null)
                {
                    using var response = await _http.PostAsync(
                        "free",
                        new StringContent(
                            "{}",
                            Encoding.UTF8,
                            "application/json"),
                        cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "ComfyUI API preparation before owned-process stop failed.");
            }

            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Owned ComfyUI process could not be stopped cleanly.");
            }
        }

        _client = null;
        _http?.Dispose();
        _http = null;
        _process?.Dispose();
        _process = null;
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static int? SafeProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return null;
        }
    }
}
