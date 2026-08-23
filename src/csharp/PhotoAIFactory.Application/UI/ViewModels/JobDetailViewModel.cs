using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class JobDetailViewModel : ObservableObject, IParameterizedNavigable
{
    private readonly IQueueQueryService queueQuery;
    private readonly IProjectContext projectContext;
    private readonly INavigationService navigationService;

    private JobId? currentJobId;
    private JobDetailDto? jobDetail;
    private bool isLoading;
    private string? errorMessage;

    public JobDetailViewModel(
        IQueueQueryService queueQuery,
        IProjectContext projectContext,
        INavigationService navigationService)
    {
        this.queueQuery = queueQuery ?? throw new ArgumentNullException(nameof(queueQuery));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        BackCommand = new RelayCommand(() => navigationService.GoBack());
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is JobId jId)
        {
            CurrentJobId = jId;
        }
        else if (parameter is string str && !string.IsNullOrWhiteSpace(str))
        {
            CurrentJobId = new JobId(str);
        }
    }

    public JobId? CurrentJobId
    {
        get => currentJobId;
        set
        {
            if (SetProperty(ref currentJobId, value))
            {
                if (value is not null)
                {
                    _ = RefreshAsync();
                }
            }
        }
    }

    public JobDetailDto? JobDetail
    {
        get => jobDetail;
        private set
        {
            if (SetProperty(ref jobDetail, value))
            {
                OnPropertyChanged(nameof(HasDetail));
                OnPropertyChanged(nameof(HasCheckpoints));
                OnPropertyChanged(nameof(HasQaResult));
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasDetail => jobDetail is not null;
    public bool HasCheckpoints => jobDetail?.Checkpoints.Count > 0;
    public bool HasQaResult => jobDetail?.QaResult is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(jobDetail?.ErrorDetails);

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

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand BackCommand { get; }

    public async Task RefreshAsync()
    {
        if (!projectContext.HasActiveProject || currentJobId is null)
        {
            JobDetail = null;
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var data = await queueQuery.GetJobDetailAsync(pId, currentJobId).ConfigureAwait(false);
            JobDetail = data;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load job details: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
