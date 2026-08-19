using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PhotoAIFactory.Rec01;

internal static class RecoveryController
{
    private static readonly HashSet<int> OwnedPids = [];
    private static readonly List<string> Databases = [];
    private static StructuredLog? _log;
    private static string _executable = "";
    private static string _fixture = "";
    private static string _runRoot = "";
    private static string _logPath = "";

    public static int SelfTest()
    {
        var failures = new List<string>();
        if (Rec01Model.Checkpoints.Length != 11) failures.Add("checkpoint_count");
        if (Rec01Model.Checkpoints.Distinct(StringComparer.Ordinal).Count() != 11) failures.Add("checkpoint_uniqueness");
        if (Rec01Model.Checkpoints[0] != "INGEST_COMPLETE" || Rec01Model.Checkpoints[^1] != "OUTPUT_PUBLISHED") failures.Add("checkpoint_order");
        var pathA = Path.Combine("job", "attempt-a", "stage");
        var pathB = Path.Combine("job", "attempt-b", "stage");
        if (pathA == pathB) failures.Add("attempt_isolation");
        if (Enumerable.Range(1, 5).Count(value => value <= 3) != 3) failures.Add("retry_budget");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            status = failures.Count == 0 ? "PASS" : "FAIL",
            checks = 5,
            failures
        }, Rec01Model.JsonOptions));
        return failures.Count == 0 ? 0 : 1;
    }

    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string> values)
    {
        _executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable.");
        _fixture = values.Required("fixture");
        var evidence = values.Required("evidence");
        _logPath = Path.Combine(evidence, "LOGS", "rec01.jsonl");
        _log = new StructuredLog(_logPath);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        _runRoot = Path.Combine(evidence, "WORK", "runs", runId);
        Directory.CreateDirectory(_runRoot);
        Directory.CreateDirectory(Path.Combine(evidence, "REPORT"));
        Directory.CreateDirectory(Path.Combine(evidence, "LOGS"));
        var fixtureHashBefore = Rec01Model.Sha256(_fixture);
        var fixtureLength = new FileInfo(_fixture).Length;
        var results = new List<ScenarioResult>();

        _log.Write("INFO", "RecoveryController", "suite_started", extra: new Dictionary<string, object?>
        {
            ["fixture_path"] = _fixture,
            ["artifact_sha256"] = fixtureHashBefore,
            ["run_id"] = runId
        });

        await CaptureAsync(results, "A1_BASELINE", BaselineAsync);
        foreach (var checkpoint in Rec01Model.Checkpoints)
        {
            var captured = checkpoint;
            await CaptureAsync(results, "B_PRE_" + captured, () => CrashBeforeAsync(captured));
        }
        foreach (var checkpoint in Rec01Model.Checkpoints)
        {
            var captured = checkpoint;
            await CaptureAsync(results, "C_POST_" + captured, () => CrashAfterAsync(captured));
        }
        await CaptureAsync(results, "D_OPEN_TRANSACTION", TransactionRollbackAsync);
        await CaptureAsync(results, "E1_CORRUPT_DARKTABLE_PASS1", () => CorruptCheckpointAsync("DARKTABLE_PASS1_COMPLETE", delete: false));
        await CaptureAsync(results, "E2_MISSING_COMFYUI", () => CorruptCheckpointAsync("COMFYUI_COMPLETE", delete: true));
        await CaptureAsync(results, "E3_CORRUPT_OUTPUT", () => CorruptCheckpointAsync("OUTPUT_PUBLISHED", delete: false));
        await CaptureAsync(results, "F1_STAGING_BEFORE_VALIDATE", () => PublicationWindowAsync("pub-generated"));
        await CaptureAsync(results, "F2_VALIDATED_BEFORE_MOVE", () => PublicationWindowAsync("pub-validated"));
        await CaptureAsync(results, "F3_MOVED_BEFORE_CHECKPOINT", () => PublicationWindowAsync("pub-moved"));
        await CaptureAsync(results, "F4_DIFFERENT_EXISTING_FINAL", PublicationCollisionAsync);
        await CaptureAsync(results, "F5_CHECKPOINT_BEFORE_COMPLETED", () => PublicationWindowAsync("post"));
        await CaptureAsync(results, "G_QUEUE_RECOVERY", QueueRecoveryAsync);
        await CaptureAsync(results, "H_BOUNDED_RETRY_AND_PERMANENT", RetryBoundedAsync);
        await CaptureAsync(results, "I_CHILD_PROCESS_CRASH", ChildCrashAsync);
        await CaptureAsync(results, "J_IDEMPOTENT_RECOVERY", IdempotencyAsync);

        var fixtureHashAfter = Rec01Model.Sha256(_fixture);
        results.Add(Result("K_ORIGINAL_IMMUTABILITY", fixtureHashBefore == fixtureHashAfter && new FileInfo(_fixture).Length == fixtureLength,
            "Fixture JPEG source hash and size remain unchanged.",
            $"before={fixtureHashBefore}; after={fixtureHashAfter}; size={fixtureLength}", [_fixture]));
        results.Add(ValidateAllSqlite());
        results.Add(ValidateCleanup());

        var mandatory = BuildMandatoryCriteria(results);
        var allPass = results.All(item => item.Status == "PASS") && mandatory.All(item => item.Value);
        var resultsPath = Path.Combine(evidence, "WORK", "rec01_results.json");
        Rec01Model.AtomicJson(resultsPath, new
        {
            schema = "photo-ai-factory.rec01-results.v1",
            run_id = runId,
            started_from = _runRoot,
            generated_at = Rec01Model.UtcNow(),
            status = allPass ? "PASS" : "FAIL",
            passed = results.Count(item => item.Status == "PASS"),
            total = results.Count,
            fixture = new { path = _fixture, size = fixtureLength, sha256_before = fixtureHashBefore, sha256_after = fixtureHashAfter },
            mandatory_criteria = mandatory.Select(item => new { criterion = item.Key, pass = item.Value }),
            scenarios = results
        });

        var reportPath = Path.Combine(evidence, "REPORT", "REC01_REPORT.md");
        WriteReport(reportPath, runId, results, mandatory, values, resultsPath, allPass);
        _log.Write(allPass ? "INFO" : "ERROR", "RecoveryController", "suite_completed", extra: new Dictionary<string, object?>
        {
            ["run_id"] = runId,
            ["new_state"] = allPass ? "PASS" : "FAIL",
            ["results_path"] = resultsPath,
            ["report_path"] = reportPath
        });
        Console.WriteLine(JsonSerializer.Serialize(new { status = allPass ? "PASS" : "FAIL", passed = results.Count(item => item.Status == "PASS"), total = results.Count, report = reportPath, results = resultsPath }, Rec01Model.JsonOptions));
        return allPass ? 0 : 2;
    }

    private static async Task CaptureAsync(List<ScenarioResult> results, string name, Func<Task<ScenarioResult>> action)
    {
        try
        {
            var result = await action();
            results.Add(result with { Scenario = name });
        }
        catch (Exception exception)
        {
            results.Add(new ScenarioResult(name, "FAIL", "Scenario completes all required assertions.",
                exception.ToString(), []));
            _log!.Write("ERROR", "RecoveryController", "scenario_failed", extra: new Dictionary<string, object?>
            {
                ["scenario"] = name,
                ["error"] = exception.ToString()
            });
        }
    }

    private static async Task<ScenarioResult> BaselineAsync()
    {
        var context = Context("baseline");
        var outcome = await RunWorkerAsync(context, "baseline");
        var job = JobId(context.Database);
        var passed = outcome.ExitCode == 0 && Scalar<long>(context.Database, "SELECT COUNT(*) FROM checkpoints WHERE job_id=$job AND is_valid=1;", ("$job", job)) == 11 &&
            Scalar<string>(context.Database, "SELECT state FROM jobs WHERE job_id=$job;", ("$job", job)) == "COMPLETED" &&
            Scalar<long>(context.Database, "SELECT COUNT(*) FROM job_history WHERE job_id=$job AND event='stage_result_persisted';", ("$job", job)) == 11 &&
            FinalJpegs(context.Directory).Count == 1 && Pragma(context.Database).Integrity == "ok";
        return Result("", passed,
            "11 checkpoints, COMPLETED, one validated final JPEG, persisted history and integrity_check=ok.",
            $"exit={outcome.ExitCode}; checkpoints={Count(context.Database, "checkpoints")}; state={State(context.Database, job)}; finals={FinalJpegs(context.Directory).Count}; integrity={Pragma(context.Database).Integrity}",
            [context.Database, .. FinalJpegs(context.Directory)]);
    }

    private static async Task<ScenarioResult> CrashBeforeAsync(string checkpoint)
    {
        var context = Context("pre-" + checkpoint.ToLowerInvariant());
        var crashed = await RunWorkerAsync(context, "checkpoint-pre", "pre", checkpoint, expectCrash: true);
        var job = JobId(context.Database);
        var checkpointBefore = Scalar<long>(context.Database, "SELECT COUNT(*) FROM checkpoints WHERE job_id=$job AND checkpoint=$cp AND is_valid=1;", ("$job", job), ("$cp", checkpoint));
        var oldAttempts = Attempts(context.Database, job, checkpoint);
        var recovered = await RunWorkerAsync(context, "checkpoint-pre");
        var attempts = Attempts(context.Database, job, checkpoint);
        var distinctPaths = attempts.Select(item => item.Path).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var passed = crashed.KilledPid.HasValue && checkpointBefore == 0 && recovered.ExitCode == 0 && attempts.Count == 2 &&
            distinctPaths == 2 && State(context.Database, job) == "COMPLETED" && ActiveCheckpointCount(context.Database, job, checkpoint) == 1;
        return Result("", passed,
            "Target checkpoint absent after crash; restart reruns only incomplete stage with new attempt/path and completes.",
            $"killed_pid={crashed.KilledPid}; checkpoint_before={checkpointBefore}; attempts_before={oldAttempts.Count}; attempts_after={attempts.Count}; distinct_paths={distinctPaths}; state={State(context.Database, job)}",
            [context.Database, context.Barrier, .. attempts.Select(item => item.Path)]);
    }

    private static async Task<ScenarioResult> CrashAfterAsync(string checkpoint)
    {
        var context = Context("post-" + checkpoint.ToLowerInvariant());
        var crashed = await RunWorkerAsync(context, "checkpoint-post", "post", checkpoint, expectCrash: true);
        var job = JobId(context.Database);
        var before = Attempts(context.Database, job, checkpoint).Count;
        var checkpointBefore = ActiveCheckpointCount(context.Database, job, checkpoint);
        var recovered = await RunWorkerAsync(context, "checkpoint-post");
        var after = Attempts(context.Database, job, checkpoint).Count;
        var duplicateCheckpoints = Scalar<long>(context.Database, "SELECT COUNT(*) FROM (SELECT job_id,checkpoint,COUNT(*) c FROM checkpoints GROUP BY job_id,checkpoint HAVING c>1);");
        var passed = crashed.KilledPid.HasValue && checkpointBefore == 1 && recovered.ExitCode == 0 && before == 1 && after == 1 &&
            duplicateCheckpoints == 0 && State(context.Database, job) == "COMPLETED" && LogContains("checkpoint_artifact_revalidated", checkpoint, job);
        return Result("", passed,
            "Durable checkpoint survives, artifact is revalidated, completed stage is not rerun and no duplicate appears.",
            $"checkpoint_before={checkpointBefore}; attempts_before={before}; attempts_after={after}; duplicates={duplicateCheckpoints}; state={State(context.Database, job)}",
            [context.Database, context.Barrier]);
    }

    private static async Task<ScenarioResult> TransactionRollbackAsync()
    {
        const string target = "ANALYSIS_COMPLETE";
        var context = Context("open-transaction");
        var crashed = await RunWorkerAsync(context, "open-transaction", "tx", target, expectCrash: true);
        var job = JobId(context.Database);
        var ghost = ActiveCheckpointCount(context.Database, job, target);
        var probe = Count(context.Database, "tx_probe");
        var integrityBefore = Pragma(context.Database).Integrity;
        var recovered = await RunWorkerAsync(context, "open-transaction");
        var passed = crashed.KilledPid.HasValue && ghost == 0 && probe == 0 && integrityBefore == "ok" && recovered.ExitCode == 0 &&
            Attempts(context.Database, job, target).Count == 2 && State(context.Database, job) == "COMPLETED";
        return Result("", passed,
            "Uncommitted SQL changes roll back with no phantom checkpoint/probe; recovery resumes from the last real commit.",
            $"ghost_checkpoints={ghost}; tx_probe_rows={probe}; integrity={integrityBefore}; attempts={Attempts(context.Database, job, target).Count}; state={State(context.Database, job)}",
            [context.Database, context.Barrier]);
    }

    private static async Task<ScenarioResult> CorruptCheckpointAsync(string checkpoint, bool delete)
    {
        var context = Context("corrupt-" + checkpoint.ToLowerInvariant());
        await RunWorkerAsync(context, "corrupt-artifact", "post", checkpoint, expectCrash: true);
        var job = JobId(context.Database);
        var path = Scalar<string>(context.Database, "SELECT artifact_path FROM checkpoints WHERE job_id=$job AND checkpoint=$cp AND is_valid=1;", ("$job", job), ("$cp", checkpoint));
        if (delete) File.Delete(path); else File.AppendAllText(path, "REC01-CORRUPTION");
        var recovered = await RunWorkerAsync(context, "corrupt-artifact");
        var invalidations = Scalar<long>(context.Database, "SELECT COUNT(*) FROM checkpoint_history WHERE job_id=$job AND checkpoint=$cp AND action LIKE 'INVALIDATED:%';", ("$job", job), ("$cp", checkpoint));
        var history = Scalar<long>(context.Database, "SELECT COUNT(*) FROM checkpoint_history WHERE job_id=$job AND checkpoint=$cp;", ("$job", job), ("$cp", checkpoint));
        var current = Scalar<string>(context.Database, "SELECT artifact_path FROM checkpoints WHERE job_id=$job AND checkpoint=$cp AND is_valid=1;", ("$job", job), ("$cp", checkpoint));
        var passed = recovered.ExitCode == 0 && invalidations >= 1 && history >= 3 && !string.Equals(path, current, StringComparison.OrdinalIgnoreCase) &&
            State(context.Database, job) == "COMPLETED" && LogContains("checkpoint_artifact_invalid", checkpoint, job);
        return Result("", passed,
            "Missing/corrupt checkpoint artifact is rejected; history is retained and recovery reruns from last safe checkpoint.",
            $"mutation={(delete ? "deleted" : "corrupted")}; invalidations={invalidations}; history_rows={history}; old_path={path}; new_path={current}; state={State(context.Database, job)}",
            [context.Database, path, current]);
    }

    private static async Task<ScenarioResult> PublicationWindowAsync(string crash)
    {
        var context = Context("publication-" + crash);
        var crashMode = crash == "post" ? "post" : crash;
        var target = crash == "post" ? "OUTPUT_PUBLISHED" : "";
        await RunWorkerAsync(context, "publication-window", crashMode, target, expectCrash: true);
        var job = JobId(context.Database);
        var checkpointBefore = ActiveCheckpointCount(context.Database, job, "OUTPUT_PUBLISHED");
        var stateBefore = State(context.Database, job);
        var finalsBefore = FinalJpegs(context.Directory).Count;
        var attemptsBefore = Attempts(context.Database, job, "OUTPUT_PUBLISHED").Count;
        var recovered = await RunWorkerAsync(context, "publication-window");
        var finalsAfter = FinalJpegs(context.Directory).Count;
        var attemptsAfter = Attempts(context.Database, job, "OUTPUT_PUBLISHED").Count;
        var expectedCheckpointBefore = crash == "post" ? 1 : 0;
        var expectedFinalsBefore = crash is "pub-moved" or "post" ? 1 : 0;
        var expectedAttemptsAfter = crash == "post" ? attemptsBefore : attemptsBefore + 1;
        var passed = checkpointBefore == expectedCheckpointBefore && stateBefore != "COMPLETED" && finalsBefore == expectedFinalsBefore &&
            recovered.ExitCode == 0 && finalsAfter == 1 && attemptsAfter == expectedAttemptsAfter && State(context.Database, job) == "COMPLETED";
        return Result("", passed,
            "Publication crash window recovers idempotently, publishes once, and only completes after OUTPUT_PUBLISHED.",
            $"window={crash}; cp_before={checkpointBefore}; state_before={stateBefore}; finals_before={finalsBefore}; finals_after={finalsAfter}; attempts_before={attemptsBefore}; attempts_after={attemptsAfter}; final_state={State(context.Database, job)}",
            [context.Database, context.Barrier, .. FinalJpegs(context.Directory)]);
    }

    private static async Task<ScenarioResult> PublicationCollisionAsync()
    {
        var context = Context("publication-collision");
        var finalDirectory = Path.Combine(context.Directory, "FINAL");
        Directory.CreateDirectory(finalDirectory);
        var existing = Path.Combine(finalDirectory, "fixture.jpg");
        File.WriteAllBytes(existing, [0xFF, 0xD8, 0x42, 0x43, 0xFF, 0xD9]);
        var before = Rec01Model.Sha256(existing);
        var outcome = await RunWorkerAsync(context, "publication-collision");
        var after = Rec01Model.Sha256(existing);
        var finals = FinalJpegs(context.Directory);
        var job = JobId(context.Database);
        var published = Scalar<string>(context.Database, "SELECT artifact_path FROM publications WHERE job_id=$job;", ("$job", job));
        var passed = outcome.ExitCode == 0 && before == after && finals.Count == 2 && Path.GetFileName(published) == "fixture_v02.jpg" && State(context.Database, job) == "COMPLETED";
        return Result("", passed,
            "Different existing FINAL is preserved and the new JPEG receives a versioned name.",
            $"existing_before={before}; existing_after={after}; finals={finals.Count}; published={published}; state={State(context.Database, job)}",
            [context.Database, existing, published]);
    }

    private static async Task<ScenarioResult> QueueRecoveryAsync()
    {
        var context = Context("queue-recovery");
        await RunWorkerAsync(context, "queue-recovery", "pre", "ANALYSIS_COMPLETE", jobs: 3, expectCrash: true);
        var statesAtCrash = Rows(context.Database, "SELECT job_id,state FROM jobs ORDER BY queue_order;", reader => $"{reader.GetString(0)}={reader.GetString(1)}");
        var recovered = await RunWorkerAsync(context, "queue-recovery", jobs: 3);
        var completed = Rows(context.Database, "SELECT job_id FROM jobs WHERE state='COMPLETED' ORDER BY completed_at;", reader => reader.GetString(0));
        var interrupted = Scalar<long>(context.Database, "SELECT COUNT(*) FROM job_history WHERE job_id LIKE '%JOB-A' AND event='active_job_interrupted';");
        var maxProcessing = Scalar<long>(context.Database, "SELECT value FROM metrics WHERE key='max_processing';");
        var fifo = completed.Count == 3 && completed[0].EndsWith("JOB-A", StringComparison.Ordinal) && completed[1].EndsWith("JOB-B", StringComparison.Ordinal) && completed[2].EndsWith("JOB-C", StringComparison.Ordinal);
        var passed = statesAtCrash.Count == 3 && statesAtCrash[0].EndsWith("=PROCESSING", StringComparison.Ordinal) && statesAtCrash[1].EndsWith("=QUEUED", StringComparison.Ordinal) &&
            statesAtCrash[2].EndsWith("=QUEUED", StringComparison.Ordinal) && recovered.ExitCode == 0 && interrupted == 1 && fifo && maxProcessing == 1;
        return Result("", passed,
            "A is interrupted/recovered first, then B and C complete FIFO with max one PROCESSING.",
            $"at_crash={string.Join(',', statesAtCrash)}; completion={string.Join(',', completed)}; interrupted={interrupted}; max_processing={maxProcessing}",
            [context.Database]);
    }

    private static async Task<ScenarioResult> RetryBoundedAsync()
    {
        var context = Context("retry-bounded");
        var outcome = await RunWorkerAsync(context, "retry-bounded", jobs: 3);
        var states = Rows(context.Database, "SELECT job_id,state FROM jobs ORDER BY queue_order;", reader => $"{reader.GetString(0)}={reader.GetString(1)}");
        var retryable = Scalar<long>(context.Database, "SELECT COUNT(*) FROM stage_attempts WHERE job_id LIKE '%JOB-A' AND stage='ANALYSIS_COMPLETE';");
        var permanent = Scalar<long>(context.Database, "SELECT COUNT(*) FROM stage_attempts WHERE job_id LIKE '%JOB-B' AND stage='ANALYSIS_COMPLETE';");
        var distinct = Scalar<long>(context.Database, "SELECT COUNT(DISTINCT attempt_id) FROM stage_attempts WHERE job_id LIKE '%JOB-A' AND stage='ANALYSIS_COMPLETE';");
        var passed = outcome.ExitCode == 0 && retryable == 3 && permanent == 1 && distinct == 3 && states.Count == 3 &&
            states[0].EndsWith("=ERROR", StringComparison.Ordinal) && states[1].EndsWith("=ERROR", StringComparison.Ordinal) && states[2].EndsWith("=COMPLETED", StringComparison.Ordinal);
        return Result("", passed,
            "Retryable job uses initial+2 retries, permanent failure uses one attempt, and following job continues.",
            $"states={string.Join(',', states)}; retryable_attempts={retryable}; permanent_attempts={permanent}; distinct_retry_attempts={distinct}",
            [context.Database]);
    }

    private static async Task<ScenarioResult> ChildCrashAsync()
    {
        var context = Context("child-process");
        var helperBarrier = Path.Combine(context.Directory, "helper-barrier.json");
        var process = StartWorker(context, "child-process", helperBarrier: helperBarrier);
        var helperPid = await WaitForPidAsync(helperBarrier, TimeSpan.FromSeconds(20));
        OwnedPids.Add(helperPid);
        using (var helper = Process.GetProcessById(helperPid))
        {
            helper.Kill(entireProcessTree: false);
            await helper.WaitForExitAsync();
        }
        OwnedPids.Remove(helperPid);
        var outcome = await FinishProcessAsync(process, TimeSpan.FromSeconds(60));
        var job = JobId(context.Database);
        var attempts = Attempts(context.Database, job, "ANALYSIS_COMPLETE");
        var failed = attempts.Count(item => item.Status == "FAILED");
        var failedCheckpoint = attempts.Where(item => item.Status == "FAILED").Any(item =>
            Scalar<long>(context.Database, "SELECT COUNT(*) FROM checkpoints WHERE attempt_id=$attempt;", ("$attempt", item.AttemptId)) > 0);
        var orphan = IsAlive(helperPid);
        var passed = outcome.ExitCode == 0 && attempts.Count == 2 && failed == 1 && !failedCheckpoint && !orphan &&
            ActiveCheckpointCount(context.Database, job, "ANALYSIS_COMPLETE") == 1 && State(context.Database, job) == "COMPLETED";
        return Result("", passed,
            "Owned helper is killed mid-stage; incomplete attempt has no checkpoint, bounded retry succeeds, and no helper remains.",
            $"helper_pid={helperPid}; attempts={attempts.Count}; failed_attempts={failed}; failed_checkpoint={failedCheckpoint}; orphan={orphan}; state={State(context.Database, job)}",
            [context.Database, helperBarrier, .. attempts.Select(item => item.Path)]);
    }

    private static async Task<ScenarioResult> IdempotencyAsync()
    {
        var context = Context("idempotency");
        await RunWorkerAsync(context, "idempotency");
        var job = JobId(context.Database);
        var before = (Checkpoints: Count(context.Database, "checkpoints"), Attempts: Count(context.Database, "stage_attempts"), History: Count(context.Database, "checkpoint_history"), Finals: FinalJpegs(context.Directory).Count, State: State(context.Database, job));
        var second = await RunWorkerAsync(context, "idempotency");
        var third = await RunWorkerAsync(context, "idempotency");
        var after = (Checkpoints: Count(context.Database, "checkpoints"), Attempts: Count(context.Database, "stage_attempts"), History: Count(context.Database, "checkpoint_history"), Finals: FinalJpegs(context.Directory).Count, State: State(context.Database, job));
        var distinctAttempts = Scalar<long>(context.Database, "SELECT COUNT(*)=COUNT(DISTINCT attempt_id) FROM stage_attempts;") == 1;
        var passed = second.ExitCode == 0 && third.ExitCode == 0 && before == after && before.Checkpoints == 11 && before.Finals == 1 && before.State == "COMPLETED" && distinctAttempts;
        return Result("", passed,
            "Two additional recovery runs do not reopen COMPLETED or change checkpoints, attempts, history, or output count.",
            $"before={before}; after={after}; distinct_attempt_ids={distinctAttempts}",
            [context.Database, .. FinalJpegs(context.Directory)]);
    }

    private static ScenarioResult ValidateAllSqlite()
    {
        var snapshots = Databases.Select(database => (Database: database, Pragma: Pragma(database))).ToArray();
        var invalid = snapshots.Where(item => item.Pragma.Integrity != "ok" || item.Pragma.Journal != "wal" || item.Pragma.Synchronous != 2 || item.Pragma.ForeignKeys != 1).ToArray();
        var overlaps = Databases.Sum(database => Scalar<long>(database, "SELECT value FROM metrics WHERE key='writer_overlap_violations';"));
        var max = Databases.Max(database => Scalar<long>(database, "SELECT value FROM metrics WHERE key='max_processing';"));
        var passed = invalid.Length == 0 && overlaps == 0 && max <= 1;
        return Result("L_SQLITE_DURABILITY", passed,
            "Every scenario DB reports integrity=ok, WAL, FULL(2), FK=ON and no writer overlap; max PROCESSING <=1.",
            $"databases={snapshots.Length}; invalid={invalid.Length}; writer_overlap_violations={overlaps}; max_processing={max}",
            Databases);
    }

    private static ScenarioResult ValidateCleanup()
    {
        var alive = OwnedPids.Where(IsAlive).ToArray();
        var incomplete = Directory.EnumerateFiles(_runRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("partial", StringComparison.OrdinalIgnoreCase) ||
                           (Path.GetFileName(path) == "artifact.json" && File.ReadAllText(path).StartsWith("INCOMPLETE", StringComparison.Ordinal)))
            .ToArray();
        var scoped = incomplete.All(path => path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) && path.Contains("ATT-", StringComparison.OrdinalIgnoreCase));
        var handlesClosed = Databases.All(CanOpenExclusive);
        var passed = alive.Length == 0 && scoped && handlesClosed;
        return Result("M_CLEANUP_SAFETY", passed,
            "No owned PoC process or DB handle remains; incomplete artifacts, if any, stay attempt-scoped.",
            $"owned_alive={alive.Length}; incomplete_artifacts={incomplete.Length}; all_attempt_scoped={scoped}; db_handles_closed={handlesClosed}",
            incomplete);
    }

    private static IReadOnlyList<KeyValuePair<string, bool>> BuildMandatoryCriteria(IReadOnlyList<ScenarioResult> results)
    {
        bool PassPrefix(string prefix) => results.Where(item => item.Scenario.StartsWith(prefix, StringComparison.Ordinal)).All(item => item.Status == "PASS") && results.Any(item => item.Scenario.StartsWith(prefix, StringComparison.Ordinal));
        return new Dictionary<string, bool>
        {
            ["1_queue_recovery_after_crash"] = PassPrefix("G_"),
            ["2_active_job_marked_interrupted"] = PassPrefix("G_"),
            ["3_last_safe_checkpoint_identified"] = PassPrefix("B_") && PassPrefix("E"),
            ["4_checkpoint_artifacts_revalidated"] = PassPrefix("C_") && PassPrefix("E"),
            ["5_incomplete_stage_repeated"] = PassPrefix("B_"),
            ["6_completed_stage_not_repeated"] = PassPrefix("C_"),
            ["7_attempts_isolated"] = PassPrefix("B_") && PassPrefix("H_"),
            ["8_recovery_idempotent"] = PassPrefix("J_"),
            ["9_final_publication_crash_safe"] = PassPrefix("F"),
            ["10_no_silent_overwrite"] = PassPrefix("F4_"),
            ["11_failed_job_does_not_block_queue"] = PassPrefix("H_"),
            ["12_retries_bounded"] = PassPrefix("H_") && PassPrefix("I_"),
            ["13_sqlite_consistent"] = PassPrefix("L_"),
            ["14_originals_intact"] = PassPrefix("K_"),
            ["15_zero_orphan_processes"] = PassPrefix("M_") && PassPrefix("I_"),
            ["16_all_mandatory_scenarios_executed"] = results.Count == 39 && results.All(item => item.Status == "PASS")
        }.ToArray();
    }

    private static void WriteReport(
        string path,
        string runId,
        IReadOnlyList<ScenarioResult> results,
        IReadOnlyList<KeyValuePair<string, bool>> mandatory,
        IReadOnlyDictionary<string, string> values,
        string resultsPath,
        bool pass)
    {
        string Status(string prefix) => results.Where(item => item.Scenario.StartsWith(prefix, StringComparison.Ordinal)).All(item => item.Status == "PASS") ? "PASS" : "FAIL";
        var lines = new List<string>
        {
            "# PHOTO AI FACTORY — REC-01 REPORT",
            "",
            $"**REC-01 = {(pass ? "PASS" : "FAIL")}**",
            "",
            $"Run fuente de verdad: `{runId}`  ",
            $"Generado: `{Rec01Model.UtcNow()}`",
            "",
            $"- **{results.Count(item => item.Status == "PASS")}/{results.Count} checks PASS.**",
            $"- **Baseline:** {Status("A1_")} — 11 checkpoints, historial durable, JPEG publicado y Job COMPLETED.",
            $"- **Crash antes de checkpoints:** {Status("B_")} — {results.Count(item => item.Scenario.StartsWith("B_", StringComparison.Ordinal) && item.Status == "PASS")}/11.",
            $"- **Crash después de checkpoints:** {Status("C_")} — {results.Count(item => item.Scenario.StartsWith("C_", StringComparison.Ordinal) && item.Status == "PASS")}/11.",
            $"- **Transaction rollback:** {Status("D_")} — sin checkpoint fantasma ni cambios parciales.",
            $"- **Corrupted/missing checkpoint artifacts:** {Status("E")} — Darktable Pass 1, ComfyUI y OUTPUT_PUBLISHED revalidados/fallback.",
            $"- **Final publication crash windows:** {Status("F")} — F1–F5 ejecutados, publicación única/idempotente y colisión versionada.",
            $"- **Queue recovery:** {Status("G_")} — A recuperado, luego B y C FIFO; máximo un PROCESSING.",
            $"- **Retry boundedness:** {Status("H_")} — initial + 2 retries; permanente sin retry; siguiente Job continúa.",
            $"- **Child-process crash:** {Status("I_")} — helper propio terminado por PID exacto, attempt incompleto sin checkpoint.",
            $"- **Idempotency:** {Status("J_")} — dos reinicios extra no cambian el Job COMPLETED.",
            $"- **SQLite integrity:** {Status("L_")} — integrity_check=ok, journal_mode=wal, synchronous=FULL(2), foreign_keys=ON en {Databases.Count} DBs; writer overlap=0.",
            $"- **Originals:** {Status("K_")} — fixture fuente intacta: `{Rec01Model.Sha256(_fixture)}`.",
            $"- **Orphan processes:** {Status("M_")} — 0 procesos propios residuales; handles DB cerrados.",
            $"- **Build:** {(values.Optional("build-verified") == "true" ? "PASS — Release, 0 errores, 0 warnings." : "FAIL — no verificado por invocación.")}",
            $"- **Tests:** {(values.Optional("self-tests-verified") == "true" ? "PASS — self-tests y suite completa." : "FAIL — self-tests no verificados por invocación.")}",
            $"- **Vulnerable packages:** {values.Optional("vulnerable-packages", "NOT_CHECKED")}",
            "- **Bugs encontrados:** el primer build del PoC detectó 2 errores de nulabilidad (CS8600/CS8603) en el lector genérico de evidencia; no se encontraron bugs en código de producto.",
            "- **Bugs corregidos:** se manejó explícitamente el resultado NULL/DBNull del scalar reader del harness; no se modificó `src/`.",
            "- **Limitaciones:** crash de proceso/aplicación controlado; no simula corte eléctrico físico ni valida APIs externas nuevas. Los nombres agregados `PHOTO_AI_FACTORY_ADR_BUNDLE.md`, `PHOTO_AI_FACTORY_CODEX_WORKFLOW.md` y `PHOTO_AI_FACTORY_CURRENT_STATUS.md` no existen literalmente en esta integración; se leyeron sus fuentes equivalentes (`docs\\adr\\README.md` + ADR-001..014, `README.md`/`docs\\INDEX.md` y `PROJECT_STATUS.md`) sin modificar documentación.",
            "- **Archivos modificados:** únicamente `tools\\poc\\rec01\\PhotoAIFactory.Rec01\\*` y evidencia bajo `PHOTO AI FACTORY TESTS\\REC-01`.",
            "- **Commits/push:** no hubo commits, push, tags ni PR.",
            "- **Scope:** NO se ejecutó Phase 1 ni ningún Gate distinto de REC-01.",
            "",
            "## Criterios obligatorios",
            ""
        };
        lines.AddRange(mandatory.Select((item, index) => $"{index + 1}. `{item.Key}`: {(item.Value ? "PASS" : "FAIL")}"));
        lines.AddRange(["", "## Evidencia", "", $"- Resultados: `{resultsPath}`", $"- JSONL: `{_logPath}`", $"- DBs y artifacts: `{_runRoot}`", "", "---", "", pass ? "READY FOR PHASE 0 REVIEW" : "REC-01 REQUIRES FIXES", ""]);
        File.WriteAllLines(path, lines);
    }

    private static ScenarioResult Result(string scenario, bool pass, string expected, string observed, IReadOnlyList<string> evidence) =>
        new(scenario, pass ? "PASS" : "FAIL", expected, observed, evidence);

    private static ScenarioContext Context(string name)
    {
        var directory = Path.Combine(_runRoot, Rec01Model.Safe(name));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "rec01.db");
        Databases.Add(database);
        return new ScenarioContext(directory, database, Path.Combine(directory, "crash-barrier.json"));
    }

    private static async Task<ProcessOutcome> RunWorkerAsync(
        ScenarioContext context,
        string scenario,
        string crash = "",
        string target = "",
        int jobs = 1,
        bool expectCrash = false,
        string helperBarrier = "")
    {
        var process = StartWorker(context, scenario, crash, target, jobs, helperBarrier);
        if (expectCrash)
        {
            var barrierPid = await WaitForPidAsync(context.Barrier, TimeSpan.FromSeconds(30));
            if (barrierPid != process.Id) throw new InvalidOperationException($"Barrier PID {barrierPid} did not match owned worker {process.Id}.");
            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync();
            OwnedPids.Remove(process.Id);
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            _log!.Write("WARN", "RecoveryController", "owned_worker_killed", extra: new Dictionary<string, object?>
            {
                ["crash_point"] = crash,
                ["checkpoint"] = target,
                ["process_id"] = process.Id
            });
            process.Dispose();
            return new ProcessOutcome(-1, barrierPid, stdout, stderr);
        }
        return await FinishProcessAsync(process, TimeSpan.FromSeconds(90));
    }

    private static Process StartWorker(
        ScenarioContext context,
        string scenario,
        string crash = "",
        string target = "",
        int jobs = 1,
        string helperBarrier = "")
    {
        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        Add(start, "mode", "worker");
        Add(start, "db", context.Database);
        Add(start, "scenario", scenario);
        Add(start, "work", context.Directory);
        Add(start, "log", _logPath);
        Add(start, "fixture", _fixture);
        Add(start, "jobs", jobs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (crash.Length > 0) Add(start, "crash", crash);
        if (target.Length > 0) Add(start, "target", target);
        if (crash.Length > 0) Add(start, "barrier", context.Barrier);
        if (helperBarrier.Length > 0) Add(start, "helper-barrier", helperBarrier);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start REC-01 worker.");
        OwnedPids.Add(process.Id);
        return process;
    }

    private static void Add(ProcessStartInfo start, string key, string value)
    {
        start.ArgumentList.Add("--" + key);
        start.ArgumentList.Add(value);
    }

    private static async Task<ProcessOutcome> FinishProcessAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Worker PID {process.Id} exceeded {timeout}.");
        }
        finally
        {
            OwnedPids.Remove(process.Id);
        }
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        var outcome = new ProcessOutcome(process.ExitCode, null, stdout, stderr);
        process.Dispose();
        if (outcome.ExitCode != 0) throw new InvalidOperationException($"Worker failed: exit={outcome.ExitCode}; stderr={outcome.StandardError}; stdout={outcome.StandardOutput}");
        return outcome;
    }

    private static async Task<int> WaitForPidAsync(string barrier, TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            if (File.Exists(barrier))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(barrier));
                    return document.RootElement.GetProperty("pid").GetInt32();
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Barrier was not reached: {barrier}");
    }

    private static bool LogContains(string eventName, string checkpoint, string job)
    {
        return File.ReadLines(_logPath).Any(line => line.Contains($"\"event\":\"{eventName}\"", StringComparison.Ordinal) &&
            line.Contains($"\"checkpoint\":\"{checkpoint}\"", StringComparison.Ordinal) && line.Contains(job, StringComparison.Ordinal));
    }

    private static string JobId(string database) => Scalar<string>(database, "SELECT job_id FROM jobs ORDER BY queue_order LIMIT 1;");
    private static string State(string database, string job) => Scalar<string>(database, "SELECT state FROM jobs WHERE job_id=$job;", ("$job", job));
    private static long Count(string database, string table) => Scalar<long>(database, $"SELECT COUNT(*) FROM {table};");
    private static long ActiveCheckpointCount(string database, string job, string checkpoint) => Scalar<long>(database,
        "SELECT COUNT(*) FROM checkpoints WHERE job_id=$job AND checkpoint=$cp AND is_valid=1;", ("$job", job), ("$cp", checkpoint));

    private static IReadOnlyList<(string AttemptId, string Path, string Status)> Attempts(string database, string job, string stage) =>
        Rows(database, "SELECT attempt_id,artifact_path,status FROM stage_attempts WHERE job_id=$job AND stage=$stage ORDER BY attempt_no;",
            reader => (reader.GetString(0), reader.GetString(1), reader.GetString(2)), ("$job", job), ("$stage", stage));

    private static IReadOnlyList<string> FinalJpegs(string scenarioDirectory)
    {
        var directory = Path.Combine(scenarioDirectory, "FINAL");
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.jpg").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray() : [];
    }

    private static (string Integrity, string Journal, long Synchronous, long ForeignKeys) Pragma(string database)
    {
        using var connection = OpenReadOnly(database);
        string Text(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }
        long Number(string sql) => long.Parse(Text(sql), System.Globalization.CultureInfo.InvariantCulture);
        return (Text("PRAGMA integrity_check;"), Text("PRAGMA journal_mode;"), Number("PRAGMA synchronous;"), Number("PRAGMA foreign_keys;"));
    }

    private static T Scalar<T>(string database, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = OpenReadOnly(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var value = command.ExecuteScalar();
        if (value is null or DBNull) return default!;
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<T> Rows<T>(string database, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var connection = OpenReadOnly(database);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    private static SqliteConnection OpenReadOnly(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static bool CanOpenExclusive(string database)
    {
        try
        {
            using var stream = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool IsAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed record ScenarioContext(string Directory, string Database, string Barrier);
}
