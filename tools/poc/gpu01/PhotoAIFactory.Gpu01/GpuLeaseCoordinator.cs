using System.Diagnostics;

namespace PhotoAIFactory.Gpu01;

internal sealed class GpuLeaseException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

internal sealed class GpuLeaseCoordinator(NvmlMonitor nvml, GpuLog log) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private LeaseState? _current;

    public string? CurrentOwner { get { lock (_sync) return _current?.Owner; } }
    public string? CurrentLeaseId { get { lock (_sync) return _current?.Id; } }

    public async Task<GpuLease> AcquireAsync(string owner, double requiredFreeMb, TimeSpan timeout,
        Process? ownerProcess = null, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await _gate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var mem = nvml.Snapshot();
            log.Write(owner, null, "lease_timeout", timer.ElapsedMilliseconds, timer.ElapsedMilliseconds, mem,
                ownerProcess is null ? null : SafePid(ownerProcess), "GPU_LEASE_TIMEOUT");
            throw new GpuLeaseException("GPU_LEASE_TIMEOUT", $"Timed out waiting {timeout.TotalMilliseconds:F0} ms for GPU lease", ex);
        }
        catch (OperationCanceledException)
        {
            var mem = nvml.Snapshot();
            log.Write(owner, null, "lease_cancelled", timer.ElapsedMilliseconds, timer.ElapsedMilliseconds, mem,
                ownerProcess is null ? null : SafePid(ownerProcess), "GPU_LEASE_CANCELLED");
            throw;
        }

        var memory = nvml.Snapshot();
        if (requiredFreeMb > memory.FreeMb)
        {
            _gate.Release();
            log.Write(owner, null, "lease_rejected_memory", timer.ElapsedMilliseconds, timer.ElapsedMilliseconds, memory,
                ownerProcess is null ? null : SafePid(ownerProcess), "GPU_INSUFFICIENT_MEMORY", new { required_free_mb = requiredFreeMb });
            throw new GpuLeaseException("GPU_INSUFFICIENT_MEMORY", $"Required {requiredFreeMb:F1} MB free, observed {memory.FreeMb:F1} MB");
        }

        var state = new LeaseState(Guid.NewGuid().ToString("N"), owner, Stopwatch.StartNew(), ownerProcess);
        lock (_sync) _current = state;
        if (ownerProcess is not null)
        {
            ownerProcess.EnableRaisingEvents = true;
            state.ExitHandler = (_, _) => Release(state, "lease_reclaimed_process_exit", "GPU_OWNER_EXITED");
            ownerProcess.Exited += state.ExitHandler;
            if (ownerProcess.HasExited) Release(state, "lease_reclaimed_process_exit", "GPU_OWNER_EXITED");
        }
        if (state.Released)
            throw new GpuLeaseException("GPU_OWNER_EXITED", $"Owner process for {owner} exited while acquiring the lease");
        log.Write(owner, state.Id, "lease_acquired", timer.ElapsedMilliseconds, 0, memory,
            ownerProcess is null ? null : SafePid(ownerProcess), details: new { required_free_mb = requiredFreeMb });
        return new GpuLease(this, state);
    }

    private void Release(LeaseState state, string eventName, string? errorCode = null)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_current, state) || state.Released) return;
            state.Released = true;
            _current = null;
        }
        if (state.Process is not null && state.ExitHandler is not null)
            state.Process.Exited -= state.ExitHandler;
        var memory = nvml.Snapshot();
        log.Write(state.Owner, state.Id, eventName, 0, state.Elapsed.ElapsedMilliseconds, memory,
            state.Process is null ? null : SafePid(state.Process), errorCode);
        _gate.Release();
    }

    public void Dispose() => _gate.Dispose();
    private static int? SafePid(Process process) { try { return process.Id; } catch { return null; } }

    internal sealed class LeaseState(string id, string owner, Stopwatch elapsed, Process? process)
    {
        public string Id { get; } = id; public string Owner { get; } = owner; public Stopwatch Elapsed { get; } = elapsed;
        public Process? Process { get; } = process; public EventHandler? ExitHandler { get; set; } public bool Released { get; set; }
    }

    internal sealed class GpuLease(GpuLeaseCoordinator owner, LeaseState state) : IAsyncDisposable, IDisposable
    {
        public string Id => state.Id; public string Owner => state.Owner;
        public void Dispose() => owner.Release(state, "lease_released");
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
