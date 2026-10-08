using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Helpers;
using WinIsland.Core.Modules;

namespace WinIsland.Core.Tests;

public class HelperModuleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
    private readonly InlineDispatcher _dispatcher = new();

    [Fact]
    public void Clipboard_keeps_recent_unique_copies_newest_first()
    {
        var clipboard = new FakeClipboard();
        using var module = new ClipboardModule(clipboard, _time);
        Assert.False(module.IsAvailable);

        clipboard.Copy("hello");
        clipboard.Copy("https://example.com");
        clipboard.Copy("hello");
        Assert.True(module.IsAvailable);
        Assert.Equal(["hello", "https://example.com"], module.Items.Select(i => i.Text));
        Assert.True(module.Items[1].IsLink);

        for (int i = 0; i < 20; i++)
        {
            clipboard.Copy($"item {i}");
        }

        Assert.Equal(ClipboardModule.Capacity, module.Items.Count);
        module.Paste(module.Items[0]);
        Assert.Equal(["item 19"], clipboard.Pasted);

        module.ClearCommand.Execute(null);
        Assert.False(module.IsAvailable);
    }

    [Fact]
    public void Clipboard_preview_is_one_line_with_age()
    {
        var item = new ClipboardItem("line one\nline two", _time.GetUtcNow().AddMinutes(-5), _time.GetUtcNow());
        Assert.Equal("line one line two", item.Preview);
        Assert.Equal("2 lines · 5m", item.Detail);
    }

    [Fact]
    public void Shelf_opens_for_a_drag_and_keeps_unique_files()
    {
        var shell = new FakeShell();
        using var shelf = new ShelfModule(shell);
        Assert.False(shelf.IsAvailable);

        shelf.IsDropTarget = true;
        Assert.True(shelf.IsAvailable);
        shelf.Add([@"C:\a\report.pdf", @"C:\a\REPORT.pdf", @"C:\b\photo.png"]);
        shelf.IsDropTarget = false;

        Assert.Equal(2, shelf.Items.Count);
        Assert.Equal("\uEA90", shelf.Items[0].Glyph);
        shelf.Open(shelf.Items[1]);
        Assert.Equal([@"C:\b\photo.png"], shell.Opened);

        shelf.ClearCommand.Execute(null);
        Assert.False(shelf.IsAvailable);
    }

    [Fact]
    public void Downloads_show_speed_while_active_and_announce_completion()
    {
        var source = new FakeDownloads();
        using var module = new DownloadsModule(source, new FakeShell(), _dispatcher);
        var attention = new List<AttentionRequest>();
        module.AttentionRequested += (_, r) => attention.Add(r);

        source.Set(new DownloadInfo(@"C:\d\x.zip.crdownload", "x.zip", 1_048_576, _time.GetUtcNow()));
        source.Set(new DownloadInfo(@"C:\d\x.zip.crdownload", "x.zip", 3_145_728, _time.GetUtcNow().AddSeconds(1)));
        Assert.Equal(ModulePriority.Activity, module.CompactPriority);
        Assert.Equal("x.zip", module.CompactText);
        Assert.Equal("3.0 MB · 2.0 MB/s", Assert.Single(module.Active).Detail);

        source.Set();
        source.Finish(@"C:\d\x.zip", "x.zip", 3_145_728);
        Assert.Equal(ModulePriority.Unavailable, module.CompactPriority);
        Assert.True(module.IsAvailable);
        Assert.Equal("x.zip", module.Latest?.Name);
        Assert.Single(attention);
    }

    [Fact]
    public void Sizes_and_ages_read_naturally()
    {
        Assert.Equal("512 B", SizeText.Format(512));
        Assert.Equal("1.5 MB", SizeText.Format(1_572_864));
        Assert.Equal("now", SizeText.Ago(TimeSpan.FromSeconds(30)));
        Assert.Equal("3h", SizeText.Ago(TimeSpan.FromHours(3.5)));
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> Pasted { get; } = [];

        public event EventHandler<ClipboardEntry>? Copied;

        public void Copy(string text) => Copied?.Invoke(this, new ClipboardEntry(text, DateTimeOffset.UtcNow));

        public Task PasteAsync(string text)
        {
            Pasted.Add(text);
            return Task.CompletedTask;
        }

        public Task CopyAsync(string text) => Task.CompletedTask;
    }

    private sealed class FakeShell : IShellLauncher
    {
        public List<string> Opened { get; } = [];

        public void Open(string path) => Opened.Add(path);

        public void Reveal(string path)
        {
        }
    }

    private sealed class FakeDownloads : IDownloadSource
    {
        public IReadOnlyList<DownloadInfo> Active { get; private set; } = [];

        public event EventHandler? Changed;

        public event EventHandler<CompletedDownload>? Completed;

        public void Set(params DownloadInfo[] active)
        {
            Active = active;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Finish(string path, string name, long bytes) =>
            Completed?.Invoke(this, new CompletedDownload(path, name, bytes, DateTimeOffset.UtcNow));
    }
}
