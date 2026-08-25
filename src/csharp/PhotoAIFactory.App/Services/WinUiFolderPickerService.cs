using PhotoAIFactory.Application.UI;
using Windows.Storage.Pickers;

namespace PhotoAIFactory.App.Services;

public sealed class WinUiFolderPickerService(IWindowService windowService) : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(string? title = null)
    {
        var hwnd = windowService.GetMainWindowHandle();
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        if (hwnd != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
