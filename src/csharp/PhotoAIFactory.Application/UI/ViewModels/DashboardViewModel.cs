using PhotoAIFactory.Application.Health;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly IDashboardQueryService dashboardQuery;
    private readonly IProjectContext projectContext;
    private readonly INavigationService navigationService;
    private readonly ProjectRuntimeCoordinator runtimeCoordinator;

    private DashboardSummaryDto? summary;
    private bool isLoading;
    private string? statusMessage;

    public DashboardViewModel(
        IDashboardQueryService dashboardQuery,
        IProjectContext projectContext,
        INavigationService navigationService,
        ProjectRuntimeCoordinator runtimeCoordinator)
    {
        this.dashboardQuery = dashboardQuery ?? throw new ArgumentNullException(nameof(dashboardQuery));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        TogglePauseCommand = new AsyncRelayCommand(TogglePauseAsync, () => projectContext.HasActiveProject);
        ViewQueueCommand = new RelayCommand(() => navigationService.NavigateTo("Queue"));
        ViewReviewsCommand = new RelayCommand(() => navigationService.NavigateTo("Review"));
        ViewHistoryCommand = new RelayCommand(() => navigationService.NavigateTo("History"));
    }

    public DashboardSummaryDto? Summary
    {
        get => summary;
        private set
        {
            if (SetProperty(ref summary, value))
            {
                OnPropertyChanged(nameof(HasSummary));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsDegradedRunning));
                OnPropertyChanged(nameof(IsBlockedStorage));
                OnPropertyChanged(nameof(IsComponentUnhealthy));
                OnPropertyChanged(nameof(PauseButtonText));
                OnPropertyChanged(nameof(AverageTimeString));
            }
        }
    }

    public bool HasSummary => summary is not null;
    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    public string? StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public bool IsIngestionUnhealthy =>
        summary?.ComponentHealth.Any(c =>
            string.Equals(c.ComponentName, "IngestionRuntime", StringComparison.OrdinalIgnoreCase) &&
            (c.State == ComponentHealthState.Unhealthy || c.CircuitOpen)) == true;

    public bool IsDegradedRunning =>
        summary?.State == ProjectState.Running &&
        (IsIngestionUnhealthy || summary?.ComponentHealth.Any(c => c.CircuitOpen || c.State == ComponentHealthState.Unhealthy) == true);

    public bool IsPaused => summary?.State is ProjectState.Paused or ProjectState.PauseRequested;
    public bool IsRunning => summary?.State is ProjectState.Running && !IsDegradedRunning;
    public bool IsBlockedStorage => summary?.State is ProjectState.BlockedStorage;
    public bool IsComponentUnhealthy => summary?.State is ProjectState.ComponentUnhealthy || IsDegradedRunning;

    public string PauseButtonText => summary switch
    {
        null => "Start Processing",
        { State: ProjectState.Stopped } => "Start Processing",
        { State: ProjectState.Paused } => "Resume Processing",
        { State: ProjectState.PauseRequested } => "Pausing...",
        { State: ProjectState.StopRequested } => "Stopping...",
        { State: ProjectState.BlockedStorage } => "Check Storage & Resume",
        { State: ProjectState.ComponentUnhealthy } => "Inspect Components & Resume",
        { State: ProjectState.Running } when IsDegradedRunning => "Inspect Components & Resume",
        { State: ProjectState.Running } => "Pause Processing",
        _ => "Start Processing"
    };

    public string AverageTimeString => summary switch
    {
        null => "—",
        { HasAverageTimeData: false } => "Insufficient Data",
        { AverageProcessingTime: not null } => $"{summary.AverageProcessingTime.Value.TotalSeconds:F1}s / photo",
        _ => "—"
    };

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand TogglePauseCommand { get; }
    public RelayCommand ViewQueueCommand { get; }
    public RelayCommand ViewReviewsCommand { get; }
    public RelayCommand ViewHistoryCommand { get; }

    public async Task RefreshAsync()
    {
        if (!projectContext.HasActiveProject)
        {
            Summary = null;
            return;
        }

        IsLoading = true;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var data = await dashboardQuery.GetDashboardSummaryAsync(pId);
            Summary = data;
            if (data is not null)
            {
                projectContext.UpdateState(data.State);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Refresh failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task TogglePauseAsync()
    {
        if (!projectContext.HasActiveProject || summary is null)
            return;

        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var opId = Guid.NewGuid().ToString("N");
            if (summary.State == ProjectState.Paused || summary.State == ProjectState.Stopped)
            {
                await runtimeCoordinator.StartOrResumeProjectAsync(pId, opId);
            }
            else if (summary.State == ProjectState.Running && !IsDegradedRunning)
            {
                await runtimeCoordinator.PauseProjectAsync(pId, opId);
            }
            else if (summary.State == ProjectState.BlockedStorage || summary.State == ProjectState.ComponentUnhealthy || IsDegradedRunning)
            {
                await runtimeCoordinator.StartOrResumeProjectAsync(pId, opId);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"State transition failed: {ex.Message}";
        }
        finally
        {
            await RefreshAsync();
        }
    }
}
