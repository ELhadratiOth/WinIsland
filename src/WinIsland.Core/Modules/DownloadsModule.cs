using System.Collections.ObjectModel;
using WinIsland.Core.Geometry;
using WinIsland.Core.Helpers;
using WinIsland.Core.Layout;
using WinIsland.Core.Mvvm;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Modules;

/// <summary>
/// Browser downloads: live size and speed while downloading (the compact pill), a notice when
/// one finishes with Open / Show in folder, and the last few finished downloads.
/// </summary>
public sealed class DownloadsModule : IslandModule
{
    public const string ModuleId = "downloads";
    private const int RecentCapacity = 5;

    private readonly IDownloadSource _source;
    private readonly IShellLauncher _shell;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, (long Bytes, DateTimeOffset At, double Speed)> _speeds = new(StringComparer.OrdinalIgnoreCase);
    private CompletedItem? _latest;

    public DownloadsModule(IDownloadSource source, IShellLauncher shell, IUiDispatcher dispatcher)
        : base(ModuleId, "Downloads", "")
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        OpenLatestCommand = new RelayCommand(() => Open(_latest), () => _latest is not null);
        RevealLatestCommand = new RelayCommand(() => Reveal(_latest), () => _latest is not null);
        AccentArgb = Palette.Blue;
        _source.Changed += OnSourceChanged;
        _source.Completed += OnCompleted;
        Apply(_source.Active);
    }

    public ObservableCollection<DownloadItem> Active { get; } = [];

    public ObservableCollection<CompletedItem> Recent { get; } = [];

    public bool HasActive => Active.Count > 0;

    public bool HasRecent => Recent.Count > 0;

    /// <summary>The most recent finished download (shown in the notice).</summary>
    public CompletedItem? Latest
    {
        get => _latest;
        private set
        {
            if (SetProperty(ref _latest, value))
            {
                OpenLatestCommand.NotifyCanExecuteChanged();
                RevealLatestCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public RelayCommand OpenLatestCommand { get; }

    public RelayCommand RevealLatestCommand { get; }

    public override DipSize GetSize(IslandSize size) => size switch
    {
        IslandSize.Compact => IslandMetrics.CompactWide,
        IslandSize.Expanded => new DipSize(420, Math.Clamp(72 + ((Active.Count + Recent.Count) * 46), 120, 340)),
        _ => base.GetSize(size),
    };

    public void Open(CompletedItem? item)
    {
        if (item is not null)
        {
            _shell.Open(item.Path);
        }
    }

    public void Reveal(CompletedItem? item)
    {
        if (item is not null)
        {
            _shell.Reveal(item.Path);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Changed -= OnSourceChanged;
            _source.Completed -= OnCompleted;
        }

        base.Dispose(disposing);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        IReadOnlyList<DownloadInfo> active = _source.Active;
        _dispatcher.TryEnqueue(() => Apply(active));
    }

    private void OnCompleted(object? sender, CompletedDownload download) => _dispatcher.TryEnqueue(() =>
    {
        var item = new CompletedItem(download.Path, download.Name, SizeText.Format(download.Bytes), SizeText.GlyphFor(download.Path));
        Recent.Insert(0, item);
        while (Recent.Count > RecentCapacity)
        {
            Recent.RemoveAt(Recent.Count - 1);
        }

        Latest = item;
        OnPropertyChanged(nameof(HasRecent));
        IsAvailable = true;
        NotifyPresentationChanged();
        RequestAttention(TimeSpan.FromSeconds(4), AttentionPriority.Normal);
    });

    private void Apply(IReadOnlyList<DownloadInfo> active)
    {
        Active.Clear();
        foreach (DownloadInfo info in active)
        {
            // Speed from the previous sample of the same file, smoothed a little.
            double speed = 0;
            if (_speeds.TryGetValue(info.PartialPath, out var previous) && info.UpdatedAt > previous.At)
            {
                double instant = (info.Bytes - previous.Bytes) / (info.UpdatedAt - previous.At).TotalSeconds;
                speed = previous.Speed <= 0 ? instant : (previous.Speed * 0.6) + (instant * 0.4);
            }

            _speeds[info.PartialPath] = (info.Bytes, info.UpdatedAt, speed);
            string detail = SizeText.Format(info.Bytes) + (speed > 0 ? $" · {SizeText.Format((long)speed)}/s" : string.Empty);
            Active.Add(new DownloadItem(info.Name, detail, SizeText.GlyphFor(info.Name)));
        }

        foreach (string gone in _speeds.Keys.Where(k => !active.Any(a => string.Equals(a.PartialPath, k, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _speeds.Remove(gone);
        }

        OnPropertyChanged(nameof(HasActive));
        IsAvailable = Active.Count > 0 || Recent.Count > 0;
        CompactPriority = Active.Count > 0 ? ModulePriority.Activity : ModulePriority.Unavailable;
        InteractivePriority = Active.Count > 0 ? ModulePriority.Activity : ModulePriority.Background;
        CompactText = Active.Count == 1 ? Active[0].Name : $"{Active.Count} downloads";
        CompactDetail = Active.Count == 1 ? SizeText.Format(active[0].Bytes) : string.Empty;
        NotifyPresentationChanged();
    }
}

public sealed record DownloadItem(string Name, string Detail, string Glyph);

public sealed record CompletedItem(string Path, string Name, string Detail, string Glyph);
