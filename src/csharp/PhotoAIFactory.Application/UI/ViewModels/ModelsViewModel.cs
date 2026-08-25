using System.Collections.ObjectModel;
using PhotoAIFactory.Application.UI;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class ModelsViewModel : ObservableObject
{
    private readonly IModelStatusService modelStatusService;
    private bool isLoading;
    private string? statusMessage;

    public ModelsViewModel(IModelStatusService modelStatusService)
    {
        this.modelStatusService = modelStatusService ?? throw new ArgumentNullException(nameof(modelStatusService));

        Components = new ObservableCollection<ComponentHealthCardDto>();
        Models = new ObservableCollection<ModelDescriptorDto>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
    }

    public ObservableCollection<ComponentHealthCardDto> Components { get; }
    public ObservableCollection<ModelDescriptorDto> Models { get; }

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

    public AsyncRelayCommand RefreshCommand { get; }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            var componentStatuses = await modelStatusService.GetComponentStatusesAsync();
            Components.Clear();
            foreach (var c in componentStatuses)
            {
                Components.Add(c);
            }

            var modelDescriptors = await modelStatusService.GetModelDescriptorsAsync();
            Models.Clear();
            foreach (var m in modelDescriptors)
            {
                Models.Add(m);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load models and components: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
