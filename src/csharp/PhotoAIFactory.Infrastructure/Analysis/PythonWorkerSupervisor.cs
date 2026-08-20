using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Contracts;

namespace PhotoAIFactory.Infrastructure.Analysis;

public sealed class PythonWorkerSupervisor(
    IOptions<AnalysisRuntimeOptions> options,
    IAppPaths paths) : IAsyncDisposable, IPythonAiClient
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? process;
    private HttpClient? http;
    private PythonAiClient? client;
    private string? token;
    private int disposed;

    public async Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await client!.GetHealthAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AiResponse> ExecuteAsync(
        string route,
        AiRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await client!.ExecuteAsync(route, request, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (process is { HasExited: false } && client is not null)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (process is { HasExited: false } && client is not null)
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            var settings = options.Value;
            if (!File.Exists(settings.PythonExecutablePath))
            {
                throw new FileNotFoundException("Isolated AI Worker Python runtime was not found.", settings.PythonExecutablePath);
            }

            var workerEntrypoint = ResolveWorkerEntrypoint(settings.WorkerEntrypointPath);
            var port = ReserveLoopbackPort();
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var start = new ProcessStartInfo
            {
                FileName = settings.PythonExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(workerEntrypoint);
            start.Environment["PAF_AI_HOST"] = "127.0.0.1";
            start.Environment["PAF_AI_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["PAF_AI_TOKEN"] = token;
            start.Environment["PAF_MODELS_ROOT"] = paths.ModelsDirectory;
            start.Environment["HF_HUB_OFFLINE"] = "1";
            start.Environment["TRANSFORMERS_OFFLINE"] = "1";
            start.Environment["HF_DATASETS_OFFLINE"] = "1";
            start.Environment["PAF_AI_LOG_LEVEL"] = "warning";

            process = new Process { StartInfo = start, EnableRaisingEvents = true };
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start isolated Python AI Worker.");
            }

            // Drain pipes so a verbose child cannot deadlock on redirected buffers.
            _ = DrainAsync(process.StandardOutput);
            _ = DrainAsync(process.StandardError);

            http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds)
            };
            client = new PythonAiClient(http, token);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.StartupTimeoutSeconds);
            Exception? last = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"Python AI Worker exited during startup with code {process.ExitCode}.");
                }

                try
                {
                    var health = await client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
                    if (string.Equals(health.Status, "HEALTHY", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(health.ApiVersion, "v1", StringComparison.Ordinal))
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    last = ex;
                }

                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("Python AI Worker did not become healthy before startup timeout.", last);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private async Task StopCoreAsync()
    {
        client = null;
        http?.Dispose();
        http = null;
        token = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // Disposal is best-effort; the OS process handle is still disposed below.
            }
            finally
            {
                process.Dispose();
                process = null;
            }
        }
    }


    private string ResolveWorkerEntrypoint(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var full = Path.GetFullPath(configuredPath);
            if (File.Exists(full))
            {
                return full;
            }

            throw new FileNotFoundException("Configured AI Worker entrypoint was not found.", full);
        }

        var componentCandidate = Path.Combine(
            paths.ComponentsDirectory, "ai-worker", "worker_entrypoint.py");
        if (File.Exists(componentCandidate))
        {
            return componentCandidate;
        }

        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            for (var depth = 0; current is not null && depth < 10; depth++, current = current.Parent)
            {
                var candidate = Path.Combine(
                    current.FullName, "src", "python", "ai-worker", "worker_entrypoint.py");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            "AI Worker entrypoint was not found in the installed component tree or repository.");
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

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0)
        {
            // Intentionally discard here; structured application logging remains the durable log boundary.
        }
    }
}
