namespace WinIsland.Core.Threading;

/// <summary>Posts work to the UI thread. Implemented over the WinUI DispatcherQueue in the app.</summary>
public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    /// <summary>Queues <paramref name="action"/> on the UI thread. Returns false if the UI is shutting down.</summary>
    bool TryEnqueue(Action action);
}

public static class UiDispatcherExtensions
{
    /// <summary>Runs inline when already on the UI thread, otherwise queues.</summary>
    public static void Run(this IUiDispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            dispatcher.TryEnqueue(action);
        }
    }
}
