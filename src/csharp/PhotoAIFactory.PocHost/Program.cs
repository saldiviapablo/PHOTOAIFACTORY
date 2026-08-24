using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Application.Processing;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Provisioning;
using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.Storage;
using PhotoAIFactory.Contracts;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Infrastructure;
using PhotoAIFactory.Infrastructure.Hosting;
using PhotoAIFactory.Infrastructure.Provisioning;
using PhotoAIFactory.Infrastructure.Storage;

static void Usage()
{
    Console.WriteLine("PHOTO AI FACTORY Phase 10 Product Host & Provisioning CLI");
    Console.WriteLine("Commands:");
    Console.WriteLine("  python-health <baseUrl> <token>");
    Console.WriteLine("  comfy-health <baseUrl>");
    Console.WriteLine("  comfy-contract <baseUrl>");
    Console.WriteLine("  darktable-version <darktable-cli-path>");
    Console.WriteLine("  hash <file>");
    Console.WriteLine("  stable <file>");
    Console.WriteLine("  provision <componentId> [releaseDir]");
    Console.WriteLine("  inspect-component <componentId> [releaseDir]");
}

if (args.Length == 0) { Usage(); return 2; }

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "python-health":
        {
            if (args.Length < 3) throw new ArgumentException("python-health requires baseUrl token");
            using var http = new HttpClient { BaseAddress = new Uri(args[1].TrimEnd('/') + "/") };
            var client = new PythonAiClient(http, args[2]);
            var health = await client.GetHealthAsync();
            Console.WriteLine(JsonSerializer.Serialize(health, ContractJson.Options));
            break;
        }
        case "comfy-health":
        {
            if (args.Length < 2) throw new ArgumentException("comfy-health requires baseUrl");
            using var http = new HttpClient { BaseAddress = new Uri(args[1].TrimEnd('/') + "/") };
            var ws = new Uri(args[1].Replace("http://", "ws://").Replace("https://", "wss://").TrimEnd('/') + "/");
            var client = new ComfyUiClient(http, ws);
            Console.WriteLine(await client.GetSystemStatsAsync());
            break;
        }
        case "comfy-contract":
        {
            if (args.Length < 2) throw new ArgumentException("comfy-contract requires baseUrl");
            using var http = new HttpClient { BaseAddress = new Uri(args[1].TrimEnd('/') + "/") };
            var ws = new Uri(args[1].Replace("http://", "ws://").Replace("https://", "wss://").TrimEnd('/') + "/");
            var client = new ComfyUiClient(http, ws);
            var clientId = Guid.NewGuid().ToString("N");
            const string workflow = """
                {
                  "1": { "class_type": "EmptyImage", "inputs": { "width": 64, "height": 64, "batch_size": 1, "color": 0 } },
                  "2": { "class_type": "PreviewImage", "inputs": { "images": ["1", 0] } }
                }
                """;

            _ = await client.GetSystemStatsAsync();
            var promptId = await client.SubmitPromptAsync(workflow, clientId);
            await client.WaitForCompletionAsync(promptId, clientId, TimeSpan.FromSeconds(30));
            var history = await client.GetHistoryAsync(promptId);
            using var historyDoc = JsonDocument.Parse(history);
            if (!historyDoc.RootElement.TryGetProperty(promptId, out _))
                throw new InvalidDataException($"ComfyUI history lacks prompt {promptId}");
            await client.CancelPendingAsync(promptId);
            await client.InterruptAsync();
            Console.WriteLine($"PASS prompt={promptId} endpoints=/system_stats,/prompt,/ws,/history/{{prompt_id}},/queue,/interrupt");
            break;
        }
        case "darktable-version":
        {
            if (args.Length < 2) throw new ArgumentException("darktable-version requires path");
            var client = new DarktableCliAdapter(args[1], new ProcessRunner());
            Console.WriteLine(await client.GetVersionAsync());
            break;
        }
        case "hash":
        {
            if (args.Length < 2) throw new ArgumentException("hash requires file");
            Console.WriteLine(await FileUtilities.Sha256Async(args[1]));
            break;
        }
        case "stable":
        {
            if (args.Length < 2) throw new ArgumentException("stable requires file");
            Console.WriteLine(await FileUtilities.WaitForStableFileAsync(args[1], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20)) ? "STABLE" : "NOT_STABLE");
            break;
        }
        case "provision":
        {
            if (args.Length < 2) throw new ArgumentException("provision requires componentId [releaseDir]");
            var releaseDir = args.Length >= 3 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "release");
            if (!Directory.Exists(releaseDir)) releaseDir = Path.Combine(AppContext.BaseDirectory, "release");
            if (!Directory.Exists(releaseDir)) releaseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "release"));
            var verifier = new ReleaseManifestVerifier(releaseDir);
            var inspector = new DriveInfoStorageSpaceInspector();
            var appPaths = new WindowsAppPaths(Options.Create(new PhotoAIFactoryRuntimeOptions()));
            var service = new ComponentProvisioningService(verifier, inspector, appPaths);
            var progress = new Progress<ComponentProvisionProgress>(p => Console.WriteLine($"[PROVISION] {p.ComponentId} {p.Phase} {p.Percentage:F1}% {p.StatusMessage}"));
            var result = await service.ProvisionAsync(args[1], progress);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            break;
        }
        case "inspect-component":
        {
            if (args.Length < 2) throw new ArgumentException("inspect-component requires componentId [releaseDir]");
            var releaseDir = args.Length >= 3 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "release");
            if (!Directory.Exists(releaseDir)) releaseDir = Path.Combine(AppContext.BaseDirectory, "release");
            if (!Directory.Exists(releaseDir)) releaseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "release"));
            var verifier = new ReleaseManifestVerifier(releaseDir);
            var inspector = new DriveInfoStorageSpaceInspector();
            var appPaths = new WindowsAppPaths(Options.Create(new PhotoAIFactoryRuntimeOptions()));
            var service = new ComponentProvisioningService(verifier, inspector, appPaths);
            var result = await service.InspectAsync(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            break;
        }
        default: Usage(); return 2;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
