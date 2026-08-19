using Microsoft.Data.Sqlite;

namespace PhotoAIFactory.Ing01;

internal sealed record PhotoRow(string Id, string ProjectId, string AssociationKey, string State, string? MasterAssetId, string? MasterKind);
internal sealed record AssetRow(string Id, string ProjectId, string PhotoId, string SourcePath, string ManagedPath, string Kind, string State, long Size, string Sha256, string? RawVariant);
internal sealed record JobRow(string Id, string ProjectId, string PhotoId, string State, string MasterAssetId, string MasterKind);
internal sealed record StoreIngestResult(PhotoRow Photo, AssetRow Asset, bool Duplicate, string? DuplicateAssetId);

internal sealed class Ing01Store : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private int _activeWriters;
    public int MaxConcurrentWriters { get; private set; }

    public Ing01Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS projects(
              id TEXT PRIMARY KEY, name TEXT NOT NULL, created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS photos(
              id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id), association_key TEXT NOT NULL,
              state TEXT NOT NULL, master_asset_id TEXT NULL, master_kind TEXT NULL,
              created_at TEXT NOT NULL, updated_at TEXT NOT NULL, UNIQUE(project_id, association_key));
            CREATE TABLE IF NOT EXISTS assets(
              id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id), photo_id TEXT NOT NULL REFERENCES photos(id),
              source_path TEXT NOT NULL, managed_path TEXT NOT NULL, kind TEXT NOT NULL, state TEXT NOT NULL,
              size INTEGER NOT NULL, sha256 TEXT NOT NULL, raw_variant TEXT NULL, created_at TEXT NOT NULL,
              UNIQUE(project_id, sha256));
            CREATE TABLE IF NOT EXISTS jobs(
              id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id), photo_id TEXT NOT NULL REFERENCES photos(id),
              state TEXT NOT NULL, master_asset_id TEXT NOT NULL, master_kind TEXT NOT NULL, started_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_assets_photo ON assets(photo_id);
            CREATE INDEX IF NOT EXISTS ix_jobs_photo ON jobs(photo_id);
            """;
        command.ExecuteNonQuery();
    }

    public string JournalMode => Scalar("PRAGMA journal_mode;")?.ToString() ?? "";
    public int ForeignKeys => Convert.ToInt32(Scalar("PRAGMA foreign_keys;"));
    public int Synchronous => Convert.ToInt32(Scalar("PRAGMA synchronous;"));

    public async Task EnsureProjectAsync(string id, string name)
    {
        await WriteAsync(() =>
        {
            using var command = Command("INSERT OR IGNORE INTO projects(id,name,created_at) VALUES($id,$name,$now);");
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$now", Now());
            command.ExecuteNonQuery(); return 0;
        });
    }

    public async Task<StoreIngestResult> IngestAsync(string projectId, string associationKey, string sourcePath,
        string managedPath, string kind, long size, string sha256, string? rawVariant)
    {
        return await WriteAsync(() =>
        {
            using var transaction = _connection.BeginTransaction();
            var duplicate = FindAssetByHash(projectId, sha256, transaction);
            if (duplicate is not null)
            {
                var duplicatePhoto = GetPhoto(duplicate.PhotoId, transaction)!;
                transaction.Commit();
                return new StoreIngestResult(duplicatePhoto, duplicate, true, duplicate.Id);
            }

            var photo = FindPhotoByKey(projectId, associationKey, transaction);
            if (photo is null)
            {
                var id = Guid.NewGuid().ToString("N"); var now = Now();
                using var create = Command("INSERT INTO photos(id,project_id,association_key,state,created_at,updated_at) VALUES($id,$project,$key,'WAITING_FOR_ASSOCIATION',$now,$now);", transaction);
                create.Parameters.AddWithValue("$id", id); create.Parameters.AddWithValue("$project", projectId);
                create.Parameters.AddWithValue("$key", associationKey); create.Parameters.AddWithValue("$now", now); create.ExecuteNonQuery();
                photo = GetPhoto(id, transaction)!;
            }

            var assetId = Guid.NewGuid().ToString("N");
            using (var insert = Command("""
                INSERT INTO assets(id,project_id,photo_id,source_path,managed_path,kind,state,size,sha256,raw_variant,created_at)
                VALUES($id,$project,$photo,$source,$managed,$kind,'ARCHIVED',$size,$hash,$variant,$now);
                """, transaction))
            {
                insert.Parameters.AddWithValue("$id", assetId); insert.Parameters.AddWithValue("$project", projectId);
                insert.Parameters.AddWithValue("$photo", photo.Id); insert.Parameters.AddWithValue("$source", sourcePath);
                insert.Parameters.AddWithValue("$managed", managedPath); insert.Parameters.AddWithValue("$kind", kind);
                insert.Parameters.AddWithValue("$size", size); insert.Parameters.AddWithValue("$hash", sha256);
                insert.Parameters.AddWithValue("$variant", (object?)rawVariant ?? DBNull.Value); insert.Parameters.AddWithValue("$now", Now()); insert.ExecuteNonQuery();
            }

            var assets = GetAssets(photo.Id, transaction);
            var raw = assets.FirstOrDefault(item => item.Kind == "RAW");
            var jpeg = assets.FirstOrDefault(item => item.Kind == "JPEG_CAMERA");
            var unsupported = raw?.RawVariant == "UNSUPPORTED_RAW_VARIANT";
            var state = unsupported ? "REVIEW_UNSUPPORTED_FORMAT" : raw is not null && jpeg is not null ? "READY_FOR_ANALYSIS" : "WAITING_FOR_ASSOCIATION";
            var master = raw ?? jpeg ?? throw new InvalidOperationException("Photo has no asset after insert");
            var masterKind = raw is not null ? "RAW" : "JPEG";
            using (var update = Command("UPDATE photos SET state=$state,master_asset_id=$master,master_kind=$kind,updated_at=$now WHERE id=$id;", transaction))
            {
                update.Parameters.AddWithValue("$state", state); update.Parameters.AddWithValue("$master", master.Id);
                update.Parameters.AddWithValue("$kind", masterKind); update.Parameters.AddWithValue("$now", Now()); update.Parameters.AddWithValue("$id", photo.Id); update.ExecuteNonQuery();
            }
            photo = GetPhoto(photo.Id, transaction)!;
            var asset = assets.Single(item => item.Id == assetId);
            transaction.Commit();
            return new StoreIngestResult(photo, asset, false, null);
        });
    }

    public async Task<int> FinalizePendingAsync(string projectId)
    {
        return await WriteAsync(() =>
        {
            using var command = Command("UPDATE photos SET state='READY_FOR_ANALYSIS',updated_at=$now WHERE project_id=$project AND state='WAITING_FOR_ASSOCIATION';");
            command.Parameters.AddWithValue("$now", Now()); command.Parameters.AddWithValue("$project", projectId); return command.ExecuteNonQuery();
        });
    }

    public async Task<JobRow> BeginJobAsync(string projectId, string photoId)
    {
        return await WriteAsync(() =>
        {
            var photo = GetPhoto(photoId) ?? throw new InvalidOperationException("Photo not found");
            if (photo.MasterAssetId is null || photo.MasterKind is null) throw new InvalidOperationException("Photo has no master");
            var job = new JobRow(Guid.NewGuid().ToString("N"), projectId, photoId, "PROCESSING", photo.MasterAssetId, photo.MasterKind);
            using var command = Command("INSERT INTO jobs(id,project_id,photo_id,state,master_asset_id,master_kind,started_at) VALUES($id,$project,$photo,$state,$master,$kind,$now);");
            command.Parameters.AddWithValue("$id", job.Id); command.Parameters.AddWithValue("$project", projectId);
            command.Parameters.AddWithValue("$photo", photoId); command.Parameters.AddWithValue("$state", job.State);
            command.Parameters.AddWithValue("$master", job.MasterAssetId); command.Parameters.AddWithValue("$kind", job.MasterKind);
            command.Parameters.AddWithValue("$now", Now()); command.ExecuteNonQuery(); return job;
        });
    }

    public PhotoRow? FindPhoto(string projectId, string key) => WithLock(() => FindPhotoByKey(projectId, key));
    public AssetRow[] AssetsForPhoto(string photoId) => WithLock(() => GetAssets(photoId).ToArray());
    public JobRow[] JobsForPhoto(string photoId) => WithLock(() =>
    {
        using var command = Command("SELECT id,project_id,photo_id,state,master_asset_id,master_kind FROM jobs WHERE photo_id=$photo ORDER BY started_at;");
        command.Parameters.AddWithValue("$photo", photoId); using var reader = command.ExecuteReader(); var result = new List<JobRow>();
        while (reader.Read()) result.Add(new JobRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return result.ToArray();
    });
    public int PhotoCount(string projectId) => Convert.ToInt32(Scalar("SELECT count(*) FROM photos WHERE project_id=$project;", ("$project", projectId)));
    public int AssetCount(string projectId) => Convert.ToInt32(Scalar("SELECT count(*) FROM assets WHERE project_id=$project;", ("$project", projectId)));
    public int SourceAssetCount(string sourcePath) => Convert.ToInt32(Scalar("SELECT count(*) FROM assets WHERE source_path=$path;", ("$path", sourcePath)));
    public int TotalPhotos => Convert.ToInt32(Scalar("SELECT count(*) FROM photos;"));
    public int TotalAssets => Convert.ToInt32(Scalar("SELECT count(*) FROM assets;"));
    public AssetRow[] AllAssets => WithLock(() =>
    {
        using var command = Command("SELECT id,project_id,photo_id,source_path,managed_path,kind,state,size,sha256,raw_variant FROM assets ORDER BY created_at;");
        using var reader = command.ExecuteReader(); var result = new List<AssetRow>(); while (reader.Read()) result.Add(ReadAsset(reader)); return result.ToArray();
    });
    public string IntegrityCheck => Scalar("PRAGMA integrity_check;")?.ToString() ?? "";

    private async Task<T> WriteAsync<T>(Func<T> action)
    {
        await _writer.WaitAsync();
        try
        {
            var active = Interlocked.Increment(ref _activeWriters); MaxConcurrentWriters = Math.Max(MaxConcurrentWriters, active);
            return action();
        }
        finally { Interlocked.Decrement(ref _activeWriters); _writer.Release(); }
    }
    private T WithLock<T>(Func<T> action) { _writer.Wait(); try { return action(); } finally { _writer.Release(); } }

    private object? Scalar(string sql, params (string Name, object Value)[] parameters) => WithLock(() =>
    {
        using var command = Command(sql); foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value); return command.ExecuteScalar();
    });
    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null) { var command = _connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; return command; }

    private PhotoRow? FindPhotoByKey(string projectId, string key, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT id,project_id,association_key,state,master_asset_id,master_kind FROM photos WHERE project_id=$project AND association_key=$key;", transaction);
        command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$key", key); using var reader = command.ExecuteReader(); return reader.Read() ? ReadPhoto(reader) : null;
    }
    private PhotoRow? GetPhoto(string id, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT id,project_id,association_key,state,master_asset_id,master_kind FROM photos WHERE id=$id;", transaction);
        command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader(); return reader.Read() ? ReadPhoto(reader) : null;
    }
    private AssetRow? FindAssetByHash(string projectId, string hash, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT id,project_id,photo_id,source_path,managed_path,kind,state,size,sha256,raw_variant FROM assets WHERE project_id=$project AND sha256=$hash;", transaction);
        command.Parameters.AddWithValue("$project", projectId); command.Parameters.AddWithValue("$hash", hash); using var reader = command.ExecuteReader(); return reader.Read() ? ReadAsset(reader) : null;
    }
    private List<AssetRow> GetAssets(string photoId, SqliteTransaction? transaction = null)
    {
        using var command = Command("SELECT id,project_id,photo_id,source_path,managed_path,kind,state,size,sha256,raw_variant FROM assets WHERE photo_id=$photo ORDER BY created_at;", transaction);
        command.Parameters.AddWithValue("$photo", photoId); using var reader = command.ExecuteReader(); var result = new List<AssetRow>(); while (reader.Read()) result.Add(ReadAsset(reader)); return result;
    }
    private static PhotoRow ReadPhoto(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
    private static AssetRow ReadAsset(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9));
    private static string Now() => DateTimeOffset.UtcNow.ToString("O");
    public void Dispose() { _connection.Dispose(); _writer.Dispose(); }
}
