using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhotoAIFactory.App.Pages;
using PhotoAIFactory.App.Services;
using PhotoAIFactory.Application.UI.ViewModels;

namespace PhotoAIFactory.App;

public sealed partial class MainWindow : Window
{
    private readonly WinUiNavigationService navigationService;
    private readonly IServiceProvider services;

    public ShellViewModel ViewModel { get; }

    public MainWindow(
        ShellViewModel viewModel,
        WinUiNavigationService navigationService,
        IServiceProvider services)
    {
        InitializeComponent();
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        this.navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        this.services = services ?? throw new ArgumentNullException(nameof(services));

        this.navigationService.RegisterFrame(ContentFrame);
        this.navigationService.SetServiceProvider(services);
        RegisterPages();

        this.navigationService.NavigateTo("Projects");
    }

    private void RegisterPages()
    {
        navigationService.RegisterRoute("Projects", typeof(ProjectsPage), typeof(ProjectsViewModel));
        navigationService.RegisterRoute("CreateProject", typeof(CreateProjectPage), typeof(CreateProjectViewModel));
        navigationService.RegisterRoute("Dashboard", typeof(DashboardPage), typeof(DashboardViewModel));
        navigationService.RegisterRoute("Queue", typeof(QueuePage), typeof(QueueViewModel));
        navigationService.RegisterRoute("JobDetail", typeof(JobDetailPage), typeof(JobDetailViewModel));
        navigationService.RegisterRoute("Review", typeof(ReviewPage), typeof(ReviewViewModel));
        navigationService.RegisterRoute("ProjectConfig", typeof(ProjectConfigPage), typeof(ProjectConfigViewModel));
        navigationService.RegisterRoute("History", typeof(HistoryPage), typeof(HistoryViewModel));
        navigationService.RegisterRoute("Models", typeof(ModelsPage), typeof(ModelsViewModel));
        navigationService.RegisterRoute("Logs", typeof(LogsPage), typeof(LogsViewModel));
        navigationService.RegisterRoute("Preferences", typeof(PreferencesPage), typeof(PreferencesViewModel));
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item && item.Tag is string pageKey)
        {
            navigationService.NavigateTo(pageKey);
        }
    }
}
