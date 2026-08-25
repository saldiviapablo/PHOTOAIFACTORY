namespace PhotoAIFactory.Application.UI;

public interface IFolderPickerService
{
    Task<string?> PickFolderAsync(string? title = null);
}
