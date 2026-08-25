using PhotoAIFactory.Application.UI;

namespace PhotoAIFactory.Application.UI.ViewModels;

public sealed class PreferencesViewModel : ObservableObject
{
    private readonly IAppPreferencesService preferencesService;
    private string theme = "System";
    private bool showDiagnostics;
    private int refreshIntervalSeconds = 3;
    private bool autoScrollQueue = true;
    private bool enableHardwareAcceleration = true;
    private bool isSaving;
    private string? statusMessage;

    public PreferencesViewModel(IAppPreferencesService preferencesService)
    {
        this.preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));

        LoadPreferencesCommand = new AsyncRelayCommand(LoadPreferencesAsync);
        SavePreferencesCommand = new AsyncRelayCommand(SavePreferencesAsync);
    }

    public string Theme
    {
        get => theme;
        set => SetProperty(ref theme, value);
    }

    public bool ShowDiagnostics
    {
        get => showDiagnostics;
        set => SetProperty(ref showDiagnostics, value);
    }

    public int RefreshIntervalSeconds
    {
        get => refreshIntervalSeconds;
        set => SetProperty(ref refreshIntervalSeconds, value);
    }

    public bool AutoScrollQueue
    {
        get => autoScrollQueue;
        set => SetProperty(ref autoScrollQueue, value);
    }

    public bool EnableHardwareAcceleration
    {
        get => enableHardwareAcceleration;
        set => SetProperty(ref enableHardwareAcceleration, value);
    }

    public bool IsSaving
    {
        get => isSaving;
        private set => SetProperty(ref isSaving, value);
    }

    public string? StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public AsyncRelayCommand LoadPreferencesCommand { get; }
    public AsyncRelayCommand SavePreferencesCommand { get; }

    public async Task LoadPreferencesAsync()
    {
        try
        {
            var prefs = await preferencesService.GetPreferencesAsync();
            Theme = prefs.Theme;
            ShowDiagnostics = prefs.ShowDiagnostics;
            RefreshIntervalSeconds = prefs.RefreshIntervalSeconds;
            AutoScrollQueue = prefs.AutoScrollQueue;
            EnableHardwareAcceleration = prefs.EnableHardwareAccelerationPreview;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load preferences: {ex.Message}";
        }
    }

    public async Task SavePreferencesAsync()
    {
        IsSaving = true;
        StatusMessage = null;
        try
        {
            var dto = new AppPreferencesDto(
                Theme,
                ShowDiagnostics,
                Math.Clamp(RefreshIntervalSeconds, 1, 60),
                AutoScrollQueue,
                EnableHardwareAcceleration);

            await preferencesService.SavePreferencesAsync(dto);
            StatusMessage = "Preferences saved successfully.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save preferences: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }
}
