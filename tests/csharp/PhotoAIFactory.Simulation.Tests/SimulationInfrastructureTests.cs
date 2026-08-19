using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Simulation.Tests.Simulation;

namespace PhotoAIFactory.Simulation.Tests;

[TestClass]
public sealed class SimulationInfrastructureTests
{
    [TestMethod]
    public void DeterministicTimeProvider_AdvancesOnlyForward()
    {
        var start = new DateTimeOffset(2026, 8, 19, 3, 0, 0, TimeSpan.Zero);
        var clock = new DeterministicTimeProvider(start);

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.AreEqual(start.AddSeconds(5), clock.GetUtcNow());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            clock.SetUtcNow(start));
    }

    [TestMethod]
    public void FaultPlan_TriggersExactlyConfiguredOccurrence()
    {
        var plan = new SimulationFaultPlan(
        [
            new("DARKTABLE_PASS2", "AFTER_OUTPUT_BEFORE_CHECKPOINT",
                SimulationFaultKind.ProcessCrash, occurrence: 2)
        ]);

        Assert.IsNull(plan.Observe("darktable_pass2", "after_output_before_checkpoint"));
        var fault = plan.Observe("DARKTABLE_PASS2", "AFTER_OUTPUT_BEFORE_CHECKPOINT");
        Assert.IsNotNull(fault);
        Assert.AreEqual(SimulationFaultKind.ProcessCrash, fault.Kind);
        Assert.AreEqual(2, fault.ObservedOccurrence);
        Assert.IsNull(plan.Observe("DARKTABLE_PASS2", "AFTER_OUTPUT_BEFORE_CHECKPOINT"));
        Assert.AreEqual(3, plan.ObservedCount("DARKTABLE_PASS2", "AFTER_OUTPUT_BEFORE_CHECKPOINT"));
    }

    [TestMethod]
    public async Task FaultPlan_IsThreadSafeAndTriggersOneOccurrenceOnce()
    {
        const int total = 200;
        const int triggerAt = 137;
        var plan = new SimulationFaultPlan(
        [
            new("PUBLISH", "BEFORE_RENAME", SimulationFaultKind.AccessDenied, triggerAt)
        ]);

        var results = await Task.WhenAll(
            Enumerable.Range(0, total)
                .Select(_ => Task.Run(() => plan.Observe("PUBLISH", "BEFORE_RENAME"))));

        Assert.AreEqual(1, results.Count(item => item is not null));
        Assert.AreEqual(total, plan.ObservedCount("PUBLISH", "BEFORE_RENAME"));
        Assert.AreEqual(triggerAt, results.Single(item => item is not null)!.ObservedOccurrence);
    }

    [TestMethod]
    public void ScenarioEventRecorder_ProducesOrderedSerializableEvents()
    {
        var clock = new DeterministicTimeProvider(
            new DateTimeOffset(2026, 8, 19, 3, 0, 0, TimeSpan.Zero));
        var recorder = new ScenarioEventRecorder(clock);

        recorder.Record("project", "created", new Dictionary<string, string?>
        {
            ["project_id"] = "P1"
        });
        clock.Advance(TimeSpan.FromSeconds(1));
        recorder.Record("project", "started");

        var events = recorder.Snapshot();

        Assert.AreEqual("1,2", string.Join(",", events.Select(item => item.Sequence)));
        Assert.IsTrue(events[1].TimestampUtc > events[0].TimestampUtc);
        var json = JsonSerializer.Serialize(events);
        Assert.IsFalse(string.IsNullOrWhiteSpace(json));
        Assert.AreEqual(2, JsonDocument.Parse(json).RootElement.GetArrayLength());
    }
}
