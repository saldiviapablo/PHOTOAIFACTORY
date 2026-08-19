using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Infrastructure.Persistence;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;

namespace PhotoAIFactory.Foundation.Tests;

[TestClass]
public sealed class ProjectLifecycleAndConfigServiceTests
{
    [TestMethod]
    public async Task NewProject_StartsStopped()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        Assert.AreEqual(ProjectState.Stopped, created.Project.State);
        Assert.AreEqual(0L, created.Project.StateRevision);
    }

    [TestMethod]
    public async Task InitialState_PersistsAfterReopen()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        var reopened = await new SqliteProjectStore(new SqliteProjectDatabase(scope.DatabasePath))
            .GetAsync(created.Project.Id);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(ProjectState.Stopped, reopened.Project.State);
        Assert.AreEqual(0L, reopened.Project.StateRevision);
    }

    [TestMethod]
    public async Task Start_StoppedToRunning()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        var result = await scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "start");
        Assert.AreEqual(LifecycleResultStatus.Transitioned, result.Status);
        Assert.AreEqual(ProjectState.Running, result.Project!.State);
    }

    [TestMethod]
    public async Task Pause_RunningWithActiveJob_GoesPauseRequested()
    {
        using var scope = new LifecycleScope { HasActiveJob = true };
        var created = await scope.CreateAndStartAsync();
        var result = await scope.Lifecycle.RequestPauseAsync(created.Project.Id, "pause");
        Assert.AreEqual(ProjectState.PauseRequested, result.Project!.State);
        Assert.AreEqual(2L, result.Project.StateRevision);
    }

    [TestMethod]
    public async Task PauseRequested_AfterSafeJobCompletion_GoesPaused()
    {
        using var scope = new LifecycleScope { HasActiveJob = true };
        var created = await scope.CreateAndStartAsync();
        await scope.Lifecycle.RequestPauseAsync(created.Project.Id, "pause");
        var result = await scope.Lifecycle.NotifySafeCompletionAsync(created.Project.Id, "safe-pause");
        Assert.AreEqual(ProjectState.Paused, result.Project!.State);
    }

    [TestMethod]
    public async Task Pause_NoActiveJob_ReachesPaused()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAndStartAsync();
        var result = await scope.Lifecycle.RequestPauseAsync(created.Project.Id, "pause");
        Assert.AreEqual(ProjectState.Paused, result.Project!.State);
        Assert.AreEqual(4, (await scope.Store.ListTransitionsAsync(created.Project.Id)).Count);
    }

    [TestMethod]
    public async Task Pause_WhenPaused_IsIdempotent()
    {
        using var scope = new LifecycleScope();
        var paused = await scope.CreatePausedAsync();
        var before = await scope.Store.ListTransitionsAsync(paused.Project.Id);
        var result = await scope.Lifecycle.RequestPauseAsync(paused.Project.Id, "pause-again");
        Assert.AreEqual(LifecycleResultStatus.AlreadyInDesiredState, result.Status);
        Assert.AreEqual(before.Count, (await scope.Store.ListTransitionsAsync(paused.Project.Id)).Count);
    }

    [TestMethod]
    public async Task Pause_WhenPauseRequested_IsIdempotent()
    {
        using var scope = new LifecycleScope { HasActiveJob = true };
        var created = await scope.CreateAndStartAsync();
        await scope.Lifecycle.RequestPauseAsync(created.Project.Id, "pause");
        var result = await scope.Lifecycle.RequestPauseAsync(created.Project.Id, "pause-again");
        Assert.AreEqual(LifecycleResultStatus.AlreadyInDesiredState, result.Status);
        Assert.AreEqual(3, (await scope.Store.ListTransitionsAsync(created.Project.Id)).Count);
    }

    [TestMethod] public void Paused_CannotDispatchNextJob() => Assert.IsFalse(ProjectDispatchGuard.CanDispatchNextJob(ProjectState.Paused));
    [TestMethod] public void PauseRequested_CannotDispatchNextJob() => Assert.IsFalse(ProjectDispatchGuard.CanDispatchNextJob(ProjectState.PauseRequested));
    [TestMethod] public void Running_CanDispatchNextJob() => Assert.IsTrue(ProjectDispatchGuard.CanDispatchNextJob(ProjectState.Running));
    [TestMethod] public void Stopped_CannotDispatchNextJob() => Assert.IsFalse(ProjectDispatchGuard.CanDispatchNextJob(ProjectState.Stopped));
    [TestMethod] public void StopRequested_CannotDispatchNextJob() => Assert.IsFalse(ProjectDispatchGuard.CanDispatchNextJob(ProjectState.StopRequested));
    [TestMethod] public void BlockedStorage_CannotDispatchNextJob() => Assert.IsFalse(ProjectDispatchGuard.CanDispatchNextJob(ProjectState.BlockedStorage));
    [TestMethod] public void ComponentUnhealthy_CannotDispatchNextJob() => Assert.IsFalse(ProjectDispatchGuard.CanDispatchNextJob(ProjectState.ComponentUnhealthy));

    [TestMethod]
    public async Task Resume_PausedToRunning()
    {
        using var scope = new LifecycleScope();
        var paused = await scope.CreatePausedAsync();
        var result = await scope.Lifecycle.StartOrResumeAsync(paused.Project.Id, "resume");
        Assert.AreEqual(ProjectState.Running, result.Project!.State);
        Assert.AreEqual(LifecycleResultStatus.Transitioned, result.Status);
    }

    [TestMethod]
    public async Task Resume_WhenRunning_IsIdempotent()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAndStartAsync();
        var before = await scope.Store.ListTransitionsAsync(created.Project.Id);
        var result = await scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "resume-again");
        Assert.AreEqual(LifecycleResultStatus.AlreadyInDesiredState, result.Status);
        Assert.AreEqual(before.Count, (await scope.Store.ListTransitionsAsync(created.Project.Id)).Count);
    }

    [TestMethod]
    public async Task Stop_RunningWithActiveJob_GoesStopRequested()
    {
        using var scope = new LifecycleScope { HasActiveJob = true };
        var created = await scope.CreateAndStartAsync();
        var result = await scope.Lifecycle.RequestStopAsync(created.Project.Id, "stop");
        Assert.AreEqual(ProjectState.StopRequested, result.Project!.State);
    }

    [TestMethod]
    public async Task StopRequested_AfterSafeCompletion_GoesStopped()
    {
        using var scope = new LifecycleScope { HasActiveJob = true };
        var created = await scope.CreateAndStartAsync();
        await scope.Lifecycle.RequestStopAsync(created.Project.Id, "stop");
        var result = await scope.Lifecycle.NotifySafeCompletionAsync(created.Project.Id, "safe-stop");
        Assert.AreEqual(ProjectState.Stopped, result.Project!.State);
    }

    [TestMethod]
    public async Task Stop_FromPaused_ReachesStopped()
    {
        using var scope = new LifecycleScope();
        var paused = await scope.CreatePausedAsync();
        var result = await scope.Lifecycle.RequestStopAsync(paused.Project.Id, "stop");
        Assert.AreEqual(ProjectState.Stopped, result.Project!.State);
    }

    [TestMethod]
    public async Task Stop_FromPauseRequested_ReachesStopped()
    {
        using var scope = new LifecycleScope { HasActiveJob = true };
        var created = await scope.CreateAndStartAsync();
        await scope.Lifecycle.RequestPauseAsync(created.Project.Id, "pause");
        scope.HasActiveJob = false;
        var result = await scope.Lifecycle.RequestStopAsync(created.Project.Id, "stop-after-pause-request");
        Assert.AreEqual(ProjectState.Stopped, result.Project!.State);
    }

    [TestMethod]
    public async Task Stop_WhenStopRequested_IsIdempotent()
    {
        using var scope = new LifecycleScope { HasActiveJob = true };
        var created = await scope.CreateAndStartAsync();
        await scope.Lifecycle.RequestStopAsync(created.Project.Id, "stop");
        var before = await scope.Store.ListTransitionsAsync(created.Project.Id);
        var repeated = await scope.Lifecycle.RequestStopAsync(created.Project.Id, "stop-again");
        Assert.AreEqual(LifecycleResultStatus.AlreadyInDesiredState, repeated.Status);
        Assert.AreEqual(before.Count, (await scope.Store.ListTransitionsAsync(created.Project.Id)).Count);
    }

    [TestMethod]
    public async Task Stop_WhenStopped_IsIdempotent()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        var result = await scope.Lifecycle.RequestStopAsync(created.Project.Id, "stop-already-stopped");
        Assert.AreEqual(LifecycleResultStatus.AlreadyInDesiredState, result.Status);
        Assert.AreEqual(1, (await scope.Store.ListTransitionsAsync(created.Project.Id)).Count);
    }

    [TestMethod]
    public async Task InvalidTransition_IsRejected()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        var result = await scope.Lifecycle.RequestPauseAsync(created.Project.Id, "invalid-pause");
        Assert.AreEqual(LifecycleResultStatus.InvalidTransition, result.Status);
        Assert.IsFalse(ProjectStateMachine.CanTransition(ProjectState.BlockedStorage, ProjectState.Running));
        Assert.IsFalse(ProjectStateMachine.CanTransition(ProjectState.ComponentUnhealthy, ProjectState.Stopped));
    }

    [TestMethod]
    public async Task Transition_PersistsStateAndAuditAtomically()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        await scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "atomic-start");
        var reopened = await scope.Store.GetAsync(created.Project.Id);
        var audit = await scope.Store.ListTransitionsAsync(created.Project.Id);
        Assert.AreEqual(ProjectState.Running, reopened!.Project.State);
        Assert.AreEqual(reopened.Project.StateRevision, audit[^1].StateRevision);
        Assert.AreEqual(ProjectState.Running, audit[^1].ToState);
    }

    [TestMethod]
    public async Task TransitionFailure_RollsBackStateAndAudit()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        await scope.ExecuteAsync("""
            CREATE TRIGGER injected_audit_failure
            BEFORE INSERT ON project_state_transitions
            WHEN NEW.state_revision > 0
            BEGIN SELECT RAISE(ABORT, 'injected audit failure'); END;
            """);
        await ThrowsAsync<SqliteException>(() =>
            scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "failing-start"));
        var reopened = await scope.Store.GetAsync(created.Project.Id);
        Assert.AreEqual(ProjectState.Stopped, reopened!.Project.State);
        Assert.AreEqual(1, (await scope.Store.ListTransitionsAsync(created.Project.Id)).Count);
    }

    [TestMethod]
    public async Task StateUpdateFailure_LeavesStateAndAuditUnchanged()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        await scope.ExecuteAsync("""
            CREATE TRIGGER injected_state_failure
            BEFORE UPDATE OF project_state ON projects
            BEGIN SELECT RAISE(ABORT, 'injected state failure'); END;
            """);
        await ThrowsAsync<SqliteException>(() =>
            scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "failing-state-update"));
        var reopened = await scope.Store.GetAsync(created.Project.Id);
        Assert.AreEqual(ProjectState.Stopped, reopened!.Project.State);
        Assert.AreEqual(1, (await scope.Store.ListTransitionsAsync(created.Project.Id)).Count);
    }

    [TestMethod]
    public async Task ConcurrentTransitions_DoNotLoseUpdates()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        var results = await Task.WhenAll(
            scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "concurrent-start-a", 0),
            scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "concurrent-start-b", 0));
        Assert.AreEqual(1, results.Count(result => result.Status == LifecycleResultStatus.Transitioned));
        var reopened = await scope.Store.GetAsync(created.Project.Id);
        Assert.AreEqual(ProjectState.Running, reopened!.Project.State);
        Assert.AreEqual(1L, reopened.Project.StateRevision);
    }

    [TestMethod]
    public async Task StaleStateRevision_IsRejected()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        await scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "start");
        var result = await scope.Store.TryTransitionAsync(
            created.Project.Id, ProjectState.Running, 0, ProjectState.PauseRequested,
            "STALE", "stale", scope.Clock.GetUtcNow());
        Assert.AreEqual(TransitionWriteStatus.ConcurrencyConflict, result.Status);
    }

    [TestMethod]
    public async Task Lifecycle_ReopensCorrectly()
    {
        using var scope = new LifecycleScope { HasActiveJob = true };
        var created = await scope.CreateAndStartAsync();
        var requested = await scope.Lifecycle.RequestPauseAsync(created.Project.Id, "pause");
        var reopenedStore = new SqliteProjectStore(new SqliteProjectDatabase(scope.DatabasePath));
        var reopenedLifecycle = new ProjectLifecycleService(
            new FixedStoreFactory(reopenedStore), scope.WorkStatus, scope.Clock);
        var reopened = await reopenedStore.GetAsync(created.Project.Id);
        Assert.AreEqual(ProjectState.PauseRequested, reopened!.Project.State);
        Assert.AreEqual(requested.Project!.StateRevision, reopened.Project.StateRevision);
        var completed = await reopenedLifecycle.NotifySafeCompletionAsync(created.Project.Id, "safe-after-reopen");
        Assert.AreEqual(ProjectState.Paused, completed.Project!.State);
    }

    [TestMethod]
    public async Task ConfigChange_WhilePaused_CreatesNewVersion()
    {
        using var scope = new LifecycleScope();
        var paused = await scope.CreatePausedAsync();
        var result = await scope.ConfigService.ApplyAsync(
            paused.Project.Id, scope.Config(exportQuality: 91), paused.LatestConfig.Id, "config-2");
        Assert.AreEqual(ConfigChangeStatus.Created, result.Status);
        Assert.AreEqual(2, result.ConfigVersion!.VersionNumber);
    }

    [TestMethod]
    public async Task FolderChange_WhilePaused_CreatesNewVersion()
    {
        using var scope = new LifecycleScope();
        var paused = await scope.CreatePausedAsync();
        var result = await scope.ConfigService.ApplyAsync(
            paused.Project.Id, scope.Config(inputSuffix: "input-2", outputSuffix: "output-2"),
            paused.LatestConfig.Id, "folder-change");
        var reopened = await scope.Store.GetAsync(paused.Project.Id);
        Assert.AreEqual(ConfigChangeStatus.Created, result.Status);
        Assert.AreEqual(ProjectState.Paused, reopened!.Project.State);
        Assert.AreEqual(2, reopened.ConfigVersions.Count);
        Assert.AreEqual(ProjectConfigCanonicalizer.ComputeSha256(result.ConfigVersion!.CanonicalJson), result.ConfigVersion.Sha256);
    }

    [TestMethod]
    public async Task FolderChange_WhileRunning_IsRejected() =>
        await AssertConfigRejectedInStateAsync(ProjectState.Running);

    [TestMethod]
    public async Task FolderChange_WhileStopped_IsRejected() =>
        await AssertConfigRejectedInStateAsync(ProjectState.Stopped);

    [TestMethod]
    public async Task ConfigChange_WhilePauseRequested_IsRejected() =>
        await AssertConfigRejectedInStateAsync(ProjectState.PauseRequested);

    [TestMethod]
    public async Task ConfigChange_WhileStopRequested_IsRejected() =>
        await AssertConfigRejectedInStateAsync(ProjectState.StopRequested);

    [TestMethod]
    public async Task ConfigChange_WhileBlockedStorage_IsRejected() =>
        await AssertConfigRejectedInStateAsync(ProjectState.BlockedStorage);

    [TestMethod]
    public async Task ConfigChange_WhileComponentUnhealthy_IsRejected() =>
        await AssertConfigRejectedInStateAsync(ProjectState.ComponentUnhealthy);

    [TestMethod]
    public async Task ConfigChange_DoesNotMutatePreviousVersion()
    {
        using var scope = new LifecycleScope();
        var paused = await scope.CreatePausedAsync();
        var before = await scope.ScalarAsync<string>(
            "SELECT config_json FROM project_config_versions WHERE version_number=1;");
        await scope.ConfigService.ApplyAsync(
            paused.Project.Id, scope.Config(exportQuality: 82), paused.LatestConfig.Id, "config-2");
        var after = await scope.ScalarAsync<string>(
            "SELECT config_json FROM project_config_versions WHERE version_number=1;");
        Assert.AreEqual(before, after);
    }

    [TestMethod]
    public async Task SameConfig_WhenPaused_DoesNotCreateDuplicateVersion()
    {
        using var scope = new LifecycleScope();
        var paused = await scope.CreatePausedAsync();
        var result = await scope.ConfigService.ApplyAsync(
            paused.Project.Id, scope.Config(), paused.LatestConfig.Id, "same-config");
        Assert.AreEqual(ConfigChangeStatus.Unchanged, result.Status);
        Assert.AreEqual(1, (await scope.Store.ListAsync(paused.Project.Id)).Count);
    }

    [TestMethod]
    public async Task ConcurrentConfigChanges_AreSerialized()
    {
        using var scope = new LifecycleScope();
        var paused = await scope.CreatePausedAsync();
        var results = await Task.WhenAll(
            scope.ConfigService.ApplyAsync(paused.Project.Id, scope.Config(exportQuality: 81), paused.LatestConfig.Id, "config-a"),
            scope.ConfigService.ApplyAsync(paused.Project.Id, scope.Config(exportQuality: 82), paused.LatestConfig.Id, "config-b"));
        Assert.AreEqual(1, results.Count(result => result.Status == ConfigChangeStatus.Created));
        Assert.AreEqual(1, results.Count(result => result.Status == ConfigChangeStatus.VersionConflict));
        Assert.AreEqual(2, (await scope.Store.ListAsync(paused.Project.Id)).Count);
    }

    [TestMethod]
    public async Task StaleExpectedConfigVersion_IsConflict()
    {
        using var scope = new LifecycleScope();
        var paused = await scope.CreatePausedAsync();
        await scope.ConfigService.ApplyAsync(
            paused.Project.Id, scope.Config(exportQuality: 81), paused.LatestConfig.Id, "config-2");
        var stale = await scope.ConfigService.ApplyAsync(
            paused.Project.Id, scope.Config(exportQuality: 82), paused.LatestConfig.Id, "config-3");
        Assert.AreEqual(ConfigChangeStatus.VersionConflict, stale.Status);
        Assert.AreEqual(2, (await scope.Store.ListAsync(paused.Project.Id)).Count);
    }

    [TestMethod]
    public async Task ConfigHash_RemainsValidAfterLifecycleChanges()
    {
        using var scope = new LifecycleScope();
        var paused = await scope.CreatePausedAsync();
        var changed = await scope.ConfigService.ApplyAsync(
            paused.Project.Id, scope.Config(exportQuality: 77), paused.LatestConfig.Id, "config-2");
        await scope.Lifecycle.StartOrResumeAsync(paused.Project.Id, "resume");
        var reopened = await scope.Store.GetAsync(paused.Project.Id);
        Assert.AreEqual(changed.ConfigVersion!.Sha256,
            ProjectConfigCanonicalizer.ComputeSha256(reopened!.LatestConfig.CanonicalJson));
    }

    [TestMethod]
    public async Task Migration002_AppliesOnce()
    {
        using var scope = new LifecycleScope();
        await scope.Database.InitializeAsync();
        await scope.Database.InitializeAsync();
        Assert.AreEqual(1L, await scope.ScalarAsync<long>(
            "SELECT count(*) FROM schema_migrations WHERE version=2;"));
    }

    [TestMethod]
    public async Task Migration002_BackfillsExistingProjectAsStopped()
    {
        using var scope = new LifecycleScope();
        var v1Database = new SqliteProjectDatabase(scope.DatabasePath, [MigrationCatalog.All[0]]);
        await v1Database.InitializeAsync();
        var canonical = ProjectConfigCanonicalizer.Serialize(scope.Config());
        await scope.ExecuteAsync("""
            INSERT INTO projects(project_id,name,creation_operation_key,created_at_utc,updated_at_utc)
            VALUES('legacy-project','Legacy','legacy-create',$now,$now);
            INSERT INTO project_config_versions(
                config_version_id,project_id,version_number,schema_version,config_json,
                config_sha256,operation_key,created_at_utc)
            VALUES('legacy-config','legacy-project',1,1,$json,$hash,'legacy-config-op',$now);
            """,
            ("$now", LifecycleScope.InitialTime.ToString("O", CultureInfo.InvariantCulture)),
            ("$json", canonical),
            ("$hash", ProjectConfigCanonicalizer.ComputeSha256(canonical)));
        await scope.Database.InitializeAsync();
        Assert.AreEqual("STOPPED", await scope.ScalarAsync<string>(
            "SELECT project_state FROM projects WHERE project_id='legacy-project';"));
        Assert.AreEqual(1L, await scope.ScalarAsync<long>(
            "SELECT count(*) FROM project_state_transitions WHERE project_id='legacy-project' AND state_revision=0;"));
    }

    [TestMethod]
    public async Task Migration002_HashDriftRejected()
    {
        using var scope = new LifecycleScope();
        await scope.Database.InitializeAsync();
        await scope.ExecuteAsync(
            "UPDATE schema_migrations SET migration_sha256=$hash WHERE version=2;",
            ("$hash", new string('0', 64)));
        await ThrowsAsync<MigrationIntegrityException>(() => scope.Database.InitializeAsync());
    }

    [TestMethod]
    public async Task Migration002_FailureRollsBack()
    {
        using var scope = new LifecycleScope();
        var v1 = MigrationCatalog.All[0];
        await new SqliteProjectDatabase(scope.DatabasePath, [v1]).InitializeAsync();
        var failing = new SqliteMigration(2, "project_lifecycle", "ALTER TABLE projects ADD COLUMN project_state TEXT; INVALID SQL;");
        await ThrowsAsync<SqliteException>(() =>
            new SqliteProjectDatabase(scope.DatabasePath, [v1, failing]).InitializeAsync());
        Assert.AreEqual(0L, await scope.ScalarAsync<long>(
            "SELECT count(*) FROM pragma_table_info('projects') WHERE name='project_state';"));
        Assert.AreEqual(1L, await scope.ScalarAsync<long>("SELECT count(*) FROM schema_migrations;"));
    }

    [TestMethod]
    public async Task ExistingMigration001_RemainsIntact()
    {
        using var scope = new LifecycleScope();
        await scope.Database.InitializeAsync();
        Assert.AreEqual(MigrationCatalog.All[0].Sha256, await scope.ScalarAsync<string>(
            "SELECT migration_sha256 FROM schema_migrations WHERE version=1;"));
    }

    [TestMethod]
    public async Task ProjectStateTimestamp_UsesInjectedTimeProvider()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        scope.Clock.SetUtcNow(LifecycleScope.InitialTime.AddHours(3));
        await scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "timed-start");
        var reopened = await scope.Store.GetAsync(created.Project.Id);
        Assert.AreEqual(scope.Clock.GetUtcNow(), reopened!.Project.StateChangedAtUtc);
        Assert.AreEqual(scope.Clock.GetUtcNow(), (await scope.Store.ListTransitionsAsync(created.Project.Id))[^1].OccurredAtUtc);
    }

    [TestMethod]
    public async Task StateAudit_IsAppendOnly()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAndStartAsync();
        await ThrowsAsync<SqliteException>(() => scope.ExecuteAsync(
            "UPDATE project_state_transitions SET reason='changed' WHERE project_id=$id;",
            ("$id", created.Project.Id.Value)));
        await ThrowsAsync<SqliteException>(() => scope.ExecuteAsync(
            "DELETE FROM project_state_transitions WHERE project_id=$id;",
            ("$id", created.Project.Id.Value)));
    }

    [TestMethod]
    public async Task RepeatedOperationId_DoesNotDuplicateTransition()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        await scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "same-operation");
        var repeated = await scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "same-operation");
        Assert.AreEqual(LifecycleResultStatus.AlreadyInDesiredState, repeated.Status);
        Assert.AreEqual(2, (await scope.Store.ListTransitionsAsync(created.Project.Id)).Count);
    }

    [TestMethod]
    public async Task ExistingSlice1And2Tests_StillPass()
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        var reopened = await scope.Store.GetAsync(created.Project.Id);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(created.LatestConfig.Sha256, reopened.LatestConfig.Sha256);
        Assert.AreEqual("ok", (await scope.ScalarAsync<string>("PRAGMA integrity_check;")).ToLowerInvariant());
    }

    [TestMethod]
    public async Task InvalidPathConfig_WhilePaused_DoesNotWrite()
    {
        using var scope = new LifecycleScope();
        var paused = await scope.CreatePausedAsync();
        Assert.ThrowsExactly<ArgumentException>(() => scope.Config(
            inputSuffix: "unsafe", outputSuffix: Path.Combine("unsafe", "nested")));
        Assert.AreEqual(1, (await scope.Store.ListAsync(paused.Project.Id)).Count);
    }

    [TestMethod]
    public async Task ReusedOperationId_ForDifferentTransition_IsConflict()
    {
        using var scope = new LifecycleScope { HasActiveJob = true };
        var created = await scope.CreateAsync();
        await scope.Lifecycle.StartOrResumeAsync(created.Project.Id, "reused-operation");
        var conflict = await scope.Lifecycle.RequestStopAsync(created.Project.Id, "reused-operation");
        Assert.AreEqual(LifecycleResultStatus.OperationConflict, conflict.Status);
        Assert.AreEqual(2, (await scope.Store.ListTransitionsAsync(created.Project.Id)).Count);
    }

    private static async Task AssertConfigRejectedInStateAsync(ProjectState state)
    {
        using var scope = new LifecycleScope();
        var created = await scope.CreateAsync();
        await scope.ForceStateAsync(created.Project.Id, state);
        var result = await scope.ConfigService.ApplyAsync(
            created.Project.Id, scope.Config(inputSuffix: "changed-input", outputSuffix: "changed-output"),
            created.LatestConfig.Id, "rejected-config");
        Assert.AreEqual(ConfigChangeStatus.RejectedProjectNotPaused, result.Status);
        Assert.AreEqual(state, result.ProjectState);
        Assert.AreEqual(1, (await scope.Store.ListAsync(created.Project.Id)).Count);
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }

        Assert.Fail($"Expected {typeof(TException).Name}.");
        throw new InvalidOperationException("Unreachable.");
    }

    private sealed class LifecycleScope : IDisposable
    {
        private readonly string root;

        public LifecycleScope()
        {
            root = Path.Combine(Path.GetTempPath(), "PhotoAIFactory.Slice3.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            DatabasePath = Path.Combine(root, "db", "project.db");
            Database = new SqliteProjectDatabase(DatabasePath);
            Store = new SqliteProjectStore(Database);
            Factory = new FixedStoreFactory(Store);
            WorkStatus = new MutableWorkStatus();
            Clock = new ControlledTimeProvider(InitialTime);
            Lifecycle = new ProjectLifecycleService(Factory, WorkStatus, Clock);
            ConfigService = new ConfigService(Factory, Clock);
            Projects = new ProjectService(Factory);
        }

        public static DateTimeOffset InitialTime => new(2026, 8, 18, 15, 0, 0, TimeSpan.Zero);
        public string DatabasePath { get; }
        public SqliteProjectDatabase Database { get; }
        public SqliteProjectStore Store { get; }
        public FixedStoreFactory Factory { get; }
        public MutableWorkStatus WorkStatus { get; }
        public ControlledTimeProvider Clock { get; }
        public ProjectLifecycleService Lifecycle { get; }
        public ConfigService ConfigService { get; }
        public ProjectService Projects { get; }

        public bool HasActiveJob
        {
            get => WorkStatus.Active;
            set => WorkStatus.Active = value;
        }

        public ProjectConfigV1 Config(
            int exportQuality = 90,
            string inputSuffix = "input",
            string outputSuffix = "output") =>
            new(
                Path.Combine(root, inputSuffix),
                Path.Combine(root, outputSuffix),
                true,
                RevealMode.DtAuto,
                true,
                "technical-standard",
                SemanticMode.Standard,
                ComfyUiMode.Auto,
                ["denoise", "upscale"],
                ["base", "portrait"],
                "jpeg",
                exportQuality,
                30);

        public Task<ProjectSnapshot> CreateAsync() =>
            Projects.CreateProjectAsync("Slice 3 Test", Config(), "create-project", InitialTime);

        public async Task<ProjectSnapshot> CreateAndStartAsync()
        {
            var created = await CreateAsync();
            await Lifecycle.StartOrResumeAsync(created.Project.Id, "start");
            return created;
        }

        public async Task<ProjectSnapshot> CreatePausedAsync()
        {
            var created = await CreateAndStartAsync();
            WorkStatus.Active = false;
            await Lifecycle.RequestPauseAsync(created.Project.Id, "pause");
            return (await Store.GetAsync(created.Project.Id))!;
        }

        public Task ForceStateAsync(ProjectId projectId, ProjectState state) => ExecuteAsync(
            """
            UPDATE projects
            SET project_state=$state, state_revision=state_revision+1, state_changed_at_utc=$changed
            WHERE project_id=$id;
            """,
            ("$state", StateToken(state)),
            ("$changed", Clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)),
            ("$id", projectId.Value));

        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await Database.OpenConfiguredConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await Database.OpenConfiguredConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            var value = await command.ExecuteScalarAsync();
            return (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private static string StateToken(ProjectState state) => state switch
        {
            ProjectState.Running => "RUNNING",
            ProjectState.PauseRequested => "PAUSE_REQUESTED",
            ProjectState.Paused => "PAUSED",
            ProjectState.StopRequested => "STOP_REQUESTED",
            ProjectState.Stopped => "STOPPED",
            ProjectState.BlockedStorage => "BLOCKED_STORAGE",
            ProjectState.ComponentUnhealthy => "COMPONENT_UNHEALTHY",
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }

    private sealed class FixedStoreFactory(IProjectStore store) : IProjectStoreFactory
    {
        public IProjectStore Open(ProjectId projectId) => store;
    }

    private sealed class MutableWorkStatus : IProjectWorkStatus
    {
        public bool Active { get; set; }
        public Task<bool> HasActiveJobAsync(ProjectId projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Active);
    }

    private sealed class ControlledTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;
        public override DateTimeOffset GetUtcNow() => current;
        public void SetUtcNow(DateTimeOffset value) => current = value;
    }
}
