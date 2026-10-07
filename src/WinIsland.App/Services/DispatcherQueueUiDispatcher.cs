using Microsoft.UI.Dispatching;
using WinIsland.Core.Threading;

namespace WinIsland.App.Services;

internal sealed class DispatcherQueueUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public bool TryEnqueue(Action action) => queue.TryEnqueue(() => action());
}
