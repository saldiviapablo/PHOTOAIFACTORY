using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhotoAIFactory.Application;
using PhotoAIFactory.Application.Analysis;
using PhotoAIFactory.Application.Ingestion;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Infrastructure.Analysis;
using PhotoAIFactory.Infrastructure.Ingestion;
using PhotoAIFactory.Infrastructure.Logging;
using PhotoAIFactory.Infrastructure.Persistence.Analysis;
using PhotoAIFactory.Infrastructure.Persistence.Ingestion;
using PhotoAIFactory.Infrastructure.Persistence.Repositories;

namespace PhotoAIFactory.Infrastructure.Hosting;

public static class PhotoAIFactoryHost
{
    public static HostApplicationBuilder CreateBuilder(
        string[]? args = null,
        Action<HostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(args ?? []);
        configure?.Invoke(builder);
        builder.AddPhotoAIFactoryFoundation();
        return builder;
    }
}

public static class PhotoAIFactoryHostingExtensions
{
    public static IHostApplicationBuilder AddPhotoAIFactoryFoundation(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(FoundationRegistrationMarker)))
        {
            return builder;
        }

        builder.Services.AddSingleton<FoundationRegistrationMarker>();
        builder.Services
            .AddOptions<PhotoAIFactoryRuntimeOptions>()
            .Bind(builder.Configuration.GetSection(PhotoAIFactoryRuntimeOptions.SectionName))
            .Validate(PhotoAIFactoryRuntimeOptions.IsValid,
                "Runtime RootPath must be an absolute path and LogFileName must be a .jsonl leaf filename.")
            .ValidateOnStart();

        builder.Services
            .AddOptions<AnalysisRuntimeOptions>()
            .Bind(builder.Configuration.GetSection(AnalysisRuntimeOptions.SectionName))
            .Validate(AnalysisRuntimeOptions.IsValid,
                "Analysis runtime options contain an invalid path or timeout.")
            .ValidateOnStart();

        builder.Services.AddSingleton<IRuntimeSession, RuntimeSession>();
        builder.Services.AddSingleton<IAppPaths, WindowsAppPaths>();
        builder.Services.AddSingleton<IRuntimeDirectoryInitializer, RuntimeDirectoryInitializer>();

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Services.AddSingleton<JsonLinesLoggerProvider>();
        builder.Services.AddSingleton<ILoggerProvider>(services =>
            services.GetRequiredService<JsonLinesLoggerProvider>());

        builder.Services.AddSingleton<IProjectStoreFactory, SqliteProjectStoreFactory>();

        builder.Services
            .AddOptions<IngestionRuntimeOptions>()
            .Bind(builder.Configuration.GetSection(IngestionRuntimeOptions.SectionName))
            .Validate(IngestionRuntimeOptions.IsValid,
                "Ingestion runtime options are outside supported safe ranges.")
            .ValidateOnStart();
        builder.Services.AddSingleton<IIngestionStoreFactory, SqliteIngestionStoreFactory>();
        builder.Services.AddSingleton<IFileStabilityProbe, DefaultFileStabilityProbe>();
        builder.Services.AddSingleton<IManagedOriginalArchive, ManagedOriginalArchive>();
        builder.Services.AddSingleton<IRawSupportClassifier, SonyArwSupportClassifier>();
        builder.Services.AddSingleton<IIngestionSessionFactory, IngestionSessionFactory>();
        builder.Services.AddSingleton<ProjectIngestionManager>();

        builder.Services.AddSingleton<IAnalysisStoreFactory, SqliteAnalysisStoreFactory>();
        builder.Services.AddSingleton<IAnalysisPreviewProvider, DarktableAnalysisPreviewProvider>();
        builder.Services.AddSingleton<IAnalysisInputResolver, AnalysisInputResolver>();
        builder.Services.AddSingleton<PythonWorkerSupervisor>();
        builder.Services.AddSingleton<IPythonAiClient>(services =>
            services.GetRequiredService<PythonWorkerSupervisor>());
        builder.Services.AddTransient<AnalysisOrchestrator>();
        builder.Services.AddTransient<ProjectAnalysisManager>();

        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IProjectWorkStatus, NoActiveProjectWorkStatus>();
        builder.Services.AddTransient<ProjectService>();
        builder.Services.AddTransient<ProjectLifecycleService>();
        builder.Services.AddTransient<ConfigService>();
        builder.Services.AddSingleton<ProcessRunner>();
        builder.Services.AddSingleton<ComponentLockReader>();
        builder.Services.AddSingleton<IGpuResourceCoordinator, GpuResourceCoordinator>();

        builder.Services.AddHostedService<RuntimeInitializationHostedService>();
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        }));
        return builder;
    }

    private sealed class FoundationRegistrationMarker;
}

internal sealed class RuntimeInitializationHostedService(
    IRuntimeDirectoryInitializer directories,
    JsonLinesLoggerProvider loggerProvider,
    ILogger<RuntimeInitializationHostedService> logger,
    IRuntimeSession session) : IHostedService
{
    private static readonly EventId ReadyEvent = new(1000, "RuntimeReady");
    private static readonly EventId StoppingEvent = new(1001, "RuntimeStopping");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await directories.InitializeAsync(cancellationToken).ConfigureAwait(false);
        loggerProvider.Activate();
        logger.LogInformation(ReadyEvent, "Runtime foundation ready for session {SessionId}", session.SessionId);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(StoppingEvent, "Runtime foundation stopping for session {SessionId}", session.SessionId);
        loggerProvider.Flush();
        return Task.CompletedTask;
    }
}
