using Microsoft.Extensions.Options;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Infrastructure.Hosting;

public sealed class WindowsAppPaths : IAppPaths
{
    public WindowsAppPaths(IOptions<PhotoAIFactoryRuntimeOptions> options)
    {
        var configuredRoot = options.Value.RootPath;
        var root = configuredRoot is null
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhotoAIFactory")
            : configuredRoot;

        RootDirectory = Normalize(root);
        ProjectsDirectory = Path.Combine(RootDirectory, "projects");
        WorkDirectory = Path.Combine(RootDirectory, "work");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        ModelsDirectory = Path.Combine(RootDirectory, "models");
        ComponentsDirectory = Path.Combine(RootDirectory, "components");
    }

    public string RootDirectory { get; }
    public string ProjectsDirectory { get; }
    public string WorkDirectory { get; }
    public string LogsDirectory { get; }
    public string ModelsDirectory { get; }
    public string ComponentsDirectory { get; }

    public string GetProjectDatabasePath(ProjectId projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId.Value) ||
            !string.Equals(Path.GetFileName(projectId.Value), projectId.Value, StringComparison.Ordinal) ||
            projectId.Value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            projectId.Value is "." or "..")
        {
            throw new ArgumentException("Project ID is not safe for a filesystem path.", nameof(projectId));
        }

        return Path.Combine(ProjectsDirectory, projectId.Value, "project.db");
    }

    private static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }
}

public sealed class RuntimeDirectoryInitializer(IAppPaths paths) : IRuntimeDirectoryInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] authorizedDirectories =
        [
            paths.RootDirectory,
            paths.ProjectsDirectory,
            paths.WorkDirectory,
            paths.LogsDirectory,
            paths.ModelsDirectory,
            paths.ComponentsDirectory
        ];

        foreach (var directory in authorizedDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
        }

        return Task.CompletedTask;
    }
}

public sealed class RuntimeSession : IRuntimeSession
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
}
