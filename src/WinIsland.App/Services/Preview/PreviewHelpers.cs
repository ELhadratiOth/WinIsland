using WinIsland.Core.Helpers;

namespace WinIsland.App.Services.Preview;

internal sealed class PreviewClipboard : IClipboardService
{
    public event EventHandler<ClipboardEntry>? Copied;

    public void Emit(string text, TimeSpan ago) => Copied?.Invoke(this, new ClipboardEntry(text, DateTimeOffset.UtcNow - ago));

    public Task PasteAsync(string text) => Task.CompletedTask;

    public Task CopyAsync(string text) => Task.CompletedTask;
}

internal sealed class PreviewDownloads : IDownloadSource
{
    public IReadOnlyList<DownloadInfo> Active => [];

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public event EventHandler<CompletedDownload>? Completed;

    public void Complete(string name, long bytes) =>
        Completed?.Invoke(this, new CompletedDownload(Path.Combine(Path.GetTempPath(), name), name, bytes, DateTimeOffset.UtcNow));
}

internal sealed class PreviewShell : IShellLauncher
{
    public void Open(string path)
    {
    }

    public void Reveal(string path)
    {
    }
}
