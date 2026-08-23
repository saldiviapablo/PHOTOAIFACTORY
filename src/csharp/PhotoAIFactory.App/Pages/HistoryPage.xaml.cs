using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PhotoAIFactory.Application.UI.ViewModels;

namespace PhotoAIFactory.App.Pages;

public sealed partial class HistoryPage : Page
{
    public HistoryViewModel ViewModel { get; private set; } = null!;

    public HistoryPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is HistoryViewModel vm)
        {
            ViewModel = vm;
            DataContext = vm;
            Bindings.Update();
            await vm.RefreshAsync();
        }
    }
}
