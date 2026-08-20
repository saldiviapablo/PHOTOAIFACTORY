using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Infrastructure;
using PhotoAIFactory.Infrastructure.Analysis;
using PhotoAIFactory.Infrastructure.Hosting;
using PhotoAIFactory.Infrastructure.Logging;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;

namespace PhotoAIFactory.Foundation.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HostCompositionTests
{
    private static readonly object CurrentDirectorySync = new();

    [TestMethod]
    public async Task Host_BuildsSuccessfully()
    {
        await using var fixture = new HostFixture();
        Assert.IsNotNull(fixture.Host.Services);
    }

    [TestMethod]
    public async Task Host_StartsAndStopsCleanly()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        await fixture.StopAsync();
        Assert.IsTrue(File.Exists(fixture.LogPath));
    }

    [TestMethod]
    public async Task RequiredServices_AreResolvable()
    {
        await using var fixture = new HostFixture();
        var services = fixture.Host.Services;
        Assert.IsNotNull(services.GetRequiredService<IAppPaths>());
        Assert.IsNotNull(services.GetRequiredService<IRuntimeDirectoryInitializer>());
        Assert.IsNotNull(services.GetRequiredService<IRuntimeSession>());
        Assert.IsNotNull(services.GetRequiredService<JsonLinesLoggerProvider>());
        Assert.IsNotNull(services.GetRequiredService<IProjectStoreFactory>());
        Assert.IsNotNull(services.GetRequiredService<ProjectService>());
        Assert.IsNotNull(services.GetRequiredService<TimeProvider>());
        Assert.IsNotNull(services.GetRequiredService<IProjectWorkStatus>());
        Assert.IsNotNull(services.GetRequiredService<ProjectLifecycleService>());
        Assert.IsNotNull(services.GetRequiredService<ConfigService>());
        Assert.IsNotNull(services.GetRequiredService<ProcessRunner>());
        Assert.IsNotNull(services.GetRequiredService<ComponentLockReader>());
        Assert.IsNotNull(services.GetRequiredService<IGpuResourceCoordinator>());
    }

    [TestMethod]
    public async Task InfrastructurePorts_ResolveToExpectedImplementations()
    {
        await using var fixture = new HostFixture();
        Assert.IsInstanceOfType<WindowsAppPaths>(fixture.Host.Services.GetRequiredService<IAppPaths>());
        Assert.IsInstanceOfType<RuntimeDirectoryInitializer>(
            fixture.Host.Services.GetRequiredService<IRuntimeDirectoryInitializer>());
        Assert.IsInstanceOfType<SqliteProjectStoreFactory>(
            fixture.Host.Services.GetRequiredService<IProjectStoreFactory>());
        Assert.IsInstanceOfType<NoActiveProjectWorkStatus>(
            fixture.Host.Services.GetRequiredService<IProjectWorkStatus>());
        Assert.IsInstanceOfType<GpuResourceCoordinator>(
            fixture.Host.Services.GetRequiredService<IGpuResourceCoordinator>());
    }

    [TestMethod]
    public void Domain_HasNoInfrastructureDependency()
    {
        var references = typeof(Project).Assembly.GetReferencedAssemblies().Select(item => item.Name).ToArray();
        Assert.IsFalse(references.Any(name => name is not null && name.StartsWith("PhotoAIFactory.Infrastructure", StringComparison.Ordinal)));
        Assert.IsFalse(references.Any(name => name is not null && name.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal)));
        Assert.IsFalse(references.Any(name => name is not null && name.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task InvalidRuntimeOptions_FailStartup()
    {
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            [$"{PhotoAIFactoryRuntimeOptions.SectionName}:RootPath"] = "relative-root"
        });
        builder.AddPhotoAIFactoryFoundation();
        await ExpectExceptionAsync<OptionsValidationException>(async () =>
        {
            using var host = builder.Build();
            await host.StartAsync();
        });
    }

    [TestMethod]
    public async Task ValidRuntimeOptions_PassStartup()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        Assert.AreEqual(fixture.RuntimeRoot, fixture.Host.Services.GetRequiredService<IAppPaths>().RootDirectory);
    }

    [TestMethod]
    public void AppPaths_UseConfiguredTestRoot()
    {
        using var root = new TemporaryRoot();
        var expected = Path.Combine(root.Path, "configured");
        var paths = CreatePaths(expected);
        Assert.AreEqual(Path.GetFullPath(expected), paths.RootDirectory);
        Assert.AreEqual(Path.Combine(expected, "projects"), paths.ProjectsDirectory);
    }

    [TestMethod]
    public void AppPaths_DoNotDependOnCurrentDirectory()
    {
        lock (CurrentDirectorySync)
        {
            using var firstRoot = new TemporaryRoot();
            using var secondRoot = new TemporaryRoot();
            var original = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = firstRoot.Path;
                var first = CreatePaths(root: null).RootDirectory;
                Environment.CurrentDirectory = secondRoot.Path;
                var second = CreatePaths(root: null).RootDirectory;
                Assert.AreEqual(first, second);
                Assert.IsTrue(first.StartsWith(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                Environment.CurrentDirectory = original;
            }
        }
    }

    [TestMethod]
    public void AppPaths_UnicodeAndSpaces()
    {
        using var root = new TemporaryRoot();
        var configured = Path.Combine(root.Path, "Raíz Ñ 日本 con espacios") + Path.DirectorySeparatorChar;
        var paths = CreatePaths(configured);
        Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured)), paths.RootDirectory);
        Assert.IsTrue(paths.LogsDirectory.Contains("Raíz Ñ 日本 con espacios", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RuntimeDirectoryInitialization_IsIdempotent()
    {
        using var root = new TemporaryRoot();
        var paths = CreatePaths(Path.Combine(root.Path, "runtime"));
        var initializer = new RuntimeDirectoryInitializer(paths);
        await initializer.InitializeAsync();
        var marker = Path.Combine(paths.WorkDirectory, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");
        await initializer.InitializeAsync();
        Assert.AreEqual("keep", await File.ReadAllTextAsync(marker));
        Assert.IsTrue(Directory.Exists(paths.ComponentsDirectory));
    }

    [TestMethod]
    public async Task RuntimeDirectoryFailure_IsExplicit()
    {
        using var root = new TemporaryRoot();
        var blockedRoot = Path.Combine(root.Path, "blocked");
        await File.WriteAllTextAsync(blockedRoot, "file blocks directory");
        await using var fixture = new HostFixture(blockedRoot);
        await ExpectExceptionAsync<IOException>(() => fixture.StartAsync());
    }

    [TestMethod]
    public async Task ProjectConfig_IsNotOverriddenByRuntimeOptions()
    {
        await using var fixture = new HostFixture();
        var input = Path.Combine(fixture.BaseRoot, "project-input");
        var output = Path.Combine(fixture.BaseRoot, "project-output");
        var config = CreateProjectConfig(input, output);
        var before = ProjectConfigCanonicalizer.Serialize(config);
        await fixture.StartAsync();
        var runtime = fixture.Host.Services.GetRequiredService<IOptions<PhotoAIFactoryRuntimeOptions>>().Value;
        Assert.AreEqual(fixture.RuntimeRoot, runtime.RootPath);
        Assert.AreEqual(before, ProjectConfigCanonicalizer.Serialize(config));
        Assert.AreEqual(Path.GetFullPath(input), config.InputFolder);
    }

    [TestMethod]
    public async Task SqliteWriteCoordinator_IsSharedForSameDatabasePath()
    {
        await using var fixture = new HostFixture();
        var factory = fixture.Host.Services.GetRequiredService<IProjectStoreFactory>();
        var projectId = ProjectId.New();
        var first = (SqliteProjectStore)factory.Open(projectId);
        var second = (SqliteProjectStore)factory.Open(projectId);
        Assert.AreNotSame(first.Database.Writer, second.Database.Writer);
        Assert.IsTrue(first.Database.Writer.SharesBoundaryWith(second.Database.Writer));
        Assert.AreEqual(first.Database.DatabasePath, second.Database.DatabasePath);
    }

    [TestMethod]
    public async Task DIResolution_DoesNotStartExternalProcesses()
    {
        await using var fixture = new HostFixture();
        var services = fixture.Host.Services;
        Assert.IsNotNull(services.GetRequiredService<ProcessRunner>());
        var python = services.GetRequiredService<IPythonAiClient>();
        Assert.IsInstanceOfType<PythonWorkerSupervisor>(python);
        var processField = typeof(PythonWorkerSupervisor).GetField(
            "process", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(processField);
        Assert.IsNull(processField.GetValue(python));
        Assert.IsNull(services.GetService<IComfyUiClient>());
        Assert.IsNull(services.GetService<IDarktableCli>());
        await fixture.StartAsync();
        Assert.AreSame(python, services.GetRequiredService<IPythonAiClient>());
        Assert.IsNull(processField.GetValue(python));
    }

    [TestMethod]
    public void Logger_WritesValidJsonLines()
    {
        using var harness = new LoggerHarness();
        harness.Logger.LogInformation(new EventId(42, "ValidJson"), "A valid line {Number}", 1);
        var lines = harness.ReadDocuments();
        Assert.AreEqual(1, lines.Count);
        Assert.AreEqual("A valid line 1", lines[0].RootElement.GetProperty("message").GetString());
    }

    [TestMethod]
    public async Task ConcurrentLogging_DoesNotCorruptJsonLines()
    {
        using var harness = new LoggerHarness();
        await Task.WhenAll(Enumerable.Range(0, 600).Select(index => Task.Run(() =>
            harness.Logger.LogInformation(new EventId(index), "Concurrent message {Index}", index))));
        var documents = harness.ReadDocuments();
        Assert.AreEqual(600, documents.Count);
        Assert.AreEqual(600, documents.Select(item => item.RootElement.GetProperty("event_id").GetInt32()).Distinct().Count());
    }

    [TestMethod]
    public void Logging_ContainsTimestampLevelCategory()
    {
        using var harness = new LoggerHarness("Slice2.Category");
        harness.Logger.LogWarning(new EventId(7), "Metadata");
        using var document = harness.ReadDocuments().Single();
        var root = document.RootElement;
        Assert.AreEqual("Warning", root.GetProperty("level").GetString());
        Assert.AreEqual("Slice2.Category", root.GetProperty("category").GetString());
        Assert.IsTrue(root.GetProperty("timestamp_utc").GetDateTimeOffset().Offset == TimeSpan.Zero);
    }

    [TestMethod]
    public void Logging_ScopeContainsCorrelationFields()
    {
        using var harness = new LoggerHarness();
        using (harness.Logger.BeginScope(new Dictionary<string, object?>
        {
            ["project_id"] = "project-1",
            ["job_id"] = "job-2",
            ["stage"] = "QA",
            ["component"] = "foundation"
        }))
        {
            harness.Logger.LogInformation("Scoped");
        }

        using var document = harness.ReadDocuments().Single();
        Assert.AreEqual("project-1", document.RootElement.GetProperty("project_id").GetString());
        Assert.AreEqual("job-2", document.RootElement.GetProperty("job_id").GetString());
        Assert.AreEqual("QA", document.RootElement.GetProperty("stage").GetString());
    }

    [TestMethod]
    public void Logging_DoesNotInventMissingCorrelationIds()
    {
        using var harness = new LoggerHarness();
        harness.Logger.LogInformation("No project context");
        using var document = harness.ReadDocuments().Single();
        var root = document.RootElement;
        Assert.IsTrue(root.TryGetProperty("session_id", out _));
        Assert.IsFalse(root.TryGetProperty("project_id", out _));
        Assert.IsFalse(root.TryGetProperty("photo_id", out _));
        Assert.IsFalse(root.TryGetProperty("job_id", out _));
        Assert.IsFalse(root.TryGetProperty("attempt_id", out _));
        Assert.IsFalse(root.TryGetProperty("request_id", out _));
    }

    [TestMethod]
    public void ExceptionLogging_IsStructured()
    {
        using var harness = new LoggerHarness();
        Exception captured;
        try
        {
            throw new InvalidOperationException("injected logger failure");
        }
        catch (Exception exception)
        {
            captured = exception;
        }

        harness.Logger.LogError(new EventId(99, "Injected"), captured, "Operation failed");
        using var document = harness.ReadDocuments().Single();
        var exceptionJson = document.RootElement.GetProperty("exception");
        Assert.AreEqual(typeof(InvalidOperationException).FullName, exceptionJson.GetProperty("type").GetString());
        Assert.AreEqual("injected logger failure", exceptionJson.GetProperty("message").GetString());
        Assert.IsTrue(exceptionJson.GetProperty("stack_trace").GetString()!.Contains(nameof(ExceptionLogging_IsStructured), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Logging_ShutdownFlushesPendingEntries()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        var logger = fixture.Host.Services.GetRequiredService<ILogger<HostCompositionTests>>();
        for (var index = 0; index < 400; index++)
        {
            logger.LogInformation(new EventId(2000 + index), "Pending shutdown message {Index}", index);
        }
        await fixture.StopAsync();
        var documents = ReadJsonLines(fixture.LogPath);
        Assert.AreEqual(400, documents.Count(item => item.RootElement.GetProperty("event_id").GetInt32() is >= 2000 and < 2400));
        DisposeDocuments(documents);
    }

    [TestMethod]
    public void Logger_Dispose_IsIdempotent()
    {
        using var root = new TemporaryRoot();
        var paths = CreatePaths(Path.Combine(root.Path, "runtime"));
        new RuntimeDirectoryInitializer(paths).InitializeAsync().GetAwaiter().GetResult();
        var provider = new JsonLinesLoggerProvider(
            paths,
            new RuntimeSession(),
            Options.Create(new PhotoAIFactoryRuntimeOptions()));
        provider.Activate();
        provider.Dispose();
        provider.Dispose();
        Assert.IsTrue(provider.IsDisposed);
    }

    [TestMethod]
    public async Task HostShutdown_PropagatesCancellation()
    {
        await using var fixture = new HostFixture(configure: services =>
        {
            services.AddSingleton<CancellationProbe>();
            services.AddHostedService(provider => provider.GetRequiredService<CancellationProbe>());
        });
        var probe = fixture.Host.Services.GetRequiredService<CancellationProbe>();
        await fixture.StartAsync();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.StopAsync();
        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(probe.Execution?.IsCompleted ?? false);
    }

    [TestMethod]
    public async Task HostShutdown_LeavesNoBackgroundWorker()
    {
        await using var fixture = new HostFixture(configure: services =>
        {
            services.AddSingleton<CancellationProbe>();
            services.AddHostedService(provider => provider.GetRequiredService<CancellationProbe>());
        });
        var probe = fixture.Host.Services.GetRequiredService<CancellationProbe>();
        await fixture.StartAsync();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.StopAsync();
        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await probe.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(probe.Execution?.IsCompletedSuccessfully ?? false);
    }

    [TestMethod]
    public async Task SessionId_IsStableWithinHost()
    {
        await using var fixture = new HostFixture();
        var first = fixture.Host.Services.GetRequiredService<IRuntimeSession>();
        var second = fixture.Host.Services.GetRequiredService<IRuntimeSession>();
        await fixture.StartAsync();
        Assert.AreSame(first, second);
        Assert.AreEqual(first.SessionId, second.SessionId);
        Assert.IsFalse(string.IsNullOrWhiteSpace(first.SessionId));
    }

    [TestMethod]
    public async Task NewHost_GetsNewSessionId()
    {
        await using var first = new HostFixture();
        await using var second = new HostFixture();
        var firstId = first.Host.Services.GetRequiredService<IRuntimeSession>().SessionId;
        var secondId = second.Host.Services.GetRequiredService<IRuntimeSession>().SessionId;
        Assert.AreNotEqual(firstId, secondId);
    }

    [TestMethod]
    public async Task ExistingSlice1PersistenceTests_StillPass()
    {
        await using var fixture = new HostFixture();
        await fixture.StartAsync();
        var service = fixture.Host.Services.GetRequiredService<ProjectService>();
        var created = await service.CreateProjectAsync(
            "DI persistence",
            CreateProjectConfig(
                Path.Combine(fixture.BaseRoot, "input"),
                Path.Combine(fixture.BaseRoot, "output")),
            "slice2-create",
            new DateTimeOffset(2026, 8, 18, 18, 0, 0, TimeSpan.Zero));
        var reopened = await service.OpenProjectAsync(created.Project.Id);
        Assert.IsNotNull(reopened);
        Assert.AreEqual(created.LatestConfig.Sha256, reopened.LatestConfig.Sha256);
    }

    [TestMethod]
    public async Task LoggerDestinationFailure_IsExplicit()
    {
        await using var fixture = new HostFixture();
        var blockedDestination = Path.Combine(fixture.RuntimeRoot, "logs", "photo-ai-factory.jsonl");
        Directory.CreateDirectory(blockedDestination);
        await ExpectAnyExceptionAsync(
            () => fixture.StartAsync(),
            typeof(IOException), typeof(UnauthorizedAccessException));
    }

    [TestMethod]
    public async Task MissingRequiredService_FailsContainerValidation()
    {
        using var root = new TemporaryRoot();
        var builder = CreateBuilder(new Dictionary<string, string?>
        {
            [$"{PhotoAIFactoryRuntimeOptions.SectionName}:RootPath"] = Path.Combine(root.Path, "runtime")
        });
        builder.AddPhotoAIFactoryFoundation();
        var appPaths = builder.Services.Where(item => item.ServiceType == typeof(IAppPaths)).ToArray();
        foreach (var descriptor in appPaths)
        {
            builder.Services.Remove(descriptor);
        }

        await ExpectAnyExceptionAsync(
            () => Task.Run(() => builder.Build()),
            typeof(AggregateException), typeof(InvalidOperationException));
    }

    [TestMethod]
    public void DuplicateLoggerDestination_IsRejected()
    {
        using var root = new TemporaryRoot();
        var paths = CreatePaths(Path.Combine(root.Path, "runtime"));
        new RuntimeDirectoryInitializer(paths).InitializeAsync().GetAwaiter().GetResult();
        var options = Options.Create(new PhotoAIFactoryRuntimeOptions());
        using var first = new JsonLinesLoggerProvider(paths, new RuntimeSession(), options);
        using var second = new JsonLinesLoggerProvider(paths, new RuntimeSession(), options);
        first.Activate();
        Assert.ThrowsExactly<IOException>(second.Activate);
    }

    private static HostApplicationBuilder CreateBuilder(IDictionary<string, string?> values)
    {
        return PhotoAIFactoryHost.CreateBuilder(configure: builder =>
        {
            builder.Environment.EnvironmentName = Environments.Production;
            builder.Configuration.AddInMemoryCollection(values);
        });
    }

    private static WindowsAppPaths CreatePaths(string? root)
    {
        var options = new PhotoAIFactoryRuntimeOptions { RootPath = root };
        return new WindowsAppPaths(Options.Create(options));
    }

    private static ProjectConfigV1 CreateProjectConfig(string input, string output) =>
        new(
            input,
            output,
            true,
            RevealMode.DtAuto,
            true,
            "technical",
            SemanticMode.Standard,
            ComfyUiMode.Auto,
            ["denoise"],
            ["base"],
            "jpeg",
            90,
            30);

    private static async Task<TException> ExpectExceptionAsync<TException>(Func<Task> action)
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

    private static async Task<Exception> ExpectAnyExceptionAsync(Func<Task> action, params Type[] expected)
    {
        try
        {
            await action();
        }
        catch (Exception exception) when (expected.Any(type => type.IsAssignableFrom(exception.GetType())))
        {
            return exception;
        }
        Assert.Fail($"Expected one of: {string.Join(", ", expected.Select(type => type.Name))}.");
        throw new InvalidOperationException("Unreachable.");
    }

    private static List<JsonDocument> ReadJsonLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var documents = new List<JsonDocument>();
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                documents.Add(JsonDocument.Parse(line));
            }
        }
        return documents;
    }

    private static void DisposeDocuments(IEnumerable<JsonDocument> documents)
    {
        foreach (var document in documents)
        {
            document.Dispose();
        }
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly TemporaryRoot temporaryRoot;
        private bool started;

        public HostFixture(
            string? runtimeRoot = null,
            Action<IServiceCollection>? configure = null)
        {
            temporaryRoot = new TemporaryRoot();
            BaseRoot = temporaryRoot.Path;
            RuntimeRoot = Path.GetFullPath(runtimeRoot ?? Path.Combine(BaseRoot, "runtime"));
            var builder = CreateBuilder(new Dictionary<string, string?>
            {
                [$"{PhotoAIFactoryRuntimeOptions.SectionName}:RootPath"] = RuntimeRoot,
                [$"{PhotoAIFactoryRuntimeOptions.SectionName}:LogFileName"] = "photo-ai-factory.jsonl"
            });
            builder.AddPhotoAIFactoryFoundation();
            configure?.Invoke(builder.Services);
            Host = builder.Build();
        }

        public string BaseRoot { get; }
        public string RuntimeRoot { get; }
        public IHost Host { get; }
        public string LogPath => Host.Services.GetRequiredService<JsonLinesLoggerProvider>().DestinationPath;

        public async Task StartAsync()
        {
            await Host.StartAsync();
            started = true;
        }

        public async Task StopAsync()
        {
            if (started)
            {
                await Host.StopAsync();
                started = false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Host.Dispose();
            temporaryRoot.Dispose();
        }
    }

    private sealed class LoggerHarness : IDisposable
    {
        private readonly TemporaryRoot root = new();

        public LoggerHarness(string category = "Slice2.Logger")
        {
            var paths = CreatePaths(Path.Combine(root.Path, "runtime"));
            new RuntimeDirectoryInitializer(paths).InitializeAsync().GetAwaiter().GetResult();
            Provider = new JsonLinesLoggerProvider(
                paths,
                new RuntimeSession(),
                Options.Create(new PhotoAIFactoryRuntimeOptions()));
            Provider.Activate();
            Logger = Provider.CreateLogger(category);
        }

        public JsonLinesLoggerProvider Provider { get; }
        public ILogger Logger { get; }

        public List<JsonDocument> ReadDocuments()
        {
            Provider.Flush();
            return ReadJsonLines(Provider.DestinationPath);
        }

        public void Dispose()
        {
            Provider.Dispose();
            root.Dispose();
        }
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PhotoAIFactory.Slice2.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class CancellationProbe : BackgroundService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Execution { get; private set; }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Execution = ObserveAsync(stoppingToken);
            return Execution;
        }

        private async Task ObserveAsync(CancellationToken stoppingToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
            }
            finally
            {
                Exited.TrySetResult();
            }
        }
    }
}
