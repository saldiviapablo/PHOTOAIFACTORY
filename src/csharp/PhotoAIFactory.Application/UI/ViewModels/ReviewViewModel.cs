using System.Collections.ObjectModel;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class ReviewViewModel : ObservableObject
{
    private readonly IReviewQueryService reviewQuery;
    private readonly IReviewService reviewService;
    private readonly IProjectContext projectContext;
    private readonly INavigationService navigationService;

    private ReviewItemDto? selectedItem;
    private bool isLoading;
    private bool isExecutingAction;
    private string? statusMessage;

    private readonly IProjectStoreFactory storeFactory;

    public ReviewViewModel(
        IReviewQueryService reviewQuery,
        IReviewService reviewService,
        IProjectStoreFactory storeFactory,
        IProjectContext projectContext,
        INavigationService navigationService)
    {
        this.reviewQuery = reviewQuery ?? throw new ArgumentNullException(nameof(reviewQuery));
        this.reviewService = reviewService ?? throw new ArgumentNullException(nameof(reviewService));
        this.storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));

        PendingReviews = new ObservableCollection<ReviewItemDto>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ApproveCommand = new AsyncRelayCommand(ApproveSelectedAsync, CanExecuteAction);
        ReprocessCommand = new AsyncRelayCommand(ReprocessSelectedAsync, CanReprocessSelected);
        RejectCommand = new AsyncRelayCommand(RejectSelectedAsync, CanExecuteAction);
        LeavePendingCommand = new AsyncRelayCommand(LeavePendingSelectedAsync, CanExecuteAction);
    }

    public ObservableCollection<ReviewItemDto> PendingReviews { get; }

    public ReviewItemDto? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (SetProperty(ref selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelectedItem));
                OnPropertyChanged(nameof(CanReprocess));
                ApproveCommand.RaiseCanExecuteChanged();
                ReprocessCommand.RaiseCanExecuteChanged();
                RejectCommand.RaiseCanExecuteChanged();
                LeavePendingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedItem => selectedItem is not null;
    public bool CanReprocess => selectedItem is not null && selectedItem.ReprocessCount < 1;

    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    public bool IsExecutingAction
    {
        get => isExecutingAction;
        private set
        {
            if (SetProperty(ref isExecutingAction, value))
            {
                ApproveCommand.RaiseCanExecuteChanged();
                ReprocessCommand.RaiseCanExecuteChanged();
                RejectCommand.RaiseCanExecuteChanged();
                LeavePendingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApproveCommand { get; }
    public AsyncRelayCommand ReprocessCommand { get; }
    public AsyncRelayCommand RejectCommand { get; }
    public AsyncRelayCommand LeavePendingCommand { get; }

    private bool CanExecuteAction() => !isExecutingAction && selectedItem is not null;
    private bool CanReprocessSelected() => CanExecuteAction() && CanReprocess;

    public async Task RefreshAsync()
    {
        if (!projectContext.HasActiveProject)
        {
            PendingReviews.Clear();
            SelectedItem = null;
            return;
        }

        IsLoading = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var list = await reviewQuery.GetPendingReviewsAsync(pId).ConfigureAwait(false);
            PendingReviews.Clear();
            foreach (var item in list)
            {
                PendingReviews.Add(item);
            }
            SelectedItem = PendingReviews.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load reviews: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task ApproveSelectedAsync()
    {
        if (selectedItem is null) return;
        IsExecutingAction = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var store = storeFactory.Open(pId);
            var snapshot = await store.GetAsync(pId).ConfigureAwait(false);
            var outputFolder = snapshot?.LatestConfig.ReadConfig().OutputFolder ?? string.Empty;

            var opId = Guid.NewGuid().ToString("N");
            await reviewService.ApproveAsync(pId, selectedItem.JobId, opId, outputFolder).ConfigureAwait(false);
            StatusMessage = $"Photo {selectedItem.PhotoName} approved and published!";
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error approving photo: {ex.Message}";
        }
        finally
        {
            IsExecutingAction = false;
        }
    }

    public async Task ReprocessSelectedAsync()
    {
        if (selectedItem is null || !CanReprocess) return;
        IsExecutingAction = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var opId = Guid.NewGuid().ToString("N");
            var childJobId = await reviewService.ReprocessAsync(pId, selectedItem.JobId, opId).ConfigureAwait(false);
            StatusMessage = $"Photo {selectedItem.PhotoName} queued for reprocessing as job {childJobId.Value[..8]}!";
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error reprocessing photo: {ex.Message}";
        }
        finally
        {
            IsExecutingAction = false;
        }
    }

    public async Task RejectSelectedAsync()
    {
        if (selectedItem is null) return;
        IsExecutingAction = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var opId = Guid.NewGuid().ToString("N");
            await reviewService.RejectAsync(pId, selectedItem.JobId, opId).ConfigureAwait(false);
            StatusMessage = $"Photo {selectedItem.PhotoName} rejected.";
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error rejecting photo: {ex.Message}";
        }
        finally
        {
            IsExecutingAction = false;
        }
    }

    public async Task LeavePendingSelectedAsync()
    {
        if (selectedItem is null) return;
        IsExecutingAction = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            await reviewService.LeavePendingAsync(pId, selectedItem.JobId).ConfigureAwait(false);
            StatusMessage = "Review left pending.";
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error updating review: {ex.Message}";
        }
        finally
        {
            IsExecutingAction = false;
        }
    }
}
