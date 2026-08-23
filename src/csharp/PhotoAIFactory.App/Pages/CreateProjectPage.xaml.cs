using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoAIFactory.Application.UI.ViewModels;

namespace PhotoAIFactory.App.Pages;

public sealed partial class CreateProjectPage : Page
{
    public CreateProjectViewModel ViewModel { get; private set; } = null!;

    public CreateProjectPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is CreateProjectViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;
            Bindings.Update();
        }
    }

    private async void BrowseInputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current);
        if (hwnd != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.InputFolder = folder.Path;
        }
    }

    private async void BrowseOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current);
        if (hwnd != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.OutputFolder = folder.Path;
        }
    }
}
