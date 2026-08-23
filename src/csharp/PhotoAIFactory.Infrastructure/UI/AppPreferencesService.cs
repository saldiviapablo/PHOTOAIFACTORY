using System.Text.Json;
using PhotoAIFactory.Application.Runtime;
using PhotoAIFactory.Application.UI;

namespace PhotoAIFactory.Infrastructure.UI;

public sealed class AppPreferencesService(IAppPaths paths) : IAppPreferencesService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly string preferencesFile = Path.Combine(paths.RootDirectory, "preferences.json");
    private AppPreferencesDto? cachedPreferences;

    public async Task<AppPreferencesDto> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        if (cachedPreferences is not null)
            return cachedPreferences;

        if (File.Exists(preferencesFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(preferencesFile, cancellationToken).ConfigureAwait(false);
                var dto = JsonSerializer.Deserialize<AppPreferencesDto>(json, JsonOpts);
                if (dto is not null)
                {
                    cachedPreferences = dto;
                    return dto;
                }
            }
            catch
            {
                // Fallback to default on corrupt file
            }
        }

        var defaultPrefs = new AppPreferencesDto(
            Theme: "System",
            ShowDiagnostics: false,
            RefreshIntervalSeconds: 3,
            AutoScrollQueue: true,
            EnableHardwareAccelerationPreview: true);

        cachedPreferences = defaultPrefs;
        return defaultPrefs;
    }

    public async Task SavePreferencesAsync(AppPreferencesDto preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        cachedPreferences = preferences;

        var dir = Path.GetDirectoryName(preferencesFile);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(preferences, JsonOpts);
        await File.WriteAllTextAsync(preferencesFile, json, cancellationToken).ConfigureAwait(false);
    }
}
