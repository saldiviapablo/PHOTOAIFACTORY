using PhotoAIFactory.Application.UI;

namespace PhotoAIFactory.Infrastructure.UI;

public sealed class NullFolderPickerService : IFolderPickerService
{
    public Task<string?> PickFolderAsync(string? title = null) => Task.FromResult<string?>(null);
}

public sealed class NullWindowService : IWindowService
{
    public IntPtr GetMainWindowHandle() => IntPtr.Zero;
}
