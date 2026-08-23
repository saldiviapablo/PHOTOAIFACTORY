using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PhotoAIFactory.Domain;
using PhotoAIFactory.Domain.Projects;

namespace PhotoAIFactory.Application.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<bool>? canExecute = canExecute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T>(Action<T?> execute, Func<T?, bool>? canExecute = null) : ICommand
{
    private readonly Action<T?> execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Func<T?, bool>? canExecute = canExecute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (parameter is null && typeof(T).IsValueType)
        {
            return canExecute?.Invoke(default) ?? true;
        }
        return canExecute?.Invoke((T?)parameter) ?? true;
    }

    public void Execute(object? parameter) => execute((T?)parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> execute;
    private readonly Func<bool>? canExecute;
    private bool isExecuting;

    public event EventHandler? CanExecuteChanged;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    public bool IsExecuting
    {
        get => isExecuting;
        private set
        {
            if (isExecuting != value)
            {
                isExecuting = value;
                RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanExecute(object? parameter)
    {
        if (isExecuting) return false;
        return canExecute?.Invoke() ?? true;
    }

    public async void Execute(object? parameter)
    {
        await ExecuteAsync();
    }

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;

        IsExecuting = true;
        try
        {
            await execute().ConfigureAwait(false);
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> execute;
    private readonly Func<T?, bool>? canExecute;
    private bool isExecuting;

    public event EventHandler? CanExecuteChanged;

    public AsyncRelayCommand(Func<T?, Task> execute, Func<T?, bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    public bool IsExecuting
    {
        get => isExecuting;
        private set
        {
            if (isExecuting != value)
            {
                isExecuting = value;
                RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanExecute(object? parameter)
    {
        if (isExecuting) return false;
        if (parameter is null && typeof(T).IsValueType)
        {
            return canExecute?.Invoke(default) ?? true;
        }
        return canExecute?.Invoke((T?)parameter) ?? true;
    }

    public async void Execute(object? parameter)
    {
        await ExecuteAsync((T?)parameter);
    }

    public async Task ExecuteAsync(T? parameter)
    {
        if (!CanExecute(parameter)) return;

        IsExecuting = true;
        try
        {
            await execute(parameter).ConfigureAwait(false);
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public interface INavigationService
{
    string CurrentPageKey { get; }
    object? CurrentParameter { get; }
    event EventHandler<string>? Navigated;
    void NavigateTo(string pageKey, object? parameter = null);
    bool CanGoBack { get; }
    void GoBack();
}

public interface IParameterizedNavigable
{
    void OnNavigatedTo(object? parameter);
}

public sealed class NullNavigationService : INavigationService
{
    public string CurrentPageKey { get; private set; } = "Projects";
    public object? CurrentParameter { get; private set; }
    public event EventHandler<string>? Navigated;
    public bool CanGoBack => false;

    public void NavigateTo(string pageKey, object? parameter = null)
    {
        CurrentPageKey = pageKey;
        CurrentParameter = parameter;
        Navigated?.Invoke(this, pageKey);
    }

    public void GoBack()
    {
    }
}

public interface IProjectContext : INotifyPropertyChanged
{
    ProjectId? ActiveProjectId { get; }
    string? ActiveProjectName { get; }
    ProjectState? ActiveProjectState { get; }
    bool HasActiveProject { get; }
    void SetActiveProject(ProjectId projectId, string name, ProjectState state);
    void ClearActiveProject();
    void UpdateState(ProjectState state);
}
