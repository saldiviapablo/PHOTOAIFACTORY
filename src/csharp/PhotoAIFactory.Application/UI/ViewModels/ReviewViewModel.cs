using System.Collections.ObjectModel;
using PhotoAIFactory.Application.Projects;
using PhotoAIFactory.Application.Qa;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;
using PhotoAIFactory.Domain.Qa;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class ReviewViewModel : ObservableObject
{
    private readonly IReviewQueryService reviewQuery;
    private readonly IReviewService reviewService;
    private readonly IProjectStoreFactory storeFactory;
    private readonly IProjectContext projectContext;
    private readonly IThumbnailService thumbnailService;

    private bool isLoading;
    private bool isExecutingAction;
    private ReviewItemDto? selectedItem;
    private string? statusMessage;

    public ReviewViewModel(
        IReviewQueryService reviewQuery,
        IReviewService reviewService,
        IProjectStoreFactory storeFactory,
        IProjectContext projectContext,
        IThumbnailService thumbnailService)
    {
        this.reviewQuery = reviewQuery ?? throw new ArgumentNullException(nameof(reviewQuery));
        this.reviewService = reviewService ?? throw new ArgumentNullException(nameof(reviewService));
        this.storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));
        this.thumbnailService = thumbnailService ?? throw new ArgumentNullException(nameof(thumbnailService));

        PendingReviews = new ObservableCollection<ReviewItemDto>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ApproveCommand = new AsyncRelayCommand(ApproveSelectedAsync, () => CanApprove);
        ReprocessCommand = new AsyncRelayCommand(ReprocessSelectedAsync, () => CanReprocess && !isExecutingAction);
        RejectCommand = new AsyncRelayCommand(RejectSelectedAsync, () => CanReject);
        LeavePendingCommand = new AsyncRelayCommand(LeavePendingSelectedAsync, () => CanLeavePending);
    }

    public ObservableCollection<ReviewItemDto> PendingReviews { get; }

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

    public ReviewItemDto? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (SetProperty(ref selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelectedItem));
                OnPropertyChanged(nameof(CanReprocess));
                OnPropertyChanged(nameof(CanApprove));
                OnPropertyChanged(nameof(CanReject));
                OnPropertyChanged(nameof(CanLeavePending));
                OnPropertyChanged(nameof(ApproveButtonLabel));
                ApproveCommand.RaiseCanExecuteChanged();
                ReprocessCommand.RaiseCanExecuteChanged();
                RejectCommand.RaiseCanExecuteChanged();
                LeavePendingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedItem => selectedItem is not null;
    public bool CanApprove => selectedItem?.JobId is not null && !isExecutingAction && (selectedItem.JobState == JobState.ReviewPre || selectedItem.JobState == JobState.ReviewFinal);
    public bool CanReject => selectedItem?.JobId is not null && !isExecutingAction && (selectedItem.JobState == JobState.ReviewPre || selectedItem.JobState == JobState.ReviewFinal);
    public bool CanReprocess => selectedItem?.JobId is not null && !isExecutingAction && selectedItem.JobState == JobState.ReviewFinal;
    public bool CanLeavePending => selectedItem?.JobId is not null && !isExecutingAction && (selectedItem.JobState == JobState.ReviewPre || selectedItem.JobState == JobState.ReviewFinal);

    public string ApproveButtonLabel => selectedItem?.JobState switch
    {
        JobState.ReviewPre => "Approve & Continue",
        JobState.ReviewFinal => "Approve & Publish",
        _ => "Approve"
    };

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
            var list = await reviewQuery.GetPendingReviewsAsync(pId);
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
        if (selectedItem?.JobId is null || !CanApprove) return;
        IsExecutingAction = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var opId = Guid.NewGuid().ToString("N");

            if (selectedItem.JobState == JobState.ReviewPre)
            {
                await reviewService.ApprovePreselectionAsync(pId, selectedItem.JobId, opId);
                StatusMessage = $"Photo {selectedItem.PhotoName} approved for processing!";
            }
            else if (selectedItem.JobState == JobState.ReviewFinal)
            {
                var store = storeFactory.Open(pId);
                var snapshot = await store.GetAsync(pId);
                var outputFolder = snapshot?.LatestConfig.ReadConfig().OutputFolder ?? string.Empty;

                await reviewService.ApproveFinalAsync(pId, selectedItem.JobId, opId, outputFolder);
                StatusMessage = $"Photo {selectedItem.PhotoName} approved and published!";
            }

            await RefreshAsync();
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
        if (selectedItem?.JobId is null || !CanReprocess) return;
        IsExecutingAction = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var opId = Guid.NewGuid().ToString("N");
            var childJobId = await reviewService.ReprocessAsync(pId, selectedItem.JobId, opId);
            StatusMessage = $"Photo {selectedItem.PhotoName} queued for reprocessing as job {childJobId.Value[..8]}!";
            await RefreshAsync();
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
        if (selectedItem?.JobId is null || !CanReject) return;
        IsExecutingAction = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var opId = Guid.NewGuid().ToString("N");

            if (selectedItem.JobState == JobState.ReviewPre)
            {
                await reviewService.RejectPreselectionAsync(pId, selectedItem.JobId, opId);
            }
            else if (selectedItem.JobState == JobState.ReviewFinal)
            {
                await reviewService.RejectFinalAsync(pId, selectedItem.JobId, opId);
            }

            StatusMessage = $"Photo {selectedItem.PhotoName} rejected.";
            await RefreshAsync();
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
        if (selectedItem?.JobId is null || !CanLeavePending) return;
        IsExecutingAction = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            await reviewService.LeavePendingAsync(pId, selectedItem.JobId);
            StatusMessage = "Review left pending.";
            await RefreshAsync();
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
