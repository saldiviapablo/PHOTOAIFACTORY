using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using PhotoAIFactory.Application.UI;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class LogsViewModel : ObservableObject
{
    private readonly IErrorLogQueryService errorLogQuery;
    private readonly IProjectContext projectContext;

    private LogLevel? selectedSeverity;
    private string filterText = string.Empty;
    private bool isLoading;
    private ErrorLogEntryDto? selectedLog;
    private string? statusMessage;

    public LogsViewModel(
        IErrorLogQueryService errorLogQuery,
        IProjectContext projectContext)
    {
        this.errorLogQuery = errorLogQuery ?? throw new ArgumentNullException(nameof(errorLogQuery));
        this.projectContext = projectContext ?? throw new ArgumentNullException(nameof(projectContext));

        Logs = new ObservableCollection<ErrorLogEntryDto>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ClearFilterCommand = new RelayCommand(ClearFilter);
    }

    public ObservableCollection<ErrorLogEntryDto> Logs { get; }

    public LogLevel? SelectedSeverity
    {
        get => selectedSeverity;
        set
        {
            if (SetProperty(ref selectedSeverity, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public string FilterText
    {
        get => filterText;
        set
        {
            if (SetProperty(ref filterText, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    public ErrorLogEntryDto? SelectedLog
    {
        get => selectedLog;
        set
        {
            if (SetProperty(ref selectedLog, value))
            {
                OnPropertyChanged(nameof(HasSelectedLog));
                OnPropertyChanged(nameof(HasTechnicalDetails));
            }
        }
    }

    public bool HasSelectedLog => selectedLog is not null;
    public bool HasTechnicalDetails => !string.IsNullOrWhiteSpace(selectedLog?.TechnicalDetails);

    public string? StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand ClearFilterCommand { get; }

    public void ClearFilter()
    {
        selectedSeverity = null;
        filterText = string.Empty;
        OnPropertyChanged(nameof(SelectedSeverity));
        OnPropertyChanged(nameof(FilterText));
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            var pId = projectContext.ActiveProjectId;
            var list = await errorLogQuery.GetErrorLogsAsync(
                projectId: pId,
                minLevel: selectedSeverity,
                limit: 200).ConfigureAwait(false);

            Logs.Clear();
            foreach (var log in list)
            {
                if (!string.IsNullOrWhiteSpace(filterText))
                {
                    if (!log.Message.Contains(filterText, StringComparison.OrdinalIgnoreCase) &&
                        !log.Component.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                Logs.Add(log);
            }
            SelectedLog = Logs.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load logs: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
