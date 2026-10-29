using CommunityToolkit.Mvvm.ComponentModel;

namespace TelltaleKube.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

public abstract partial class PageViewModel : ViewModelBase, IAsyncDisposable
{
    public abstract string Title { get; }

    /// <summary>Called when the page is removed from navigation.</summary>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return OnClosedAsync();
    }

    /// <summary>Release watchers, streams and sessions here.</summary>
    protected virtual ValueTask OnClosedAsync() => ValueTask.CompletedTask;
}
