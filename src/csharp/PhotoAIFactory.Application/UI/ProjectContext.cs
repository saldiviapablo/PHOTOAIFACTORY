using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.UI;

public sealed class ProjectContext : ObservableObject, IProjectContext
{
    private ProjectId? activeProjectId;
    private string? activeProjectName;
    private ProjectState? activeProjectState;

    public ProjectId? ActiveProjectId
    {
        get => activeProjectId;
        private set
        {
            if (SetProperty(ref activeProjectId, value))
            {
                OnPropertyChanged(nameof(HasActiveProject));
            }
        }
    }

    public string? ActiveProjectName
    {
        get => activeProjectName;
        private set => SetProperty(ref activeProjectName, value);
    }

    public ProjectState? ActiveProjectState
    {
        get => activeProjectState;
        private set => SetProperty(ref activeProjectState, value);
    }

    public bool HasActiveProject => activeProjectId is not null;

    public void SetActiveProject(ProjectId projectId, string name, ProjectState state)
    {
        ActiveProjectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
        ActiveProjectName = name;
        ActiveProjectState = state;
    }

    public void ClearActiveProject()
    {
        ActiveProjectId = null;
        ActiveProjectName = null;
        ActiveProjectState = null;
    }

    public void UpdateState(ProjectState state)
    {
        ActiveProjectState = state;
    }
}
