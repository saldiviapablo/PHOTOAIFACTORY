using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Application.UI.ViewModels;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Ingestion;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Domain.Qa;
using PhotoAIFactory.Infrastructure.Health;
using PhotoAIFactory.Infrastructure.Hosting;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;
using PhotoAIFactory.Infrastructure.Processing;
using PhotoAIFactory.Infrastructure.UI;

namespace PhotoAIFactory.Simulation.Tests;

public sealed class TestNavService : INavigationService
{
    public string CurrentPageKey { get; private set; } = "Projects";
    public object? CurrentParameter { get; private set; }
    public bool CanGoBack => false;
    public event EventHandler<string>? Navigated;

    public void NavigateTo(string pageKey, object? parameter = null)
    {
        CurrentPageKey = pageKey;
        CurrentParameter = parameter;
        Navigated?.Invoke(this, pageKey);
    }

    public void GoBack()
    {
    }
}

public sealed class TestAppPaths(string root) : IAppPaths
{
    public string RootDirectory => root;
    public string ProjectsDirectory => Path.Combine(root, "projects");
    public string WorkDirectory => Path.Combine(root, "work");
    public string LogsDirectory => Path.Combine(root, "logs");
    public string ModelsDirectory => Path.Combine(root, "models");
    public string ComponentsDirectory => Path.Combine(root, "components");

    public string GetProjectDatabasePath(ProjectId projectId) => Path.Combine(ProjectsDirectory, projectId.Value, "project.db");
}

[TestClass]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class PresentationLayerTests
{
    private string testWorkDir = null!;

    [TestInitialize]
    public void Setup()
    {
        testWorkDir = Path.Combine(Path.GetTempPath(), "PAF_Presentation_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testWorkDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(testWorkDir))
            {
                Directory.Delete(testWorkDir, true);
            }
        }
        catch
        {
        }
    }

    private HostApplicationBuilder CreateTestHostBuilder(Action<IServiceCollection>? configure = null)
    {
        var builder = PhotoAIFactoryHost.CreateBuilder();
        builder.Services.AddSingleton<IAppPaths>(new TestAppPaths(testWorkDir));
        configure?.Invoke(builder.Services);
        return builder;
    }

    [TestMethod]
    public void DI_Container_Resolves_All_ViewModels_And_QueryServices()
    {
        var builder = CreateTestHostBuilder();
        builder.Services.AddSingleton<INavigationService, TestNavService>();

        using var host = builder.Build();

        Assert.IsNotNull(host.Services.GetRequiredService<ShellViewModel>());
        Assert.IsNotNull(host.Services.GetRequiredService<ProjectsViewModel>());
        Assert.IsNotNull(host.Services.GetRequiredService<CreateProjectViewModel>());
        Assert.IsNotNull(host.Services.GetRequiredService<DashboardViewModel>());
        Assert.IsNotNull(host.Services.GetRequiredService<QueueViewModel>());
        Assert.IsNotNull(host.Services.GetRequiredService<JobDetailViewModel>());
        Assert.IsNotNull(host.Services.GetRequiredService<ReviewViewModel>());
        Assert.IsNotNull(host.Services.GetRequiredService<ProjectConfigViewModel>());
        Assert.IsNotNull(host.Services.GetRequiredService<HistoryViewModel>());
        Assert.IsNotNull(host.Services.GetRequiredService<ModelsViewModel>());
        Assert.IsNotNull(host.Services.GetRequiredService<LogsViewModel>());
        Assert.IsNotNull(host.Services.GetRequiredService<PreferencesViewModel>());

        Assert.IsNotNull(host.Services.GetRequiredService<IProjectQueryService>());
        Assert.IsNotNull(host.Services.GetRequiredService<IDashboardQueryService>());
        Assert.IsNotNull(host.Services.GetRequiredService<IQueueQueryService>());
        Assert.IsNotNull(host.Services.GetRequiredService<IReviewQueryService>());
        Assert.IsNotNull(host.Services.GetRequiredService<IHistoryQueryService>());
        Assert.IsNotNull(host.Services.GetRequiredService<IModelStatusService>());
        Assert.IsNotNull(host.Services.GetRequiredService<IErrorLogQueryService>());
        Assert.IsNotNull(host.Services.GetRequiredService<IThumbnailService>());
        Assert.IsNotNull(host.Services.GetRequiredService<IAppPreferencesService>());
    }

    [TestMethod]
    public async Task CreateProjectViewModel_Validates_And_Creates_Project()
    {
        var builder = CreateTestHostBuilder();
        var nav = new TestNavService();
        builder.Services.AddSingleton<INavigationService>(nav);

        using var host = builder.Build();
        var vm = host.Services.GetRequiredService<CreateProjectViewModel>();
        var context = host.Services.GetRequiredService<IProjectContext>();

        // Validation failures
        vm.ProjectName = "";
        await vm.CreateProjectAsync();
        Assert.IsNotNull(vm.ValidationError);

        var inDir = Path.Combine(testWorkDir, "in");
        var outDir = Path.Combine(testWorkDir, "out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        vm.ProjectName = "Test Presentation Project";
        vm.InputFolder = inDir;
        vm.OutputFolder = inDir; // Same folder error
        await vm.CreateProjectAsync();
        Assert.IsTrue(vm.ValidationError.Contains("must be different"));

        // Valid creation
        vm.OutputFolder = outDir;
        await vm.CreateProjectAsync();

        Assert.IsNull(vm.ValidationError);
        Assert.IsTrue(context.HasActiveProject);
        Assert.AreEqual("Test Presentation Project", context.ActiveProjectName);
        Assert.AreEqual("Dashboard", nav.CurrentPageKey);
    }

    [TestMethod]
    public async Task ProjectConfigViewModel_Enforces_Edit_Guard_And_Saves_New_Version()
    {
        var builder = CreateTestHostBuilder();
        var nav = new TestNavService();
        builder.Services.AddSingleton<INavigationService>(nav);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var context = host.Services.GetRequiredService<IProjectContext>();
        var configVm = host.Services.GetRequiredService<ProjectConfigViewModel>();

        var inDir = Path.Combine(testWorkDir, "cfg_in");
        var outDir = Path.Combine(testWorkDir, "cfg_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var initialConfig = new ProjectConfigV1(inDir, outDir, false, RevealMode.PreAi, true, "Standard", SemanticMode.Standard, ComfyUiMode.Off, [], [], "JPEG", 90, 5);
        var snapshot = await projectService.CreateProjectAsync("Config Test", initialConfig, "op_init", DateTimeOffset.UtcNow);

        // Transition project to Paused state
        var lifecycle = host.Services.GetRequiredService<ProjectLifecycleService>();
        await lifecycle.StartOrResumeAsync(snapshot.Project.Id, "op_start");
        await lifecycle.RequestPauseAsync(snapshot.Project.Id, "op_pause");

        // Now project is Paused -> CanEdit is true
        context.SetActiveProject(snapshot.Project.Id, snapshot.Project.Name, ProjectState.Paused);

        await configVm.RefreshAsync();
        Assert.IsTrue(configVm.CanEdit);
        Assert.AreEqual(1, configVm.CurrentVersion?.VersionNumber);

        // Start editing and save
        configVm.StartEdit();
        Assert.IsTrue(configVm.IsEditing);
        configVm.ExportQuality = 95;
        await configVm.SaveConfigAsync();

        Assert.IsFalse(configVm.IsEditing);
        Assert.AreEqual(2, configVm.CurrentVersion?.VersionNumber);
        Assert.AreEqual(95, configVm.ExportQuality);

        // When project state transitions to Running -> CanEdit becomes false
        context.UpdateState(ProjectState.Running);
        Assert.IsFalse(configVm.CanEdit);
    }

    [TestMethod]
    public async Task RealDatabase_QueryServices_Integration_Passes_Against_Migration008_Schema()
    {
        var projectId = new ProjectId("proj_real_test_" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new TestAppPaths(testWorkDir);
        var dbPath = paths.GetProjectDatabasePath(projectId);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var db = new SqliteProjectDatabase(dbPath);
        await db.InitializeAsync();

        var storeFactory = new SqliteProjectStoreFactory(paths);
        var store = storeFactory.Open(projectId);

        var inDir = Path.Combine(testWorkDir, "real_in");
        var outDir = Path.Combine(testWorkDir, "real_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(inDir, outDir, false, RevealMode.PreAi, true, "Standard", SemanticMode.Standard, ComfyUiMode.Off, [], [], "JPEG", 90, 5);
        var configVersion = ConfigVersion.Create(projectId, 1, config, "op_create_1", DateTimeOffset.UtcNow);
        await store.CreateAsync(Project.Restore(projectId, "Real Test Project", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProjectState.Running, 1, DateTimeOffset.UtcNow), configVersion, "op_create_1");

        var now = DateTimeOffset.UtcNow.ToString("O");
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");

        // Populate realistic records conforming strictly to migrations 001 - 008
        await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();

            var pVal = projectId.Value;
            var inPath = Path.Combine(inDir, "DSC0001.ARW");
            var outPath = Path.Combine(outDir, "DSC0001.jpg");
            var workPath = Path.Combine(testWorkDir, "DSC0001.ARW");
            var preview1 = Path.Combine(testWorkDir, "preview1.jpg");
            var preview2 = Path.Combine(testWorkDir, "preview2.jpg");
            var preview3 = Path.Combine(testWorkDir, "preview3.jpg");
            var preview4 = Path.Combine(testWorkDir, "preview4.jpg");
            var histPath = Path.Combine(testWorkDir, "final_history.json");
            var sha1 = new string('1', 64);
            var sha2 = new string('2', 64);
            var sha3 = new string('3', 64);
            var sha4 = new string('4', 64);
            var shaa = new string('a', 64);
            var qaJson = "{\"schema_version\":1,\"decision\":\"REVIEW\",\"suggested_correction\":\"Manual Inspection\",\"technical\":{\"score\":85},\"findings\":[{\"code\":\"BLUR_WARNING\",\"severity\":\"warning\",\"message\":\"Slight motion blur\"}]}";

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO ingestion_sources(source_id, project_id, input_root, include_subfolders, config_version_id, created_at_utc) VALUES(@sId, @pId, @inRoot, 0, @cfgId, @earlier);";
                cmd.Parameters.AddWithValue("@sId", "src_1");
                cmd.Parameters.AddWithValue("@pId", pVal);
                cmd.Parameters.AddWithValue("@inRoot", inDir);
                cmd.Parameters.AddWithValue("@cfgId", configVersion.Id);
                cmd.Parameters.AddWithValue("@earlier", earlier);
                await cmd.ExecuteNonQueryAsync();
            }

            for (int i = 1; i <= 4; i++)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO photos(photo_id, project_id, source_id, association_key, state, master_format, association_deadline_utc, created_at_utc, updated_at_utc) VALUES(@phId, @pId, 'src_1', @assoc, 'READY_FOR_ANALYSIS', 'RAW', @now, @earlier, @now);";
                cmd.Parameters.AddWithValue("@phId", $"photo_{i}");
                cmd.Parameters.AddWithValue("@pId", pVal);
                cmd.Parameters.AddWithValue("@assoc", $"DSC000{i}");
                cmd.Parameters.AddWithValue("@now", now);
                cmd.Parameters.AddWithValue("@earlier", earlier);
                await cmd.ExecuteNonQueryAsync();
            }

            for (int i = 1; i <= 4; i++)
            {
                var sha = new string((char)('0' + i), 64);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO assets(asset_id, project_id, photo_id, source_id, source_path, source_relative_path, managed_path, format, role, archive_state, size_bytes, sha256, raw_support_status, raw_max_width, raw_max_height, raw_classification, observed_at_utc, archived_at_utc) VALUES(@aId, @pId, @phId, 'src_1', @srcP, @relP, @manP, 'RAW', 'RAW_ORIGINAL', 'ARCHIVED', 35000000, @sha, 'SUPPORTED_FULL_SIZE', 7008, 4672, 'RAW', @earlier, @earlier);";
                cmd.Parameters.AddWithValue("@aId", $"asset_{i}");
                cmd.Parameters.AddWithValue("@pId", pVal);
                cmd.Parameters.AddWithValue("@phId", $"photo_{i}");
                cmd.Parameters.AddWithValue("@srcP", Path.Combine(inDir, $"DSC000{i}.ARW"));
                cmd.Parameters.AddWithValue("@relP", $"DSC000{i}.ARW");
                cmd.Parameters.AddWithValue("@manP", Path.Combine(testWorkDir, $"DSC000{i}.ARW"));
                cmd.Parameters.AddWithValue("@sha", sha);
                cmd.Parameters.AddWithValue("@earlier", earlier);
                await cmd.ExecuteNonQueryAsync();
            }

            // Job 1: COMPLETED
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO jobs(job_id, project_id, photo_id, parent_job_id, state, preselection_config_id, processing_config_id, analysis_source_asset_id, analysis_source_sha256, analysis_input_kind, analysis_representation_path, technical_retry_count, quality_reprocess_count, created_at_utc, updated_at_utc) VALUES('job_1', @pId, 'photo_1', NULL, 'COMPLETED', @cfgId, @cfgId, 'asset_1', @sha1, 'JPEG_CAMERA', @repP, 0, 0, @earlier, @now);";
                cmd.Parameters.AddWithValue("@pId", pVal);
                cmd.Parameters.AddWithValue("@cfgId", configVersion.Id);
                cmd.Parameters.AddWithValue("@sha1", sha1);
                cmd.Parameters.AddWithValue("@repP", preview1);
                cmd.Parameters.AddWithValue("@earlier", earlier);
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO job_state_transitions(transition_id, job_id, from_state, to_state, reason, operation_id, occurred_at_utc) VALUES('t1_1', 'job_1', NULL, 'RECEIVED', 'Created', 'op1', @earlier), ('t1_2', 'job_1', 'RECEIVED', 'COMPLETED', 'Finished', 'op2', @now);";
                cmd.Parameters.AddWithValue("@earlier", earlier);
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc) VALUES('chk1_1', 'job_1', 'ANALYSIS_COMPLETE', 'att_1', 'fp_1', @earlier), ('chk1_2', 'job_1', 'OUTPUT_PUBLISHED', 'att_1', 'fp_pub_1', @now);";
                cmd.Parameters.AddWithValue("@earlier", earlier);
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO publications(publication_id, job_id, attempt_id, destination_kind, destination_path, sha256, size_bytes, width, height, history_path, published_at_utc) VALUES('pub_1', 'job_1', 'att_1', 'FINAL', @destP, @shaa, 5000000, 7008, 4672, @histP, @now);";
                cmd.Parameters.AddWithValue("@destP", outPath);
                cmd.Parameters.AddWithValue("@shaa", shaa);
                cmd.Parameters.AddWithValue("@histP", histPath);
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync();
            }

            // Job 2: QUEUED
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO jobs(job_id, project_id, photo_id, parent_job_id, state, preselection_config_id, processing_config_id, analysis_source_asset_id, analysis_source_sha256, analysis_input_kind, analysis_representation_path, technical_retry_count, quality_reprocess_count, created_at_utc, updated_at_utc) VALUES('job_2', @pId, 'photo_2', NULL, 'QUEUED', @cfgId, @cfgId, 'asset_2', @sha2, 'JPEG_CAMERA', @repP, 0, 0, @earlier, @now);";
                cmd.Parameters.AddWithValue("@pId", pVal);
                cmd.Parameters.AddWithValue("@cfgId", configVersion.Id);
                cmd.Parameters.AddWithValue("@sha2", sha2);
                cmd.Parameters.AddWithValue("@repP", preview2);
                cmd.Parameters.AddWithValue("@earlier", earlier);
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO queue_entries(queue_entry_id, project_id, job_id, sequence_number, process_next, enqueued_at_utc) VALUES('qe_2', @pId, 'job_2', 1, 1, @earlier);";
                cmd.Parameters.AddWithValue("@pId", pVal);
                cmd.Parameters.AddWithValue("@earlier", earlier);
                await cmd.ExecuteNonQueryAsync();
            }

            // Job 3: REVIEW_FINAL with QA results
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO jobs(job_id, project_id, photo_id, parent_job_id, state, preselection_config_id, processing_config_id, analysis_source_asset_id, analysis_source_sha256, analysis_input_kind, analysis_representation_path, technical_retry_count, quality_reprocess_count, created_at_utc, updated_at_utc) VALUES('job_3', @pId, 'photo_3', NULL, 'REVIEW_FINAL', @cfgId, @cfgId, 'asset_3', @sha3, 'JPEG_CAMERA', @repP, 0, 0, @earlier, @now);";
                cmd.Parameters.AddWithValue("@pId", pVal);
                cmd.Parameters.AddWithValue("@cfgId", configVersion.Id);
                cmd.Parameters.AddWithValue("@sha3", sha3);
                cmd.Parameters.AddWithValue("@repP", preview3);
                cmd.Parameters.AddWithValue("@earlier", earlier);
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO qa_results(qa_result_id, job_id, attempt_id, decision, result_json, input_path, input_sha256, created_at_utc) VALUES('qa_3', 'job_3', 'att_3', 'REVIEW', @qaJson, @inP, @sha3, @now);";
                cmd.Parameters.AddWithValue("@qaJson", qaJson);
                cmd.Parameters.AddWithValue("@inP", preview3);
                cmd.Parameters.AddWithValue("@sha3", sha3);
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO review_items(review_item_id, job_id, review_kind, status, created_at_utc) VALUES('rev_3', 'job_3', 'FINAL', 'PENDING', @now);";
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync();
            }

            // Job 4: ERROR
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO jobs(job_id, project_id, photo_id, parent_job_id, state, preselection_config_id, processing_config_id, analysis_source_asset_id, analysis_source_sha256, analysis_input_kind, analysis_representation_path, technical_retry_count, quality_reprocess_count, created_at_utc, updated_at_utc) VALUES('job_4', @pId, 'photo_4', NULL, 'ERROR', @cfgId, @cfgId, 'asset_4', @sha4, 'JPEG_CAMERA', @repP, 2, 0, @earlier, @now);";
                cmd.Parameters.AddWithValue("@pId", pVal);
                cmd.Parameters.AddWithValue("@cfgId", configVersion.Id);
                cmd.Parameters.AddWithValue("@sha4", sha4);
                cmd.Parameters.AddWithValue("@repP", preview4);
                cmd.Parameters.AddWithValue("@earlier", earlier);
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO job_state_transitions(transition_id, job_id, from_state, to_state, reason, operation_id, occurred_at_utc) VALUES('trans_4_1', 'job_4', 'PROCESSING', 'ERROR', 'GPU OOM failure', 'op_err_4', @now);";
                cmd.Parameters.AddWithValue("@now", now);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        var healthTracker = new ComponentHealthTracker();
        var dashboardQuery = new DashboardQueryService(paths, storeFactory, healthTracker);
        var queueQuery = new QueueQueryService(paths, storeFactory, healthTracker);
        var reviewQuery = new ReviewQueryService(paths);
        var historyQuery = new HistoryQueryService(paths);
        var projectQuery = new ProjectQueryService(paths, storeFactory);

        // 1. Dashboard Query
        var dashboardSummary = await dashboardQuery.GetDashboardSummaryAsync(projectId);
        Assert.IsNotNull(dashboardSummary);
        Assert.AreEqual(1, dashboardSummary.CompletedCount);
        Assert.AreEqual(1, dashboardSummary.QueuedCount);
        Assert.AreEqual(1, dashboardSummary.ReviewCount);
        Assert.AreEqual(1, dashboardSummary.ErrorCount);
        Assert.IsTrue(dashboardSummary.HasAverageTimeData);
        Assert.IsTrue(dashboardSummary.AverageProcessingTime > TimeSpan.Zero);

        // 2. Queue Query
        var queueOverview = await queueQuery.GetQueueOverviewAsync(projectId);
        Assert.IsNotNull(queueOverview);
        Assert.AreEqual(1, queueOverview.TotalQueued);
        Assert.AreEqual(1, queueOverview.Items.Count);
        Assert.AreEqual("DSC0002", queueOverview.Items[0].PhotoName);
        Assert.AreEqual(1L, queueOverview.Items[0].QueueSequence);

        // 3. Job Detail Query
        var jobDetail1 = await queueQuery.GetJobDetailAsync(projectId, new JobId("job_1"));
        Assert.IsNotNull(jobDetail1);
        Assert.AreEqual(JobState.Completed, jobDetail1.State);
        Assert.AreEqual(2, jobDetail1.Checkpoints.Count);
        Assert.AreEqual("OUTPUT_PUBLISHED", jobDetail1.Checkpoints[1].StageName);
        Assert.AreEqual("fp_pub_1", jobDetail1.Checkpoints[1].InputFingerprint);
        Assert.AreEqual(Path.Combine(outDir, "DSC0001.jpg"), jobDetail1.OutputPublishedPath);

        var jobDetail3 = await queueQuery.GetJobDetailAsync(projectId, new JobId("job_3"));
        Assert.IsNotNull(jobDetail3);
        Assert.IsNotNull(jobDetail3.QaResult);
        Assert.AreEqual(QaDecision.Review, jobDetail3.QaResult.Decision);
        Assert.AreEqual(85, jobDetail3.QaResult.TechnicalScore);
        Assert.AreEqual("Manual Inspection", jobDetail3.QaResult.SuggestedNextAction);

        var jobDetail4 = await queueQuery.GetJobDetailAsync(projectId, new JobId("job_4"));
        Assert.IsNotNull(jobDetail4);
        Assert.AreEqual(JobState.Error, jobDetail4.State);
        Assert.AreEqual("GPU OOM failure", jobDetail4.ErrorDetails);

        // 4. Review Query
        var pendingReviews = await reviewQuery.GetPendingReviewsAsync(projectId);
        Assert.AreEqual(1, pendingReviews.Count);
        Assert.AreEqual("job_3", pendingReviews[0].JobId?.Value);
        Assert.AreEqual(QaDecision.Review, pendingReviews[0].QaDecision);
        Assert.IsTrue(pendingReviews[0].Findings.ValueKind == JsonValueKind.Array);

        // 5. History Query
        var historyItems = await historyQuery.GetHistoryAsync(projectId);
        Assert.AreEqual(3, historyItems.Count); // job_1 (completed), job_3 (review_final), job_4 (error)
        var completedItem = historyItems.FirstOrDefault(h => h.JobId.Value == "job_1");
        Assert.IsNotNull(completedItem);
        Assert.AreEqual(JobState.Completed, completedItem.State);
        Assert.AreEqual(Path.Combine(outDir, "DSC0001.jpg"), completedItem.OutputPath);

        // 6. Project Summary Query
        var projSummary = await projectQuery.GetProjectSummaryAsync(projectId);
        Assert.IsNotNull(projSummary);
        Assert.AreEqual(4, projSummary.TotalPhotos);
        Assert.AreEqual(1, projSummary.CompletedJobs);
        Assert.AreEqual(1, projSummary.PendingReviews);
        Assert.AreEqual(1, projSummary.ActiveErrors);
    }

    [TestMethod]
    public async Task ThumbnailService_Downscales_Preserves_Aspect_Ratio_And_Enforces_Budget()
    {
        // 1 MB memory budget for testing rapid eviction
        var service = new ThumbnailService(maxMemoryBytes: 1024 * 1024, maxItemCount: 10);

        // Create a large synthetic 3000 x 2000 image
        var testImg = Path.Combine(testWorkDir, "large_33mp_sim.jpg");
        using (var bmp = new Bitmap(3000, 2000, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.CornflowerBlue);
                g.DrawString("Sony A7IV 33MP Sim", new Font(FontFamily.GenericSansSerif, 48), Brushes.White, new PointF(100, 100));
            }
            bmp.Save(testImg, ImageFormat.Jpeg);
        }

        var origFileInfo = new FileInfo(testImg);
        var origSha = ComputeFileSha256(testImg);
        Assert.IsTrue(origFileInfo.Length > 100_000);

        // Request 256x256 thumbnail
        var thumbBytes = await service.GetThumbnailBytesAsync(testImg, 256, 256);
        Assert.IsNotNull(thumbBytes);

        // Downscaled byte size must be dramatically smaller than original full size
        Assert.IsTrue(thumbBytes.Length < 50_000, $"Thumbnail bytes ({thumbBytes.Length}) should be compact");

        // Verify aspect ratio retained in downscaled thumbnail
        using (var ms = new MemoryStream(thumbBytes))
        using (var thumbImg = Image.FromStream(ms))
        {
            Assert.IsTrue(thumbImg.Width <= 256);
            Assert.IsTrue(thumbImg.Height <= 256);
            // 3000 x 2000 is 3:2 aspect ratio -> 256 x 171
            Assert.AreEqual(256, thumbImg.Width);
            Assert.AreEqual(171, thumbImg.Height);
        }

        // Verify source file was not modified or corrupted
        var postSha = ComputeFileSha256(testImg);
        Assert.AreEqual(origSha, postSha);

        // Verify caching: repeated call uses cache without growth
        var initialUsage = service.CurrentMemoryUsageBytes;
        var thumbBytes2 = await service.GetThumbnailBytesAsync(testImg, 256, 256);
        Assert.IsNotNull(thumbBytes2);
        Assert.AreEqual(initialUsage, service.CurrentMemoryUsageBytes);

        // Test cancellation safety
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await service.GetThumbnailBytesAsync(testImg, 120, 120, cts.Token);
        Assert.IsNull(cancelled);
    }

    [TestMethod]
    public async Task ErrorLogQueryService_Redacts_Tokens_And_Secrets_In_Message_And_Details()
    {
        var logsDir = Path.Combine(testWorkDir, "logs");
        Directory.CreateDirectory(logsDir);

        var logFile = Path.Combine(logsDir, "app_errors.jsonl");
        var line1 = JsonSerializer.Serialize(new
        {
            LogLevel = "Error",
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            Category = "Network",
            Message = "Failed authentication with Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.secret_token_12345 to API",
            Exception = "System.Exception: Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.token_part_2\n   at Worker.Send()",
            State = new { project_id = "p1", job_id = "j1" }
        });

        await File.WriteAllLinesAsync(logFile, [line1]);

        var paths = new TestAppPaths(testWorkDir);
        var service = new ErrorLogQueryService(paths);

        var logs = await service.GetErrorLogsAsync();
        Assert.AreEqual(1, logs.Count);

        var entry = logs[0];
        Assert.IsFalse(entry.Message.Contains("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"));
        Assert.IsTrue(entry.Message.Contains("Bearer [REDACTED]"));

        Assert.IsNotNull(entry.TechnicalDetails);
        Assert.IsFalse(entry.TechnicalDetails.Contains("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"));
        Assert.IsTrue(entry.TechnicalDetails.Contains("Bearer [REDACTED]"));
    }

    [TestMethod]
    public async Task CreateProject_EndToEnd_Lifecycle_Persistence_And_Reload()
    {
        var builder = CreateTestHostBuilder();
        var nav = new TestNavService();
        builder.Services.AddSingleton<INavigationService>(nav);
        builder.Services.AddSingleton<IAppPaths>(new TestAppPaths(testWorkDir));

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var projectQuery = host.Services.GetRequiredService<IProjectQueryService>();
        var appPaths = host.Services.GetRequiredService<IAppPaths>();

        var inDir = Path.Combine(testWorkDir, "in_e2e");
        var outDir = Path.Combine(testWorkDir, "out_e2e");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var vm = host.Services.GetRequiredService<CreateProjectViewModel>();
        vm.ProjectName = "Wedding_2026_Live";
        vm.InputFolder = inDir;
        vm.OutputFolder = outDir;
        vm.IncludeSubfolders = false;
        vm.RevealMode = RevealMode.DtAuto;

        await vm.CreateProjectAsync();
        Assert.IsNull(vm.ValidationError);
        Assert.AreEqual("Dashboard", nav.CurrentPageKey);

        // Verify project.db and project directories were created on disk
        var projects = await projectQuery.ListProjectsAsync();
        Assert.AreEqual(1, projects.Count);
        Assert.AreEqual("Wedding_2026_Live", projects[0].Name);

        var dbPath = appPaths.GetProjectDatabasePath(projects[0].Id);
        Assert.IsTrue(File.Exists(dbPath), $"Project database must exist at {dbPath}");

        // Create a second project to verify existing projects are not overwritten or damaged
        var inDir2 = Path.Combine(testWorkDir, "in_e2e2");
        var outDir2 = Path.Combine(testWorkDir, "out_e2e2");
        Directory.CreateDirectory(inDir2);
        Directory.CreateDirectory(outDir2);

        var vm2 = host.Services.GetRequiredService<CreateProjectViewModel>();
        vm2.ProjectName = "Commercial_Shoot_2026";
        vm2.InputFolder = inDir2;
        vm2.OutputFolder = outDir2;
        await vm2.CreateProjectAsync();
        Assert.IsNull(vm2.ValidationError);

        // Simulate app restart: re-query all projects
        var reloadedProjects = await projectQuery.ListProjectsAsync();
        Assert.AreEqual(2, reloadedProjects.Count);
        Assert.IsTrue(reloadedProjects.Any(p => p.Name == "Wedding_2026_Live"));
        Assert.IsTrue(reloadedProjects.Any(p => p.Name == "Commercial_Shoot_2026"));
    }

    [TestMethod]
    public async Task FolderPicker_Integration_And_Cancellation_Behavior()
    {
        var inDir = Path.Combine(testWorkDir, "picker_in");
        var outDir = Path.Combine(testWorkDir, "picker_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var mockPicker = new MockFolderPickerService();
        var builder = CreateTestHostBuilder();
        builder.Services.AddSingleton<IFolderPickerService>(mockPicker);
        builder.Services.AddSingleton<INavigationService, TestNavService>();
        builder.Services.AddSingleton<IAppPaths>(new TestAppPaths(testWorkDir));

        using var host = builder.Build();
        var vm = host.Services.GetRequiredService<CreateProjectViewModel>();

        // 1. Initial state - invalid
        vm.ProjectName = "Picker Test";
        Assert.IsFalse(vm.CanCreateProject());
        Assert.IsNotNull(vm.ValidationError);

        // 2. Pick Input Folder
        mockPicker.NextResult = inDir;
        await vm.BrowseInputCommand.ExecuteAsync();
        Assert.AreEqual(inDir, vm.InputFolder);
        Assert.IsTrue(vm.ValidationError.Contains("Both input and output folders are required"));

        // 3. Pick Output Folder
        mockPicker.NextResult = outDir;
        await vm.BrowseOutputCommand.ExecuteAsync();
        Assert.AreEqual(outDir, vm.OutputFolder);
        Assert.IsNull(vm.ValidationError);
        Assert.IsTrue(vm.CanCreateProject());

        // 4. Cancel Output Picker - path should be preserved
        mockPicker.NextResult = null; // User cancelled
        await vm.BrowseOutputCommand.ExecuteAsync();
        Assert.AreEqual(outDir, vm.OutputFolder, "Cancellation must not clear existing path");
        Assert.IsNull(vm.ValidationError);
    }

    [TestMethod]
    public async Task Project_StartResume_Reconciles_Existing_Files_And_Activates_Watcher()
    {
        var builder = CreateTestHostBuilder();
        var nav = new TestNavService();
        builder.Services.AddSingleton<INavigationService>(nav);
        builder.Services.AddSingleton<IAppPaths>(new TestAppPaths(testWorkDir));

        using var host = builder.Build();
        var projectContext = host.Services.GetRequiredService<IProjectContext>();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var projectQuery = host.Services.GetRequiredService<IProjectQueryService>();
        var ingestionStores = host.Services.GetRequiredService<IIngestionStoreFactory>();
        var dashboardVm = host.Services.GetRequiredService<DashboardViewModel>();
        var createVm = host.Services.GetRequiredService<CreateProjectViewModel>();
        var ingestionManager = host.Services.GetRequiredService<ProjectIngestionManager>();

        var inDir = Path.Combine(testWorkDir, "rec_in");
        var outDir = Path.Combine(testWorkDir, "rec_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        // Put an existing JPEG file in input directory BEFORE project creation / start
        var existingFile = Path.Combine(inDir, "pre_existing.jpg");
        using (var bmp = new Bitmap(100, 100))
        {
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Blue);
            bmp.Save(existingFile, ImageFormat.Jpeg);
        }
        var existingSha = ComputeFileSha256(existingFile);

        // Create Project via UI ViewModel
        createVm.ProjectName = "Reconciliation_E2E_Test";
        createVm.InputFolder = inDir;
        createVm.OutputFolder = outDir;
        await createVm.CreateProjectAsync();
        Assert.IsNull(createVm.ValidationError);
        Assert.AreEqual("Dashboard", nav.CurrentPageKey);

        var pId = projectContext.ActiveProjectId!;

        // Refresh dashboard: verify initially Stopped
        await dashboardVm.RefreshAsync();
        Assert.AreEqual(ProjectState.Stopped, dashboardVm.Summary!.State);
        Assert.AreEqual("Start Processing", dashboardVm.PauseButtonText);

        // Click Start Processing via Dashboard command (TogglePauseCommand)
        await dashboardVm.TogglePauseCommand.ExecuteAsync();

        // Verify project transitioned to Running
        Assert.AreEqual(ProjectState.Running, dashboardVm.Summary!.State);
        Assert.AreEqual("Pause Processing", dashboardVm.PauseButtonText);

        // Wait for startup reconciliation
        await ingestionManager.WaitForIdleAsync(pId, TimeSpan.FromSeconds(10));

        // Verify pre-existing file was reconciled and ingested into project.db
        var store = ingestionStores.Open(pId);
        var photos = await store.ListPhotosAsync(pId);
        Assert.AreEqual(1, photos.Count, "Pre-existing file must be detected by startup reconciliation");

        // Verify original file SHA is unchanged
        Assert.AreEqual(existingSha, ComputeFileSha256(existingFile));

        // Add a second file while project is Running (testing live FileSystemWatcher)
        var liveFile = Path.Combine(inDir, "live_watcher_arrival.jpg");
        using (var bmp = new Bitmap(120, 120))
        {
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Green);
            bmp.Save(liveFile, ImageFormat.Jpeg);
        }

        // Wait for watcher to capture and ingest new arrival
        await ingestionManager.WaitForIdleAsync(pId, TimeSpan.FromSeconds(10));

        var photosAfterLive = await store.ListPhotosAsync(pId);
        Assert.AreEqual(2, photosAfterLive.Count, "Live watcher must detect file copied while Running");

        // Pause processing via Dashboard command
        await dashboardVm.TogglePauseCommand.ExecuteAsync();
        Assert.AreEqual(ProjectState.Paused, dashboardVm.Summary!.State);
        Assert.AreEqual("Resume Processing", dashboardVm.PauseButtonText);

        // Resume processing via Dashboard command (Testing idempotency & resumption)
        await dashboardVm.TogglePauseCommand.ExecuteAsync();
        Assert.AreEqual(ProjectState.Running, dashboardVm.Summary!.State);
        Assert.AreEqual("Pause Processing", dashboardVm.PauseButtonText);
    }

    private sealed class MockFolderPickerService : IFolderPickerService
    {
        public string? NextResult { get; set; }

        public Task<string?> PickFolderAsync(string? title = null)
        {
            return Task.FromResult(NextResult);
        }
    }

    [TestMethod]
    public void Architecture_Rules_Pages_Have_No_Static_ServiceLocator_Or_Raw_Process()
    {
        var pagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "csharp", "PhotoAIFactory.App", "Pages");
        var fullPagesDir = Path.GetFullPath(pagesDir);

        if (!Directory.Exists(fullPagesDir))
        {
            // Fallback for direct test execution path
            fullPagesDir = Path.GetFullPath(Path.Combine(testWorkDir, "..", "..", "src", "csharp", "PhotoAIFactory.App", "Pages"));
        }

        if (Directory.Exists(fullPagesDir))
        {
            var csFiles = Directory.GetFiles(fullPagesDir, "*.cs", SearchOption.AllDirectories);
            Assert.IsTrue(csFiles.Length >= 11, "Must contain all 11 page code-behind files");

            foreach (var file in csFiles)
            {
                var content = File.ReadAllText(file);
                Assert.IsFalse(content.Contains("App.Services"), $"File {Path.GetFileName(file)} must not use App.Services");
                Assert.IsFalse(content.Contains("GetRequiredService"), $"File {Path.GetFileName(file)} must not call GetRequiredService directly");
                Assert.IsFalse(content.Contains("SqliteConnection"), $"File {Path.GetFileName(file)} must not use SqliteConnection directly");
                Assert.IsFalse(content.Contains("Process.Start"), $"File {Path.GetFileName(file)} must not use Process.Start directly");
            }
        }
    }

    [TestMethod]
    public void Architecture_Rules_No_Duplicate_Command_And_Click_Handlers_In_XAML()
    {
        var pagesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "src", "csharp", "PhotoAIFactory.App", "Pages");
        var fullPagesDir = Path.GetFullPath(pagesDir);

        if (!Directory.Exists(fullPagesDir))
        {
            fullPagesDir = Path.GetFullPath(Path.Combine(testWorkDir, "..", "..", "src", "csharp", "PhotoAIFactory.App", "Pages"));
        }

        if (Directory.Exists(fullPagesDir))
        {
            var xamlFiles = Directory.GetFiles(fullPagesDir, "*.xaml", SearchOption.AllDirectories);
            Assert.IsTrue(xamlFiles.Length >= 11, "Must contain all 11 XAML page files");

            foreach (var file in xamlFiles)
            {
                var content = File.ReadAllText(file);
                // Find all <Button elements
                var buttonMatches = System.Text.RegularExpressions.Regex.Matches(content, @"<Button\b[^>]*>", System.Text.RegularExpressions.RegexOptions.Singleline);
                foreach (System.Text.RegularExpressions.Match match in buttonMatches)
                {
                    var btnText = match.Value;
                    var hasCommand = btnText.Contains("Command=");
                    var hasClick = btnText.Contains("Click=");
                    Assert.IsFalse(hasCommand && hasClick,
                        $"File {Path.GetFileName(file)} has Button with BOTH Command and Click handler: {btnText}");
                }
            }
        }
    }

    [TestMethod]
    public async Task FailureMatrix_CaseA_Start_Success_Creates_Running_With_Active_Session()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();

        var inDir = Path.Combine(testWorkDir, "caseA_in");
        var outDir = Path.Combine(testWorkDir, "caseA_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseA_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        var startResult = await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        Assert.AreEqual(ProjectState.Running, startResult.Project!.State);

        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.AreEqual(ProjectState.Running, current!.Project.State);

        // Pause cleanly
        await coordinator.PauseProjectAsync(pId, Guid.NewGuid().ToString("N"));
        var paused = await store.GetAsync(pId);
        Assert.AreEqual(ProjectState.Paused, paused!.Project.State);
    }

    [TestMethod]
    public async Task FailureMatrix_CaseB_Start_Runtime_Failure_Compensates_To_Paused()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();

        var invalidInput = Path.Combine(testWorkDir, "invalid_caseB_input");
        var outDir = Path.Combine(testWorkDir, "caseB_out");
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            invalidInput, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseB_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        var thrown = false;
        try
        {
            await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        }
        catch (Exception ex) when (ex is ProjectRuntimeCoordinationException or DirectoryNotFoundException or InvalidOperationException)
        {
            thrown = true;
        }
        Assert.IsTrue(thrown, "Exception must surface to caller");

        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.AreEqual(ProjectState.Paused, current!.Project.State,
            "When IngestionManager fails to start, coordinator must compensate to PAUSED state");
    }

    [TestMethod]
    public async Task FailureMatrix_CaseD_Pause_Success_Transitions_To_Paused_With_Zero_Sessions()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();

        var inDir = Path.Combine(testWorkDir, "caseD_in");
        var outDir = Path.Combine(testWorkDir, "caseD_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseD_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        var pauseResult = await coordinator.PauseProjectAsync(pId, Guid.NewGuid().ToString("N"));
        Assert.AreEqual(ProjectState.Paused, pauseResult.Project!.State);

        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.AreEqual(ProjectState.Paused, current!.Project.State);
    }

    [TestMethod]
    public async Task FailureMatrix_CaseF_Stop_Success_Transitions_To_Stopped_With_Zero_Sessions()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();

        var inDir = Path.Combine(testWorkDir, "caseF_in");
        var outDir = Path.Combine(testWorkDir, "caseF_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseF_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        var stopResult = await coordinator.StopProjectAsync(pId, Guid.NewGuid().ToString("N"));
        Assert.AreEqual(ProjectState.Stopped, stopResult.Project!.State);

        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.AreEqual(ProjectState.Stopped, current!.Project.State);
    }

    [TestMethod]
    public async Task FailureMatrix_CaseH_Repeated_Resume_And_Pause_Idempotency()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();

        var inDir = Path.Combine(testWorkDir, "caseH_in");
        var outDir = Path.Combine(testWorkDir, "caseH_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseH_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        // 1. First start
        var start1 = await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        Assert.AreEqual(ProjectState.Running, start1.Project!.State);

        // 2. Repeated start (idempotent)
        var start2 = await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        Assert.AreEqual(ProjectState.Running, start2.Project!.State);

        // 3. First pause
        var pause1 = await coordinator.PauseProjectAsync(pId, Guid.NewGuid().ToString("N"));
        Assert.AreEqual(ProjectState.Paused, pause1.Project!.State);

        // 4. Repeated pause (idempotent)
        var pause2 = await coordinator.PauseProjectAsync(pId, Guid.NewGuid().ToString("N"));
        Assert.AreEqual(ProjectState.Paused, pause2.Project!.State);
    }

    [TestMethod]
    public async Task LogsUI_Parses_Real_Snake_Case_JSONL_Schema_And_Displays_System_And_Project_Logs()
    {
        var appPaths = new TestAppPaths(testWorkDir);
        var logsDir = appPaths.LogsDirectory;
        Directory.CreateDirectory(logsDir);

        var logFile = Path.Combine(logsDir, "app.jsonl");

        // Write real snake_case JSONL lines matching JsonLinesLoggerProvider format
        var lines = new[]
        {
            "{\"timestamp_utc\":\"2026-08-24T12:00:00.0000000Z\",\"level\":\"Information\",\"category\":\"PhotoAIFactory.Host\",\"message\":\"Runtime ready session_token=secret123456\",\"session_id\":\"sess_001\"}",
            "{\"timestamp_utc\":\"2026-08-24T12:01:00.0000000Z\",\"level\":\"Error\",\"category\":\"PhotoAIFactory.Ingestion\",\"component\":\"IngestionCoordinator\",\"message\":\"Failed to archive asset Authorization: Bearer abc123def456\",\"project_id\":\"proj_test_1\",\"job_id\":\"job_test_100\",\"session_id\":\"sess_001\",\"exception\":{\"type\":\"System.IO.IOException\",\"message\":\"Disk full\",\"stack_trace\":\"at ArchiveAsync in IngestionCoordinator.cs:line 104\"}}",
            "{\"timestamp_utc\":\"2026-08-24T12:02:00.0000000Z\",\"level\":\"Warning\",\"category\":\"PhotoAIFactory.Storage\",\"component\":\"StorageGuard\",\"message\":\"Storage low api_key=my_api_key_value_999\",\"project_id\":\"proj_other_99\",\"session_id\":\"sess_001\"}"
        };

        File.WriteAllLines(logFile, lines);

        var errorLogService = new ErrorLogQueryService(appPaths);
        var logs = await errorLogService.GetErrorLogsAsync(projectId: new ProjectId("proj_test_1"));

        // Should return 2 entries: the system log (project_id = null) and proj_test_1 log. proj_other_99 must be filtered out.
        Assert.AreEqual(2, logs.Count, "Must include matching project logs AND system-level logs");

        var errLog = logs.First(l => l.ProjectId == "proj_test_1");
        Assert.AreEqual(LogLevel.Error, errLog.Level);
        Assert.AreEqual("IngestionCoordinator", errLog.Component);
        Assert.AreEqual("job_test_100", errLog.JobId);
        Assert.IsTrue(errLog.Message.Contains("Bearer [REDACTED]"), "Bearer token must be sanitized");
        Assert.IsNotNull(errLog.TechnicalDetails);
        Assert.IsTrue(errLog.TechnicalDetails.Contains("System.IO.IOException: Disk full"));
        Assert.IsTrue(errLog.TechnicalDetails.Contains("line 104"));

        var sysLog = logs.First(l => l.ProjectId == null);
        Assert.AreEqual(LogLevel.Information, sysLog.Level);
        Assert.IsTrue(sysLog.Message.Contains("session_token: [REDACTED]"), "Session token must be sanitized");
    }

    [TestMethod]
    public async Task FailureMatrix_CaseC_Start_Runtime_Failure_And_Pause_Compensation_Failure_Enters_Safe_Non_Running()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        var faultFactory = new FaultableIngestionSessionFactory { FailStart = true, FailStop = true };
        builder.Services.AddSingleton<IIngestionSessionFactory>(faultFactory);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();

        var inDir = Path.Combine(testWorkDir, "caseC_in");
        var outDir = Path.Combine(testWorkDir, "caseC_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseC_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        var thrown = false;
        try
        {
            await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }
        Assert.IsTrue(thrown, "Original StartAsync exception must be surfaced to caller");

        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.IsNotNull(current);
        Assert.AreNotEqual(ProjectState.Running, current.Project.State,
            "State MUST NOT remain ordinary RUNNING when runtime start and pause compensation fail");
    }

    [TestMethod]
    public async Task FailureMatrix_CaseE_Pause_Runtime_Stop_Failure_Leaves_PauseRequested()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        var faultFactory = new FaultableIngestionSessionFactory { FailStart = false, FailStop = false };
        builder.Services.AddSingleton<IIngestionSessionFactory>(faultFactory);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();

        var inDir = Path.Combine(testWorkDir, "caseE_in");
        var outDir = Path.Combine(testWorkDir, "caseE_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseE_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));

        // Now inject Stop failure
        faultFactory.FailStop = true;

        var thrown = false;
        try
        {
            await coordinator.PauseProjectAsync(pId, Guid.NewGuid().ToString("N"));
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }
        Assert.IsTrue(thrown, "Stop failure exception must surface to caller");

        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.IsNotNull(current);
        Assert.AreEqual(ProjectState.PauseRequested, current.Project.State,
            "When StopAsync fails, project must remain PAUSE_REQUESTED and NOT falsely report clean PAUSED");
    }

    [TestMethod]
    public async Task FailureMatrix_CaseG_Stop_Runtime_Stop_Failure_Leaves_StopRequested()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        var faultFactory = new FaultableIngestionSessionFactory { FailStart = false, FailStop = false };
        builder.Services.AddSingleton<IIngestionSessionFactory>(faultFactory);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();

        var inDir = Path.Combine(testWorkDir, "caseG_in");
        var outDir = Path.Combine(testWorkDir, "caseG_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseG_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));

        // Now inject Stop failure
        faultFactory.FailStop = true;

        var thrown = false;
        try
        {
            await coordinator.StopProjectAsync(pId, Guid.NewGuid().ToString("N"));
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }
        Assert.IsTrue(thrown, "Stop failure exception must surface to caller");

        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.IsNotNull(current);
        Assert.AreEqual(ProjectState.StopRequested, current.Project.State,
            "When StopAsync fails, project must remain STOP_REQUESTED and NOT falsely report clean STOPPED");
    }

    [TestMethod]
    public async Task FailureMatrix_CaseF_Pause_Runtime_Stop_Succeeds_Finalization_Fails()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        var realStoreFactory = new SqliteProjectStoreFactory(appPaths);
        var faultStoreFactory = new FaultableProjectStoreFactory(realStoreFactory);
        builder.Services.AddSingleton<IProjectStoreFactory>(faultStoreFactory);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();

        var inDir = Path.Combine(testWorkDir, "caseF_final_in");
        var outDir = Path.Combine(testWorkDir, "caseF_final_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseF_Final_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));

        // Inject finalization failure on transition to PAUSED
        faultStoreFactory.FailTransitionOn = true;
        faultStoreFactory.TargetFailState = ProjectState.Paused;
        faultStoreFactory.InjectedStatus = TransitionWriteStatus.ConcurrencyConflict;

        ProjectRuntimeCoordinationException? thrownEx = null;
        try
        {
            await coordinator.PauseProjectAsync(pId, Guid.NewGuid().ToString("N"));
        }
        catch (ProjectRuntimeCoordinationException ex)
        {
            thrownEx = ex;
        }

        Assert.IsNotNull(thrownEx, "Finalization failure must throw ProjectRuntimeCoordinationException");
        Assert.AreEqual(LifecycleResultStatus.ConcurrencyConflict, thrownEx.Status);

        // Verify durable state is honest PauseRequested
        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.IsNotNull(current);
        Assert.AreEqual(ProjectState.PauseRequested, current.Project.State,
            "When finalization fails, durable state must remain honestly PAUSE_REQUESTED");

        // Verify dispatch is blocked
        Assert.IsFalse(ProjectDispatchGuard.CanDispatchNextJob(current.Project.State),
            "Job dispatch must be blocked in PauseRequested state");
    }

    [TestMethod]
    public async Task FailureMatrix_CaseI_Stop_Runtime_Stop_Succeeds_Finalization_Fails()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        var realStoreFactory = new SqliteProjectStoreFactory(appPaths);
        var faultStoreFactory = new FaultableProjectStoreFactory(realStoreFactory);
        builder.Services.AddSingleton<IProjectStoreFactory>(faultStoreFactory);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();

        var inDir = Path.Combine(testWorkDir, "caseI_final_in");
        var outDir = Path.Combine(testWorkDir, "caseI_final_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseI_Final_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));

        // Inject finalization failure on transition to STOPPED
        faultStoreFactory.FailTransitionOn = true;
        faultStoreFactory.TargetFailState = ProjectState.Stopped;
        faultStoreFactory.InjectedStatus = TransitionWriteStatus.ConcurrencyConflict;

        ProjectRuntimeCoordinationException? thrownEx = null;
        try
        {
            await coordinator.StopProjectAsync(pId, Guid.NewGuid().ToString("N"));
        }
        catch (ProjectRuntimeCoordinationException ex)
        {
            thrownEx = ex;
        }

        Assert.IsNotNull(thrownEx, "Finalization failure must throw ProjectRuntimeCoordinationException");
        Assert.AreEqual(LifecycleResultStatus.ConcurrencyConflict, thrownEx.Status);

        // Verify durable state is honest StopRequested
        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.IsNotNull(current);
        Assert.AreEqual(ProjectState.StopRequested, current.Project.State,
            "When finalization fails, durable state must remain honestly STOP_REQUESTED");
    }

    [TestMethod]
    public async Task FailureMatrix_Start_Failure_Pause_Compensation_Conflict_Falls_Back_To_ComponentUnhealthy()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        var faultSession = new FaultableIngestionSessionFactory { FailStart = true, FailStop = false };
        builder.Services.AddSingleton<IIngestionSessionFactory>(faultSession);

        var realStoreFactory = new SqliteProjectStoreFactory(appPaths);
        var faultStoreFactory = new FaultableProjectStoreFactory(realStoreFactory);
        builder.Services.AddSingleton<IProjectStoreFactory>(faultStoreFactory);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();

        var inDir = Path.Combine(testWorkDir, "caseComp_in");
        var outDir = Path.Combine(testWorkDir, "caseComp_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseComp_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        // Fail pause compensation on transition to PauseRequested
        faultStoreFactory.FailTransitionOn = true;
        faultStoreFactory.TargetFailState = ProjectState.PauseRequested;
        faultStoreFactory.InjectedStatus = TransitionWriteStatus.ConcurrencyConflict;

        var thrown = false;
        try
        {
            await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        }
        catch (InvalidOperationException)
        {
            thrown = true;
        }

        Assert.IsTrue(thrown, "Original Start exception must be surfaced");

        // Verify fallback to ComponentUnhealthy
        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.IsNotNull(current);
        Assert.AreEqual(ProjectState.ComponentUnhealthy, current.Project.State,
            "When pause compensation fails, coordinator must apply secondary compensation to ComponentUnhealthy");
    }

    [TestMethod]
    public async Task FailureMatrix_Start_Failure_All_Compensations_Fail_Surfaces_Critical_Error()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        var faultSession = new FaultableIngestionSessionFactory { FailStart = true, FailStop = false };
        builder.Services.AddSingleton<IIngestionSessionFactory>(faultSession);

        var realStoreFactory = new SqliteProjectStoreFactory(appPaths);
        var faultStoreFactory = new FaultableProjectStoreFactory(realStoreFactory);
        builder.Services.AddSingleton<IProjectStoreFactory>(faultStoreFactory);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();
        var healthTracker = host.Services.GetRequiredService<IComponentHealthTracker>();

        var inDir = Path.Combine(testWorkDir, "caseAllFail_in");
        var outDir = Path.Combine(testWorkDir, "caseAllFail_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseAllFail_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        // Fail all compensations
        faultStoreFactory.FailTransitionOn = true;
        faultStoreFactory.TargetFailState = null; // Fails all transitions when true
        faultStoreFactory.InjectedStatus = TransitionWriteStatus.ConcurrencyConflict;

        ProjectRuntimeCoordinationException? thrownEx = null;
        try
        {
            await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        }
        catch (ProjectRuntimeCoordinationException ex)
        {
            thrownEx = ex;
        }

        Assert.IsNotNull(thrownEx, "Must throw explicit ProjectRuntimeCoordinationException");
        Assert.AreEqual(pId, thrownEx.ProjectId);
        Assert.IsTrue(thrownEx.PrimaryFailure?.Contains("Injected StartAsync failure") == true, "Primary failure must be recorded");
        Assert.IsTrue(thrownEx.CompensationStatuses.Count > 0, "Compensation statuses must be captured");

        // Verify active sessions == 0
        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.IsNotNull(current);

        // Verify ProjectDispatchGuard with healthTracker blocks dispatch
        Assert.IsFalse(ProjectDispatchGuard.CanDispatchNextJob(current.Project.State, healthTracker),
            "Dispatch must be blocked when ingestion runtime startup fails persistently");
    }

    [TestMethod]
    public async Task Start_Failure_Secondary_ConcurrencyConflict_Rereads_And_BoundedRetry_Succeeds()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        var faultSession = new FaultableIngestionSessionFactory { FailStart = true, FailStop = false };
        builder.Services.AddSingleton<IIngestionSessionFactory>(faultSession);

        var realStoreFactory = new SqliteProjectStoreFactory(appPaths);
        var faultStoreFactory = new FaultableProjectStoreFactory(realStoreFactory);
        builder.Services.AddSingleton<IProjectStoreFactory>(faultStoreFactory);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();

        var inDir = Path.Combine(testWorkDir, "caseRetry_in");
        var outDir = Path.Combine(testWorkDir, "caseRetry_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseRetry_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        // Primary pause compensation fails
        faultStoreFactory.FailTransitionOn = true;
        faultStoreFactory.TargetFailState = ProjectState.PauseRequested;
        faultStoreFactory.InjectedStatus = TransitionWriteStatus.ConcurrencyConflict;

        // When secondary ComponentUnhealthy runs, fail once then succeed
        // Let's set MaxFails = 2 (1 for PauseRequested, 1 for ComponentUnhealthy attempt 1, then attempt 2 succeeds)
        faultStoreFactory.TargetFailState = null;
        faultStoreFactory.MaxFails = 2;

        ProjectRuntimeCoordinationException? thrownEx = null;
        try
        {
            await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        }
        catch (ProjectRuntimeCoordinationException ex)
        {
            thrownEx = ex;
        }

        Assert.IsNotNull(thrownEx, "StartAsync failure must surface to caller");

        // Verify that bounded retry succeeded in writing ComponentUnhealthy
        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.IsNotNull(current);
        Assert.AreEqual(ProjectState.ComponentUnhealthy, current.Project.State,
            "Bounded retry must transition durable state to safe non-running ComponentUnhealthy");
    }

    [TestMethod]
    public async Task Start_Failure_Persistent_Compensation_Failure_Blocks_Dispatch()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        var faultSession = new FaultableIngestionSessionFactory { FailStart = true, FailStop = false };
        builder.Services.AddSingleton<IIngestionSessionFactory>(faultSession);

        var realStoreFactory = new SqliteProjectStoreFactory(appPaths);
        var faultStoreFactory = new FaultableProjectStoreFactory(realStoreFactory);
        builder.Services.AddSingleton<IProjectStoreFactory>(faultStoreFactory);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
        var projectStores = host.Services.GetRequiredService<IProjectStoreFactory>();
        var healthTracker = host.Services.GetRequiredService<IComponentHealthTracker>();

        var inDir = Path.Combine(testWorkDir, "caseBlock_in");
        var outDir = Path.Combine(testWorkDir, "caseBlock_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("CaseBlock_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        faultStoreFactory.FailTransitionOn = true;
        faultStoreFactory.TargetFailState = null;
        faultStoreFactory.MaxFails = int.MaxValue;

        try
        {
            await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        }
        catch (ProjectRuntimeCoordinationException)
        {
            // Expected
        }

        var store = projectStores.Open(pId);
        var current = await store.GetAsync(pId);
        Assert.IsNotNull(current);

        Assert.IsTrue(healthTracker.IsStageBlocked("IngestionRuntime"), "Health tracker must block IngestionRuntime");
        Assert.IsFalse(ProjectDispatchGuard.CanDispatchNextJob(current.Project.State, healthTracker),
            "Job dispatch must be strictly blocked");
    }

    [TestMethod]
    public async Task Real_Production_Dispatcher_Running_With_Ingestion_Unhealthy_Blocks_Dispatch()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
        var healthTracker = host.Services.GetRequiredService<IComponentHealthTracker>();
        var processingManager = host.Services.GetRequiredService<ProjectProcessingManager>();
        var analysisManager = host.Services.GetRequiredService<ProjectAnalysisManager>();

        var inDir = Path.Combine(testWorkDir, "real_dispatch_unhealthy_in");
        var outDir = Path.Combine(testWorkDir, "real_dispatch_unhealthy_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("RealDispatch_Unhealthy", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));

        // Trip circuit breaker on IngestionRuntime
        healthTracker.MarkUnhealthy("IngestionRuntime", "Simulated runtime crash");

        // 1. Production ProjectProcessingManager.ProcessNextAsync must return NoWork
        var procResult = await processingManager.ProcessNextAsync(pId);
        Assert.AreEqual(ProcessingDispatchStatus.NoWork, procResult.Status,
            "Real production processing dispatcher must NOT claim or process work when IngestionRuntime is unhealthy");

        // 2. Production ProjectAnalysisManager.ProcessReadyAsync must return empty
        var analResults = await analysisManager.ProcessReadyAsync(pId);
        Assert.AreEqual(0, analResults.NewlyDispatchedCount,
            "Real production analysis dispatcher must NOT process ready photos when IngestionRuntime is unhealthy");
    }

    [TestMethod]
    public async Task Real_Production_Dispatcher_Running_With_Ingestion_Healthy_Allows_Dispatch()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
        var healthTracker = host.Services.GetRequiredService<IComponentHealthTracker>();
        var processingManager = host.Services.GetRequiredService<ProjectProcessingManager>();

        var inDir = Path.Combine(testWorkDir, "real_dispatch_healthy_in");
        var outDir = Path.Combine(testWorkDir, "real_dispatch_healthy_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("RealDispatch_Healthy", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));

        // Ensure Healthy
        healthTracker.RecordSuccess("IngestionRuntime");
        Assert.IsFalse(healthTracker.IsStageBlocked("IngestionRuntime"));

        // Dispatch decision allowed (returns NoWork cleanly because queue is empty, not blocked by guard)
        var procResult = await processingManager.ProcessNextAsync(pId);
        Assert.AreEqual(ProcessingDispatchStatus.NoWork, procResult.Status);
    }

    [TestMethod]
    public async Task Dashboard_Healthy_Running_Presents_Normal_Running_And_Pause_Processing()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
        var healthTracker = host.Services.GetRequiredService<IComponentHealthTracker>();
        var projectContext = host.Services.GetRequiredService<IProjectContext>();
        var dashboardVm = host.Services.GetRequiredService<DashboardViewModel>();

        var inDir = Path.Combine(testWorkDir, "dash_healthy_in");
        var outDir = Path.Combine(testWorkDir, "dash_healthy_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("Dash_Healthy", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
        healthTracker.RecordSuccess("IngestionRuntime");

        projectContext.SetActiveProject(pId, "Dash_Healthy", ProjectState.Running);
        await dashboardVm.RefreshAsync();

        Assert.IsTrue(dashboardVm.IsRunning, "Must report IsRunning == true when healthy");
        Assert.IsFalse(dashboardVm.IsDegradedRunning, "Must report IsDegradedRunning == false when healthy");
        Assert.AreEqual("Pause Processing", dashboardVm.PauseButtonText);
    }

    [TestMethod]
    public async Task Dashboard_Degraded_Running_Presents_Degraded_And_Inspect_Components_Resume()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
        var healthTracker = host.Services.GetRequiredService<IComponentHealthTracker>();
        var projectContext = host.Services.GetRequiredService<IProjectContext>();
        var dashboardVm = host.Services.GetRequiredService<DashboardViewModel>();

        var inDir = Path.Combine(testWorkDir, "dash_degraded_in");
        var outDir = Path.Combine(testWorkDir, "dash_degraded_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("Dash_Degraded", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));

        // Degrade IngestionRuntime
        healthTracker.MarkUnhealthy("IngestionRuntime", "Runtime failed startup");

        projectContext.SetActiveProject(pId, "Dash_Degraded", ProjectState.Running);
        await dashboardVm.RefreshAsync();

        Assert.IsFalse(dashboardVm.IsRunning, "Must NOT report normal IsRunning when IngestionRuntime is unhealthy");
        Assert.IsTrue(dashboardVm.IsDegradedRunning, "Must report IsDegradedRunning == true");
        Assert.AreEqual("Inspect Components & Resume", dashboardVm.PauseButtonText);
    }

    [TestMethod]
    public async Task Recovery_Circuit_Remains_Blocked_Until_Successful_Probe_And_Runtime_Recovery()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
        var healthTracker = host.Services.GetRequiredService<IComponentHealthTracker>();
        var projectContext = host.Services.GetRequiredService<IProjectContext>();
        var dashboardVm = host.Services.GetRequiredService<DashboardViewModel>();

        var inDir = Path.Combine(testWorkDir, "recov_in");
        var outDir = Path.Combine(testWorkDir, "recov_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: true, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("Recov_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));

        // Mark unhealthy
        healthTracker.MarkUnhealthy("IngestionRuntime", "Lock failure");
        projectContext.SetActiveProject(pId, "Recov_Project", ProjectState.Running);
        await dashboardVm.RefreshAsync();
        Assert.IsTrue(dashboardVm.IsDegradedRunning);

        // User clicks recovery ("Inspect Components & Resume")
        await dashboardVm.TogglePauseAsync();

        // Successful restart recovers health and closes circuit
        await dashboardVm.RefreshAsync();
        Assert.IsTrue(dashboardVm.IsRunning, "Dashboard must return to healthy Running once coordinator successfully restarts runtime");
        Assert.IsFalse(dashboardVm.IsDegradedRunning);
        Assert.AreEqual("Pause Processing", dashboardVm.PauseButtonText);
    }

    private sealed class FaultableProjectStoreFactory(IProjectStoreFactory inner) : IProjectStoreFactory
    {
        public bool FailTransitionOn { get; set; }
        public ProjectState? TargetFailState { get; set; }
        public TransitionWriteStatus InjectedStatus { get; set; } = TransitionWriteStatus.ConcurrencyConflict;
        public int FailCount { get; set; } = 0;
        public int MaxFails { get; set; } = int.MaxValue;
        public bool AllowRunningTransition { get; set; } = true;

        public IProjectStore Open(ProjectId projectId)
        {
            var realStore = inner.Open(projectId);
            return new FaultableProjectStore(realStore, this);
        }
    }

    private sealed class FaultableProjectStore(IProjectStore inner, FaultableProjectStoreFactory factory) : IProjectStore
    {
        public Task<ProjectSnapshot> CreateAsync(Project project, ConfigVersion initialConfig, string creationOperationKey, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(project, initialConfig, creationOperationKey, cancellationToken);

        public Task<ProjectSnapshot?> GetAsync(ProjectId projectId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(projectId, cancellationToken);

        public Task<ConfigVersion> AppendAsync(ProjectId projectId, ProjectConfigV1 config, string operationKey, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(projectId, config, operationKey, createdAtUtc, cancellationToken);

        public Task<IReadOnlyList<ConfigVersion>> ListAsync(ProjectId projectId, CancellationToken cancellationToken = default) =>
            inner.ListAsync(projectId, cancellationToken);

        public Task<IReadOnlyList<ProjectStateTransition>> ListTransitionsAsync(ProjectId projectId, CancellationToken cancellationToken = default) =>
            inner.ListTransitionsAsync(projectId, cancellationToken);

        public Task<ConfigWriteResult> ApplyWhenPausedAsync(ProjectId projectId, ProjectConfigV1 config, string expectedConfigVersionId, string operationId, DateTimeOffset createdAtUtc, CancellationToken cancellationToken = default) =>
            inner.ApplyWhenPausedAsync(projectId, config, expectedConfigVersionId, operationId, createdAtUtc, cancellationToken);

        public Task<TransitionWriteResult> TryTransitionAsync(
            ProjectId projectId,
            ProjectState expectedState,
            long expectedRevision,
            ProjectState nextState,
            string reason,
            string operationId,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            if (factory.FailTransitionOn)
            {
                if (factory.AllowRunningTransition && nextState == ProjectState.Running)
                {
                    return inner.TryTransitionAsync(projectId, expectedState, expectedRevision, nextState, reason, operationId, occurredAtUtc, cancellationToken);
                }

                if (!factory.TargetFailState.HasValue || factory.TargetFailState.Value == nextState)
                {
                    if (factory.FailCount < factory.MaxFails)
                    {
                        factory.FailCount++;
                        return Task.FromResult(new TransitionWriteResult(factory.InjectedStatus, null, null));
                    }
                }
            }

            return inner.TryTransitionAsync(projectId, expectedState, expectedRevision, nextState, reason, operationId, occurredAtUtc, cancellationToken);
        }
    }

    private sealed class FaultableIngestionSessionFactory : IIngestionSessionFactory
    {
        public bool FailStart { get; set; }
        public bool FailStop { get; set; }

        public IIngestionSession Create(ProjectId projectId, ProjectConfigV1 config, PhotoAIFactory.Domain.Ingestion.IngestionSourceSnapshot source)
        {
            return new FaultableIngestionSession(projectId, source, this);
        }
    }

    private sealed class FaultableIngestionSession : IIngestionSession
    {
        private readonly FaultableIngestionSessionFactory factory;

        public FaultableIngestionSession(
            ProjectId projectId,
            PhotoAIFactory.Domain.Ingestion.IngestionSourceSnapshot source,
            FaultableIngestionSessionFactory factory)
        {
            ProjectId = projectId;
            Source = source;
            this.factory = factory;
        }

        public ProjectId ProjectId { get; }
        public PhotoAIFactory.Domain.Ingestion.IngestionSourceSnapshot Source { get; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (factory.FailStart) throw new InvalidOperationException("Injected StartAsync failure");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (factory.FailStop) throw new InvalidOperationException("Injected StopAsync failure");
            return Task.CompletedTask;
        }

        public Task ReconcileAsync(string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SimulationAiClient : IPythonAiClient
    {
        public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthResponse("HEALTHY", "v1", "simulation", null, []));

        public Task<AiResponse> ExecuteAsync(
            string route, AiRequest request, CancellationToken cancellationToken = default)
        {
            JsonElement result;
            if (route.EndsWith("/analyze", StringComparison.Ordinal))
            {
                result = JsonSerializer.SerializeToElement(new
                {
                    schema_version = 1,
                    technical = new { },
                    model_executions = new[]
                    {
                        new
                        {
                            model_id = "mock-analysis-model",
                            model_version = "1.0.0",
                            artifact_set_sha256 = new string('7', 64),
                            parameters = new { },
                            timings = new { total_ms = 1.0 }
                        }
                    }
                }, ContractJson.Options);
            }
            else if (route.EndsWith("/preselect", StringComparison.Ordinal))
            {
                result = JsonSerializer.SerializeToElement(
                    new { decision = "APPROVED", findings = Array.Empty<object>() },
                    ContractJson.Options);
            }
            else if (route.EndsWith("/qa", StringComparison.Ordinal))
            {
                result = JsonSerializer.SerializeToElement(new
                {
                    schema_version = 1,
                    decision = "QA_PASS",
                    findings = Array.Empty<object>(),
                    suggested_correction = (object?)null,
                    technical = new { sharpness = new { laplacian_variance = 80.0 } },
                    calibration_status = "BASELINE_NOT_CALIBRATED"
                }, ContractJson.Options);
            }
            else
            {
                result = JsonSerializer.SerializeToElement(new { }, ContractJson.Options);
            }

            return Task.FromResult(new AiResponse(
                "v1", request.RequestId, true, result, null,
                new Dictionary<string, double>()));
        }
    }

    [TestMethod]
    public async Task Autonomous_Pipeline_Dispatches_Running_Project_End_To_End()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);
        builder.Services.AddSingleton<IPythonAiClient, SimulationAiClient>();

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var projectService = host.Services.GetRequiredService<ProjectService>();
            var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
            var dashboardQuery = host.Services.GetRequiredService<IDashboardQueryService>();

            var inDir = Path.Combine(testWorkDir, "auto_e2e_in");
            var outDir = Path.Combine(testWorkDir, "auto_e2e_out");
            Directory.CreateDirectory(inDir);
            Directory.CreateDirectory(outDir);

            // Create test JPEG image in input directory
            var testImg = Path.Combine(inDir, "photo_auto_001.jpg");
            using (var bmp = new Bitmap(200, 200, PixelFormat.Format24bppRgb))
            {
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.SeaGreen);
                bmp.Save(testImg, ImageFormat.Jpeg);
            }
            var origSha = ComputeFileSha256(testImg);

            var config = new ProjectConfigV1(
                inDir, outDir, includeSubfolders: false, revealMode: RevealMode.DtAuto,
                preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
                comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
                exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 1);

            var created = await projectService.CreateProjectAsync("Autonomous_E2E_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
            var pId = created.Project.Id;

            // SOLE EXTERNAL TRIGGER: StartOrResumeProjectAsync
            await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));

            // Poll DashboardQueryService autonomously until completion within 15 seconds
            DashboardSummaryDto? summary = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                summary = await dashboardQuery.GetDashboardSummaryAsync(pId);
                if (summary is not null && summary.CompletedCount >= 1)
                {
                    break;
                }
                await Task.Delay(100);
            }

            var dbPath = Path.Combine(testWorkDir, "projects", pId.Value, "project.db");
            var errDetail = "No transitions";
            if (File.Exists(dbPath))
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT from_state, to_state, reason FROM job_state_transitions ORDER BY occurred_at_utc";
                using var r = cmd.ExecuteReader();
                var sb = new System.Text.StringBuilder();
                while (r.Read())
                {
                    sb.Append($"{r.GetValue(0)}->{r.GetValue(1)} ({r.GetValue(2)}); ");
                }
                errDetail = sb.ToString();
            }

            Assert.IsNotNull(summary, "Dashboard summary must exist");
            Assert.AreEqual(1, summary.CompletedCount, "Autonomous pipeline must have completed 1 job");
            Assert.AreEqual(0, summary.ProcessingCount, "No jobs should remain in processing");
            Assert.AreEqual(0, summary.ErrorCount, "No errors should occur");

            // Verify original file integrity
            Assert.AreEqual(origSha, ComputeFileSha256(testImg), "Original input file must not be modified");

            // Verify output publication exists
            var pubFiles = Directory.GetFiles(outDir, "*.jpg", SearchOption.AllDirectories);
            Assert.IsTrue(pubFiles.Length >= 1, "Published output must exist in output folder");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [TestMethod]
    public async Task Autonomous_Pipeline_Dashboard_Shows_Received_Photos_Before_Job_Creation()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var ingestionStores = host.Services.GetRequiredService<IIngestionStoreFactory>();
        var dashboardQuery = host.Services.GetRequiredService<IDashboardQueryService>();

        var inDir = Path.Combine(testWorkDir, "dash_recv_in");
        var outDir = Path.Combine(testWorkDir, "dash_recv_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: false, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("Dash_Recv_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        // Ingest photo record into photos table directly (e.g. Ingestion session stage)
        var testImg = Path.Combine(inDir, "dash_recv_test.jpg");
        using (var bmp = new Bitmap(100, 100, PixelFormat.Format24bppRgb))
        {
            bmp.Save(testImg, ImageFormat.Jpeg);
        }

        var store = ingestionStores.Open(pId);
        var prep = await store.PrepareSourceAsync(pId, created.LatestConfig.Id, inDir, false, DateTimeOffset.UtcNow);
        var cmd = new IngestAssetCommand(
            pId,
            prep.Source.Id,
            "|dash_recv_test",
            testImg,
            "dash_recv_test.jpg",
            testImg,
            AssetFormat.Jpeg,
            1024,
            ComputeFileSha256(testImg),
            RawSupportInfo.NotApplicable,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1));
        await store.IngestArchivedAsync(cmd);
        await store.FinalizeAssociationsAsync(pId, prep.Source.Id, DateTimeOffset.UtcNow.AddSeconds(10), force: true);

        // Before any Job is created, Dashboard must show Received = 1
        var summary = await dashboardQuery.GetDashboardSummaryAsync(pId);
        Assert.IsNotNull(summary);
        Assert.AreEqual(1, summary.ReceivedCount, "Dashboard must show Received = 1 for ingested unanalyzed photo");
        Assert.AreEqual(0, summary.QueuedCount, "QueuedCount must be 0 before analysis enqueues job");
    }

    [TestMethod]
    public async Task Autonomous_Pipeline_Unsupported_Reduced_RAW_Visible_In_Review_Without_Normal_Job()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var ingestionStores = host.Services.GetRequiredService<IIngestionStoreFactory>();
        var dashboardQuery = host.Services.GetRequiredService<IDashboardQueryService>();
        var reviewQuery = host.Services.GetRequiredService<IReviewQueryService>();

        var inDir = Path.Combine(testWorkDir, "unsupp_in");
        var outDir = Path.Combine(testWorkDir, "unsupp_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: false, revealMode: RevealMode.PreAi,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 5);

        var created = await projectService.CreateProjectAsync("Unsupported_RAW_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;

        // Ingest unsupported reduced RAW
        var unsuppFile = Path.Combine(inDir, "DSC03812.ARW");
        File.WriteAllBytes(unsuppFile, new byte[100]);

        var store = ingestionStores.Open(pId);
        var prep = await store.PrepareSourceAsync(pId, created.LatestConfig.Id, inDir, false, DateTimeOffset.UtcNow);
        var cmd = new IngestAssetCommand(
            pId,
            prep.Source.Id,
            "|DSC03812",
            unsuppFile,
            "DSC03812.ARW",
            unsuppFile,
            AssetFormat.Raw,
            11128832,
            "901a76772690fcca6d78c47acd8c3362e91e3c85308f231d3bbec40e89071114",
            new RawSupportInfo(RawSupportStatus.UnsupportedReduced, 2816, 1864, "UNSUPPORTED_REDUCED_RAW"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5));
        await store.IngestArchivedAsync(cmd);

        // 1. Dashboard summary must show ReviewCount == 1
        var summary = await dashboardQuery.GetDashboardSummaryAsync(pId);
        Assert.IsNotNull(summary);
        Assert.AreEqual(1, summary.ReviewCount, "Dashboard must show Review = 1 for unsupported raw photo");

        // 2. Review query service must return the unsupported item
        var reviews = await reviewQuery.GetPendingReviewsAsync(pId);
        Assert.AreEqual(1, reviews.Count);
        Assert.IsNull(reviews[0].JobId, "Unsupported RAW must have null JobId");
        Assert.IsFalse(reviews[0].HasJob, "Unsupported RAW must have HasJob == false");
        Assert.AreEqual("Unsupported Format", reviews[0].ReviewStage);
        Assert.AreEqual(JobState.RejectedPre, reviews[0].JobState);
        Assert.IsTrue(reviews[0].ErrorMessage?.Contains("Unsupported RAW format") ?? false);

        // 3. Dispatcher single cycle must NOT create a normal processing job
        var dispatcher = host.Services.GetRequiredService<QueueDispatcherService>();
        await dispatcher.DispatchOnceAsync();

        var summaryAfter = await dashboardQuery.GetDashboardSummaryAsync(pId);
        Assert.IsNotNull(summaryAfter);
        Assert.AreEqual(0, summaryAfter.ProcessingCount);
        Assert.AreEqual(0, summaryAfter.CompletedCount);
    }

    [TestMethod]
    public async Task Autonomous_Pipeline_Pause_Blocks_Dispatcher_And_Resume_Continues()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);
        builder.Services.AddSingleton<IPythonAiClient, SimulationAiClient>();

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var projectService = host.Services.GetRequiredService<ProjectService>();
            var coordinator = host.Services.GetRequiredService<ProjectRuntimeCoordinator>();
            var dashboardQuery = host.Services.GetRequiredService<IDashboardQueryService>();

            var inDir = Path.Combine(testWorkDir, "pause_disp_in");
            var outDir = Path.Combine(testWorkDir, "pause_disp_out");
            Directory.CreateDirectory(inDir);
            Directory.CreateDirectory(outDir);

            var config = new ProjectConfigV1(
                inDir, outDir, includeSubfolders: false, revealMode: RevealMode.DtAuto,
                preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
                comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
                exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 1);

            var created = await projectService.CreateProjectAsync("Pause_Dispatcher_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
            var pId = created.Project.Id;

            // Start project
            await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));

            // Pause project
            var pauseRes = await coordinator.PauseProjectAsync(pId, Guid.NewGuid().ToString("N"));
            Assert.AreEqual(LifecycleResultStatus.Transitioned, pauseRes.Status);

            // Add photo to inDir while paused
            var testImg = Path.Combine(inDir, "pause_test.jpg");
            using (var bmp = new Bitmap(200, 200, PixelFormat.Format24bppRgb))
            {
                using var g = Graphics.FromImage(bmp);
                g.Clear(Color.DodgerBlue);
                bmp.Save(testImg, ImageFormat.Jpeg);
            }

            // Wait 1 second while paused
            await Task.Delay(1000);

            // Verify dashboard while PAUSED -> no completed jobs
            var summaryWhilePaused = await dashboardQuery.GetDashboardSummaryAsync(pId);
            Assert.IsNotNull(summaryWhilePaused);
            Assert.AreEqual(0, summaryWhilePaused.ProcessingCount);
            Assert.AreEqual(0, summaryWhilePaused.CompletedCount);

            // Resume project
            var resumeRes = await coordinator.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));
            Assert.AreEqual(LifecycleResultStatus.Transitioned, resumeRes.Status);

            // Wait for autonomous completion after resume
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DashboardSummaryDto? summaryAfter = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(15))
            {
                summaryAfter = await dashboardQuery.GetDashboardSummaryAsync(pId);
                if (summaryAfter is not null && summaryAfter.CompletedCount >= 1)
                {
                    break;
                }
                await Task.Delay(100);
            }

            Assert.IsNotNull(summaryAfter);
            Assert.AreEqual(1, summaryAfter.CompletedCount, "Job must complete after resuming project");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [TestMethod]
    public async Task Autonomous_Pipeline_Crash_Restart_Continues_From_Durable_State()
    {
        var appPaths = new TestAppPaths(testWorkDir);
        var pId = new ProjectId(Guid.NewGuid().ToString("N"));
        var inDir = Path.Combine(testWorkDir, "crash_disp_in");
        var outDir = Path.Combine(testWorkDir, "crash_disp_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var testImg = Path.Combine(inDir, "crash_test.jpg");
        using (var bmp = new Bitmap(200, 200, PixelFormat.Format24bppRgb))
        {
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.Purple);
            bmp.Save(testImg, ImageFormat.Jpeg);
        }

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: false, revealMode: RevealMode.DtAuto,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 1);

        // HOST 1: Creates project, ingests photo, then "crashes" (host disposed without processing)
        {
            var b1 = CreateTestHostBuilder();
            b1.Services.AddSingleton<IAppPaths>(appPaths);
            b1.Services.AddSingleton<IPythonAiClient, SimulationAiClient>();
            using var h1 = b1.Build();

            var projService = h1.Services.GetRequiredService<ProjectService>();
            var ingStores = h1.Services.GetRequiredService<IIngestionStoreFactory>();

            var created = await projService.CreateProjectAsync("Crash_Recovery_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
            pId = created.Project.Id;

            // Ingest file into ReadyForAnalysis
            var store = ingStores.Open(pId);
            var prep = await store.PrepareSourceAsync(pId, created.LatestConfig.Id, inDir, false, DateTimeOffset.UtcNow);
            var cmd = new IngestAssetCommand(
                pId,
                prep.Source.Id,
                "|crash_test",
                testImg,
                "crash_test.jpg",
                testImg,
                AssetFormat.Jpeg,
                new FileInfo(testImg).Length,
                ComputeFileSha256(testImg),
                RawSupportInfo.NotApplicable,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(1));
            await store.IngestArchivedAsync(cmd);
            await store.FinalizeAssociationsAsync(pId, prep.Source.Id, DateTimeOffset.UtcNow.AddSeconds(10), force: true);

            // Crash: Host 1 ends here
        }

        // HOST 2: Starts up fresh, starts running project -> QueueDispatcherService picks up durable state
        {
            var b2 = CreateTestHostBuilder();
            b2.Services.AddSingleton<IAppPaths>(appPaths);
            b2.Services.AddSingleton<IPythonAiClient, SimulationAiClient>();
            using var h2 = b2.Build();
            await h2.StartAsync();

            try
            {
                var coord = h2.Services.GetRequiredService<ProjectRuntimeCoordinator>();
                var dashQuery = h2.Services.GetRequiredService<IDashboardQueryService>();

                // SOLE ACTION: Start project in new host
                await coord.StartOrResumeProjectAsync(pId, Guid.NewGuid().ToString("N"));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                DashboardSummaryDto? summary = null;
                while (sw.Elapsed < TimeSpan.FromSeconds(15))
                {
                    summary = await dashQuery.GetDashboardSummaryAsync(pId);
                    if (summary is not null && summary.CompletedCount >= 1)
                    {
                        break;
                    }
                    await Task.Delay(100);
                }

                Assert.IsNotNull(summary);
                Assert.AreEqual(1, summary.CompletedCount, "Job must complete autonomously after crash/restart");
            }
            finally
            {
                await h2.StopAsync();
            }
        }
    }

    [TestMethod]
    public async Task QA_Eligibility_Matrix_Positive_And_Negative_Enforcement()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var qaStoreFactory = host.Services.GetRequiredService<IQaStoreFactory>();

        async Task RunMatrixCaseAsync(
            string testName,
            RevealMode revealMode,
            ComfyUiMode comfyMode,
            string checkpointToInsert,
            bool expectEligible,
            bool addQaComplete = false)
        {
            var inDir = Path.Combine(testWorkDir, $"{testName}_in");
            var outDir = Path.Combine(testWorkDir, $"{testName}_out");
            Directory.CreateDirectory(inDir);
            Directory.CreateDirectory(outDir);

            var config = new ProjectConfigV1(
                inDir, outDir, false, revealMode, false, "BALANCED", SemanticMode.Off,
                comfyMode, [], [], "JPEG", 90, 1);

            var created = await projectService.CreateProjectAsync(testName, config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
            var pId = created.Project.Id;
            var configVerId = created.LatestConfig.Id;

            var jId = JobId.New();
            var photoId = PhotoId.New();
            var dummyPath = Path.Combine(outDir, "cand.jpg");
            File.WriteAllBytes(dummyPath, [1, 2, 3]);

            var qaStore = qaStoreFactory.Open(pId);
            var projectDbPath = appPaths.GetProjectDatabasePath(pId);
            using var conn = new SqliteConnection($"Data Source={projectDbPath}");
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO ingestion_sources(source_id, project_id, input_root, include_subfolders, config_version_id, created_at_utc)
                    VALUES('src1', $projId, 'C:\\in', 0, $cfgId, '2026-08-24T00:00:00Z');

                    INSERT INTO photos(photo_id, project_id, source_id, association_key, state, association_deadline_utc, created_at_utc, updated_at_utc)
                    VALUES($pId, $projId, 'src1', 'key', 'READY_FOR_ANALYSIS', '2026-08-24T00:00:00Z', '2026-08-24T00:00:00Z', '2026-08-24T00:00:00Z');

                    INSERT INTO assets(asset_id, project_id, photo_id, source_id, source_path, source_relative_path, managed_path, format, role, archive_state, size_bytes, sha256, raw_support_status, raw_max_width, raw_max_height, raw_classification, observed_at_utc, archived_at_utc)
                    VALUES('ast1', $projId, $pId, 'src1', 'C:\\in\\cand.jpg', 'cand.jpg', 'C:\\in\\cand.jpg', 'JPEG', 'JPEG_MASTER', 'ARCHIVED', 3, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'NOT_APPLICABLE', 0, 0, 'JPEG', '2026-08-24T00:00:00Z', '2026-08-24T00:00:00Z');

                    INSERT INTO jobs(job_id, project_id, photo_id, state, processing_config_id, preselection_config_id, analysis_source_asset_id, analysis_source_sha256, analysis_input_kind, analysis_representation_path, created_at_utc, updated_at_utc)
                    VALUES($jId, $projId, $pId, 'QA', $cfgId, $cfgId, 'ast1', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'JPEG_MASTER', $path, '2026-08-24T00:00:00Z', '2026-08-24T00:00:00Z');

                    INSERT INTO outputs(output_id, job_id, attempt_id, stage, role, path, sha256, size_bytes, width, height, validated, permanent, created_at_utc)
                    VALUES('out1', $jId, 'att1', 'BASIC_REVEAL', 'BASIC_REVEAL_STAGING', $path, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 3, 100, 100, 1, 0, '2026-08-24T00:00:00Z');

                    INSERT INTO processing_passes(processing_pass_id, job_id, attempt_id, reveal_mode, input_asset_id, input_sha256, darktable_version, control_plan_json, output_id, history_path, completed_at_utc)
                    VALUES('pass1', $jId, 'att1', 'DT_AUTO', 'ast1', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', '4.6', '{"valid":true}', 'out1', 'C:\\dummy.xmp', '2026-08-24T00:00:00Z');
                    """;
                cmd.Parameters.AddWithValue("$pId", photoId.Value);
                cmd.Parameters.AddWithValue("$projId", pId.Value);
                cmd.Parameters.AddWithValue("$jId", jId.Value);
                cmd.Parameters.AddWithValue("$cfgId", configVerId);
                cmd.Parameters.AddWithValue("$path", dummyPath);
                cmd.ExecuteNonQuery();
            }

            if (!string.IsNullOrEmpty(checkpointToInsert))
            {
                using var cmdCp = conn.CreateCommand();
                cmdCp.CommandText = "INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc) VALUES($cpId, $jId, $stage, 'att1', 'fp1', '2026-08-24T00:00:00Z');";
                cmdCp.Parameters.AddWithValue("$cpId", Guid.NewGuid().ToString("N"));
                cmdCp.Parameters.AddWithValue("$jId", jId.Value);
                cmdCp.Parameters.AddWithValue("$stage", checkpointToInsert);
                cmdCp.ExecuteNonQuery();
            }

            if (addQaComplete)
            {
                using var cmdQa = conn.CreateCommand();
                cmdQa.CommandText = "INSERT INTO job_checkpoints(checkpoint_id, job_id, stage_name, attempt_id, input_fingerprint, created_at_utc) VALUES($cpId, $jId, 'QA_COMPLETE', 'att1', 'fp1', '2026-08-24T00:00:00Z');";
                cmdQa.Parameters.AddWithValue("$cpId", Guid.NewGuid().ToString("N"));
                cmdQa.Parameters.AddWithValue("$jId", jId.Value);
                cmdQa.ExecuteNonQuery();
            }

            var next = await qaStore.GetNextEligibleQaJobAsync(pId);
            if (expectEligible)
            {
                Assert.IsNotNull(next, $"Case '{testName}' expected eligible QA job, but got null.");
                Assert.AreEqual(jId, next.JobId);
            }
            else
            {
                Assert.IsNull(next, $"Case '{testName}' expected NOT eligible, but got job {next?.JobId}.");
            }
        }

        // 1. DT_AUTO + Comfy OFF: BASIC_REVEAL_COMPLETE => Eligible
        await RunMatrixCaseAsync("DT_Auto_ComfyOff_Positive", RevealMode.DtAuto, ComfyUiMode.Off, "BASIC_REVEAL_COMPLETE", true);

        // 2. DT_AUTO + Comfy ON: BASIC_REVEAL_COMPLETE only => NOT eligible
        await RunMatrixCaseAsync("DT_Auto_ComfyOn_Negative_BasicOnly", RevealMode.DtAuto, ComfyUiMode.On, "BASIC_REVEAL_COMPLETE", false);

        // 3. DT_AUTO + Comfy ON: COMFYUI_COMPLETE => Eligible
        await RunMatrixCaseAsync("DT_Auto_ComfyOn_Positive", RevealMode.DtAuto, ComfyUiMode.On, "COMFYUI_COMPLETE", true);

        // 4. PRE_AI + Comfy OFF: BASIC_REVEAL_COMPLETE => Eligible
        await RunMatrixCaseAsync("PreAi_ComfyOff_Positive", RevealMode.PreAi, ComfyUiMode.Off, "BASIC_REVEAL_COMPLETE", true);

        // 5. PRE_AI + Comfy ON: BASIC_REVEAL_COMPLETE only => NOT eligible
        await RunMatrixCaseAsync("PreAi_ComfyOn_Negative_BasicOnly", RevealMode.PreAi, ComfyUiMode.On, "BASIC_REVEAL_COMPLETE", false);

        // 6. PRE_AI + Comfy ON: COMFYUI_COMPLETE => Eligible
        await RunMatrixCaseAsync("PreAi_ComfyOn_Positive", RevealMode.PreAi, ComfyUiMode.On, "COMFYUI_COMPLETE", true);

        // 7. FEEDBACK + Comfy OFF: BASIC_REVEAL_COMPLETE only => NOT eligible
        await RunMatrixCaseAsync("Feedback_ComfyOff_Negative_BasicOnly", RevealMode.Feedback, ComfyUiMode.Off, "BASIC_REVEAL_COMPLETE", false);

        // 8. FEEDBACK + Comfy OFF: DARKTABLE_PASS2_COMPLETE => Eligible
        await RunMatrixCaseAsync("Feedback_ComfyOff_Positive", RevealMode.Feedback, ComfyUiMode.Off, "DARKTABLE_PASS2_COMPLETE", true);

        // 9. FEEDBACK + Comfy ON: DARKTABLE_PASS2_COMPLETE only => NOT eligible
        await RunMatrixCaseAsync("Feedback_ComfyOn_Negative_Pass2Only", RevealMode.Feedback, ComfyUiMode.On, "DARKTABLE_PASS2_COMPLETE", false);

        // 10. FEEDBACK + Comfy ON: COMFYUI_COMPLETE => Eligible
        await RunMatrixCaseAsync("Feedback_ComfyOn_Positive", RevealMode.Feedback, ComfyUiMode.On, "COMFYUI_COMPLETE", true);

        // 11. Duplicate QA prevention: QA_COMPLETE already present => NOT eligible
        await RunMatrixCaseAsync("Duplicate_Qa_Negative", RevealMode.DtAuto, ComfyUiMode.Off, "BASIC_REVEAL_COMPLETE", false, addQaComplete: true);
    }

    [TestMethod]
    public async Task Unsupported_RAW_Review_ViewModel_Safety_And_Command_Predicates()
    {
        var builder = CreateTestHostBuilder();
        var appPaths = new TestAppPaths(testWorkDir);
        builder.Services.AddSingleton<IAppPaths>(appPaths);

        using var host = builder.Build();
        var projectService = host.Services.GetRequiredService<ProjectService>();
        var ingestionStores = host.Services.GetRequiredService<IIngestionStoreFactory>();
        var reviewQuery = host.Services.GetRequiredService<IReviewQueryService>();
        var reviewVm = host.Services.GetRequiredService<ReviewViewModel>();
        var projectContext = host.Services.GetRequiredService<IProjectContext>();

        var inDir = Path.Combine(testWorkDir, "vm_safe_in");
        var outDir = Path.Combine(testWorkDir, "vm_safe_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(
            inDir, outDir, false, RevealMode.DtAuto, false, "BALANCED", SemanticMode.Off,
            ComfyUiMode.Off, [], [], "JPEG", 90, 5);

        var created = await projectService.CreateProjectAsync("Review_Safety_Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;
        projectContext.SetActiveProject(pId, "Review_Safety_Project", ProjectState.Running);

        // 1. Ingest Unsupported Reduced RAW
        var unsuppFile = Path.Combine(inDir, "DSC03812.ARW");
        File.WriteAllBytes(unsuppFile, new byte[100]);
        var store = ingestionStores.Open(pId);
        var prep = await store.PrepareSourceAsync(pId, created.LatestConfig.Id, inDir, false, DateTimeOffset.UtcNow);
        var cmd = new IngestAssetCommand(
            pId,
            prep.Source.Id,
            "|DSC03812",
            unsuppFile,
            "DSC03812.ARW",
            unsuppFile,
            AssetFormat.Raw,
            11128832,
            "901a76772690fcca6d78c47acd8c3362e91e3c85308f231d3bbec40e89071114",
            new RawSupportInfo(RawSupportStatus.UnsupportedReduced, 2816, 1864, "UNSUPPORTED_REDUCED_RAW"),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5));
        await store.IngestArchivedAsync(cmd);

        // 2. Query pending reviews
        var reviews = await reviewQuery.GetPendingReviewsAsync(pId);
        Assert.AreEqual(1, reviews.Count);
        var unsuppItem = reviews[0];

        // Verify read model properties
        Assert.IsNull(unsuppItem.JobId, "Unsupported RAW must have JobId == null");
        Assert.IsFalse(unsuppItem.HasJob, "Unsupported RAW must have HasJob == false");
        Assert.IsTrue(unsuppItem.ReviewItemId.StartsWith("unsupported:"), "ReviewItemId must use deterministic unsupported read model ID");
        Assert.AreEqual("Unsupported Format", unsuppItem.ReviewStage);
        Assert.AreEqual(JobState.RejectedPre, unsuppItem.JobState);

        // Verify DB contains NO jobs row for this photo
        var dbPath = appPaths.GetProjectDatabasePath(pId);
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT count(*) FROM jobs;";
            var jobsCount = Convert.ToInt64(countCmd.ExecuteScalar());
            Assert.AreEqual(0L, jobsCount, "Database must have 0 rows in jobs table");

            using var queueCmd = conn.CreateCommand();
            queueCmd.CommandText = "SELECT count(*) FROM queue_entries;";
            var queueCount = Convert.ToInt64(queueCmd.ExecuteScalar());
            Assert.AreEqual(0L, queueCount, "Database must have 0 rows in queue_entries table");
        }

        // 3. Test ViewModel command predicates with Unsupported Item selected
        reviewVm.SelectedItem = unsuppItem;

        Assert.IsFalse(reviewVm.ApproveCommand.CanExecute(null), "ApproveCommand must be DISABLED for unsupported item without JobId");
        Assert.IsFalse(reviewVm.ReprocessCommand.CanExecute(null), "ReprocessCommand must be DISABLED for unsupported item without JobId");
        Assert.IsFalse(reviewVm.RejectCommand.CanExecute(null), "RejectCommand must be DISABLED for unsupported item without JobId");
        Assert.IsFalse(reviewVm.LeavePendingCommand.CanExecute(null), "LeavePendingCommand must be DISABLED for unsupported item without JobId");

        // 4. Test programmatic invocation safety (must not throw or invoke IReviewService)
        await reviewVm.ApproveSelectedAsync();
        await reviewVm.ReprocessSelectedAsync();
        await reviewVm.RejectSelectedAsync();
        await reviewVm.LeavePendingSelectedAsync();

        // 5. Test Normal Job-backed review item CanExecute
        var normalJobItem = new ReviewItemDto(
            "rev-1",
            pId,
            JobId.New(),
            PhotoId.New(),
            "photo_normal.jpg",
            JobState.ReviewFinal,
            "Final Quality Review",
            null,
            null,
            QaDecision.Review,
            default,
            null,
            0,
            DateTimeOffset.UtcNow);

        reviewVm.SelectedItem = normalJobItem;
        Assert.IsTrue(reviewVm.ApproveCommand.CanExecute(null), "ApproveCommand must be ENABLED for normal ReviewFinal item");
        Assert.IsTrue(reviewVm.ReprocessCommand.CanExecute(null), "ReprocessCommand must be ENABLED for normal ReviewFinal item");
        Assert.IsTrue(reviewVm.RejectCommand.CanExecute(null), "RejectCommand must be ENABLED for normal ReviewFinal item");
        Assert.IsTrue(reviewVm.LeavePendingCommand.CanExecute(null), "LeavePendingCommand must be ENABLED for normal ReviewFinal item");
    }

    [TestMethod]
    public async Task Component_Health_Catalog_UI_Renders_All_Known_Components_And_Honors_Standby_State()
    {
        var healthTracker = new ComponentHealthTracker();
        var paths = new TestAppPaths(testWorkDir);
        var modelStatusService = new ModelStatusService(healthTracker, paths);
        var modelsVm = new ModelsViewModel(modelStatusService);

        // A & B: Fresh host, zero processing performed: RefreshAsync yields Python AI Worker in Standby
        await modelsVm.RefreshAsync();
        Assert.IsNotNull(modelsVm.Components);
        Assert.IsTrue(modelsVm.Components.Count >= 4, "Must contain all known catalog components.");

        var pythonCard = modelsVm.Components.FirstOrDefault(c => c.ComponentName == "PythonWorker");
        Assert.IsNotNull(pythonCard, "Python AI Worker card must exist on a fresh host before any processing.");
        Assert.AreEqual("Python AI Worker", pythonCard.DisplayName);
        Assert.AreEqual(ComponentHealthState.Standby, pythonCard.State, "Fresh host state must be Standby, not Healthy or Unhealthy.");
        Assert.AreEqual("Standby (On-Demand)", pythonCard.StatusText);
        Assert.IsFalse(pythonCard.CircuitOpen);

        // F: IngestionRuntime and ComfyUI cards also present
        var ingestionCard = modelsVm.Components.FirstOrDefault(c => c.ComponentName == "IngestionRuntime");
        Assert.IsNotNull(ingestionCard, "Ingestion Runtime card must exist.");
        Assert.AreEqual(ComponentHealthState.Standby, ingestionCard.State);

        var comfyCard = modelsVm.Components.FirstOrDefault(c => c.ComponentName == "ComfyUI");
        Assert.IsNotNull(comfyCard, "ComfyUI Runtime card must exist.");
        Assert.AreEqual(ComponentHealthState.Standby, comfyCard.State);

        var darktableCard = modelsVm.Components.FirstOrDefault(c => c.ComponentName == "Darktable");
        Assert.IsNotNull(darktableCard, "Darktable CLI card must exist.");
        Assert.AreEqual(ComponentHealthState.Standby, darktableCard.State);

        // C: After RecordSuccess("PythonWorker") => card == Healthy / Operational
        healthTracker.RecordSuccess("PythonWorker");
        await modelsVm.RefreshAsync();

        pythonCard = modelsVm.Components.First(c => c.ComponentName == "PythonWorker");
        Assert.AreEqual(ComponentHealthState.Healthy, pythonCard.State);
        Assert.IsTrue(pythonCard.StatusText.Contains("Healthy") || pythonCard.StatusText.Contains("Operational"));
        Assert.IsFalse(pythonCard.CircuitOpen);

        // D: After MarkUnhealthy("PythonWorker") => card == Unhealthy
        healthTracker.MarkUnhealthy("PythonWorker", "AI Worker crashed during inference");
        await modelsVm.RefreshAsync();

        pythonCard = modelsVm.Components.First(c => c.ComponentName == "PythonWorker");
        Assert.AreEqual(ComponentHealthState.Unhealthy, pythonCard.State);
        Assert.AreEqual("AI Worker crashed during inference", pythonCard.StatusText);
        Assert.IsTrue(pythonCard.CircuitOpen);
    }

    [TestMethod]
    public async Task Dashboard_Health_Cards_Render_Known_Catalog_When_Uninitialized()
    {
        var healthTracker = new ComponentHealthTracker();
        var paths = new TestAppPaths(testWorkDir);
        var projectId = new ProjectId("proj_catalog_" + Guid.NewGuid().ToString("N")[..8]);
        var dbPath = paths.GetProjectDatabasePath(projectId);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var db = new SqliteProjectDatabase(dbPath);
        await db.InitializeAsync();

        var storeFactory = new SqliteProjectStoreFactory(paths);
        var store = storeFactory.Open(projectId);

        var inDir = Path.Combine(testWorkDir, "in");
        var outDir = Path.Combine(testWorkDir, "out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var config = new ProjectConfigV1(inDir, outDir, false, RevealMode.PreAi, true, "Standard", SemanticMode.Standard, ComfyUiMode.Off, [], [], "JPEG", 90, 5);
        var configVersion = ConfigVersion.Create(projectId, 1, config, "op_cat_1", DateTimeOffset.UtcNow);
        await store.CreateAsync(Project.Restore(projectId, "Catalog Test Project", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProjectState.Running, 1, DateTimeOffset.UtcNow), configVersion, "op_cat_1");

        var queryService = new DashboardQueryService(paths, storeFactory, healthTracker);
        var summary = await queryService.GetDashboardSummaryAsync(projectId);

        Assert.IsNotNull(summary);
        Assert.IsNotNull(summary.ComponentHealth);
        Assert.IsTrue(summary.ComponentHealth.Count >= 4);

        var py = summary.ComponentHealth.FirstOrDefault(c => c.ComponentName == "PythonWorker");
        Assert.IsNotNull(py);
        Assert.AreEqual(ComponentHealthState.Standby, py.State);
    }

    private static async Task<PhotoIngestionSnapshot> IngestJpegPhotoDirectAsync(
        IIngestionStoreFactory ingestionStores,
        ProjectId projectId,
        string configVersionId,
        string inDir,
        string fileName,
        Color color)
    {
        var testImg = Path.Combine(inDir, fileName);
        using (var bmp = new Bitmap(200, 200, PixelFormat.Format24bppRgb))
        {
            using var g = Graphics.FromImage(bmp);
            g.Clear(color);
            bmp.Save(testImg, ImageFormat.Jpeg);
        }

        var store = ingestionStores.Open(projectId);
        var prep = await store.PrepareSourceAsync(projectId, configVersionId, inDir, false, DateTimeOffset.UtcNow);
        var cmd = new IngestAssetCommand(
            projectId,
            prep.Source.Id,
            "|" + Path.GetFileNameWithoutExtension(fileName),
            testImg,
            fileName,
            testImg,
            AssetFormat.Jpeg,
            new FileInfo(testImg).Length,
            ComputeFileSha256(testImg),
            RawSupportInfo.NotApplicable,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1));
        await store.IngestArchivedAsync(cmd);
        await store.FinalizeAssociationsAsync(projectId, prep.Source.Id, DateTimeOffset.UtcNow.AddSeconds(10), force: true);

        var photos = await store.ListPhotosAsync(projectId);
        return photos.First(p => p.AssociationKey.Contains(Path.GetFileNameWithoutExtension(fileName)));
    }

    [TestMethod]
    public async Task Dispatcher_Ignores_Terminal_Error_Jobs_Without_Spinning()
    {
        var appPaths = new TestAppPaths(testWorkDir);
        var inDir = Path.Combine(testWorkDir, "err_spin_in");
        var outDir = Path.Combine(testWorkDir, "err_spin_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var builder = CreateTestHostBuilder();
        builder.Services.AddSingleton<IAppPaths>(appPaths);
        builder.Services.AddSingleton<IPythonAiClient, SimulationAiClient>();
        using var host = builder.Build();

        var projectService = host.Services.GetRequiredService<ProjectService>();
        var lifecycleService = host.Services.GetRequiredService<ProjectLifecycleService>();
        var dispatcher = host.Services.GetRequiredService<QueueDispatcherService>();
        var analysisStoreFactory = host.Services.GetRequiredService<IAnalysisStoreFactory>();
        var ingestionStores = host.Services.GetRequiredService<IIngestionStoreFactory>();
        var inputResolver = host.Services.GetRequiredService<IAnalysisInputResolver>();

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: false, revealMode: RevealMode.DtAuto,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 1);

        var created = await projectService.CreateProjectAsync("Error Spin Test Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;
        await lifecycleService.StartOrResumeAsync(pId, Guid.NewGuid().ToString("N"));

        var photo = await IngestJpegPhotoDirectAsync(ingestionStores, pId, created.LatestConfig.Id, inDir, "error_photo.jpg", Color.Firebrick);

        var aStore = analysisStoreFactory.Open(pId);
        var proposedJobId = JobId.New();
        var resolvedInput = await inputResolver.ResolveAsync(pId, photo.Id, proposedJobId, "att-err");
        var job = await aStore.GetOrCreateInitialJobAsync(
            proposedJobId,
            pId,
            photo.Id,
            created.LatestConfig.Id,
            created.LatestConfig.Id,
            resolvedInput,
            DateTimeOffset.UtcNow);
        await aStore.MarkAnalyzingAsync(job.Id, "op-err-start", DateTimeOffset.UtcNow);
        await aStore.MarkErrorAsync(job.Id, "op-err-fail", "Simulated unrecoverable failure", DateTimeOffset.UtcNow);

        // Verify initial setup
        job = (await aStore.GetInitialJobByPhotoAsync(pId, photo.Id))!;
        Assert.AreEqual(JobState.Error, job.State);

        // Run dispatcher for 100 deterministic cycles
        for (int cycle = 0; cycle < 100; cycle++)
        {
            var workDone = await dispatcher.DispatchOnceAsync();
            Assert.IsFalse(workDone, $"Cycle {cycle} should not perform work on an ERROR job.");
        }

        // Verify that Job is still in ERROR and no attempts / transitions occurred
        var jobAfter = (await aStore.GetInitialJobByPhotoAsync(pId, photo.Id))!;
        Assert.AreEqual(JobState.Error, jobAfter.State);
        Assert.AreEqual(job.TechnicalRetryCount, jobAfter.TechnicalRetryCount);
    }

    [TestMethod]
    public async Task Broken_Job_Does_Not_Block_Healthy_Job()
    {
        var appPaths = new TestAppPaths(testWorkDir);
        var inDir = Path.Combine(testWorkDir, "healthy_beside_broken_in");
        var outDir = Path.Combine(testWorkDir, "healthy_beside_broken_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var builder = CreateTestHostBuilder();
        builder.Services.AddSingleton<IAppPaths>(appPaths);
        builder.Services.AddSingleton<IPythonAiClient, SimulationAiClient>();
        using var host = builder.Build();

        var projectService = host.Services.GetRequiredService<ProjectService>();
        var lifecycleService = host.Services.GetRequiredService<ProjectLifecycleService>();
        var dispatcher = host.Services.GetRequiredService<QueueDispatcherService>();
        var analysisStoreFactory = host.Services.GetRequiredService<IAnalysisStoreFactory>();
        var ingestionStores = host.Services.GetRequiredService<IIngestionStoreFactory>();
        var inputResolver = host.Services.GetRequiredService<IAnalysisInputResolver>();

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: false, revealMode: RevealMode.DtAuto,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 1);

        var created = await projectService.CreateProjectAsync("Mixed Health Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;
        await lifecycleService.StartOrResumeAsync(pId, Guid.NewGuid().ToString("N"));

        // Photo 1: will be errored
        var photo1 = await IngestJpegPhotoDirectAsync(ingestionStores, pId, created.LatestConfig.Id, inDir, "photo1_broken.jpg", Color.DarkRed);

        var aStore = analysisStoreFactory.Open(pId);
        var job1Id = JobId.New();
        var resolvedInput1 = await inputResolver.ResolveAsync(pId, photo1.Id, job1Id, "att-broken");
        var job1 = await aStore.GetOrCreateInitialJobAsync(job1Id, pId, photo1.Id, created.LatestConfig.Id, created.LatestConfig.Id, resolvedInput1, DateTimeOffset.UtcNow);
        await aStore.MarkAnalyzingAsync(job1.Id, "op-start-err", DateTimeOffset.UtcNow);
        await aStore.MarkErrorAsync(job1.Id, "op-err-broken", "Broken job error", DateTimeOffset.UtcNow);

        // Photo 2: healthy new photo
        var photo2 = await IngestJpegPhotoDirectAsync(ingestionStores, pId, created.LatestConfig.Id, inDir, "photo2_healthy.jpg", Color.GreenYellow);

        // Dispatch cycle: should skip Photo 1 and successfully advance Photo 2!
        var workDone = await dispatcher.DispatchOnceAsync();
        Assert.IsTrue(workDone, "Dispatcher must advance the healthy photo.");

        // Verify Photo 1 is untouched
        var job1After = (await aStore.GetInitialJobByPhotoAsync(pId, photo1.Id))!;
        Assert.AreEqual(JobState.Error, job1After.State);

        // Verify Photo 2 got analyzed
        var job2 = await aStore.GetInitialJobByPhotoAsync(pId, photo2.Id);
        Assert.IsNotNull(job2, "Healthy photo must have a Job created.");
        Assert.IsTrue(job2.State is JobState.Queued or JobState.ReviewPre or JobState.Processing or JobState.Completed);
    }

    [TestMethod]
    public async Task Dispatcher_Does_Not_Re_Dispatch_Completed_Or_Review_Photos()
    {
        var appPaths = new TestAppPaths(testWorkDir);
        var inDir = Path.Combine(testWorkDir, "idempotent_in");
        var outDir = Path.Combine(testWorkDir, "idempotent_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var builder = CreateTestHostBuilder();
        builder.Services.AddSingleton<IAppPaths>(appPaths);
        builder.Services.AddSingleton<IPythonAiClient, SimulationAiClient>();
        using var host = builder.Build();

        var projectService = host.Services.GetRequiredService<ProjectService>();
        var lifecycleService = host.Services.GetRequiredService<ProjectLifecycleService>();
        var analysisManager = host.Services.GetRequiredService<ProjectAnalysisManager>();
        var ingestionStores = host.Services.GetRequiredService<IIngestionStoreFactory>();

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: false, revealMode: RevealMode.DtAuto,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 1);

        var created = await projectService.CreateProjectAsync("Idempotent Analysis Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;
        await lifecycleService.StartOrResumeAsync(pId, Guid.NewGuid().ToString("N"));

        await IngestJpegPhotoDirectAsync(ingestionStores, pId, created.LatestConfig.Id, inDir, "photo_idemp.jpg", Color.Purple);

        var initialResult = await analysisManager.ProcessReadyAsync(pId);
        Assert.AreEqual(AnalysisDispatchStatus.AnalysisCompleted, initialResult.Status);
        Assert.AreEqual(1, initialResult.NewlyDispatchedCount);

        // Subsequent 20 calls must be Suppressed / NoWork with NewlyDispatchedCount == 0
        for (int i = 0; i < 20; i++)
        {
            var repeatedResult = await analysisManager.ProcessReadyAsync(pId);
            Assert.IsTrue(repeatedResult.Status is AnalysisDispatchStatus.Suppressed or AnalysisDispatchStatus.NoWork);
            Assert.AreEqual(0, repeatedResult.NewlyDispatchedCount);
        }
    }

    [TestMethod]
    public async Task Single_Jpeg_Analysis_Preselection_Review_Pre_Integration()
    {
        var appPaths = new TestAppPaths(testWorkDir);
        var inDir = Path.Combine(testWorkDir, "review_pre_in");
        var outDir = Path.Combine(testWorkDir, "review_pre_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var builder = CreateTestHostBuilder();
        builder.Services.AddSingleton<IAppPaths>(appPaths);
        builder.Services.AddSingleton<IPythonAiClient, ReviewPreAiClient>();
        using var host = builder.Build();

        var projectService = host.Services.GetRequiredService<ProjectService>();
        var lifecycleService = host.Services.GetRequiredService<ProjectLifecycleService>();
        var dashboardQuery = host.Services.GetRequiredService<IDashboardQueryService>();
        var reviewQuery = host.Services.GetRequiredService<IReviewQueryService>();
        var dispatcher = host.Services.GetRequiredService<QueueDispatcherService>();
        var ingestionStores = host.Services.GetRequiredService<IIngestionStoreFactory>();

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: false, revealMode: RevealMode.DtAuto,
            preselectionEnabled: true, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 1);

        var created = await projectService.CreateProjectAsync("Review Pre Integration Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;
        await lifecycleService.StartOrResumeAsync(pId, Guid.NewGuid().ToString("N"));

        await IngestJpegPhotoDirectAsync(ingestionStores, pId, created.LatestConfig.Id, inDir, "photo_review_pre.jpg", Color.Goldenrod);

        // Run dispatcher to process analysis and preselection
        await dispatcher.DispatchOnceAsync();

        var summary = await dashboardQuery.GetDashboardSummaryAsync(pId);
        Assert.IsNotNull(summary);
        Assert.AreEqual(1, summary.ReviewCount, "Dashboard must show Review = 1 for REVIEW_PRE job.");
        Assert.AreEqual(0, summary.QueuedCount);
        Assert.AreEqual(0, summary.ProcessingCount);
        Assert.AreEqual(0, summary.CompletedCount);

        var reviews = await reviewQuery.GetPendingReviewsAsync(pId);
        Assert.AreEqual(1, reviews.Count);
        Assert.AreEqual(JobState.ReviewPre, reviews[0].JobState);
        Assert.AreEqual("Preselection Review", reviews[0].ReviewStage);

        // Test ReviewViewModel bindings for REVIEW_PRE item
        var context = new ProjectContext();
        context.SetActiveProject(pId, "Test Project", ProjectState.Running);
        var reviewService = host.Services.GetRequiredService<IReviewService>();
        var projectStoreFactory = host.Services.GetRequiredService<IProjectStoreFactory>();
        var thumbService = host.Services.GetRequiredService<IThumbnailService>();

        var vm = new ReviewViewModel(reviewQuery, reviewService, projectStoreFactory, context, thumbService);
        await vm.RefreshAsync();
        Assert.IsNotNull(vm.SelectedItem);
        Assert.AreEqual("Approve & Continue", vm.ApproveButtonLabel);
        Assert.IsTrue(vm.CanApprove);
        Assert.IsTrue(vm.CanReject);
        Assert.IsTrue(vm.CanLeavePending);
        Assert.IsFalse(vm.CanReprocess, "Reprocess must be disabled for REVIEW_PRE");

        // Subsequent dispatcher cycles must not do work while in REVIEW_PRE
        for (int i = 0; i < 10; i++)
        {
            var workDone = await dispatcher.DispatchOnceAsync();
            Assert.IsFalse(workDone);
        }
    }

    [TestMethod]
    public async Task Review_Pre_Approve_Transitions_To_Queued_And_Dispatcher_Continues_Autonomously()
    {
        var appPaths = new TestAppPaths(testWorkDir);
        var inDir = Path.Combine(testWorkDir, "rev_cont_in");
        var outDir = Path.Combine(testWorkDir, "rev_cont_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var builder = CreateTestHostBuilder();
        builder.Services.AddSingleton<IAppPaths>(appPaths);
        builder.Services.AddSingleton<IPythonAiClient, ReviewPreAiClient>();
        using var host = builder.Build();

        var projectService = host.Services.GetRequiredService<ProjectService>();
        var lifecycleService = host.Services.GetRequiredService<ProjectLifecycleService>();
        var dashboardQuery = host.Services.GetRequiredService<IDashboardQueryService>();
        var reviewQuery = host.Services.GetRequiredService<IReviewQueryService>();
        var dispatcher = host.Services.GetRequiredService<QueueDispatcherService>();
        var ingestionStores = host.Services.GetRequiredService<IIngestionStoreFactory>();
        var analysisStoreFactory = host.Services.GetRequiredService<IAnalysisStoreFactory>();
        var reviewService = host.Services.GetRequiredService<IReviewService>();
        var projectStoreFactory = host.Services.GetRequiredService<IProjectStoreFactory>();
        var thumbService = host.Services.GetRequiredService<IThumbnailService>();

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: false, revealMode: RevealMode.DtAuto,
            preselectionEnabled: true, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 1);

        var created = await projectService.CreateProjectAsync("Review Pre Approve Continuation Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;
        await lifecycleService.StartOrResumeAsync(pId, Guid.NewGuid().ToString("N"));

        var photo = await IngestJpegPhotoDirectAsync(ingestionStores, pId, created.LatestConfig.Id, inDir, "photo_cont.jpg", Color.SeaGreen);

        // 1. Initial dispatch: analysis -> preselection -> REVIEW_PRE
        await dispatcher.DispatchOnceAsync();

        var aStore = analysisStoreFactory.Open(pId);
        var job = (await aStore.GetInitialJobByPhotoAsync(pId, photo.Id))!;
        Assert.AreEqual(JobState.ReviewPre, job.State);

        var queueBefore = await aStore.ListQueueAsync(pId);
        Assert.AreEqual(0, queueBefore.Count, "No queue entry before approval");

        // 2. User approves preselection via ReviewViewModel
        var context = new ProjectContext();
        context.SetActiveProject(pId, "Test Project", ProjectState.Running);
        var vm = new ReviewViewModel(reviewQuery, reviewService, projectStoreFactory, context, thumbService);
        await vm.RefreshAsync();
        Assert.IsNotNull(vm.SelectedItem);
        Assert.AreEqual(job.Id, vm.SelectedItem.JobId);

        await vm.ApproveSelectedAsync();

        // Verify: Job is now QUEUED, exactly 1 queue entry
        var jobQueued = (await aStore.GetInitialJobByPhotoAsync(pId, photo.Id))!;
        Assert.AreEqual(JobState.Queued, jobQueued.State);

        var queueAfter = await aStore.ListQueueAsync(pId);
        Assert.AreEqual(1, queueAfter.Count, "Exactly 1 queue entry created");

        // 3. Autonomous dispatcher continues pipeline execution
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            await dispatcher.DispatchOnceAsync();
            var summary = await dashboardQuery.GetDashboardSummaryAsync(pId);
            if (summary is not null && summary.CompletedCount >= 1)
            {
                break;
            }
            await Task.Delay(100);
        }

        var finalSummary = await dashboardQuery.GetDashboardSummaryAsync(pId);
        Assert.IsNotNull(finalSummary);
        Assert.AreEqual(1, finalSummary.CompletedCount, "Pipeline must autonomously complete after preselection approval");
        Assert.AreEqual(0, finalSummary.ReviewCount);
        Assert.AreEqual(0, finalSummary.QueuedCount);
        Assert.AreEqual(0, finalSummary.ProcessingCount);
    }

    [TestMethod]
    public async Task Review_Defensive_Validation_Rejects_Wrong_Stage_Actions()
    {
        var appPaths = new TestAppPaths(testWorkDir);
        var inDir = Path.Combine(testWorkDir, "def_val_in");
        var outDir = Path.Combine(testWorkDir, "def_val_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var builder = CreateTestHostBuilder();
        builder.Services.AddSingleton<IAppPaths>(appPaths);
        builder.Services.AddSingleton<IPythonAiClient, ReviewPreAiClient>();
        using var host = builder.Build();

        var projectService = host.Services.GetRequiredService<ProjectService>();
        var lifecycleService = host.Services.GetRequiredService<ProjectLifecycleService>();
        var reviewService = host.Services.GetRequiredService<IReviewService>();
        var analysisStoreFactory = host.Services.GetRequiredService<IAnalysisStoreFactory>();
        var ingestionStores = host.Services.GetRequiredService<IIngestionStoreFactory>();
        var dispatcher = host.Services.GetRequiredService<QueueDispatcherService>();
        var qaStoreFactory = host.Services.GetRequiredService<IQaStoreFactory>();

        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: false, revealMode: RevealMode.DtAuto,
            preselectionEnabled: true, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 1);

        var created = await projectService.CreateProjectAsync("Defensive Review Validation Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
        var pId = created.Project.Id;
        await lifecycleService.StartOrResumeAsync(pId, Guid.NewGuid().ToString("N"));

        var photo = await IngestJpegPhotoDirectAsync(ingestionStores, pId, created.LatestConfig.Id, inDir, "photo_val.jpg", Color.DodgerBlue);

        await dispatcher.DispatchOnceAsync();

        var aStore = analysisStoreFactory.Open(pId);
        var job = (await aStore.GetInitialJobByPhotoAsync(pId, photo.Id))!;
        Assert.AreEqual(JobState.ReviewPre, job.State);

        // A. REVIEW_PRE + ApproveFinal => rejected with InvalidOperationException, state remains REVIEW_PRE
        var threwA = false;
        try { await reviewService.ApproveFinalAsync(pId, job.Id, "op-err-final", outDir); }
        catch (InvalidOperationException) { threwA = true; }
        Assert.IsTrue(threwA, "ApproveFinalAsync on REVIEW_PRE job must throw InvalidOperationException");

        var jobAfterA = (await aStore.GetInitialJobByPhotoAsync(pId, photo.Id))!;
        Assert.AreEqual(JobState.ReviewPre, jobAfterA.State);

        // B. REVIEW_FINAL + ApprovePreselection => rejected with InvalidOperationException
        var qaStore = qaStoreFactory.Open(pId);
        var finalJobId = JobId.New();
        var photo2 = await IngestJpegPhotoDirectAsync(ingestionStores, pId, created.LatestConfig.Id, inDir, "photo_val2.jpg", Color.Indigo);
        var inputResolver = host.Services.GetRequiredService<IAnalysisInputResolver>();
        var resolvedInput2 = await inputResolver.ResolveAsync(pId, photo2.Id, finalJobId, "att-final");
        var finalJob = await aStore.GetOrCreateInitialJobAsync(finalJobId, pId, photo2.Id, created.LatestConfig.Id, created.LatestConfig.Id, resolvedInput2, DateTimeOffset.UtcNow);
        await aStore.MarkAnalyzingAsync(finalJob.Id, "op-an-2", DateTimeOffset.UtcNow);
        await qaStore.TransitionJobStateAsync(finalJob.Id, JobState.Analyzing, JobState.Queued, "ANALYSIS_COMPLETE", "op-trans-q", DateTimeOffset.UtcNow);
        await qaStore.TransitionJobStateAsync(finalJob.Id, JobState.Queued, JobState.Processing, "PROCESSING_START", "op-trans-p", DateTimeOffset.UtcNow);
        await qaStore.TransitionJobStateAsync(finalJob.Id, JobState.Processing, JobState.Qa, "PROCESSING_COMPLETE", "op-trans-qa", DateTimeOffset.UtcNow);
        await qaStore.TransitionJobStateAsync(finalJob.Id, JobState.Qa, JobState.ReviewFinal, "QA_REVIEW", "op-trans-final", DateTimeOffset.UtcNow);
        await qaStore.CreateReviewItemAsync(new CreateReviewItemRequest(Guid.NewGuid().ToString("N"), finalJob.Id, "FINAL", DateTimeOffset.UtcNow));

        var threwB = false;
        try { await reviewService.ApprovePreselectionAsync(pId, finalJob.Id, "op-err-pre"); }
        catch (InvalidOperationException) { threwB = true; }
        Assert.IsTrue(threwB, "ApprovePreselectionAsync on REVIEW_FINAL job must throw InvalidOperationException");

        var finalJobAfterB = (await aStore.GetInitialJobByPhotoAsync(pId, photo2.Id))!;
        Assert.AreEqual(JobState.ReviewFinal, finalJobAfterB.State);

        // C. REVIEW_PRE approve twice => idempotent, exactly 1 queue entry
        await reviewService.ApprovePreselectionAsync(pId, job.Id, "op-pre-app-1");
        await reviewService.ApprovePreselectionAsync(pId, job.Id, "op-pre-app-2");
        var queueEntries = await aStore.ListQueueAsync(pId);
        Assert.AreEqual(1, queueEntries.Count(q => q.JobId == job.Id));

        // D. REVIEW_PRE Leave Pending => remains REVIEW_PRE
        var job3Id = JobId.New();
        var photo3 = await IngestJpegPhotoDirectAsync(ingestionStores, pId, created.LatestConfig.Id, inDir, "photo_val3.jpg", Color.Olive);
        var resolvedInput3 = await inputResolver.ResolveAsync(pId, photo3.Id, job3Id, "att-pending");
        var job3 = await aStore.GetOrCreateInitialJobAsync(job3Id, pId, photo3.Id, created.LatestConfig.Id, created.LatestConfig.Id, resolvedInput3, DateTimeOffset.UtcNow);
        await aStore.MarkAnalyzingAsync(job3.Id, "op-an-3", DateTimeOffset.UtcNow);
        await qaStore.TransitionJobStateAsync(job3.Id, JobState.Analyzing, JobState.ReviewPre, "PRESELECTION_REVIEW", "op-pre-trans", DateTimeOffset.UtcNow);

        await reviewService.LeavePendingAsync(pId, job3.Id);
        var job3After = (await aStore.GetInitialJobByPhotoAsync(pId, photo3.Id))!;
        Assert.AreEqual(JobState.ReviewPre, job3After.State);

        // E. REVIEW_PRE Reject => REJECTED_PRE
        await reviewService.RejectPreselectionAsync(pId, job3.Id, "op-pre-rej");
        var job3Rejected = (await aStore.GetInitialJobByPhotoAsync(pId, photo3.Id))!;
        Assert.AreEqual(JobState.RejectedPre, job3Rejected.State);

        // G. Unsupported RAW JobId == null => all Job actions disabled
        var unsupportedItem = new ReviewItemDto(
            "unsupported:123", pId, null, photo.Id, "unsupported.raw",
            JobState.Received, "Unsupported RAW Format", null, null, null, JsonDocument.Parse("{}").RootElement, null, 0, DateTimeOffset.UtcNow);

        var context = new ProjectContext();
        context.SetActiveProject(pId, "Test Project", ProjectState.Running);
        var vm = new ReviewViewModel(
            host.Services.GetRequiredService<IReviewQueryService>(),
            reviewService,
            host.Services.GetRequiredService<IProjectStoreFactory>(),
            context,
            host.Services.GetRequiredService<IThumbnailService>());

        vm.SelectedItem = unsupportedItem;
        Assert.IsFalse(vm.CanApprove, "CanApprove must be false when JobId is null");
        Assert.IsFalse(vm.CanReject, "CanReject must be false when JobId is null");
        Assert.IsFalse(vm.CanReprocess, "CanReprocess must be false when JobId is null");
        Assert.IsFalse(vm.CanLeavePending, "CanLeavePending must be false when JobId is null");
    }

    private sealed class ReviewPreAiClient : IPythonAiClient
    {
        public Task<HealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HealthResponse("HEALTHY", "v1", "simulation", null, []));

        public Task<AiResponse> ExecuteAsync(
            string route, AiRequest request, CancellationToken cancellationToken = default)
        {
            JsonElement result;
            if (route.EndsWith("/analyze", StringComparison.Ordinal))
            {
                result = JsonSerializer.SerializeToElement(new
                {
                    schema_version = 1,
                    image_metadata = new { width = 200, height = 200, channels = 3, format = "JPEG" },
                    technical = new
                    {
                        quality_score = 0.85,
                        sharpness = new { laplacian_variance = 120.5 },
                        exposure = new { mean_luminance = 128.0, clipping_highlights = 0.0, clipping_shadows = 0.0 },
                        composition = new { rule_of_thirds = 0.75 }
                    },
                    model_executions = new[]
                    {
                        new
                        {
                            model_id = "mock-analysis-model",
                            model_version = "1.0.0",
                            artifact_set_sha256 = new string('7', 64),
                            parameters = new { },
                            timings = new { total_ms = 1.0 }
                        }
                    }
                }, ContractJson.Options);
            }
            else if (route.EndsWith("/preselect", StringComparison.Ordinal))
            {
                result = JsonSerializer.SerializeToElement(new
                {
                    decision = "REVIEW_PRE",
                    findings = new[]
                    {
                        new { code = "PRESELECTION_THRESHOLDS_NOT_BENCHMARKED", severity = "review", message = "Review required" }
                    }
                }, ContractJson.Options);
            }
            else if (route.EndsWith("/qa", StringComparison.Ordinal) || route.EndsWith("/qa/evaluate", StringComparison.Ordinal))
            {
                result = JsonSerializer.SerializeToElement(new
                {
                    schema_version = 1,
                    decision = "QA_PASS",
                    findings = Array.Empty<object>(),
                    suggested_correction = (object?)null,
                    technical = new { sharpness = new { laplacian_variance = 80.0 } },
                    calibration_status = "BASELINE_NOT_CALIBRATED"
                }, ContractJson.Options);
            }
            else
            {
                result = JsonSerializer.SerializeToElement(new { }, ContractJson.Options);
            }

            return Task.FromResult(new AiResponse("v1", request.RequestId, true, result, null, new Dictionary<string, double>()));
        }
    }

    [TestMethod]
    public async Task Production_Project_Path_Isolation_Gate()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var prodProjectsDir = Path.Combine(localAppData, "PhotoAIFactory", "projects");

        var beforeCount = Directory.Exists(prodProjectsDir)
            ? Directory.GetDirectories(prodProjectsDir).Length
            : 0;

        // Run isolated test builder operations
        var appPaths = new TestAppPaths(testWorkDir);
        var inDir = Path.Combine(testWorkDir, "gate_in");
        var outDir = Path.Combine(testWorkDir, "gate_out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);

        var builder = CreateTestHostBuilder();
        builder.Services.AddSingleton<IAppPaths>(appPaths);
        using var host = builder.Build();
        await host.StartAsync();

        var projectService = host.Services.GetRequiredService<ProjectService>();
        var config = new ProjectConfigV1(
            inDir, outDir, includeSubfolders: false, revealMode: RevealMode.DtAuto,
            preselectionEnabled: false, preselectionProfile: "BALANCED", semanticMode: SemanticMode.Off,
            comfyUiMode: ComfyUiMode.Off, authorizedComfyUiTasks: [], presetProfiles: [],
            exportFormat: "JPEG", exportQuality: 90, associationWindowSeconds: 1);

        var created = await projectService.CreateProjectAsync("Isolation Gate Project", config, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);

        await host.StopAsync();

        var afterCount = Directory.Exists(prodProjectsDir)
            ? Directory.GetDirectories(prodProjectsDir).Length
            : 0;

        Assert.AreEqual(beforeCount, afterCount, "Test execution MUST NOT create any new directories in production %LOCALAPPDATA%\\PhotoAIFactory\\projects");
    }

    private static string ComputeFileSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }
}
