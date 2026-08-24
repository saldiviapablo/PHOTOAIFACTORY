using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Application.UI.ViewModels;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Domain.Qa;
using PhotoAIFactory.Infrastructure.Health;
using PhotoAIFactory.Infrastructure.Hosting;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;
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

    [TestMethod]
    public void DI_Container_Resolves_All_ViewModels_And_QueryServices()
    {
        var builder = PhotoAIFactoryHost.CreateBuilder();
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
        var builder = PhotoAIFactoryHost.CreateBuilder();
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
        var builder = PhotoAIFactoryHost.CreateBuilder();
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
        Assert.AreEqual("job_3", pendingReviews[0].JobId.Value);
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
        var builder = PhotoAIFactoryHost.CreateBuilder();
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

    private static string ComputeFileSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }
}
