using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using PhotoAIFactory.Application.UI;

namespace PhotoAIFactory.App.Services;

public sealed class WinUiNavigationService : INavigationService
{
    private Frame? frame;
    private IServiceProvider? services;
    private readonly Dictionary<string, (Type PageType, Type ViewModelType)> routes = new(StringComparer.OrdinalIgnoreCase);

    public string CurrentPageKey { get; private set; } = "Projects";
    public object? CurrentParameter { get; private set; }

    public event EventHandler<string>? Navigated;

    public bool CanGoBack => frame?.CanGoBack ?? false;

    public void SetServiceProvider(IServiceProvider serviceProvider)
    {
        services = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public void RegisterFrame(Frame navigationFrame)
    {
        frame = navigationFrame ?? throw new ArgumentNullException(nameof(navigationFrame));
    }

    public void RegisterRoute(string key, Type pageType, Type viewModelType)
    {
        routes[key] = (pageType, viewModelType);
    }

    public void NavigateTo(string pageKey, object? parameter = null)
    {
        CurrentPageKey = pageKey;
        CurrentParameter = parameter;

        if (routes.TryGetValue(pageKey, out var route))
        {
            object? viewModel = null;
            if (services is not null)
            {
                viewModel = services.GetRequiredService(route.ViewModelType);
                if (parameter is not null && viewModel is IParameterizedNavigable navigable)
                {
                    navigable.OnNavigatedTo(parameter);
                }
            }

            frame?.Navigate(route.PageType, viewModel);
            Navigated?.Invoke(this, pageKey);
        }
        else
        {
            Navigated?.Invoke(this, pageKey);
        }
    }

    public void GoBack()
    {
        if (frame is not null && frame.CanGoBack)
        {
            frame.GoBack();
        }
    }
}
