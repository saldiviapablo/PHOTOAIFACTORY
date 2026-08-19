using System.Diagnostics;
using PhotoAIFactory.Application;

namespace PhotoAIFactory.Infrastructure;

public sealed class ProcessRunner
{
    public async Task<ProcessExecutionResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var started = Stopwatch.StartNew();
        if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        started.Stop();
        return new ProcessExecutionResult(process.ExitCode, stdout, stderr, started.Elapsed);
    }
}
