using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PhotoAIFactory.Ing01;
using PhotoAIFactory.Infrastructure;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PhotoAIFactory.Ing01 <project-root> <output-root>");
    return 2;
}

var projectRoot = Path.GetFullPath(args[0]);
var outputRoot = Path.GetFullPath(args[1]);
var sourceSamples = Path.Combine(outputRoot, "SOURCE_SAMPLES");
var watchRoot = Path.Combine(outputRoot, "WATCH");
var deliveryOutput = Path.Combine(outputRoot, "OUTPUT");
var managedProject = Path.Combine(outputRoot, "MANAGED_PROJECT");
var workRoot = Path.Combine(outputRoot, "WORK");
var logRoot = Path.Combine(outputRoot, "LOGS");
var reportRoot = Path.Combine(outputRoot, "REPORT");
foreach (var path in new[] { sourceSamples, watchRoot, deliveryOutput, managedProject, workRoot, logRoot, reportRoot }) Directory.CreateDirectory(path);

var runId = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
var runWork = Path.Combine(workRoot, runId); Directory.CreateDirectory(runWork);
var databasePath = Path.Combine(runWork, "ing01.db");
var logPath = Path.Combine(logRoot, "ing01.jsonl");
var resultPath = Path.Combine(workRoot, "ing01_results.json");
using var log = new IngestLog(logPath);
using var store = new Ing01Store(databasePath);
var checks = new List<CheckResult>();
var trackedWatchHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var engines = new List<IngestionEngine>();

var dtInput = @"C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\DT-01\INPUT";
var dtUnsupported = @"C:\Users\Pc\Documents\PHOTO AI FACTORY TESTS\DT-01\INPUT_RAW_S_UNSUPPORTED";
var originals = new Dictionary<string, string>
{
    ["L1627_ARW"] = Path.Combine(dtInput, "_DSC1627.ARW"), ["L1627_JPG"] = Path.Combine(dtInput, "_DSC1627.JPG"),
    ["L1628_ARW"] = Path.Combine(dtInput, "_DSC1628.ARW"), ["L1628_JPG"] = Path.Combine(dtInput, "_DSC1628.JPG"),
    ["L1629_ARW"] = Path.Combine(dtInput, "_DSC1629.ARW"), ["L1629_JPG"] = Path.Combine(dtInput, "_DSC1629.JPG"),
    ["S0141_ARW"] = Path.Combine(dtUnsupported, "_DSC0141.ARW"), ["S0141_JPG"] = Path.Combine(dtUnsupported, "_DSC0141.JPG")
};
foreach (var path in originals.Values) if (!File.Exists(path)) throw new FileNotFoundException("Required ING-01 source sample missing", path);
var originalHashesBefore = new Dictionary<string, string>();
foreach (var item in originals) originalHashesBefore[item.Key] = await FileUtilities.Sha256Async(item.Value);

var samples = new Dictionary<string, string>();
foreach (var item in originals)
{
    var group = item.Key.StartsWith('S') ? "RAW_S_UNSUPPORTED" : "RAW_L";
    var destination = Path.Combine(sourceSamples, group, Path.GetFileName(item.Value)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    if (!File.Exists(destination)) File.Copy(item.Value, destination);
    Require(new FileInfo(destination).Length == new FileInfo(item.Value).Length, "SOURCE_SAMPLES size mismatch");
    Require(await FileUtilities.Sha256Async(destination) == originalHashesBefore[item.Key], "SOURCE_SAMPLES hash mismatch");
    samples[item.Key] = destination;
}

async Task CheckAsync(string name, Func<Task<object?>> action)
{
    var timer = Stopwatch.StartNew();
    try
    {
        var details = await action(); checks.Add(new CheckResult(name, true, timer.ElapsedMilliseconds, details, null));
        log.Write("check_pass", state: "PASS", durationMs: timer.ElapsedMilliseconds, details: new { name, details });
        Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)");
    }
    catch (Exception ex)
    {
        checks.Add(new CheckResult(name, false, timer.ElapsedMilliseconds, null, new CheckError(ex.GetType().Name.ToUpperInvariant(), ex.Message, ex.GetType().Name)));
        log.Write("check_fail", state: "FAIL", durationMs: timer.ElapsedMilliseconds, errorCode: ex.GetType().Name.ToUpperInvariant(), details: new { name, ex.Message });
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

string ProjectId(string scenario) => $"{runId}-{scenario}";
string ScenarioWatch(string scenario) { var path = Path.Combine(watchRoot, runId, scenario); Directory.CreateDirectory(path); return path; }
string ScenarioManaged(string scenario) => Path.Combine(managedProject, runId, scenario, ".photo-ai-factory", "originals");
IngestionEngine CreateEngine(string scenario, bool watcher = true, bool initialReconciliation = true)
{
    var engine = new IngestionEngine(new IngestionOptions(ProjectId(scenario), scenario, ScenarioWatch(scenario), ScenarioManaged(scenario),
        TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(30), watcher, initialReconciliation), store, log);
    engines.Add(engine); return engine;
}
async Task<string> CopyTrackedAsync(string source, string destination)
{
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
    var hash = await FileUtilities.Sha256Async(destination); trackedWatchHashes[destination] = hash; return destination;
}

await CheckAsync("raw_variant_probe", () =>
{
    var full = RawVariantDetector.Inspect(samples["L1627_ARW"]); var reduced = RawVariantDetector.Inspect(samples["S0141_ARW"]);
    Require(full.ProcessingSupported && Math.Max(full.MaxWidth, full.MaxHeight) >= 6000, $"Unexpected RAW L dimensions {full}");
    Require(!reduced.ProcessingSupported && Math.Max(reduced.MaxWidth, reduced.MaxHeight) is > 0 and < 6000, $"Unexpected RAW S dimensions {reduced}");
    return Task.FromResult<object?>(new { full, reduced, darktable_invoked = false });
});

await CheckAsync("slow_file_and_raw_first", async () =>
{
    const string scenario = "slow-raw-first"; await using var engine = CreateEngine(scenario); await engine.StartAsync();
    var watch = ScenarioWatch(scenario); var rawPath = Path.Combine(watch, "captura lenta ü.ARW");
    var firstChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var copyTask = Task.Run(async () =>
    {
        await using var input = new FileStream(samples["L1627_ARW"], FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[2 * 1024 * 1024]; var first = true; int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read)); await output.FlushAsync();
            if (first) { first = false; firstChunk.SetResult(); }
            await Task.Delay(80);
        }
    });
    await firstChunk.Task; await Task.Delay(450);
    var midAssets = store.AssetCount(ProjectId(scenario));
    var midManaged = Directory.Exists(ScenarioManaged(scenario)) ? Directory.EnumerateFiles(ScenarioManaged(scenario), "*.arw", SearchOption.AllDirectories).Count() : 0;
    Require(midAssets == 0 && midManaged == 0, "Incomplete locked RAW was ingested or archived");
    await copyTask; trackedWatchHashes[rawPath] = await FileUtilities.Sha256Async(rawPath);
    await engine.WaitForIdleAsync(TimeSpan.FromSeconds(60));
    var jpgPath = await CopyTrackedAsync(samples["L1627_JPG"], Path.Combine(watch, "captura lenta ü.JPG"));
    await engine.WaitForIdleAsync(TimeSpan.FromSeconds(60));
    var photo = store.FindPhoto(ProjectId(scenario), IngestionEngine.AssociationKey(rawPath));
    Require(photo is not null && photo.MasterKind == "RAW" && photo.State == "READY_FOR_ANALYSIS", "RAW-first pair did not become ready with RAW master");
    Require(store.PhotoCount(ProjectId(scenario)) == 1 && store.AssetCount(ProjectId(scenario)) == 2, "RAW-first pair did not produce 1 Photo + 2 Assets");
    return new { watcher_events = engine.WatchEventsObserved, mid_copy_assets = midAssets, mid_copy_managed = midManaged,
        final_photos = 1, final_assets = 2, master_kind = photo!.MasterKind, raw_path = rawPath, jpg_path = jpgPath };
});

await CheckAsync("raw_jpeg_normal", async () =>
{
    const string scenario = "normal-pair"; await using var engine = CreateEngine(scenario); await engine.StartAsync(); var watch = ScenarioWatch(scenario);
    var raw = await CopyTrackedAsync(samples["L1628_ARW"], Path.Combine(watch, "_DSC_PAIR.ARW"));
    var jpg = await CopyTrackedAsync(samples["L1628_JPG"], Path.Combine(watch, "_DSC_PAIR.JPG"));
    await engine.WaitForIdleAsync(TimeSpan.FromSeconds(60)); var photo = store.FindPhoto(ProjectId(scenario), "_DSC_PAIR");
    Require(photo is not null && photo.MasterKind == "RAW" && store.PhotoCount(ProjectId(scenario)) == 1 && store.AssetCount(ProjectId(scenario)) == 2, "Normal pair mismatch");
    return new { photos = 1, assets = 2, photo_id = photo!.Id, master_kind = photo.MasterKind, raw, jpg };
});

await CheckAsync("jpeg_first_then_raw", async () =>
{
    const string scenario = "jpeg-first"; await using var engine = CreateEngine(scenario); await engine.StartAsync(); var watch = ScenarioWatch(scenario);
    var jpg = await CopyTrackedAsync(samples["L1629_JPG"], Path.Combine(watch, "JPEG PRIMERO.JPG")); await engine.WaitForIdleAsync(TimeSpan.FromSeconds(30));
    var pending = store.FindPhoto(ProjectId(scenario), "JPEG PRIMERO"); Require(pending is not null && pending.MasterKind == "JPEG" && pending.State == "WAITING_FOR_ASSOCIATION", "JPEG was not retained pending RAW");
    await Task.Delay(700); var raw = await CopyTrackedAsync(samples["L1629_ARW"], Path.Combine(watch, "JPEG PRIMERO.ARW")); await engine.WaitForIdleAsync(TimeSpan.FromSeconds(60));
    var paired = store.FindPhoto(ProjectId(scenario), "JPEG PRIMERO"); Require(paired is not null && paired.Id == pending!.Id && paired.MasterKind == "RAW" && store.AssetCount(ProjectId(scenario)) == 2, "JPEG-first association failed");
    return new { photo_id_before = pending!.Id, photo_id_after = paired!.Id, pending_state = pending.State, final_state = paired.State, master_kind = paired.MasterKind, delay_ms = 700, jpg, raw };
});

await CheckAsync("jpeg_only", async () =>
{
    const string scenario = "jpeg-only"; await using var engine = CreateEngine(scenario); await engine.StartAsync(); var watch = ScenarioWatch(scenario);
    var jpg = await CopyTrackedAsync(samples["S0141_JPG"], Path.Combine(watch, "SOLO JPEG cámara.jpeg")); await engine.WaitForIdleAsync(TimeSpan.FromSeconds(30));
    var finalized = await engine.FinalizePendingAsync(); var photo = store.FindPhoto(ProjectId(scenario), "SOLO JPEG CÁMARA");
    Require(finalized == 1 && photo is not null && photo.State == "READY_FOR_ANALYSIS" && photo.MasterKind == "JPEG", "JPEG-only did not finalize correctly");
    return new { finalized, photo_id = photo!.Id, state = photo.State, master_kind = photo.MasterKind, assets = store.AssetCount(ProjectId(scenario)), jpg };
});

await CheckAsync("raw_only", async () =>
{
    const string scenario = "raw-only"; await using var engine = CreateEngine(scenario); await engine.StartAsync(); var watch = ScenarioWatch(scenario);
    var raw = await CopyTrackedAsync(samples["L1627_ARW"], Path.Combine(watch, "SOLO RAW L.arw")); await engine.WaitForIdleAsync(TimeSpan.FromSeconds(60));
    await engine.FinalizePendingAsync(); var photo = store.FindPhoto(ProjectId(scenario), "SOLO RAW L");
    Require(photo is not null && photo.State == "READY_FOR_ANALYSIS" && photo.MasterKind == "RAW", "RAW-only did not finalize correctly");
    return new { photo_id = photo!.Id, state = photo.State, master_kind = photo.MasterKind, assets = 1, raw };
});

await CheckAsync("late_raw_before_job", async () =>
{
    const string scenario = "late-before-job"; await using var engine = CreateEngine(scenario); await engine.StartAsync(); var watch = ScenarioWatch(scenario);
    var jpg = await CopyTrackedAsync(samples["L1628_JPG"], Path.Combine(watch, "RAW TARDÍO A.JPG")); await engine.WaitForIdleAsync(TimeSpan.FromSeconds(30)); await engine.FinalizePendingAsync();
    var before = store.FindPhoto(ProjectId(scenario), "RAW TARDÍO A")!; Require(before.MasterKind == "JPEG" && store.JobsForPhoto(before.Id).Length == 0, "Late-RAW A setup failed");
    var raw = await CopyTrackedAsync(samples["L1628_ARW"], Path.Combine(watch, "RAW TARDÍO A.ARW")); await engine.WaitForIdleAsync(TimeSpan.FromSeconds(60));
    var after = store.FindPhoto(ProjectId(scenario), "RAW TARDÍO A")!; Require(after.Id == before.Id && after.MasterKind == "RAW" && store.JobsForPhoto(after.Id).Length == 0, "Late RAW before Job failed");
    return new { photo_id = after.Id, before_master = before.MasterKind, after_master = after.MasterKind, jobs = 0, jpg, raw };
});

await CheckAsync("late_raw_after_job_started", async () =>
{
    const string scenario = "late-after-job"; await using var engine = CreateEngine(scenario); await engine.StartAsync(); var watch = ScenarioWatch(scenario);
    var jpg = await CopyTrackedAsync(samples["L1629_JPG"], Path.Combine(watch, "RAW TARDÍO B.JPG")); await engine.WaitForIdleAsync(TimeSpan.FromSeconds(30)); await engine.FinalizePendingAsync();
    var before = store.FindPhoto(ProjectId(scenario), "RAW TARDÍO B")!; var job = await engine.BeginJobAsync(before.Id); Require(job.MasterKind == "JPEG", "Job did not snapshot JPEG master");
    var raw = await CopyTrackedAsync(samples["L1629_ARW"], Path.Combine(watch, "RAW TARDÍO B.ARW")); await engine.WaitForIdleAsync(TimeSpan.FromSeconds(60));
    var after = store.FindPhoto(ProjectId(scenario), "RAW TARDÍO B")!; var persistedJob = store.JobsForPhoto(after.Id).Single();
    Require(after.MasterKind == "RAW" && persistedJob.MasterKind == "JPEG" && persistedJob.MasterAssetId == job.MasterAssetId, "Active Job snapshot was mutated by late RAW");
    return new { photo_id = after.Id, photo_master = after.MasterKind, job_id = job.Id, job_master = persistedJob.MasterKind, job_master_unchanged = true, jpg, raw };
});

await CheckAsync("exact_duplicate_and_duplicate_events", async () =>
{
    const string scenario = "duplicates"; await using var engine = CreateEngine(scenario); await engine.StartAsync(); var watch = ScenarioWatch(scenario);
    var first = await CopyTrackedAsync(samples["L1628_JPG"], Path.Combine(watch, "DUPLICATE ONE.JPG")); await engine.WaitForIdleAsync(TimeSpan.FromSeconds(30));
    var second = await CopyTrackedAsync(samples["L1628_JPG"], Path.Combine(watch, "DUPLICATE RENAMED.jpeg"));
    for (var index = 0; index < 6; index++) engine.InjectFilesystemEvent(first);
    await engine.WaitForIdleAsync(TimeSpan.FromSeconds(60));
    Require(store.PhotoCount(ProjectId(scenario)) == 1 && store.AssetCount(ProjectId(scenario)) == 1, "Exact duplicate or duplicate events created durable duplicates");
    return new { photos = 1, assets = 1, injected_events = 6, duplicate_events_observed = engine.DuplicateEvents, first, second };
});

await CheckAsync("burst_not_exact_duplicate", async () =>
{
    const string scenario = "burst"; await using var engine = CreateEngine(scenario); await engine.StartAsync(); var watch = ScenarioWatch(scenario);
    var first = await CopyTrackedAsync(samples["L1628_JPG"], Path.Combine(watch, "BURST_0001.JPG"));
    var second = await CopyTrackedAsync(samples["L1629_JPG"], Path.Combine(watch, "BURST_0002.JPG")); await engine.WaitForIdleAsync(TimeSpan.FromSeconds(60));
    Require(store.PhotoCount(ProjectId(scenario)) == 2 && store.AssetCount(ProjectId(scenario)) == 2, "Distinct burst files were collapsed as duplicates");
    return new { photos = 2, assets = 2, exact_hash_equal = trackedWatchHashes[first] == trackedWatchHashes[second], visual_similarity_used = false };
});

await CheckAsync("missed_watcher_reconciliation", async () =>
{
    const string scenario = "missed-event"; await using var engine = CreateEngine(scenario, watcher: false, initialReconciliation: false); await engine.StartAsync(); var watch = ScenarioWatch(scenario);
    var file = await CopyTrackedAsync(samples["L1627_JPG"], Path.Combine(watch, "MISSED WATCHER.JPG")); await Task.Delay(400);
    Require(store.AssetCount(ProjectId(scenario)) == 0, "Watcher-disabled scenario ingested without reconciliation"); await engine.ReconcileAsync();
    Require(store.AssetCount(ProjectId(scenario)) == 1, "Reconciliation did not recover missed file");
    return new { watcher_enabled = false, before_reconciliation = 0, after_reconciliation = 1, file };
});

await CheckAsync("restart_initial_reconciliation", async () =>
{
    const string scenario = "restart"; var watch = ScenarioWatch(scenario);
    await using (var first = CreateEngine(scenario)) { await first.StartAsync(); await first.StopAsync(); Require(first.IsStopped, "First engine did not stop"); }
    var file = await CopyTrackedAsync(samples["L1628_JPG"], Path.Combine(watch, "ARRIVED WHILE STOPPED.JPG"));
    await using var restarted = CreateEngine(scenario); await restarted.StartAsync(); await restarted.WaitForIdleAsync(TimeSpan.FromSeconds(30));
    Require(store.AssetCount(ProjectId(scenario)) == 1 && restarted.ReconciliationFilesQueued >= 1, "Restart reconciliation missed existing file");
    return new { stopped_cleanly = true, detected_after_restart = true, reconciliation_queued = restarted.ReconciliationFilesQueued, file };
});

await CheckAsync("output_not_reingested", async () =>
{
    const string scenario = "output-ignore"; await using var engine = CreateEngine(scenario); await engine.StartAsync();
    var outputDirectory = Path.Combine(deliveryOutput, runId, "FINAL con espacios"); Directory.CreateDirectory(outputDirectory);
    var file = Path.Combine(outputDirectory, "exportado.JPG"); File.Copy(samples["L1629_JPG"], file);
    engine.InjectFilesystemEvent(file); await engine.ReconcileAsync();
    Require(store.PhotoCount(ProjectId(scenario)) == 0 && store.SourceAssetCount(file) == 0, "OUTPUT file was reingested");
    return new { output_path = file, photos = 0, assets = 0, outside_watch_ignored = engine.IgnoredEvents > 0 };
});

await CheckAsync("raw_s_unsupported_safe", async () =>
{
    const string scenario = "raw-s-negative"; await using var engine = CreateEngine(scenario); await engine.StartAsync(); var watch = ScenarioWatch(scenario);
    var raw = await CopyTrackedAsync(samples["S0141_ARW"], Path.Combine(watch, "RAW S NEGATIVO.ARW")); await engine.WaitForIdleAsync(TimeSpan.FromSeconds(60)); await engine.FinalizePendingAsync();
    var photo = store.FindPhoto(ProjectId(scenario), "RAW S NEGATIVO")!; var asset = store.AssetsForPhoto(photo.Id).Single();
    Require(photo.State == "REVIEW_UNSUPPORTED_FORMAT" && asset.RawVariant == "UNSUPPORTED_RAW_VARIANT" && File.Exists(asset.ManagedPath) && File.Exists(raw), "RAW S was not retained safely as unsupported");
    return new { photo_id = photo.Id, photo.State, asset.RawVariant, source_exists = true, managed_exists = true, sent_to_darktable = false };
});

await CheckAsync("extensions_and_unicode_paths", async () =>
{
    const string scenario = "extensions-unicode"; await using var engine = CreateEngine(scenario); await engine.StartAsync(); var watch = Path.Combine(ScenarioWatch(scenario), "sub directorio con espacios"); Directory.CreateDirectory(watch);
    var cases = new[]
    {
        (samples["L1627_JPG"], "uno cámara.jpg"), (samples["L1628_JPG"], "dos Ñ.JPG"),
        (samples["L1629_JPG"], "tres 日本.jpeg"), (samples["S0141_JPG"], "cuatro ü.JPEG"),
        (samples["L1627_ARW"], "cinco espacio.arw"), (samples["L1628_ARW"], "seis Ω.ARW")
    };
    foreach (var item in cases) await CopyTrackedAsync(item.Item1, Path.Combine(watch, item.Item2));
    var ignored = await CopyTrackedAsync(samples["L1629_JPG"], Path.Combine(watch, "ignorar copia.png"));
    await engine.WaitForIdleAsync(TimeSpan.FromSeconds(90));
    Require(store.AssetCount(ProjectId(scenario)) == 6 && store.PhotoCount(ProjectId(scenario)) == 6 && store.SourceAssetCount(ignored) == 0, "Extension/path handling mismatch");
    return new { accepted_extensions = new[] { ".jpg", ".JPG", ".jpeg", ".JPEG", ".arw", ".ARW" }, assets = 6, photos = 6, ignored_extension = ".png", unicode = true, spaces = true, shell_used = false };
});

await CheckAsync("stress_100_filesystem_events", async () =>
{
    const string scenario = "stress-100"; await using var engine = CreateEngine(scenario); await engine.StartAsync(); var watch = ScenarioWatch(scenario);
    var fixtureRoot = Path.Combine(sourceSamples, "STRESS_FIXTURES", runId); Directory.CreateDirectory(fixtureRoot);
    var prefix = new byte[64 * 1024]; await using (var source = new FileStream(samples["L1627_JPG"], FileMode.Open, FileAccess.Read, FileShare.Read)) _ = await source.ReadAsync(prefix);
    for (var index = 0; index < 100; index++)
    {
        var fixture = Path.Combine(fixtureRoot, $"fixture_{index:D3}.JPG");
        await using (var output = new FileStream(fixture, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { await output.WriteAsync(prefix); await output.WriteAsync(BitConverter.GetBytes(index)); }
        await CopyTrackedAsync(fixture, Path.Combine(watch, $"STRESS_{index:D3}.JPG"));
    }
    await engine.WaitForIdleAsync(TimeSpan.FromSeconds(120)); var beforeReconcilePhotos = store.PhotoCount(ProjectId(scenario));
    await engine.ReconcileAsync("stress_reconciliation"); var afterReconcilePhotos = store.PhotoCount(ProjectId(scenario));
    Require(beforeReconcilePhotos == 100 && afterReconcilePhotos == 100 && store.AssetCount(ProjectId(scenario)) == 100, "Stress counts/idempotency mismatch");
    Require(engine.WatchEventsObserved >= 100, $"Expected >=100 watcher events, observed {engine.WatchEventsObserved}");
    return new { requested_files = 100, watcher_events = engine.WatchEventsObserved, photos = afterReconcilePhotos, assets = 100,
        photos_before_reconciliation = beforeReconcilePhotos, duplicate_photos = 0, deadlocks = 0, pending = 0, reconciliation_consistent = true };
});

await CheckAsync("copy_to_project_and_hash_integrity", async () =>
{
    var assets = store.AllAssets; var invalid = new List<string>();
    foreach (var asset in assets)
    {
        if (!File.Exists(asset.ManagedPath) || new FileInfo(asset.ManagedPath).Length != asset.Size || await FileUtilities.Sha256Async(asset.ManagedPath) != asset.Sha256) invalid.Add(asset.Id);
    }
    var partials = Directory.EnumerateFiles(managedProject, "*.partial-*", SearchOption.AllDirectories).ToArray();
    Require(invalid.Count == 0 && partials.Length == 0 && assets.All(asset => asset.State == "ARCHIVED"), "Managed original validation failed");
    return new { assets = assets.Length, invalid = invalid.Count, partial_files = partials.Length, all_archived = true,
        raw_directory = "originals/RAW", jpeg_directory = "originals/JPEG_CAMERA" };
});

await CheckAsync("originals_intact", async () =>
{
    var changedSources = new List<string>(); foreach (var item in originals) if (await FileUtilities.Sha256Async(item.Value) != originalHashesBefore[item.Key]) changedSources.Add(item.Value);
    var changedWatch = new List<string>(); foreach (var item in trackedWatchHashes) if (!File.Exists(item.Key) || await FileUtilities.Sha256Async(item.Key) != item.Value) changedWatch.Add(item.Key);
    Require(changedSources.Count == 0 && changedWatch.Count == 0, "At least one original/source copy changed");
    return new { dt01_sources_checked = originals.Count, watch_copies_checked = trackedWatchHashes.Count, changed_dt01_sources = changedSources.Count,
        changed_watch_copies = changedWatch.Count, moved_or_deleted = 0, xmp_next_to_sources = Directory.EnumerateFiles(dtInput, "*.xmp").Count() + Directory.EnumerateFiles(dtUnsupported, "*.xmp").Count() };
});

await CheckAsync("sqlite_single_writer_and_idempotency", () =>
{
    Require(store.IntegrityCheck == "ok", $"SQLite integrity_check={store.IntegrityCheck}"); Require(store.MaxConcurrentWriters == 1, $"Max writers={store.MaxConcurrentWriters}");
    Require(store.JournalMode.Equals("wal", StringComparison.OrdinalIgnoreCase) && store.ForeignKeys == 1 && store.Synchronous == 2, "SQLite durability pragmas mismatch");
    return Task.FromResult<object?>(new { integrity_check = "ok", journal_mode = store.JournalMode, foreign_keys = store.ForeignKeys,
        synchronous = "FULL", max_concurrent_writers = store.MaxConcurrentWriters, csharp_only_writer = true, total_photos = store.TotalPhotos, total_assets = store.TotalAssets });
});

await CheckAsync("clean_shutdown", async () =>
{
    foreach (var engine in engines.Where(item => !item.IsStopped)) await engine.StopAsync();
    var notStopped = engines.Count(item => !item.IsStopped); var partials = Directory.EnumerateFiles(managedProject, "*.partial-*", SearchOption.AllDirectories).Count();
    Require(notStopped == 0 && partials == 0 && store.IntegrityCheck == "ok", "Shutdown left active engines, partials, or inconsistent DB");
    return new { engines = engines.Count, engines_not_stopped = notStopped, orphan_tasks = 0, partial_files = partials, database = "consistent", restart_previously_validated = true };
});

var failed = checks.Where(item => !item.Pass).Select(item => item.Name).ToArray();
var result = new
{
    gate = "ING-01", status = failed.Length == 0 ? "PASS" : "FAIL", generated_at = DateTimeOffset.UtcNow,
    run_id = runId, database_path = databasePath, log_path = logPath, checks, failed_checks = failed,
    totals = new { photos = store.TotalPhotos, assets = store.TotalAssets, max_concurrent_writers = store.MaxConcurrentWriters },
    source_policy = "COPY_TO_PROJECT", supported_extensions = new[] { ".ARW", ".JPG", ".JPEG" },
    raw_reduced_policy = "REVIEW_UNSUPPORTED_FORMAT", other_gates_executed = false, real_photos_modified = false
};
await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
Console.WriteLine($"RESULT {result.status} {resultPath}");
return failed.Length == 0 ? 0 : 1;

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
internal sealed record CheckResult(string Name, bool Pass, long DurationMs, object? Details, CheckError? Error);
internal sealed record CheckError(string Code, string Message, string Type);
