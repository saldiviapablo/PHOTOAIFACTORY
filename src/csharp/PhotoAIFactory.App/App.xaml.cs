using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using PhotoAIFactory.App.Services;
using PhotoAIFactory.Application.UI;
using PhotoAIFactory.Application.UI.ViewModels;
using PhotoAIFactory.Infrastructure.Hosting;

namespace PhotoAIFactory.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private IHost? host;
    private MainWindow? mainWindow;
    private int isShuttingDown;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var navigationService = new WinUiNavigationService();

        var builder = PhotoAIFactoryHost.CreateBuilder();
        builder.Services.AddSingleton<INavigationService>(navigationService);
        builder.Services.AddSingleton(navigationService);

        host = builder.Build();

        await host.StartAsync();

        mainWindow = new MainWindow(
            host.Services.GetRequiredService<ShellViewModel>(),
            navigationService,
            host.Services);

        mainWindow.Closed += (_, _) =>
        {
            Shutdown();
        };

        mainWindow.Activate();
    }

    private void Shutdown()
    {
        if (Interlocked.Exchange(ref isShuttingDown, 1) != 0)
        {
            return;
        }

        if (host is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                host.StopAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
            }
            finally
            {
                host.Dispose();
                host = null;
            }
        }
    }
}
