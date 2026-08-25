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
}
