using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private readonly INavigationService navigationService;
    private readonly IProjectContext projectContext;
    private readonly IComponentHealthTracker healthTracker;
    private string title = "PHOTO AI FACTORY";
    private bool hasUnhealthyComponents;

    public ShellViewModel(
        INavigationService navigationService,
        IProjectContext projectContext,
        IComponentHealthTracker healthTracker)
    {
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));
        this.healthTracker = healthTracker ?? throw new ArgumentNullException(nameof(healthTracker));

        this.projectContext.PropertyChanged += (_, _) => UpdateTitleAndState();
        this.navigationService.Navigated += (_, _) => OnPropertyChanged(nameof(CurrentPageKey));

        NavigateCommand = new RelayCommand<string>(pageKey =>
        {
            if (!string.IsNullOrWhiteSpace(pageKey))
            {
                navigationService.NavigateTo(pageKey);
            }
        });

        RefreshHealth();
    }

    public string CurrentPageKey => navigationService.CurrentPageKey;

    public IProjectContext Context => projectContext;

    public string Title
    {
        get => title;
        private set => SetProperty(ref title, value);
    }

    public bool HasUnhealthyComponents
    {
        get => hasUnhealthyComponents;
        private set => SetProperty(ref hasUnhealthyComponents, value);
    }

    public RelayCommand<string> NavigateCommand { get; }

    public void RefreshHealth()
    {
        var statuses = healthTracker.GetAllStatuses();
        HasUnhealthyComponents = statuses.Any(s => s.CircuitBreakerOpen || s.State == ComponentHealthState.Unhealthy);
    }

    private void UpdateTitleAndState()
    {
        if (projectContext.HasActiveProject)
        {
            Title = $"PHOTO AI FACTORY — {projectContext.ActiveProjectName} ({projectContext.ActiveProjectState})";
        }
        else
        {
            Title = "PHOTO AI FACTORY";
        }
        RefreshHealth();
    }
}
