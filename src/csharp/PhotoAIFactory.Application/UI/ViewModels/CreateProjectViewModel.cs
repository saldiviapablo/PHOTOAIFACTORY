using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class CreateProjectViewModel : ObservableObject
{
    private readonly ProjectService projectService;
    private readonly IProjectContext projectContext;
    private readonly INavigationService navigationService;
    private readonly IFolderPickerService folderPickerService;

    private string projectName = string.Empty;
    private string inputFolder = string.Empty;
    private string outputFolder = string.Empty;
    private bool includeSubfolders = true;
    private RevealMode revealMode = RevealMode.PreAi;
    private bool preselectionEnabled = true;
    private string preselectionProfile = "BALANCED";
    private SemanticMode semanticMode = SemanticMode.Standard;
    private ComfyUiMode comfyUiMode = ComfyUiMode.Off;
    private string exportFormat = "JPEG";
    private int exportQuality = 92;
    private bool isCreating;
    private string? validationError;

    public CreateProjectViewModel(
        ProjectService projectService,
        IProjectContext projectContext,
        INavigationService navigationService,
        IFolderPickerService folderPickerService)
    {
        this.projectService = projectService ?? throw new ArgumentNullException(nameof(projectService));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));

        CreateProjectCommand = new AsyncRelayCommand(CreateProjectAsync, CanCreateProject);
        CancelCommand = new RelayCommand(() => navigationService.NavigateTo("Projects"));
        BrowseInputCommand = new AsyncRelayCommand(BrowseInputFolderAsync);
        BrowseOutputCommand = new AsyncRelayCommand(BrowseOutputFolderAsync);
    }

    public string ProjectName
    {
        get => projectName;
        set
        {
            if (SetProperty(ref projectName, value))
            {
                Validate();
                CreateProjectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string InputFolder
    {
        get => inputFolder;
        set
        {
            if (SetProperty(ref inputFolder, value))
            {
                Validate();
                CreateProjectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string OutputFolder
    {
        get => outputFolder;
        set
        {
            if (SetProperty(ref outputFolder, value))
            {
                Validate();
                CreateProjectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IncludeSubfolders
    {
        get => includeSubfolders;
        set => SetProperty(ref includeSubfolders, value);
    }

    public RevealMode RevealMode
    {
        get => revealMode;
        set => SetProperty(ref revealMode, value);
    }

    public bool PreselectionEnabled
    {
        get => preselectionEnabled;
        set => SetProperty(ref preselectionEnabled, value);
    }

    public string PreselectionProfile
    {
        get => preselectionProfile;
        set => SetProperty(ref preselectionProfile, value);
    }

    public SemanticMode SemanticMode
    {
        get => semanticMode;
        set => SetProperty(ref semanticMode, value);
    }

    public ComfyUiMode ComfyUiMode
    {
        get => comfyUiMode;
        set => SetProperty(ref comfyUiMode, value);
    }

    public string ExportFormat
    {
        get => exportFormat;
        set => SetProperty(ref exportFormat, value);
    }

    public int ExportQuality
    {
        get => exportQuality;
        set => SetProperty(ref exportQuality, value);
    }

    public bool IsCreating
    {
        get => isCreating;
        private set => SetProperty(ref isCreating, value);
    }

    public string? ValidationError
    {
        get => validationError;
        private set => SetProperty(ref validationError, value);
    }

    public AsyncRelayCommand CreateProjectCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand BrowseInputCommand { get; }
    public AsyncRelayCommand BrowseOutputCommand { get; }

    public async Task BrowseInputFolderAsync()
    {
        var path = await folderPickerService.PickFolderAsync("Select Input Folder");
        if (!string.IsNullOrWhiteSpace(path))
        {
            InputFolder = path;
        }
    }

    public async Task BrowseOutputFolderAsync()
    {
        var path = await folderPickerService.PickFolderAsync("Select Output Folder");
        if (!string.IsNullOrWhiteSpace(path))
        {
            OutputFolder = path;
        }
    }

    public bool CanCreateProject()
    {
        if (isCreating) return false;
        if (string.IsNullOrWhiteSpace(projectName)) return false;
        if (string.IsNullOrWhiteSpace(inputFolder)) return false;
        if (string.IsNullOrWhiteSpace(outputFolder)) return false;
        return string.IsNullOrEmpty(validationError);
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            ValidationError = "Project name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(inputFolder) || string.IsNullOrWhiteSpace(outputFolder))
        {
            ValidationError = "Both input and output folders are required.";
            return;
        }

        var inFull = Path.GetFullPath(inputFolder.Trim());
        var outFull = Path.GetFullPath(outputFolder.Trim());

        if (string.Equals(inFull, outFull, StringComparison.OrdinalIgnoreCase))
        {
            ValidationError = "Output folder must be different from input folder to prevent re-ingestion loops.";
            return;
        }

        ValidationError = null;
    }

    public async Task CreateProjectAsync()
    {
        Validate();
        if (!string.IsNullOrEmpty(ValidationError))
            return;

        IsCreating = true;
        try
        {
            var config = new ProjectConfigV1(
                InputFolder.Trim(),
                OutputFolder.Trim(),
                IncludeSubfolders,
                RevealMode,
                PreselectionEnabled,
                PreselectionProfile,
                SemanticMode,
                ComfyUiMode,
                [],
                [],
                ExportFormat,
                ExportQuality,
                5);

            var project = await projectService.CreateProjectAsync(
                ProjectName.Trim(),
                config,
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow);
            projectContext.SetActiveProject(project.Project.Id, project.Project.Name, project.Project.State);
            navigationService.NavigateTo("Dashboard");
        }
        catch (Exception ex)
        {
            ValidationError = $"Failed to create project: {ex.Message}";
        }
        finally
        {
            IsCreating = false;
        }
    }
}
