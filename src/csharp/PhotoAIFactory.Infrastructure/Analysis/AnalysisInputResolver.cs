using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Analysis;
using PhotoAIFactory.Domain.Ingestion;

namespace PhotoAIFactory.Infrastructure.Analysis;

public sealed class AnalysisInputResolver(
    IIngestionStoreFactory ingestionStores,
    IAnalysisPreviewProvider previewProvider) : IAnalysisInputResolver
{
    public async Task<ResolvedAnalysisInput> ResolveAsync(
        ProjectId projectId,
        PhotoId photoId,
        JobId jobId,
        string attemptId,
        CancellationToken cancellationToken = default)
    {
        var store = ingestionStores.Open(projectId);
        var photos = await store.ListPhotosAsync(projectId, cancellationToken).ConfigureAwait(false);
        var photo = photos.SingleOrDefault(item => item.Id == photoId)
            ?? throw new AnalysisInputException("PHOTO_NOT_FOUND", $"Photo {photoId.Value} was not found.");

        if (photo.State != IngestionPhotoState.ReadyForAnalysis)
        {
            throw new AnalysisInputException(
                "PHOTO_NOT_READY",
                $"Photo {photoId.Value} is {photo.State}, not READY_FOR_ANALYSIS.");
        }

        var assets = (await store.ListAssetsAsync(projectId, cancellationToken).ConfigureAwait(false))
            .Where(item => item.PhotoId == photoId)
            .ToArray();

        var jpegCamera = assets.SingleOrDefault(item => item.Role == AssetRole.JpegCamera);
        if (jpegCamera is not null)
        {
            ValidateManagedAsset(jpegCamera);
            return new(jpegCamera.Id, jpegCamera.Sha256, AnalysisInputKind.JpegCamera, jpegCamera.ManagedPath, false);
        }

        var jpegMaster = assets.SingleOrDefault(item => item.Role == AssetRole.JpegMaster);
        if (jpegMaster is not null)
        {
            ValidateManagedAsset(jpegMaster);
            return new(jpegMaster.Id, jpegMaster.Sha256, AnalysisInputKind.JpegMaster, jpegMaster.ManagedPath, false);
        }

        var raw = assets.SingleOrDefault(item =>
            item.Role == AssetRole.RawOriginal && item.Format == AssetFormat.Raw);
        if (raw is null)
        {
            throw new AnalysisInputException("ANALYSIS_INPUT_MISSING", "No managed analysis source Asset exists.");
        }

        ValidateManagedAsset(raw);
        if (raw.RawSupport.Status != RawSupportStatus.SupportedFullSize)
        {
            throw new AnalysisInputException(
                raw.RawSupport.Status == RawSupportStatus.UnsupportedReduced
                    ? "UNSUPPORTED_REDUCED_RAW"
                    : "UNKNOWN_RAW",
                $"RAW {raw.ManagedPath} is {raw.RawSupport.Status} and cannot enter V1 RAW analysis.");
        }

        var previewPath = previewProvider.GetPreviewPath(projectId, jobId, attemptId, raw);
        return new(raw.Id, raw.Sha256, AnalysisInputKind.RawPreview, previewPath, true);
    }

    public async Task EnsureRepresentationAsync(
        ProjectId projectId,
        JobId jobId,
        string attemptId,
        ResolvedAnalysisInput input,
        CancellationToken cancellationToken = default)
    {
        if (input.Kind is AnalysisInputKind.JpegCamera or AnalysisInputKind.JpegMaster)
        {
            if (!File.Exists(input.RepresentationPath) || new FileInfo(input.RepresentationPath).Length == 0)
            {
                throw new AnalysisInputException(
                    "MANAGED_JPEG_MISSING",
                    $"Managed JPEG analysis representation is missing or empty: {input.RepresentationPath}");
            }

            return;
        }

        var store = ingestionStores.Open(projectId);
        var raw = (await store.ListAssetsAsync(projectId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == input.SourceAssetId)
            ?? throw new AnalysisInputException(
                "ANALYSIS_SOURCE_ASSET_MISSING",
                $"Frozen analysis source Asset {input.SourceAssetId.Value} no longer exists.");

        ValidateManagedAsset(raw);
        if (raw.RawSupport.Status != RawSupportStatus.SupportedFullSize)
        {
            throw new AnalysisInputException(
                "ANALYSIS_SOURCE_CHANGED",
                "Frozen RAW source is no longer classified SUPPORTED_FULL_SIZE.");
        }

        await previewProvider.EnsurePreviewAsync(
            projectId, jobId, attemptId, raw, input.RepresentationPath, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateManagedAsset(AssetSnapshot asset)
    {
        if (asset.ArchiveState != AssetArchiveState.Archived ||
            string.IsNullOrWhiteSpace(asset.ManagedPath) ||
            string.IsNullOrWhiteSpace(asset.Sha256) ||
            asset.Sha256.Length != 64)
        {
            throw new AnalysisInputException(
                "MANAGED_ORIGINAL_NOT_VALIDATED",
                $"Asset {asset.Id.Value} is not a validated managed original.");
        }

        if (!File.Exists(asset.ManagedPath) || new FileInfo(asset.ManagedPath).Length != asset.SizeBytes)
        {
            throw new AnalysisInputException(
                "MANAGED_ORIGINAL_MISSING_OR_SIZE_MISMATCH",
                $"Managed original is missing or size-mismatched: {asset.ManagedPath}");
        }
    }
}

public sealed class AnalysisInputException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}
