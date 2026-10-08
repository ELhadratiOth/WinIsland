using WinIsland.Core.Media;

namespace WinIsland.App.Services.Preview;

/// <summary>A browser extension that "reports" a YouTube video, so the preview shows 👍 / 👎.</summary>
internal sealed class PreviewReactions : IBrowserReactions
{
    private BrowserReactionState? _state;

    public BrowserReactionState? Current => _state;

    public bool IsExtensionConnected => _state is not null;

    public event EventHandler? Changed;

    public void Report(string title, string channel, bool liked)
    {
        _state = new BrowserReactionState("youtube", title, channel, liked, false, true, DateTimeOffset.UtcNow);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _state = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<bool> SendAsync(BrowserReaction reaction, CancellationToken cancellationToken)
    {
        if (_state is { } s)
        {
            Report(s.Title, s.Channel, reaction == BrowserReaction.Like ? !s.Liked : s.Liked);
        }

        return Task.FromResult(true);
    }

    public Task<bool> LooksLikeYouTubeAsync(string title, CancellationToken cancellationToken) => Task.FromResult(false);
}
