using WinIsland.Core.Media;

namespace WinIsland.App.Services.Preview;

/// <summary>A "connected" music library so the preview shows the like button.</summary>
internal sealed class PreviewLibrary : IMusicLibrary
{
    private bool _saved = true;

    public bool IsConnected => true;

    public event EventHandler? ConnectionChanged
    {
        add { }
        remove { }
    }

    public Task<string?> FindTrackAsync(string artist, string title, CancellationToken cancellationToken) => Task.FromResult<string?>("preview");

    public Task<bool> IsSavedAsync(string trackId, CancellationToken cancellationToken) => Task.FromResult(_saved);

    public Task SetSavedAsync(string trackId, bool saved, CancellationToken cancellationToken)
    {
        _saved = saved;
        return Task.CompletedTask;
    }
}
