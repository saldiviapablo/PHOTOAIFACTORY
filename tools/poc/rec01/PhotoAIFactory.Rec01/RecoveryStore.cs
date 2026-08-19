using Microsoft.Data.Sqlite;

namespace PhotoAIFactory.Rec01;

internal sealed record JobRow(string JobId, string ProjectId, string PhotoId, int QueueOrder, string State);
internal sealed record CheckpointRow(string Checkpoint, string AttemptId, string ArtifactPath, long ArtifactSize, string ArtifactSha256, bool IsValid);
internal sealed record AttemptRow(string AttemptId, string ArtifactPath, long ArtifactSize, string ArtifactSha256, string Status);

internal sealed class RecoveryStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly StructuredLog _log;
    private readonly int _pid = Environment.ProcessId;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private bool _disposed;

    public RecoveryStore(string database, StructuredLog log)
    {
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        _connection.Open();
        Execute("PRAGMA foreign_keys=ON;");
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=FULL;");
        Execute("PRAGMA busy_timeout=5000;");
        CreateSchema();
        RegisterWriter();
    }

    public SqliteConnection Connection => _connection;

    private void CreateSchema() => Execute(
        """
        CREATE TABLE IF NOT EXISTS jobs(
          job_id TEXT PRIMARY KEY, project_id TEXT NOT NULL, photo_id TEXT NOT NULL,
          queue_order INTEGER NOT NULL, state TEXT NOT NULL, current_stage TEXT NOT NULL DEFAULT '',
          created_at TEXT NOT NULL, completed_at TEXT NULL);
        CREATE TABLE IF NOT EXISTS stage_attempts(
          attempt_id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES jobs(job_id),
          stage TEXT NOT NULL, attempt_no INTEGER NOT NULL, status TEXT NOT NULL,
          artifact_path TEXT NOT NULL DEFAULT '', artifact_size INTEGER NOT NULL DEFAULT 0,
          artifact_sha256 TEXT NOT NULL DEFAULT '', error_kind TEXT NOT NULL DEFAULT '',
          created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
          UNIQUE(job_id, stage, attempt_no));
        CREATE TABLE IF NOT EXISTS checkpoints(
          checkpoint_id INTEGER PRIMARY KEY AUTOINCREMENT,
          job_id TEXT NOT NULL REFERENCES jobs(job_id), checkpoint TEXT NOT NULL,
          attempt_id TEXT NOT NULL REFERENCES stage_attempts(attempt_id),
          artifact_path TEXT NOT NULL, artifact_size INTEGER NOT NULL, artifact_sha256 TEXT NOT NULL,
          is_valid INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
          UNIQUE(job_id, checkpoint));
        CREATE TABLE IF NOT EXISTS checkpoint_history(
          history_id INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL,
          checkpoint TEXT NOT NULL, attempt_id TEXT NOT NULL, action TEXT NOT NULL,
          artifact_path TEXT NOT NULL, artifact_sha256 TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS job_history(
          history_id INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL,
          attempt_id TEXT NOT NULL DEFAULT '', stage TEXT NOT NULL DEFAULT '',
          event TEXT NOT NULL, previous_state TEXT NOT NULL DEFAULT '', new_state TEXT NOT NULL DEFAULT '',
          details TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS publications(
          job_id TEXT PRIMARY KEY REFERENCES jobs(job_id), artifact_path TEXT NOT NULL,
          artifact_sha256 TEXT NOT NULL, owner_path TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS writer_sessions(
          session_id TEXT PRIMARY KEY, pid INTEGER NOT NULL, started_at TEXT NOT NULL,
          ended_at TEXT NULL, result TEXT NOT NULL, overlap_detected INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS metrics(key TEXT PRIMARY KEY, value INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS tx_probe(id TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT OR IGNORE INTO metrics(key,value) VALUES('max_processing',0),('writer_overlap_violations',0);
        """);

    private void RegisterWriter()
    {
        var activePids = Query("SELECT pid FROM writer_sessions WHERE ended_at IS NULL;", reader => reader.GetInt32(0));
        var live = activePids.Where(IsProcessAlive).ToArray();
        Execute("UPDATE writer_sessions SET ended_at=$now,result='CRASHED' WHERE ended_at IS NULL;", ("$now", Rec01Model.UtcNow()));
        var overlap = live.Length > 0 ? 1 : 0;
        if (overlap != 0) Execute("UPDATE metrics SET value=value+1 WHERE key='writer_overlap_violations';");
        Execute(
            "INSERT INTO writer_sessions(session_id,pid,started_at,result,overlap_detected) VALUES($id,$pid,$at,'ACTIVE',$overlap);",
            ("$id", _sessionId), ("$pid", _pid), ("$at", Rec01Model.UtcNow()), ("$overlap", overlap));
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid == Environment.ProcessId) return true;
        try { return !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    public void EnsureJobs(string scenario, int count)
    {
        if (Scalar<long>("SELECT COUNT(*) FROM jobs;") > 0) return;
        for (var index = 0; index < count; index++)
        {
            var letter = (char)('A' + index);
            var job = $"{Rec01Model.Safe(scenario).ToUpperInvariant()}-JOB-{letter}";
            Execute(
                "INSERT INTO jobs(job_id,project_id,photo_id,queue_order,state,created_at) VALUES($job,$project,$photo,$order,'QUEUED',$at);",
                ("$job", job), ("$project", "REC01-PROJECT"), ("$photo", $"PHOTO-{letter}"),
                ("$order", index), ("$at", Rec01Model.UtcNow()));
            History(job, "", "", "job_enqueued", "", "QUEUED", "FIFO");
        }
    }

    public void RecoverInterrupted(StructuredLog log)
    {
        var active = Query("SELECT job_id,project_id,photo_id FROM jobs WHERE state='PROCESSING';",
            reader => (reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        foreach (var item in active)
        {
            Execute("UPDATE jobs SET state='INTERRUPTED' WHERE job_id=$job;", ("$job", item.Item1));
            History(item.Item1, "", "", "active_job_interrupted", "PROCESSING", "INTERRUPTED", "crash recovery");
            log.Write("WARN", "RecoveryWorker", "active_job_interrupted", item.Item2, item.Item3, item.Item1,
                extra: new Dictionary<string, object?> { ["previous_state"] = "PROCESSING", ["new_state"] = "INTERRUPTED", ["recovery_action"] = "resume_from_last_safe_checkpoint" });
        }
    }

    public IReadOnlyList<JobRow> RunnableJobs() => Query(
        "SELECT job_id,project_id,photo_id,queue_order,state FROM jobs WHERE state IN ('QUEUED','INTERRUPTED','RETRYING') ORDER BY queue_order,created_at;",
        reader => new JobRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4)));

    public void SetState(JobRow job, string state, string stage = "")
    {
        var previous = Scalar<string>("SELECT state FROM jobs WHERE job_id=$job;", ("$job", job.JobId));
        Execute("UPDATE jobs SET state=$state,current_stage=$stage,completed_at=CASE WHEN $state='COMPLETED' THEN $at ELSE completed_at END WHERE job_id=$job;",
            ("$state", state), ("$stage", stage), ("$at", Rec01Model.UtcNow()), ("$job", job.JobId));
        History(job.JobId, "", stage, "state_transition", previous, state, "");
        if (state == "PROCESSING")
        {
            var processing = Scalar<long>("SELECT COUNT(*) FROM jobs WHERE state='PROCESSING';");
            Execute("UPDATE metrics SET value=MAX(value,$value) WHERE key='max_processing';", ("$value", processing));
        }
    }

    public int NextAttemptNumber(string jobId, string stage) =>
        checked((int)Scalar<long>("SELECT COALESCE(MAX(attempt_no),0)+1 FROM stage_attempts WHERE job_id=$job AND stage=$stage;", ("$job", jobId), ("$stage", stage)));

    public string StartAttempt(string jobId, string stage)
    {
        var number = NextAttemptNumber(jobId, stage);
        var attempt = $"ATT-{Rec01Model.Safe(jobId)}-{stage}-{number:D2}";
        Execute(
            "INSERT INTO stage_attempts(attempt_id,job_id,stage,attempt_no,status,created_at,updated_at) VALUES($attempt,$job,$stage,$number,'STARTED',$at,$at);",
            ("$attempt", attempt), ("$job", jobId), ("$stage", stage), ("$number", number), ("$at", Rec01Model.UtcNow()));
        return attempt;
    }

    public void ValidateAttempt(string attemptId, string path, long size, string sha)
    {
        Execute("UPDATE stage_attempts SET status='VALIDATED',artifact_path=$path,artifact_size=$size,artifact_sha256=$sha,updated_at=$at WHERE attempt_id=$attempt;",
            ("$path", path), ("$size", size), ("$sha", sha), ("$at", Rec01Model.UtcNow()), ("$attempt", attemptId));
    }

    public void FailAttempt(string attemptId, string path, string errorKind)
    {
        var size = File.Exists(path) ? new FileInfo(path).Length : 0;
        var sha = File.Exists(path) ? Rec01Model.Sha256(path) : "";
        Execute("UPDATE stage_attempts SET status='FAILED',artifact_path=$path,artifact_size=$size,artifact_sha256=$sha,error_kind=$error,updated_at=$at WHERE attempt_id=$attempt;",
            ("$path", path), ("$size", size), ("$sha", sha), ("$error", errorKind), ("$at", Rec01Model.UtcNow()), ("$attempt", attemptId));
    }

    public void PersistStageHistory(JobRow job, string attemptId, string stage, string path, string sha) =>
        History(job.JobId, attemptId, stage, "stage_result_persisted", "", "", $"path={path};sha256={sha}");

    public void CommitCheckpoint(JobRow job, string stage, string attemptId, string path, long size, string sha)
    {
        using var transaction = _connection.BeginTransaction();
        UpsertCheckpoint(transaction, job.JobId, stage, attemptId, path, size, sha);
        Execute(transaction, "UPDATE stage_attempts SET status='CHECKPOINTED',updated_at=$at WHERE attempt_id=$attempt;",
            ("$at", Rec01Model.UtcNow()), ("$attempt", attemptId));
        Execute(transaction,
            "INSERT INTO checkpoint_history(job_id,checkpoint,attempt_id,action,artifact_path,artifact_sha256,created_at) VALUES($job,$cp,$attempt,'COMMITTED',$path,$sha,$at);",
            ("$job", job.JobId), ("$cp", stage), ("$attempt", attemptId), ("$path", path), ("$sha", sha), ("$at", Rec01Model.UtcNow()));
        transaction.Commit();
    }

    public void OpenUncommittedCheckpointAndBlock(JobRow job, string stage, string attemptId, string path, long size, string sha, Action barrier)
    {
        using var transaction = _connection.BeginTransaction();
        Execute(transaction, "INSERT INTO tx_probe(id,value) VALUES($id,'UNCOMMITTED');", ("$id", attemptId));
        UpsertCheckpoint(transaction, job.JobId, stage, attemptId, path, size, sha);
        Execute(transaction, "UPDATE stage_attempts SET status='CHECKPOINTED',updated_at=$at WHERE attempt_id=$attempt;",
            ("$at", Rec01Model.UtcNow()), ("$attempt", attemptId));
        barrier();
        Thread.Sleep(Timeout.Infinite);
    }

    private void UpsertCheckpoint(SqliteTransaction transaction, string jobId, string stage, string attemptId, string path, long size, string sha) =>
        Execute(transaction,
            """
            INSERT INTO checkpoints(job_id,checkpoint,attempt_id,artifact_path,artifact_size,artifact_sha256,is_valid,created_at,updated_at)
            VALUES($job,$cp,$attempt,$path,$size,$sha,1,$at,$at)
            ON CONFLICT(job_id,checkpoint) DO UPDATE SET attempt_id=excluded.attempt_id,
              artifact_path=excluded.artifact_path,artifact_size=excluded.artifact_size,
              artifact_sha256=excluded.artifact_sha256,is_valid=1,updated_at=excluded.updated_at;
            """,
            ("$job", jobId), ("$cp", stage), ("$attempt", attemptId), ("$path", path),
            ("$size", size), ("$sha", sha), ("$at", Rec01Model.UtcNow()));

    public IReadOnlyList<CheckpointRow> Checkpoints(string jobId) => Query(
        "SELECT checkpoint,attempt_id,artifact_path,artifact_size,artifact_sha256,is_valid FROM checkpoints WHERE job_id=$job;",
        reader => new CheckpointRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4), reader.GetInt32(5) == 1),
        ("$job", jobId));

    public void InvalidateFrom(JobRow job, int start, string reason)
    {
        for (var index = start; index < Rec01Model.Checkpoints.Length; index++)
        {
            var stage = Rec01Model.Checkpoints[index];
            var row = Checkpoints(job.JobId).FirstOrDefault(item => item.Checkpoint == stage && item.IsValid);
            if (row is null) continue;
            Execute("UPDATE checkpoints SET is_valid=0,updated_at=$at WHERE job_id=$job AND checkpoint=$cp;",
                ("$at", Rec01Model.UtcNow()), ("$job", job.JobId), ("$cp", stage));
            Execute("INSERT INTO checkpoint_history(job_id,checkpoint,attempt_id,action,artifact_path,artifact_sha256,created_at) VALUES($job,$cp,$attempt,$action,$path,$sha,$at);",
                ("$job", job.JobId), ("$cp", stage), ("$attempt", row.AttemptId), ("$action", "INVALIDATED:" + reason),
                ("$path", row.ArtifactPath), ("$sha", row.ArtifactSha256), ("$at", Rec01Model.UtcNow()));
        }
    }

    public void RecordPublication(string jobId, string path, string sha, string ownerPath)
    {
        Execute(
            "INSERT INTO publications(job_id,artifact_path,artifact_sha256,owner_path,created_at) VALUES($job,$path,$sha,$owner,$at) ON CONFLICT(job_id) DO UPDATE SET artifact_path=excluded.artifact_path,artifact_sha256=excluded.artifact_sha256,owner_path=excluded.owner_path;",
            ("$job", jobId), ("$path", path), ("$sha", sha), ("$owner", ownerPath), ("$at", Rec01Model.UtcNow()));
    }

    public AttemptRow Attempt(string attemptId) => Query(
        "SELECT attempt_id,artifact_path,artifact_size,artifact_sha256,status FROM stage_attempts WHERE attempt_id=$attempt;",
        reader => new AttemptRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4)),
        ("$attempt", attemptId)).Single();

    public void History(string jobId, string attemptId, string stage, string eventName, string previous, string next, string details)
    {
        Execute(
            "INSERT INTO job_history(job_id,attempt_id,stage,event,previous_state,new_state,details,created_at) VALUES($job,$attempt,$stage,$event,$previous,$next,$details,$at);",
            ("$job", jobId), ("$attempt", attemptId), ("$stage", stage), ("$event", eventName),
            ("$previous", previous), ("$next", next), ("$details", details), ("$at", Rec01Model.UtcNow()));
    }

    public T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        var value = command.ExecuteScalar();
        if (value is null or DBNull) return default!;
        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(map(reader));
        return rows;
    }

    public void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }

    private void Execute(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            Execute("UPDATE writer_sessions SET ended_at=$at,result='CLEAN' WHERE session_id=$id;",
                ("$at", Rec01Model.UtcNow()), ("$id", _sessionId));
        }
        finally
        {
            _connection.Dispose();
            _disposed = true;
        }
    }
}
