using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoAIFactory.Application.UI.ViewModels;

namespace PhotoAIFactory.App.Pages;

public sealed partial class QueuePage : Page
{
    public QueueViewModel ViewModel { get; private set; } = null!;

    public QueuePage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is QueueViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;
            Bindings.Update();
            await vm.RefreshAsync();
        }
    }
}
