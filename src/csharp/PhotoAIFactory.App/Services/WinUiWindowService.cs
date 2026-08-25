using Microsoft.UI.Xaml;
using PhotoAIFactory.Application.UI;

namespace PhotoAIFactory.App.Services;

public sealed class WinUiWindowService : IWindowService
{
    private IntPtr hwnd;

    public void RegisterMainWindow(Window window)
    {
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
    }

    public IntPtr GetMainWindowHandle() => hwnd;
}
