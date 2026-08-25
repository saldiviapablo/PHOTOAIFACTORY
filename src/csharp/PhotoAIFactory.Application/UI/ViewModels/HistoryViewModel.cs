using System.Collections.ObjectModel;
using System.Diagnostics;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Domain;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class HistoryViewModel : ObservableObject
{
    private readonly IHistoryQueryService historyQuery;
    private readonly IProjectContext projectContext;
    private readonly INavigationService navigationService;

    private bool isLoading;
    private HistoryItemDto? selectedItem;
    private string? statusMessage;

    public HistoryViewModel(
        IHistoryQueryService historyQuery,
        IProjectContext projectContext,
        INavigationService navigationService)
    {
        this.historyQuery = historyQuery ?? throw new ArgumentNullException(nameof(historyQuery));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));

        HistoryItems = new ObservableCollection<HistoryItemDto>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenOutputImageCommand = new RelayCommand<string>(OpenPublishedFile, path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        OpenContainingFolderCommand = new RelayCommand<string>(OpenFolderInExplorer, path => !string.IsNullOrWhiteSpace(path));
        ViewJobDetailCommand = new RelayCommand<HistoryItemDto>(ViewJobDetail, item => item is not null);
    }

    public ObservableCollection<HistoryItemDto> HistoryItems { get; }

    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    public HistoryItemDto? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (SetProperty(ref selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelectedItem));
                OpenOutputImageCommand.RaiseCanExecuteChanged();
                OpenContainingFolderCommand.RaiseCanExecuteChanged();
                ViewJobDetailCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedItem => selectedItem is not null;

    public string? StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand<string> OpenOutputImageCommand { get; }
    public RelayCommand<string> OpenContainingFolderCommand { get; }
    public RelayCommand<HistoryItemDto> ViewJobDetailCommand { get; }

    public async Task RefreshAsync()
    {
        if (!projectContext.HasActiveProject)
        {
            HistoryItems.Clear();
            SelectedItem = null;
            return;
        }

        IsLoading = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId!;
            var list = await historyQuery.GetHistoryAsync(pId);
            HistoryItems.Clear();
            foreach (var item in list)
            {
                HistoryItems.Add(item);
            }
            SelectedItem = HistoryItems.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load history: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ViewJobDetail(HistoryItemDto? item)
    {
        if (item is null) return;
        navigationService.NavigateTo("JobDetail", item.JobId);
    }

    private static void OpenPublishedFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch
        {
            // Ignore shell launching errors safely
        }
    }

    private static void OpenFolderInExplorer(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        var folder = File.Exists(filePath) ? Path.GetDirectoryName(filePath) : filePath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
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
