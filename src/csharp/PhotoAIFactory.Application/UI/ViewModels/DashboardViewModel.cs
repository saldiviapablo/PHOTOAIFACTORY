using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly IDashboardQueryService dashboardQuery;
    private readonly ProjectLifecycleService lifecycleService;
    private readonly IProjectContext projectContext;
    private readonly INavigationService navigationService;

    private DashboardSummaryDto? summary;
    private bool isLoading;
    private string? statusMessage;

    public DashboardViewModel(
        IDashboardQueryService dashboardQuery,
        ProjectLifecycleService lifecycleService,
        IProjectContext projectContext,
        INavigationService navigationService)
    {
        this.dashboardQuery = dashboardQuery ?? throw new ArgumentNullException(nameof(dashboardQuery));
        this.lifecycleService = lifecycleService ?? throw new ArgumentNullException(nameof(lifecycleService));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));

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

    public bool IsPaused => summary?.State is ProjectState.Paused or ProjectState.PauseRequested;
    public bool IsRunning => summary?.State is ProjectState.Running;
    public bool IsBlockedStorage => summary?.State is ProjectState.BlockedStorage;
    public bool IsComponentUnhealthy => summary?.State is ProjectState.ComponentUnhealthy;

    public string PauseButtonText => summary?.State switch
    {
        ProjectState.Paused => "Resume Processing",
        ProjectState.PauseRequested => "Pausing...",
        ProjectState.BlockedStorage => "Check Storage & Resume",
        _ => "Pause Processing"
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
            var data = await dashboardQuery.GetDashboardSummaryAsync(pId).ConfigureAwait(false);
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
                await lifecycleService.StartOrResumeAsync(pId, opId).ConfigureAwait(false);
            }
            else if (summary.State == ProjectState.Running)
            {
                await lifecycleService.RequestPauseAsync(pId, opId).ConfigureAwait(false);
            }
            else if (summary.State == ProjectState.BlockedStorage)
            {
                await lifecycleService.StartOrResumeAsync(pId, opId).ConfigureAwait(false);
            }

            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"State transition failed: {ex.Message}";
        }
    }
}
