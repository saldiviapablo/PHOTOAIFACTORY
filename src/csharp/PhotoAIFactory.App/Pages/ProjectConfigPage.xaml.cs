using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoAIFactory.Application.UI.ViewModels;

namespace PhotoAIFactory.App.Pages;

public sealed partial class ProjectConfigPage : Page
{
    public ProjectConfigViewModel ViewModel { get; private set; } = null!;

    public ProjectConfigPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ProjectConfigViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;
            Bindings.Update();
            await vm.RefreshAsync();
        }
    }
}
