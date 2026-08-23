using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoAIFactory.Application.UI.ViewModels;

namespace PhotoAIFactory.App.Pages;

public sealed partial class ProjectsPage : Page
{
    public ProjectsViewModel ViewModel { get; private set; } = null!;

    public ProjectsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is ProjectsViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;
            Bindings.Update();
            await vm.LoadProjectsAsync();
        }
    }
}
