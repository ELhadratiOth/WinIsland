using System.Collections.ObjectModel;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Geometry;
using WinIsland.Core.Helpers;
using WinIsland.Core.Layout;
using WinIsland.Core.Mvvm;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Modules;

/// <summary>
/// The last few things you copied (text only, kept in memory, never on disk). Clicking one
/// pastes it into the app you were using; the island never takes focus, so it lands there.
/// </summary>
public sealed class ClipboardModule : IslandModule
{
    public const string ModuleId = "clipboard";
    public const int Capacity = 12;

    private readonly IClipboardService _clipboard;
    private readonly TimeProvider _time;

    public ClipboardModule(IClipboardService clipboard, TimeProvider time)
        : base(ModuleId, "Clipboard", "\uE77F")
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        ClearCommand = new RelayCommand(Clear);
        InteractivePriority = ModulePriority.Background;
        AccentArgb = Palette.Blue;
        _clipboard.Copied += OnCopied;
    }

    public ObservableCollection<ClipboardItem> Items { get; } = [];

    public RelayCommand ClearCommand { get; }

    public override DipSize GetSize(IslandSize size) => size switch
    {
        IslandSize.Expanded => new DipSize(420, Math.Clamp(64 + (Items.Count * 44), 150, 330)),
        _ => base.GetSize(size),
    };

    public void Paste(ClipboardItem item) => _ = Run(_clipboard.PasteAsync(item.Text));

    public void Copy(ClipboardItem item) => _ = Run(_clipboard.CopyAsync(item.Text));

    public void Remove(ClipboardItem item)
    {
        Items.Remove(item);
        Changed();
    }

    protected override void OnViewActiveChanged(bool active)
    {
        if (active)
        {
            DateTimeOffset now = _time.GetUtcNow();
            foreach (ClipboardItem item in Items)
            {
                item.Refresh(now);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clipboard.Copied -= OnCopied;
        }

        base.Dispose(disposing);
    }

    private void OnCopied(object? sender, ClipboardEntry entry)
    {
        string text = entry.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Copying something again moves it to the top instead of duplicating it.
        ClipboardItem? existing = Items.FirstOrDefault(i => i.Text == text);
        if (existing is not null)
        {
            Items.Remove(existing);
        }

        Items.Insert(0, new ClipboardItem(text, entry.CopiedAt, _time.GetUtcNow()));
        while (Items.Count > Capacity)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        Changed();
    }

    private void Clear()
    {
        Items.Clear();
        Changed();
    }

    private void Changed()
    {
        IsAvailable = Items.Count > 0;
        NotifyPresentationChanged();
    }

    private static async Task Run(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(ClipboardModule), "Clipboard action failed", ex);
        }
    }
}

public sealed class ClipboardItem : ObservableObject
{
    private string _detail = string.Empty;

    public ClipboardItem(string text, DateTimeOffset copiedAt, DateTimeOffset now)
    {
        Text = text;
        CopiedAt = copiedAt;
        string singleLine = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        Preview = singleLine.Length > 140 ? singleLine[..140] + "…" : singleLine;
        IsLink = Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https";
        int lines = text.Split('\n').Length;
        Kind = IsLink ? "Link" : lines > 1 ? $"{lines} lines" : $"{text.Length} characters";
        Glyph = IsLink ? "\uE71B" : lines > 1 ? "\uE8A5" : "\uE8C8";
        Refresh(now);
    }

    public string Text { get; }

    public DateTimeOffset CopiedAt { get; }

    public string Preview { get; }

    public bool IsLink { get; }

    public string Kind { get; }

    public string Glyph { get; }

    /// <summary>"Link · 2m".</summary>
    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    internal void Refresh(DateTimeOffset now) => Detail = $"{Kind} · {SizeText.Ago(now - CopiedAt)}";
}
