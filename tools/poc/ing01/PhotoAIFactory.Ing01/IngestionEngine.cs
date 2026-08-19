using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using PhotoAIFactory.Infrastructure;

namespace PhotoAIFactory.Ing01;

internal sealed record IngestionOptions(string ProjectId, string ProjectName, string WatchRoot, string ManagedRoot,
    TimeSpan StableFor, TimeSpan StabilityTimeout, bool EnableWatcher = true, bool InitialReconciliation = true);

internal sealed class IngestionEngine : IAsyncDisposable
{
    private readonly IngestionOptions _options;
    private readonly Ing01Store _store;
    private readonly IngestLog _log;
    private readonly Channel<IngestRequest> _channel = Channel.CreateUnbounded<IngestRequest>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, byte> _scheduled = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FileStamp> _completed = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Task? _consumer;
    private int _pending;
    private bool _started;

    public IngestionEngine(IngestionOptions options, Ing01Store store, IngestLog log) { _options = options; _store = store; _log = log; }
    public int WatchEventsObserved { get; private set; }
    public int ReconciliationFilesQueued { get; private set; }
    public int InjectedEvents { get; private set; }
    public int WaitingTransitions { get; private set; }
    public int IngestedAssets { get; private set; }
    public int DuplicateEvents { get; private set; }
    public int IgnoredEvents { get; private set; }
    public int CoalescedEvents { get; private set; }
    public bool IsStopped { get; private set; }

    public async Task StartAsync()
    {
        if (_started) throw new InvalidOperationException("Engine already started");
        _started = true; IsStopped = false;
        Directory.CreateDirectory(_options.WatchRoot); Directory.CreateDirectory(_options.ManagedRoot);
        await _store.EnsureProjectAsync(_options.ProjectId, _options.ProjectName);
        _consumer = ConsumeAsync();
        if (_options.EnableWatcher)
        {
            _watcher = new FileSystemWatcher(_options.WatchRoot)
            {
                IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024, EnableRaisingEvents = true
            };
            _watcher.Created += WatcherEvent; _watcher.Changed += WatcherEvent; _watcher.Renamed += WatcherEvent;
            _watcher.Error += (_, args) => _log.Write("watcher_error", state: "RECONCILIATION_REQUIRED", errorCode: "WATCHER_ERROR", details: new { message = args.GetException().Message });
            _log.Write("watcher_started", path: _options.WatchRoot, state: "RUNNING");
        }
        if (_options.InitialReconciliation) await ReconcileAsync("initial_reconciliation");
    }

    public async Task ReconcileAsync(string reason = "manual_reconciliation")
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_options.WatchRoot, "*", SearchOption.AllDirectories))
        {
            Queue(path, reason); count++;
        }
        ReconciliationFilesQueued += count;
        _log.Write("reconciliation_scan", path: _options.WatchRoot, state: "QUEUED", details: new { reason, queued = count });
        await WaitForIdleAsync(TimeSpan.FromSeconds(60));
    }

    public void InjectFilesystemEvent(string path)
    {
        InjectedEvents++; Queue(path, "injected_watcher_event");
    }

    public Task<int> FinalizePendingAsync() => _store.FinalizePendingAsync(_options.ProjectId);
    public Task<JobRow> BeginJobAsync(string photoId) => _store.BeginJobAsync(_options.ProjectId, photoId);

    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            if (Volatile.Read(ref _pending) == 0)
            {
                await Task.Delay(250);
                if (Volatile.Read(ref _pending) == 0) return;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Ingestion queue did not become idle in {timeout.TotalSeconds:F0}s; pending={_pending}");
    }

    public async Task StopAsync()
    {
        if (IsStopped) return;
        _watcher?.Dispose(); _watcher = null;
        await WaitForIdleAsync(TimeSpan.FromSeconds(60));
        _channel.Writer.TryComplete();
        if (_consumer is not null) await _consumer.WaitAsync(TimeSpan.FromSeconds(60));
        IsStopped = true;
        _log.Write("watcher_stopped", path: _options.WatchRoot, state: "STOPPED", details: new { pending = _pending });
    }

    private void WatcherEvent(object sender, FileSystemEventArgs args) { WatchEventsObserved++; Queue(args.FullPath, "watcher"); }
    private void Queue(string path, string source)
    {
        path = Path.GetFullPath(path);
        if (_completed.TryGetValue(path, out var stamp) && File.Exists(path))
        {
            var info = new FileInfo(path);
            if (info.Length == stamp.Size && info.LastWriteTimeUtc == stamp.LastWriteTimeUtc)
            {
                CoalescedEvents++;
                _log.Write("event_coalesced_unchanged", path, state: "ALREADY_INGESTED", details: new { source });
                return;
            }
        }
        if (!_scheduled.TryAdd(path, 0))
        {
            CoalescedEvents++;
            _log.Write("event_coalesced_pending", path, state: "WAITING_FOR_FILE", details: new { source });
            return;
        }
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(new IngestRequest(path, source)))
        {
            _scheduled.TryRemove(path, out _);
            Interlocked.Decrement(ref _pending);
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (var request in _channel.Reader.ReadAllAsync())
        {
            try { await ProcessAsync(request); }
            catch (Exception ex)
            {
                _log.Write("ingest_error", request.Path, state: "ERROR", errorCode: ex.GetType().Name.ToUpperInvariant(), details: new { ex.Message, source = request.Source });
            }
            finally { _scheduled.TryRemove(request.Path, out _); Interlocked.Decrement(ref _pending); }
        }
    }

    private async Task ProcessAsync(IngestRequest request)
    {
        var timer = Stopwatch.StartNew();
        var fullPath = Path.GetFullPath(request.Path);
        if (!IsInsideWatch(fullPath)) { IgnoredEvents++; _log.Write("ignored_outside_watch", fullPath, state: "IGNORED", errorCode: "OUTSIDE_WATCH"); return; }
        var extension = Path.GetExtension(fullPath);
        if (!IsSupported(extension)) { IgnoredEvents++; _log.Write("ignored_extension", fullPath, state: "IGNORED", errorCode: "UNSUPPORTED_EXTENSION"); return; }
        if (!File.Exists(fullPath)) { IgnoredEvents++; _log.Write("ignored_missing", fullPath, state: "IGNORED", errorCode: "FILE_NOT_FOUND"); return; }

        WaitingTransitions++;
        _log.Write("waiting_for_file", fullPath, kind: Kind(extension), state: "WAITING_FOR_FILE", details: new { request.Source });
        var stable = await WaitForStableExclusiveAsync(fullPath, _options.StableFor, _options.StabilityTimeout);
        if (!stable) { _log.Write("file_stability_timeout", fullPath, kind: Kind(extension), state: "WAITING_FOR_FILE", durationMs: timer.ElapsedMilliseconds, errorCode: "FILE_NOT_STABLE"); return; }

        var info = new FileInfo(fullPath); var hash = await FileUtilities.Sha256Async(fullPath);
        RawVariant? variant = null;
        if (extension.Equals(".arw", StringComparison.OrdinalIgnoreCase)) variant = RawVariantDetector.Inspect(fullPath);
        var kind = Kind(extension);
        _log.Write("file_stable", fullPath, kind: kind, state: "STABLE", size: info.Length, sha256: hash,
            association: AssociationKey(fullPath), durationMs: timer.ElapsedMilliseconds,
            details: variant is null ? null : new { variant.Classification, variant.MaxWidth, variant.MaxHeight, variant.ProcessingSupported });

        var managedPath = await ArchiveAsync(fullPath, kind, info.Length, hash);
        var result = await _store.IngestAsync(_options.ProjectId, AssociationKey(fullPath), fullPath, managedPath, kind,
            info.Length, hash, variant?.Classification);
        if (result.Duplicate)
        {
            _completed[fullPath] = new FileStamp(info.Length, info.LastWriteTimeUtc);
            DuplicateEvents++;
            _log.Write("duplicate_exact", fullPath, result.Photo.Id, result.DuplicateAssetId, kind, result.Photo.State,
                info.Length, hash, result.Photo.AssociationKey, timer.ElapsedMilliseconds, "DUPLICATE_EXACT",
                new { request.Source, existing_managed_path = result.Asset.ManagedPath });
            return;
        }
        IngestedAssets++;
        _completed[fullPath] = new FileStamp(info.Length, info.LastWriteTimeUtc);
        _log.Write("asset_archived", fullPath, result.Photo.Id, result.Asset.Id, result.Asset.Kind, result.Photo.State,
            result.Asset.Size, result.Asset.Sha256, result.Photo.AssociationKey, timer.ElapsedMilliseconds,
            details: new { result.Asset.ManagedPath, result.Asset.RawVariant, master_kind = result.Photo.MasterKind, request.Source });
    }

    private async Task<string> ArchiveAsync(string sourcePath, string kind, long expectedSize, string expectedHash)
    {
        var directory = Path.Combine(_options.ManagedRoot, kind == "RAW" ? "RAW" : "JPEG_CAMERA");
        Directory.CreateDirectory(directory);
        var canonicalExtension = kind == "RAW" ? ".arw" : ".jpg";
        var destination = Path.Combine(directory, expectedHash + canonicalExtension);
        if (!File.Exists(destination))
        {
            var partial = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await input.CopyToAsync(output);
                await ValidateManagedAsync(partial, expectedSize, expectedHash);
                File.Move(partial, destination);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        await ValidateManagedAsync(destination, expectedSize, expectedHash);
        return destination;
    }

    private static async Task ValidateManagedAsync(string path, long expectedSize, string expectedHash)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedSize) throw new IOException("Managed original size validation failed");
        await using (var readable = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { _ = readable.ReadByte(); }
        var hash = await FileUtilities.Sha256Async(path);
        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Managed original SHA-256 validation failed");
    }

    private static async Task<bool> WaitForStableExclusiveAsync(string path, TimeSpan stableFor, TimeSpan timeout)
    {
        var timer = Stopwatch.StartNew(); long? previousLength = null; var stableSince = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            try
            {
                using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                var length = probe.Length;
                if (previousLength == length)
                {
                    if (stableSince.Elapsed >= stableFor) return true;
                }
                else { previousLength = length; stableSince.Restart(); }
            }
            catch (IOException) { previousLength = null; stableSince.Restart(); }
            catch (UnauthorizedAccessException) { previousLength = null; stableSince.Restart(); }
            await Task.Delay(50);
        }
        return false;
    }

    private bool IsInsideWatch(string path)
    {
        var root = Path.GetFullPath(_options.WatchRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
    internal static string AssociationKey(string path) => Path.GetFileNameWithoutExtension(path).Normalize().ToUpperInvariant();
    internal static bool IsSupported(string extension) => extension.Equals(".arw", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    private static string Kind(string extension) => extension.Equals(".arw", StringComparison.OrdinalIgnoreCase) ? "RAW" : "JPEG_CAMERA";
    public async ValueTask DisposeAsync() { if (!IsStopped) await StopAsync(); }
    private sealed record IngestRequest(string Path, string Source);
    private sealed record FileStamp(long Size, DateTime LastWriteTimeUtc);
}
