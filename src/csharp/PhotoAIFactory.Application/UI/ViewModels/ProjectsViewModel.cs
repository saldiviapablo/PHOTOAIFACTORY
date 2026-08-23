using System.Collections.ObjectModel;
using System.Diagnostics;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class ProjectsViewModel : ObservableObject
{
    private readonly IProjectQueryService projectQuery;
    private readonly IProjectContext projectContext;
    private readonly INavigationService navigationService;
    private bool isLoading;
    private ProjectSummaryDto? selectedProject;
    private string? errorMessage;

    public ProjectsViewModel(
        IProjectQueryService projectQuery,
        IProjectContext projectContext,
        INavigationService navigationService)
    {
        this.projectQuery = projectQuery ?? throw new ArgumentNullException(nameof(projectQuery));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));

        Projects = new ObservableCollection<ProjectSummaryDto>();

        LoadProjectsCommand = new AsyncRelayCommand(LoadProjectsAsync);
        OpenProjectCommand = new RelayCommand<ProjectSummaryDto>(OpenProject, p => p is not null);
        CloseProjectCommand = new RelayCommand(CloseActiveProject, () => projectContext.HasActiveProject);
        CreateNewProjectCommand = new RelayCommand(() => navigationService.NavigateTo("CreateProject"));
        OpenInputFolderCommand = new RelayCommand<string>(OpenFolderInExplorer, path => !string.IsNullOrWhiteSpace(path));
        OpenOutputFolderCommand = new RelayCommand<string>(OpenFolderInExplorer, path => !string.IsNullOrWhiteSpace(path));
    }

    public ObservableCollection<ProjectSummaryDto> Projects { get; }

    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set => SetProperty(ref errorMessage, value);
    }

    public ProjectSummaryDto? SelectedProject
    {
        get => selectedProject;
        set
        {
            if (SetProperty(ref selectedProject, value))
            {
                OpenProjectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand LoadProjectsAsyncCommand => (AsyncRelayCommand)LoadProjectsCommand;
    public System.Windows.Input.ICommand LoadProjectsCommand { get; }
    public RelayCommand<ProjectSummaryDto> OpenProjectCommand { get; }
    public RelayCommand CloseProjectCommand { get; }
    public RelayCommand CreateNewProjectCommand { get; }
    public RelayCommand<string> OpenInputFolderCommand { get; }
    public RelayCommand<string> OpenOutputFolderCommand { get; }

    public async Task LoadProjectsAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var list = await projectQuery.ListProjectsAsync().ConfigureAwait(false);
            Projects.Clear();
            foreach (var p in list)
            {
                Projects.Add(p);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load projects: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void OpenProject(ProjectSummaryDto? summary)
    {
        if (summary is null) return;
        projectContext.SetActiveProject(summary.Id, summary.Name, summary.State);
        navigationService.NavigateTo("Dashboard");
    }

    public void CloseActiveProject()
    {
        projectContext.ClearActiveProject();
        CloseProjectCommand.RaiseCanExecuteChanged();
    }

    private static void OpenFolderInExplorer(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folderPath,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch
        {
            // Ignore shell launching errors safely
        }
    }
}
