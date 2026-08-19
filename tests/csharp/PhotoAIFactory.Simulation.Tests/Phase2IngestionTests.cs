using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Ingestion;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Infrastructure.Ingestion;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Ingestion;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class Phase2IngestionTests
{
    private string? root;
    private SqliteProjectDatabase? database;
    private SqliteProjectStore? projectStore;
    private SqliteIngestionStore? ingestionStore;
    private ProjectId? projectId;
    private ProjectConfigV1? config;
    private ConfigVersion? configVersion;
    private readonly DateTimeOffset now =
        new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);

    [TestInitialize]
    public async Task Initialize()
    {
        root = Path.Combine(Path.GetTempPath(), "PhotoAIFactory-Phase2", Guid.NewGuid().ToString("N"));
        var input = Path.Combine(root, "input");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);

        database = new SqliteProjectDatabase(Path.Combine(root, "project.db"));
        projectStore = new SqliteProjectStore(database);
        ingestionStore = new SqliteIngestionStore(database);
        config = NewConfig(input, output, associationWindowSeconds: 1);

        var project = Project.Create("Phase2 test", now);
        configVersion = ConfigVersion.Create(project.Id, 1, config, "create", now);
        await projectStore.CreateAsync(project, configVersion, "create");
        projectId = project.Id;
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Extensions_AreCaseInsensitive()
    {
        Assert.IsTrue(IngestionCoordinator.IsSupportedExtension(".ARW"));
        Assert.IsTrue(IngestionCoordinator.IsSupportedExtension(".jpg"));
        Assert.IsTrue(IngestionCoordinator.IsSupportedExtension(".JPEG"));
        Assert.IsFalse(IngestionCoordinator.IsSupportedExtension(".png"));
    }

    [TestMethod]
    public void AssociationKey_SeparatesSubfolders()
    {
        Assert.AreNotEqual(
            IngestionCoordinator.AssociationKey(Path.Combine("A", "DSC0001.JPG")),
            IngestionCoordinator.AssociationKey(Path.Combine("B", "DSC0001.ARW")));
        Assert.AreEqual(
            IngestionCoordinator.AssociationKey(Path.Combine("A", "DSC0001.JPG")),
            IngestionCoordinator.AssociationKey(Path.Combine("A", "dsc0001.arw")));
    }

    [TestMethod]
    public async Task JpegOnly_FinalizesToJpegMaster()
    {
        var source = await PrepareSourceAsync();
        var jpeg = await IngestDirectAsync(source, "IMG0001.JPG", AssetFormat.Jpeg, "a");
        Assert.AreEqual(IngestionPhotoState.WaitingForAssociation, jpeg.Photo.State);

        Assert.AreEqual(1, await ingestionStore!.FinalizeAssociationsAsync(
            projectId!, source.Id, now.AddSeconds(2), force: false));

        var photo = (await ingestionStore.ListPhotosAsync(projectId!)).Single();
        var asset = (await ingestionStore.ListAssetsAsync(projectId!)).Single();
        Assert.AreEqual(IngestionPhotoState.ReadyForAnalysis, photo.State);
        Assert.AreEqual(AssetFormat.Jpeg, photo.MasterFormat);
        Assert.AreEqual(AssetRole.JpegMaster, asset.Role);
    }

    [TestMethod]
    public async Task RawOnly_FinalizesToRawMaster()
    {
        var source = await PrepareSourceAsync();
        await IngestDirectAsync(source, "IMG0002.ARW", AssetFormat.Raw, "raw");
        await ingestionStore!.FinalizeAssociationsAsync(
            projectId!, source.Id, now.AddSeconds(2), force: false);

        var photo = (await ingestionStore.ListPhotosAsync(projectId!)).Single();
        Assert.AreEqual(IngestionPhotoState.ReadyForAnalysis, photo.State);
        Assert.AreEqual(AssetFormat.Raw, photo.MasterFormat);
    }

    [TestMethod]
    public async Task RawAndJpeg_PairIntoOnePhoto()
    {
        var source = await PrepareSourceAsync();
        await IngestDirectAsync(source, "IMG0003.JPG", AssetFormat.Jpeg, "jpeg");
        await IngestDirectAsync(source, "IMG0003.ARW", AssetFormat.Raw, "raw");

        var photos = await ingestionStore!.ListPhotosAsync(projectId!);
        var assets = await ingestionStore.ListAssetsAsync(projectId!);
        Assert.AreEqual(1, photos.Count);
        Assert.AreEqual(2, assets.Count);
        Assert.AreEqual(IngestionPhotoState.ReadyForAnalysis, photos[0].State);
        Assert.AreEqual(AssetFormat.Raw, photos[0].MasterFormat);
        Assert.AreEqual(AssetRole.JpegCamera, assets.Single(a => a.Format == AssetFormat.Jpeg).Role);
    }

    [TestMethod]
    public async Task ExactDuplicate_DoesNotCreateAnotherPhoto()
    {
        var source = await PrepareSourceAsync();
        var first = await IngestDirectAsync(source, "IMG0004.JPG", AssetFormat.Jpeg, "same");
        var duplicateCommand = DirectCommand(source, "OTHER.JPG", AssetFormat.Jpeg, "same");
        var duplicate = await ingestionStore!.IngestArchivedAsync(duplicateCommand);

        Assert.AreEqual(IngestAssetStatus.DuplicateExact, duplicate.Status);
        Assert.AreEqual(first.Photo.Id, duplicate.Photo.Id);
        Assert.AreEqual(1, (await ingestionStore.ListPhotosAsync(projectId!)).Count);
        Assert.AreEqual(1, (await ingestionStore.ListAssetsAsync(projectId!)).Count);
    }

    [TestMethod]
    public async Task LateRaw_ReplacesJpegMasterBeforeJobsExist()
    {
        var source = await PrepareSourceAsync();
        await IngestDirectAsync(source, "IMG0005.JPG", AssetFormat.Jpeg, "jpeg");
        await ingestionStore!.FinalizeAssociationsAsync(
            projectId!, source.Id, now.AddSeconds(2), force: false);

        var raw = await IngestDirectAsync(source, "IMG0005.ARW", AssetFormat.Raw, "raw");
        Assert.AreEqual(IngestAssetStatus.LateRawAttached, raw.Status);
        Assert.AreEqual(AssetFormat.Raw, raw.Photo.MasterFormat);

        var assets = await ingestionStore.ListAssetsAsync(projectId!);
        Assert.AreEqual(AssetRole.JpegCamera, assets.Single(a => a.Format == AssetFormat.Jpeg).Role);
    }

    [TestMethod]
    public async Task UnsupportedRaw_RoutesToReview()
    {
        var source = await PrepareSourceAsync();
        var command = DirectCommand(
            source, "IMG0006.ARW", AssetFormat.Raw, "small",
            new RawSupportInfo(RawSupportStatus.UnsupportedReduced, 3000, 2000, "UNSUPPORTED_REDUCED_RAW"));
        var result = await ingestionStore!.IngestArchivedAsync(command);
        Assert.AreEqual(IngestionPhotoState.ReviewUnsupportedFormat, result.Photo.State);
    }

    [TestMethod]
    public async Task UnknownRaw_RoutesToReview()
    {
        var source = await PrepareSourceAsync();
        var command = DirectCommand(
            source, "IMG0007.ARW", AssetFormat.Raw, "unknown",
            new RawSupportInfo(RawSupportStatus.Unknown, 0, 0, "UNKNOWN_RAW_VARIANT"));
        var result = await ingestionStore!.IngestArchivedAsync(command);
        Assert.AreEqual(IngestionPhotoState.ReviewUnsupportedFormat, result.Photo.State);
    }

    [TestMethod]
    public async Task SourceChange_WithPendingAssociations_IsBlocked()
    {
        var source = await PrepareSourceAsync();
        await IngestDirectAsync(source, "IMG0008.JPG", AssetFormat.Jpeg, "pending");

        var otherRoot = Path.Combine(root!, "other-input");
        Directory.CreateDirectory(otherRoot);
        var blocked = await ingestionStore!.PrepareSourceAsync(
            projectId!, configVersion!.Id, otherRoot, true, now.AddMinutes(1));

        Assert.AreEqual(
            PrepareIngestionSourceStatus.PendingAssociationsRequireResolution,
            blocked.Status);
        Assert.AreEqual(1, blocked.PendingAssociationCount);
        Assert.AreEqual(source.Id, blocked.Source.Id);
    }

    [TestMethod]
    public async Task ForceResolvePending_AllowsNewSourceGeneration()
    {
        var source = await PrepareSourceAsync();
        await IngestDirectAsync(source, "IMG0009.JPG", AssetFormat.Jpeg, "pending");
        Assert.AreEqual(1, await ingestionStore!.FinalizeAssociationsAsync(
            projectId!, source.Id, now, force: true));

        var otherRoot = Path.Combine(root!, "other-input");
        Directory.CreateDirectory(otherRoot);
        var prepared = await ingestionStore.PrepareSourceAsync(
            projectId!, configVersion!.Id, otherRoot, true, now.AddMinutes(1));

        Assert.AreEqual(PrepareIngestionSourceStatus.Ready, prepared.Status);
        Assert.AreNotEqual(source.Id, prepared.Source.Id);
    }

    [TestMethod]
    public async Task ManagedArchive_CopiesValidatesAndDoesNotModifySource()
    {
        var path = Path.Combine(config!.InputFolder, "archive.JPG");
        var bytes = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        var hashBefore = await ShaAsync(path);

        var archive = new ManagedOriginalArchive(TimeProvider.System);
        var result = await archive.ArchiveAsync(
            path, config.OutputFolder, AssetFormat.Jpeg, bytes.Length, hashBefore);

        Assert.IsTrue(File.Exists(result.ManagedPath));
        Assert.AreEqual(hashBefore, await ShaAsync(result.ManagedPath));
        Assert.AreEqual(hashBefore, await ShaAsync(path));
        Assert.AreEqual(0, Directory.EnumerateFiles(
            Path.GetDirectoryName(result.ManagedPath)!, "*.partial-*").Count());
    }

    [TestMethod]
    public async Task Coordinator_ReconciliationStyleIngest_UsesRealFilesystemAndSqlite()
    {
        var source = await PrepareSourceAsync();
        var path = Path.Combine(config!.InputFolder, "real.JPEG");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5, 6]);

        var coordinator = new IngestionCoordinator(
            config,
            source,
            ingestionStore!,
            new DefaultFileStabilityProbe(),
            new ManagedOriginalArchive(TimeProvider.System),
            new StubRawClassifier(),
            TimeProvider.System);

        var result = await coordinator.IngestPathAsync(
            path, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(3));

        Assert.IsNotNull(result);
        Assert.AreEqual(1, (await ingestionStore!.ListPhotosAsync(projectId!)).Count);
        Assert.AreEqual(1, (await ingestionStore.ListAssetsAsync(projectId!)).Count);
    }

    [TestMethod]
    public async Task Migration003_IsIdempotentAndIntegrityCheckIsOk()
    {
        await database!.InitializeAsync();
        await database.InitializeAsync();

        await using var connection = await database.OpenConfiguredConnectionAsync();
        await using var migrations = connection.CreateCommand();
        migrations.CommandText = "SELECT count(*) FROM schema_migrations WHERE version=3;";
        Assert.AreEqual(1L, Convert.ToInt64(await migrations.ExecuteScalarAsync()));

        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        Assert.AreEqual("ok", Convert.ToString(await integrity.ExecuteScalarAsync()));
    }

    [TestMethod]
    public async Task SingleWriterBoundary_IsSharedWithProjectStore()
    {
        var otherDatabase = new SqliteProjectDatabase(database!.DatabasePath);
        Assert.IsTrue(database.Writer.SharesBoundaryWith(otherDatabase.Writer));

        var source = await PrepareSourceAsync();
        var tasks = Enumerable.Range(0, 12).Select(i =>
            IngestDirectAsync(source, $"C{i:D2}.JPG", AssetFormat.Jpeg, $"content-{i}"));
        await Task.WhenAll(tasks);

        Assert.AreEqual(12, (await ingestionStore!.ListPhotosAsync(projectId!)).Count);
        Assert.AreEqual(1, database.Writer.MaxObservedConcurrentWriters);
        Assert.AreEqual(0, database.Writer.OverlapViolationCount);
    }

    [TestMethod]
    public async Task BoundedChannel_ReconciliationDoesNotStarveFilesBeyondCapacity()
    {
        var source = await PrepareSourceAsync();
        var coordinator = new IngestionCoordinator(
            config!,
            source,
            ingestionStore!,
            new DefaultFileStabilityProbe(),
            new ManagedOriginalArchive(TimeProvider.System),
            new StubRawClassifier(),
            TimeProvider.System);
        var options = new IngestionRuntimeOptions
        {
            StableForMilliseconds = 100,
            StabilityTimeoutSeconds = 10,
            ReconciliationIntervalSeconds = 30,
            ChannelCapacity = 16,
            WatcherInternalBufferKilobytes = 4,
            EnableWatcher = false
        };
        await using var session = new FileSystemIngestionSession(
            projectId!,
            config!,
            source,
            ingestionStore!,
            coordinator,
            options,
            TimeProvider.System,
            NullLogger<FileSystemIngestionSession>.Instance);

        for (var index = 0; index < 40; index++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(config!.InputFolder, $"burst-{index:D2}.JPG"),
                BitConverter.GetBytes(index));
        }

        await session.StartAsync();
        await session.WaitForIdleAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(40, (await ingestionStore!.ListPhotosAsync(projectId!)).Count);
        Assert.AreEqual(40, (await ingestionStore.ListAssetsAsync(projectId!)).Count);
        await session.StopAsync();
    }

    [TestMethod]
    public async Task ArwClassifier_DistinguishesFullReducedAndUnknownConservatively()
    {
        var classifier = new SonyArwSupportClassifier();
        var full = Path.Combine(root!, "full.ARW");
        var reduced = Path.Combine(root!, "reduced.ARW");
        var unknown = Path.Combine(root!, "unknown.ARW");
        WriteMinimalTiff(full, 7000, 4600);
        WriteMinimalTiff(reduced, 3000, 2000);
        await File.WriteAllBytesAsync(unknown, [0, 1, 2, 3]);

        Assert.AreEqual(
            RawSupportStatus.SupportedFullSize,
            (await classifier.ClassifyAsync(full)).Status);
        Assert.AreEqual(
            RawSupportStatus.UnsupportedReduced,
            (await classifier.ClassifyAsync(reduced)).Status);
        Assert.AreEqual(
            RawSupportStatus.Unknown,
            (await classifier.ClassifyAsync(unknown)).Status);
    }

    private async Task<IngestionSourceSnapshot> PrepareSourceAsync()
    {
        var result = await ingestionStore!.PrepareSourceAsync(
            projectId!, configVersion!.Id, config!.InputFolder,
            config.IncludeSubfolders, now);
        Assert.AreEqual(PrepareIngestionSourceStatus.Ready, result.Status);
        return result.Source;
    }

    private async Task<IngestAssetResult> IngestDirectAsync(
        IngestionSourceSnapshot source,
        string relative,
        AssetFormat format,
        string content)
    {
        return await ingestionStore!.IngestArchivedAsync(
            DirectCommand(source, relative, format, content));
    }

    private IngestAssetCommand DirectCommand(
        IngestionSourceSnapshot source,
        string relative,
        AssetFormat format,
        string content,
        RawSupportInfo? rawSupport = null)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var sourcePath = Path.Combine(config!.InputFolder, relative);
        var managedPath = Path.Combine(config.OutputFolder, ".photo-ai-factory", "originals",
            format == AssetFormat.Raw ? "RAW" : "JPEG_CAMERA", hash + (format == AssetFormat.Raw ? ".arw" : ".jpg"));

        return new(
            projectId!,
            source.Id,
            IngestionCoordinator.AssociationKey(relative),
            sourcePath,
            relative,
            managedPath,
            format,
            bytes.Length,
            hash,
            rawSupport ?? (format == AssetFormat.Raw
                ? new RawSupportInfo(RawSupportStatus.SupportedFullSize, 7000, 4600, "FULL_SIZE_RAW")
                : RawSupportInfo.NotApplicable),
            now,
            now,
            TimeSpan.FromSeconds(config.AssociationWindowSeconds));
    }

    private static ProjectConfigV1 NewConfig(
        string input,
        string output,
        int associationWindowSeconds) =>
        new(
            input,
            output,
            includeSubfolders: true,
            RevealMode.DtAuto,
            preselectionEnabled: true,
            preselectionProfile: "default",
            SemanticMode.Standard,
            ComfyUiMode.Off,
            authorizedComfyUiTasks: [],
            presetProfiles: ["baseline"],
            exportFormat: "JPEG",
            exportQuality: 92,
            associationWindowSeconds);

    private static async Task<string> ShaAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static void WriteMinimalTiff(string path, ushort width, ushort height)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)'I');
        writer.Write((byte)'I');
        writer.Write((ushort)42);
        writer.Write((uint)8);
        writer.Write((ushort)2);

        writer.Write((ushort)256);
        writer.Write((ushort)3);
        writer.Write((uint)1);
        writer.Write(width);
        writer.Write((ushort)0);

        writer.Write((ushort)257);
        writer.Write((ushort)3);
        writer.Write((uint)1);
        writer.Write(height);
        writer.Write((ushort)0);

        writer.Write((uint)0);
    }

    private sealed class StubRawClassifier : IRawSupportClassifier
    {
        public Task<RawSupportInfo> ClassifyAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RawSupportInfo(
                RawSupportStatus.SupportedFullSize, 7000, 4600, "FULL_SIZE_RAW"));
    }
}
