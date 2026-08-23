using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class ProjectConfigViewModel : ObservableObject
{
    private readonly ConfigService configService;
    private readonly IProjectStoreFactory storeFactory;
    private readonly IProjectContext projectContext;

    private ConfigVersion? currentVersion;
    private ProjectConfigV1? currentConfig;
    private bool isEditing;
    private bool isSaving;
    private string? statusMessage;

    // Editable draft fields
    private string inputFolder = string.Empty;
    private string outputFolder = string.Empty;
    private bool includeSubfolders;
    private RevealMode revealMode;
    private bool preselectionEnabled;
    private string preselectionProfile = "BALANCED";
    private SemanticMode semanticMode;
    private ComfyUiMode comfyUiMode;
    private string exportFormat = "JPEG";
    private int exportQuality = 92;

    public ProjectConfigViewModel(
        ConfigService configService,
        IProjectStoreFactory storeFactory,
        IProjectContext projectContext)
    {
        this.configService = configService ?? throw new ArgumentNullException(nameof(configService));
        this.storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        StartEditCommand = new RelayCommand(StartEdit, () => CanEdit);
        CancelEditCommand = new RelayCommand(CancelEdit);
        SaveConfigCommand = new AsyncRelayCommand(SaveConfigAsync, () => isEditing && !isSaving);
    }

    public ConfigVersion? CurrentVersion
    {
        get => currentVersion;
        private set
        {
            if (SetProperty(ref currentVersion, value))
            {
                OnPropertyChanged(nameof(HasConfig));
                OnPropertyChanged(nameof(VersionDisplay));
                OnPropertyChanged(nameof(ConfigHashDisplay));
            }
        }
    }

    public ProjectConfigV1? CurrentConfig
    {
        get => currentConfig;
        private set
        {
            if (SetProperty(ref currentConfig, value))
            {
                OnPropertyChanged(nameof(HasConfig));
            }
        }
    }

    public bool HasConfig => currentVersion is not null && currentConfig is not null;
    public string VersionDisplay => currentVersion is not null ? $"Version {currentVersion.VersionNumber} (Schema {currentVersion.SchemaVersion})" : "—";
    public string ConfigHashDisplay => currentVersion?.Sha256 ?? "—";

    public bool CanEdit => projectContext.HasActiveProject && projectContext.ActiveProjectState is ProjectState.Paused or ProjectState.Stopped;

    public bool IsEditing
    {
        get => isEditing;
        private set
        {
            if (SetProperty(ref isEditing, value))
            {
                StartEditCommand.RaiseCanExecuteChanged();
                SaveConfigCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSaving
    {
        get => isSaving;
        private set
        {
            if (SetProperty(ref isSaving, value))
            {
                SaveConfigCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string InputFolder
    {
        get => inputFolder;
        set => SetProperty(ref inputFolder, value);
    }

    public string OutputFolder
    {
        get => outputFolder;
        set => SetProperty(ref outputFolder, value);
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

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand StartEditCommand { get; }
    public RelayCommand CancelEditCommand { get; }
    public AsyncRelayCommand SaveConfigCommand { get; }

    public async Task RefreshAsync()
    {
        if (!projectContext.HasActiveProject)
        {
            CurrentVersion = null;
            CurrentConfig = null;
            IsEditing = false;
            return;
        }

        try
        {
            var pId = projectContext.ActiveProjectId!;
            var store = storeFactory.Open(pId);
            var projectWrapper = await store.GetAsync(pId).ConfigureAwait(false);
            if (projectWrapper is not null)
            {
                CurrentVersion = projectWrapper.LatestConfig;
                CurrentConfig = projectWrapper.LatestConfig.ReadConfig();
                PopulateDraftFields(CurrentConfig);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load configuration: {ex.Message}";
        }
    }

    private void PopulateDraftFields(ProjectConfigV1 config)
    {
        InputFolder = config.InputFolder;
        OutputFolder = config.OutputFolder;
        IncludeSubfolders = config.IncludeSubfolders;
        RevealMode = config.RevealMode;
        PreselectionEnabled = config.PreselectionEnabled;
        PreselectionProfile = config.PreselectionProfile;
        SemanticMode = config.SemanticMode;
        ComfyUiMode = config.ComfyUiMode;
        ExportFormat = config.ExportFormat;
        ExportQuality = config.ExportQuality;
    }

    public void StartEdit()
    {
        if (!CanEdit)
        {
            StatusMessage = "Configuration can only be edited when project is in PAUSED state.";
            return;
        }

        if (currentConfig is not null)
        {
            PopulateDraftFields(currentConfig);
        }
        IsEditing = true;
        StatusMessage = null;
    }

    public void CancelEdit()
    {
        if (currentConfig is not null)
        {
            PopulateDraftFields(currentConfig);
        }
        IsEditing = false;
        StatusMessage = null;
    }

    public async Task SaveConfigAsync()
    {
        if (!CanEdit || !isEditing) return;

        IsSaving = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var newConfig = new ProjectConfigV1(
                InputFolder.Trim(),
                OutputFolder.Trim(),
                IncludeSubfolders,
                RevealMode,
                PreselectionEnabled,
                PreselectionProfile,
                SemanticMode,
                ComfyUiMode,
                currentConfig?.AuthorizedComfyUiTasks ?? [],
                currentConfig?.PresetProfiles ?? [],
                ExportFormat,
                ExportQuality,
                currentConfig?.AssociationWindowSeconds ?? 5);

            var opId = Guid.NewGuid().ToString("N");
            var result = await configService.ApplyAsync(pId, newConfig, currentVersion?.Id ?? string.Empty, opId).ConfigureAwait(false);
            if (result.Status == ConfigChangeStatus.Created && result.ConfigVersion is not null)
            {
                CurrentVersion = result.ConfigVersion;
                CurrentConfig = result.ConfigVersion.ReadConfig();
                IsEditing = false;
                StatusMessage = $"Configuration saved as Version {result.ConfigVersion.VersionNumber} (applies to future processing).";
            }
            else
            {
                StatusMessage = $"Failed to save configuration: {result.Status}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save configuration: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }
}
