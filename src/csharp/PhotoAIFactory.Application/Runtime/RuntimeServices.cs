using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.Runtime;

public interface IAppPaths
{
    string RootDirectory { get; }
    string ProjectsDirectory { get; }
    string WorkDirectory { get; }
    string LogsDirectory { get; }
    string ModelsDirectory { get; }
    string ComponentsDirectory { get; }
    string GetProjectDatabasePath(ProjectId projectId);
}

public interface IRuntimeDirectoryInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IRuntimeSession
{
    string SessionId { get; }
}
