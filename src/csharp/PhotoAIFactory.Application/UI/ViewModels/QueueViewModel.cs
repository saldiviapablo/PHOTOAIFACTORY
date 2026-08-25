using System.Collections.ObjectModel;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class QueueViewModel : ObservableObject
{
    private readonly IQueueQueryService queueQuery;
    private readonly IProjectContext projectContext;
    private readonly INavigationService navigationService;

    private QueueOverviewDto? overview;
    private bool isLoading;
    private QueueItemDto? selectedItem;
    private string? statusMessage;

    public QueueViewModel(
        IQueueQueryService queueQuery,
        IProjectContext projectContext,
        INavigationService navigationService)
    {
        this.queueQuery = queueQuery ?? throw new ArgumentNullException(nameof(queueQuery));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));

        QueueItems = new ObservableCollection<QueueItemDto>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ViewJobDetailCommand = new RelayCommand<QueueItemDto>(ViewJobDetail, item => item is not null);
    }

    public ObservableCollection<QueueItemDto> QueueItems { get; }

    public QueueOverviewDto? Overview
    {
        get => overview;
        private set
        {
            if (SetProperty(ref overview, value))
            {
                OnPropertyChanged(nameof(HasActiveJob));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(IsStorageBlocked));
                OnPropertyChanged(nameof(IsComponentUnhealthy));
            }
        }
    }

    public bool HasActiveJob => overview?.ActiveJob is not null;
    public bool IsPaused => overview?.IsPaused ?? false;
    public bool IsStorageBlocked => overview?.IsStorageBlocked ?? false;
    public bool IsComponentUnhealthy => overview?.IsComponentUnhealthy ?? false;

    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    public QueueItemDto? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (SetProperty(ref selectedItem, value))
            {
                ViewJobDetailCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand<QueueItemDto> ViewJobDetailCommand { get; }

    public async Task RefreshAsync()
    {
        if (!projectContext.HasActiveProject)
        {
            Overview = null;
            QueueItems.Clear();
            return;
        }

        IsLoading = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var data = await queueQuery.GetQueueOverviewAsync(pId);
            Overview = data;
            QueueItems.Clear();
            if (data is not null)
            {
                foreach (var item in data.Items)
                {
                    QueueItems.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Queue refresh failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ViewJobDetail(QueueItemDto? item)
    {
        if (item is null) return;
        navigationService.NavigateTo("JobDetail", item.JobId);
    }
}
