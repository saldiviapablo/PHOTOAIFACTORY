using System.Text.Json;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Infrastructure;

var tests = new List<(string Name, Func<Task> Test)>
{
    ("State machine accepts Queued->Processing", () => { Assert(JobStateMachine.CanTransition(JobState.Queued, JobState.Processing)); return Task.CompletedTask; }),
    ("State machine rejects Completed->Processing", () => { Assert(!JobStateMachine.CanTransition(JobState.Completed, JobState.Processing)); return Task.CompletedTask; }),
    ("Terminal states", () => { Assert(JobStateMachine.IsTerminal(JobState.Completed)); Assert(JobStateMachine.IsTerminal(JobState.Cancelled)); return Task.CompletedTask; }),
    ("AI request JSON", () => {
        using var doc = JsonDocument.Parse("{}");
        var r = new AiRequest("v1", "req", "job", "analyze", ["x.jpg"], doc.RootElement.Clone());
        var json = JsonSerializer.Serialize(r, ContractJson.Options);
        Assert(json.Contains("api_version")); Assert(json.Contains("input_paths")); return Task.CompletedTask;
    }),
    ("GPU coordinator serializes leases", async () => {
        var gpu = new GpuResourceCoordinator();
        await using var lease = await gpu.AcquireAsync("python");
        Assert(gpu.CurrentOwner == "python");
    }),
    ("SHA256 known value", async () => {
        var p = Path.GetTempFileName();
        try { await File.WriteAllTextAsync(p, "abc"); Assert(await FileUtilities.Sha256Async(p) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"); }
        finally { File.Delete(p); }
    })
};

var failed = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Count-failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

static void Assert(bool condition)
{
    if (!condition) throw new Exception("Assertion failed");
}
