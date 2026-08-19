using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Ingestion;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Infrastructure.Persistence.Ingestion;

public sealed class SqliteIngestionStore(SqliteProjectDatabase database) : IIngestionStore
{
    public async Task<PrepareIngestionSourceResult> PrepareSourceAsync(
        ProjectId projectId,
        string configVersionId,
        string inputRoot,
        bool includeSubfolders,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = ProjectPathPolicy.Normalize(inputRoot);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        try
        {
            var latest = await GetLatestSourceAsync(
                connection, transaction, projectId, cancellationToken).ConfigureAwait(false);

            if (latest is not null &&
                latest.ClosedAtUtc is null &&
                string.Equals(latest.InputRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                latest.IncludeSubfolders == includeSubfolders)
            {
                await UpdateSourceConfigAsync(
                    connection, transaction, latest.Id, configVersionId, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(PrepareIngestionSourceStatus.Ready, latest with { ConfigVersionId = configVersionId }, 0);
            }

            if (latest is not null && latest.ClosedAtUtc is null)
            {
                var pending = await CountPendingAsync(
                    connection, transaction, projectId, latest.Id, cancellationToken).ConfigureAwait(false);
                if (pending > 0)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new(
                        PrepareIngestionSourceStatus.PendingAssociationsRequireResolution,
                        latest,
                        pending);
                }

                await CloseSourceAsync(
                    connection, transaction, latest.Id, nowUtc, cancellationToken).ConfigureAwait(false);
            }

            var created = new IngestionSourceSnapshot(
                IngestionSourceId.New(),
                projectId,
                normalizedRoot,
                includeSubfolders,
                configVersionId,
                EnsureUtc(nowUtc),
                null);
            await InsertSourceAsync(connection, transaction, created, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(PrepareIngestionSourceStatus.Ready, created, 0);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> CountPendingAsync(
        ProjectId projectId,
        IngestionSourceId sourceId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await CountPendingAsync(
            connection, null, projectId, sourceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> FinalizeAssociationsAsync(
        ProjectId projectId,
        IngestionSourceId sourceId,
        DateTimeOffset nowUtc,
        bool force,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        try
        {
            var photos = await LoadPendingPhotosAsync(
                connection, transaction, projectId, sourceId, EnsureUtc(nowUtc), force, cancellationToken)
                .ConfigureAwait(false);
            foreach (var photo in photos)
            {
                var assets = await LoadAssetsForPhotoAsync(
                    connection, transaction, photo.Id, cancellationToken).ConfigureAwait(false);
                await FinalizePhotoAsync(
                    connection, transaction, photo, assets, EnsureUtc(nowUtc), cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return photos.Count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<AssetSnapshot?> FindAssetByHashAsync(
        ProjectId projectId,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await FindAssetByHashAsync(
            connection, null, projectId, sha256, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IngestAssetResult> IngestArchivedAsync(
        IngestAssetCommand command,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var lease = await database.Writer.EnterAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
            cancellationToken).ConfigureAwait(false);

        try
        {
            var duplicate = await FindAssetByHashAsync(
                connection, transaction, command.ProjectId, command.Sha256, cancellationToken)
                .ConfigureAwait(false);
            if (duplicate is not null)
            {
                var duplicatePhoto = await LoadPhotoByIdAsync(
                    connection, transaction, duplicate.PhotoId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Duplicate asset points to a missing Photo.");
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new(IngestAssetStatus.DuplicateExact, duplicatePhoto, duplicate, duplicate.Id);
            }

            var photo = await FindPhotoAsync(
                connection, transaction, command.ProjectId, command.SourceId,
                command.AssociationKey, cancellationToken).ConfigureAwait(false);

            if (photo is null)
            {
                var observed = EnsureUtc(command.ObservedAtUtc);
                photo = new PhotoIngestionSnapshot(
                    PhotoId.New(),
                    command.ProjectId,
                    command.SourceId,
                    command.AssociationKey,
                    IngestionPhotoState.WaitingForAssociation,
                    null,
                    null,
                    observed.Add(command.AssociationWindow),
                    observed,
                    observed);
                await InsertPhotoAsync(connection, transaction, photo, cancellationToken).ConfigureAwait(false);
            }

            var existingAssets = await LoadAssetsForPhotoAsync(
                connection, transaction, photo.Id, cancellationToken).ConfigureAwait(false);
            if (existingAssets.Any(item => item.Format == command.Format))
            {
                throw new InvalidDataException(
                    $"Association collision: Photo {photo.Id.Value} already has a distinct {command.Format} asset.");
            }

            var asset = new AssetSnapshot(
                AssetId.New(),
                command.ProjectId,
                photo.Id,
                command.SourceId,
                Path.GetFullPath(command.SourcePath),
                command.SourceRelativePath,
                Path.GetFullPath(command.ManagedPath),
                command.Format,
                command.Format == AssetFormat.Raw ? AssetRole.RawOriginal : AssetRole.JpegPending,
                AssetArchiveState.Archived,
                command.SizeBytes,
                command.Sha256.ToLowerInvariant(),
                command.RawSupport,
                EnsureUtc(command.ObservedAtUtc),
                EnsureUtc(command.ArchivedAtUtc));

            await InsertAssetAsync(connection, transaction, asset, cancellationToken).ConfigureAwait(false);
            var assets = existingAssets.Append(asset).ToArray();
            var wasJpegOnlyReady = photo.State == IngestionPhotoState.ReadyForAnalysis &&
                photo.MasterFormat == AssetFormat.Jpeg &&
                command.Format == AssetFormat.Raw;

            photo = await RecalculatePhotoAsync(
                connection, transaction, photo, assets, EnsureUtc(command.ObservedAtUtc), cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(
                wasJpegOnlyReady ? IngestAssetStatus.LateRawAttached : IngestAssetStatus.Created,
                photo,
                asset);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<PhotoIngestionSnapshot>> ListPhotosAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<PhotoIngestionSnapshot>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT photo_id, project_id, source_id, association_key, state,
                   master_asset_id, master_format, association_deadline_utc,
                   created_at_utc, updated_at_utc
            FROM photos
            WHERE project_id=$project
            ORDER BY created_at_utc, photo_id;
            """;
        command.Parameters.AddWithValue("$project", projectId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadPhoto(reader));
        }
        return results;
    }

    public async Task<IReadOnlyList<AssetSnapshot>> ListAssetsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<AssetSnapshot>();
        await using var command = connection.CreateCommand();
        command.CommandText = AssetSelect + " WHERE project_id=$project ORDER BY archived_at_utc, asset_id;";
        command.Parameters.AddWithValue("$project", projectId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadAsset(reader));
        }
        return results;
    }

    public async Task<IngestionSourceSnapshot?> GetLatestSourceAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConfiguredConnectionAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await GetLatestSourceAsync(
            connection, null, projectId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PhotoIngestionSnapshot> RecalculatePhotoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PhotoIngestionSnapshot photo,
        IReadOnlyList<AssetSnapshot> assets,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var raw = assets.SingleOrDefault(item => item.Format == AssetFormat.Raw);
        var jpeg = assets.SingleOrDefault(item => item.Format == AssetFormat.Jpeg);

        if (raw is not null && jpeg is not null)
        {
            await UpdateAssetRoleAsync(
                connection, transaction, jpeg.Id, AssetRole.JpegCamera, cancellationToken).ConfigureAwait(false);
        }

        var state = photo.State;
        AssetId? masterId = photo.MasterAssetId;
        AssetFormat? masterFormat = photo.MasterFormat;

        if (raw is not null)
        {
            state = raw.RawSupport.Status == RawSupportStatus.SupportedFullSize
                ? (jpeg is not null || photo.State == IngestionPhotoState.ReadyForAnalysis
                    ? IngestionPhotoState.ReadyForAnalysis
                    : IngestionPhotoState.WaitingForAssociation)
                : IngestionPhotoState.ReviewUnsupportedFormat;
            masterId = raw.Id;
            masterFormat = AssetFormat.Raw;
        }
        else if (jpeg is not null && photo.State == IngestionPhotoState.ReadyForAnalysis)
        {
            masterId = jpeg.Id;
            masterFormat = AssetFormat.Jpeg;
        }

        return await UpdatePhotoAsync(
            connection, transaction, photo, state, masterId, masterFormat, nowUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task FinalizePhotoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PhotoIngestionSnapshot photo,
        IReadOnlyList<AssetSnapshot> assets,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var raw = assets.SingleOrDefault(item => item.Format == AssetFormat.Raw);
        var jpeg = assets.SingleOrDefault(item => item.Format == AssetFormat.Jpeg);

        if (raw is not null)
        {
            if (jpeg is not null)
            {
                await UpdateAssetRoleAsync(
                    connection, transaction, jpeg.Id, AssetRole.JpegCamera, cancellationToken).ConfigureAwait(false);
            }

            var state = raw.RawSupport.Status == RawSupportStatus.SupportedFullSize
                ? IngestionPhotoState.ReadyForAnalysis
                : IngestionPhotoState.ReviewUnsupportedFormat;
            await UpdatePhotoAsync(
                connection, transaction, photo, state, raw.Id, AssetFormat.Raw, nowUtc, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (jpeg is not null)
        {
            await UpdateAssetRoleAsync(
                connection, transaction, jpeg.Id, AssetRole.JpegMaster, cancellationToken).ConfigureAwait(false);
            await UpdatePhotoAsync(
                connection, transaction, photo, IngestionPhotoState.ReadyForAnalysis,
                jpeg.Id, AssetFormat.Jpeg, nowUtc, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<PhotoIngestionSnapshot> UpdatePhotoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PhotoIngestionSnapshot current,
        IngestionPhotoState state,
        AssetId? masterId,
        AssetFormat? masterFormat,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE photos
            SET state=$state, master_asset_id=$masterAsset, master_format=$masterFormat, updated_at_utc=$updated
            WHERE photo_id=$photo;
            """;
        command.Parameters.AddWithValue("$state", PhotoStateToken(state));
        command.Parameters.AddWithValue("$masterAsset", (object?)masterId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$masterFormat", masterFormat is null ? DBNull.Value : FormatToken(masterFormat.Value));
        command.Parameters.AddWithValue("$updated", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$photo", current.Id.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new DBConcurrencyException("Photo ingestion state update affected an unexpected row count.");
        }

        return current with
        {
            State = state,
            MasterAssetId = masterId,
            MasterFormat = masterFormat,
            UpdatedAtUtc = nowUtc
        };
    }

    private static async Task UpdateAssetRoleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AssetId assetId,
        AssetRole role,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE assets SET role=$role WHERE asset_id=$asset;";
        command.Parameters.AddWithValue("$role", RoleToken(role));
        command.Parameters.AddWithValue("$asset", assetId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<PhotoIngestionSnapshot>> LoadPendingPhotosAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectId projectId,
        IngestionSourceId sourceId,
        DateTimeOffset nowUtc,
        bool force,
        CancellationToken cancellationToken)
    {
        var results = new List<PhotoIngestionSnapshot>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT photo_id, project_id, source_id, association_key, state,
                   master_asset_id, master_format, association_deadline_utc,
                   created_at_utc, updated_at_utc
            FROM photos
            WHERE project_id=$project AND source_id=$source
              AND state='WAITING_FOR_ASSOCIATION'
              AND ($force=1 OR association_deadline_utc <= $now)
            ORDER BY created_at_utc, photo_id;
            """;
        command.Parameters.AddWithValue("$project", projectId.Value);
        command.Parameters.AddWithValue("$source", sourceId.Value);
        command.Parameters.AddWithValue("$force", force ? 1 : 0);
        command.Parameters.AddWithValue("$now", FormatUtc(nowUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadPhoto(reader));
        }
        return results;
    }

    private static async Task<int> CountPendingAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProjectId projectId,
        IngestionSourceId sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT count(*) FROM photos
            WHERE project_id=$project AND source_id=$source AND state='WAITING_FOR_ASSOCIATION';
            """;
        command.Parameters.AddWithValue("$project", projectId.Value);
        command.Parameters.AddWithValue("$source", sourceId.Value);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<IngestionSourceSnapshot?> GetLatestSourceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_id, project_id, input_root, include_subfolders,
                   config_version_id, created_at_utc, closed_at_utc
            FROM ingestion_sources
            WHERE project_id=$project
            ORDER BY created_at_utc DESC, source_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$project", projectId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSource(reader)
            : null;
    }

    private static async Task InsertSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IngestionSourceSnapshot source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ingestion_sources(
                source_id, project_id, input_root, include_subfolders,
                config_version_id, created_at_utc, closed_at_utc)
            VALUES($source, $project, $root, $subs, $config, $created, NULL);
            """;
        command.Parameters.AddWithValue("$source", source.Id.Value);
        command.Parameters.AddWithValue("$project", source.ProjectId.Value);
        command.Parameters.AddWithValue("$root", source.InputRoot);
        command.Parameters.AddWithValue("$subs", source.IncludeSubfolders ? 1 : 0);
        command.Parameters.AddWithValue("$config", source.ConfigVersionId);
        command.Parameters.AddWithValue("$created", FormatUtc(source.CreatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateSourceConfigAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IngestionSourceId sourceId,
        string configVersionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE ingestion_sources SET config_version_id=$config WHERE source_id=$source;";
        command.Parameters.AddWithValue("$config", configVersionId);
        command.Parameters.AddWithValue("$source", sourceId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CloseSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IngestionSourceId sourceId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE ingestion_sources SET closed_at_utc=$closed WHERE source_id=$source AND closed_at_utc IS NULL;";
        command.Parameters.AddWithValue("$closed", FormatUtc(nowUtc));
        command.Parameters.AddWithValue("$source", sourceId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertPhotoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PhotoIngestionSnapshot photo,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO photos(
                photo_id, project_id, source_id, association_key, state,
                master_asset_id, master_format, association_deadline_utc,
                created_at_utc, updated_at_utc)
            VALUES($photo, $project, $source, $key, $state,
                   NULL, NULL, $deadline, $created, $updated);
            """;
        command.Parameters.AddWithValue("$photo", photo.Id.Value);
        command.Parameters.AddWithValue("$project", photo.ProjectId.Value);
        command.Parameters.AddWithValue("$source", photo.SourceId.Value);
        command.Parameters.AddWithValue("$key", photo.AssociationKey);
        command.Parameters.AddWithValue("$state", PhotoStateToken(photo.State));
        command.Parameters.AddWithValue("$deadline", FormatUtc(photo.AssociationDeadlineUtc));
        command.Parameters.AddWithValue("$created", FormatUtc(photo.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", FormatUtc(photo.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAssetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AssetSnapshot asset,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO assets(
                asset_id, project_id, photo_id, source_id, source_path, source_relative_path,
                managed_path, format, role, archive_state, size_bytes, sha256,
                raw_support_status, raw_max_width, raw_max_height, raw_classification,
                observed_at_utc, archived_at_utc)
            VALUES($asset, $project, $photo, $source, $sourcePath, $relative,
                   $managed, $format, $role, 'ARCHIVED', $size, $hash,
                   $rawStatus, $rawWidth, $rawHeight, $rawClassification,
                   $observed, $archived);
            """;
        command.Parameters.AddWithValue("$asset", asset.Id.Value);
        command.Parameters.AddWithValue("$project", asset.ProjectId.Value);
        command.Parameters.AddWithValue("$photo", asset.PhotoId.Value);
        command.Parameters.AddWithValue("$source", asset.SourceId.Value);
        command.Parameters.AddWithValue("$sourcePath", asset.SourcePath);
        command.Parameters.AddWithValue("$relative", asset.SourceRelativePath);
        command.Parameters.AddWithValue("$managed", asset.ManagedPath);
        command.Parameters.AddWithValue("$format", FormatToken(asset.Format));
        command.Parameters.AddWithValue("$role", RoleToken(asset.Role));
        command.Parameters.AddWithValue("$size", asset.SizeBytes);
        command.Parameters.AddWithValue("$hash", asset.Sha256);
        command.Parameters.AddWithValue("$rawStatus", RawSupportToken(asset.RawSupport.Status));
        command.Parameters.AddWithValue("$rawWidth", asset.RawSupport.MaxWidth);
        command.Parameters.AddWithValue("$rawHeight", asset.RawSupport.MaxHeight);
        command.Parameters.AddWithValue("$rawClassification", asset.RawSupport.Classification);
        command.Parameters.AddWithValue("$observed", FormatUtc(asset.ObservedAtUtc));
        command.Parameters.AddWithValue("$archived", FormatUtc(asset.ArchivedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PhotoIngestionSnapshot?> FindPhotoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProjectId projectId,
        IngestionSourceId sourceId,
        string associationKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT photo_id, project_id, source_id, association_key, state,
                   master_asset_id, master_format, association_deadline_utc,
                   created_at_utc, updated_at_utc
            FROM photos
            WHERE project_id=$project AND source_id=$source AND association_key=$key;
            """;
        command.Parameters.AddWithValue("$project", projectId.Value);
        command.Parameters.AddWithValue("$source", sourceId.Value);
        command.Parameters.AddWithValue("$key", associationKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPhoto(reader) : null;
    }

    private static async Task<PhotoIngestionSnapshot?> LoadPhotoByIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PhotoId photoId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT photo_id, project_id, source_id, association_key, state,
                   master_asset_id, master_format, association_deadline_utc,
                   created_at_utc, updated_at_utc
            FROM photos WHERE photo_id=$photo;
            """;
        command.Parameters.AddWithValue("$photo", photoId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadPhoto(reader) : null;
    }

    private static async Task<AssetSnapshot?> FindAssetByHashAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProjectId projectId,
        string sha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = AssetSelect + " WHERE project_id=$project AND sha256=$hash LIMIT 1;";
        command.Parameters.AddWithValue("$project", projectId.Value);
        command.Parameters.AddWithValue("$hash", sha256.ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAsset(reader) : null;
    }

    private static async Task<IReadOnlyList<AssetSnapshot>> LoadAssetsForPhotoAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PhotoId photoId,
        CancellationToken cancellationToken)
    {
        var results = new List<AssetSnapshot>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = AssetSelect + " WHERE photo_id=$photo ORDER BY archived_at_utc, asset_id;";
        command.Parameters.AddWithValue("$photo", photoId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadAsset(reader));
        }
        return results;
    }

    private static PhotoIngestionSnapshot ReadPhoto(SqliteDataReader reader) =>
        new(
            new PhotoId(reader.GetString(0)),
            new ProjectId(reader.GetString(1)),
            new IngestionSourceId(reader.GetString(2)),
            reader.GetString(3),
            ParsePhotoState(reader.GetString(4)),
            reader.IsDBNull(5) ? null : new AssetId(reader.GetString(5)),
            reader.IsDBNull(6) ? null : ParseFormat(reader.GetString(6)),
            ParseUtc(reader.GetString(7)),
            ParseUtc(reader.GetString(8)),
            ParseUtc(reader.GetString(9)));

    private static AssetSnapshot ReadAsset(SqliteDataReader reader) =>
        new(
            new AssetId(reader.GetString(0)),
            new ProjectId(reader.GetString(1)),
            new PhotoId(reader.GetString(2)),
            new IngestionSourceId(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            ParseFormat(reader.GetString(7)),
            ParseRole(reader.GetString(8)),
            AssetArchiveState.Archived,
            reader.GetInt64(10),
            reader.GetString(11),
            new(
                ParseRawSupport(reader.GetString(12)),
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.GetString(15)),
            ParseUtc(reader.GetString(16)),
            ParseUtc(reader.GetString(17)));

    private static IngestionSourceSnapshot ReadSource(SqliteDataReader reader) =>
        new(
            new IngestionSourceId(reader.GetString(0)),
            new ProjectId(reader.GetString(1)),
            reader.GetString(2),
            reader.GetInt32(3) != 0,
            reader.GetString(4),
            ParseUtc(reader.GetString(5)),
            reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6)));

    private static string FormatToken(AssetFormat value) => value switch
    {
        AssetFormat.Raw => "RAW",
        AssetFormat.Jpeg => "JPEG",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static AssetFormat ParseFormat(string value) => value switch
    {
        "RAW" => AssetFormat.Raw,
        "JPEG" => AssetFormat.Jpeg,
        _ => throw new InvalidDataException($"Unknown asset format '{value}'.")
    };

    private static string RoleToken(AssetRole value) => value switch
    {
        AssetRole.RawOriginal => "RAW_ORIGINAL",
        AssetRole.JpegPending => "JPEG_PENDING",
        AssetRole.JpegCamera => "JPEG_CAMERA",
        AssetRole.JpegMaster => "JPEG_MASTER",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static AssetRole ParseRole(string value) => value switch
    {
        "RAW_ORIGINAL" => AssetRole.RawOriginal,
        "JPEG_PENDING" => AssetRole.JpegPending,
        "JPEG_CAMERA" => AssetRole.JpegCamera,
        "JPEG_MASTER" => AssetRole.JpegMaster,
        _ => throw new InvalidDataException($"Unknown asset role '{value}'.")
    };

    private static string PhotoStateToken(IngestionPhotoState value) => value switch
    {
        IngestionPhotoState.WaitingForAssociation => "WAITING_FOR_ASSOCIATION",
        IngestionPhotoState.ReadyForAnalysis => "READY_FOR_ANALYSIS",
        IngestionPhotoState.ReviewUnsupportedFormat => "REVIEW_UNSUPPORTED_FORMAT",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static IngestionPhotoState ParsePhotoState(string value) => value switch
    {
        "WAITING_FOR_ASSOCIATION" => IngestionPhotoState.WaitingForAssociation,
        "READY_FOR_ANALYSIS" => IngestionPhotoState.ReadyForAnalysis,
        "REVIEW_UNSUPPORTED_FORMAT" => IngestionPhotoState.ReviewUnsupportedFormat,
        _ => throw new InvalidDataException($"Unknown ingestion Photo state '{value}'.")
    };

    private static string RawSupportToken(RawSupportStatus value) => value switch
    {
        RawSupportStatus.NotApplicable => "NOT_APPLICABLE",
        RawSupportStatus.SupportedFullSize => "SUPPORTED_FULL_SIZE",
        RawSupportStatus.UnsupportedReduced => "UNSUPPORTED_REDUCED",
        RawSupportStatus.Unknown => "UNKNOWN",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static RawSupportStatus ParseRawSupport(string value) => value switch
    {
        "NOT_APPLICABLE" => RawSupportStatus.NotApplicable,
        "SUPPORTED_FULL_SIZE" => RawSupportStatus.SupportedFullSize,
        "UNSUPPORTED_REDUCED" => RawSupportStatus.UnsupportedReduced,
        "UNKNOWN" => RawSupportStatus.Unknown,
        _ => throw new InvalidDataException($"Unknown RAW support status '{value}'.")
    };

    private static DateTimeOffset EnsureUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero ? value : value.ToUniversalTime();

    private static string FormatUtc(DateTimeOffset value) =>
        EnsureUtc(value).ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private const string AssetSelect = """
        SELECT asset_id, project_id, photo_id, source_id, source_path, source_relative_path,
               managed_path, format, role, archive_state, size_bytes, sha256,
               raw_support_status, raw_max_width, raw_max_height, raw_classification,
               observed_at_utc, archived_at_utc
        FROM assets
        """;
}
