using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;
using PhotoAIFactory.Simulation.Tests.Simulation;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class FoundationVirtualScenarioTests
{
    private string? root;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(
            Path.GetTempPath(),
            "PhotoAIFactory-Simulation",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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
    public async Task Scenario_NoActiveJob_StartPauseAndReopen_UsesRealSqlite()
    {
        var fixture = CreateFixture();
        var created = await fixture.ProjectService.CreateProjectAsync(
            "Virtual factory project",
            CreateConfig(),
            "scenario:create",
            fixture.Clock.GetUtcNow());

        var start = await fixture.Lifecycle.StartOrResumeAsync(
            created.Project.Id,
            "scenario:start",
            created.Project.StateRevision);

        Assert.AreEqual(LifecycleResultStatus.Transitioned, start.Status);
        Assert.AreEqual(ProjectState.Running, start.Project!.State);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var pause = await fixture.Lifecycle.RequestPauseAsync(
            created.Project.Id,
            "scenario:pause",
            start.Project.StateRevision);

        Assert.AreEqual(LifecycleResultStatus.Transitioned, pause.Status);
        Assert.AreEqual(ProjectState.Paused, pause.Project!.State);

        var reopened = await fixture.Store.GetAsync(created.Project.Id);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(ProjectState.Paused, reopened.Project.State);

        var transitions = await fixture.Store.ListTransitionsAsync(created.Project.Id);
        Assert.AreEqual(
            "PROJECT_CREATED|PROJECT_STARTED|PAUSE_REQUESTED|NO_ACTIVE_JOB_SAFE_PAUSE",
            string.Join("|", transitions.Select(item => item.Reason)));
    }

    [TestMethod]
    public async Task Scenario_ActiveJob_PauseWaitsForSafeCompletionAndSurvivesReopen()
    {
        var fixture = CreateFixture();
        var created = await fixture.ProjectService.CreateProjectAsync(
            "Virtual active-job project",
            CreateConfig(),
            "scenario-active:create",
            fixture.Clock.GetUtcNow());

        var running = await fixture.Lifecycle.StartOrResumeAsync(
            created.Project.Id,
            "scenario-active:start",
            created.Project.StateRevision);
        Assert.AreEqual(ProjectState.Running, running.Project!.State);

        fixture.WorkStatus.SetActive(created.Project.Id, true);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));

        var requested = await fixture.Lifecycle.RequestPauseAsync(
            created.Project.Id,
            "scenario-active:pause",
            running.Project.StateRevision);

        Assert.AreEqual(ProjectState.PauseRequested, requested.Project!.State);

        var interruptedView = await fixture.Store.GetAsync(created.Project.Id);
        Assert.IsNotNull(interruptedView);
        Assert.AreEqual(ProjectState.PauseRequested, interruptedView.Project.State);

        fixture.WorkStatus.SetActive(created.Project.Id, false);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var completed = await fixture.Lifecycle.NotifySafeCompletionAsync(
            created.Project.Id,
            "scenario-active:safe-complete",
            requested.Project.StateRevision);

        Assert.AreEqual(ProjectState.Paused, completed.Project!.State);

        var reopened = await fixture.Store.GetAsync(created.Project.Id);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(ProjectState.Paused, reopened.Project.State);
        Assert.AreEqual(completed.Project.StateRevision, reopened.Project.StateRevision);
    }

    [TestMethod]
    public async Task Scenario_FaultPlan_IsExternalToProductionStateMachine()
    {
        var fixture = CreateFixture();
        var faults = new SimulationFaultPlan(
        [
            new("PROJECT_LIFECYCLE", "BEFORE_PAUSE_REQUEST",
                SimulationFaultKind.ProcessCrash, occurrence: 1)
        ]);

        var created = await fixture.ProjectService.CreateProjectAsync(
            "Fault isolation project",
            CreateConfig(),
            "scenario-fault:create",
            fixture.Clock.GetUtcNow());
        var running = await fixture.Lifecycle.StartOrResumeAsync(
            created.Project.Id,
            "scenario-fault:start",
            created.Project.StateRevision);

        var injected = faults.Observe("PROJECT_LIFECYCLE", "BEFORE_PAUSE_REQUEST");
        Assert.IsNotNull(injected);

        var unchanged = await fixture.Store.GetAsync(created.Project.Id);
        Assert.IsNotNull(unchanged);
        Assert.AreEqual(ProjectState.Running, unchanged.Project.State);
        Assert.AreEqual(running.Project!.StateRevision, unchanged.Project.StateRevision);
    }

    private Fixture CreateFixture()
    {
        var dbPath = Path.Combine(root!, "projects", "virtual-project.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var database = new SqliteProjectDatabase(dbPath);
        var store = new SqliteProjectStore(database);
        var factory = new SingleProjectStoreFactory(store);
        var clock = new DeterministicTimeProvider(
            new DateTimeOffset(2026, 8, 19, 3, 0, 0, TimeSpan.Zero));
        var workStatus = new ScriptedProjectWorkStatus();

        return new Fixture(
            store,
            new ProjectService(store),
            new ProjectLifecycleService(factory, workStatus, clock),
            workStatus,
            clock);
    }

    private ProjectConfigV1 CreateConfig() =>
        new(
            Path.Combine(root!, "input"),
            Path.Combine(root!, "output"),
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
            associationWindowSeconds: 30);

    private sealed record Fixture(
        SqliteProjectStore Store,
        ProjectService ProjectService,
        ProjectLifecycleService Lifecycle,
        ScriptedProjectWorkStatus WorkStatus,
        DeterministicTimeProvider Clock);
}
