using WinIsland.Core.Geometry;
using WinIsland.Core.Layout;
using WinIsland.Core.Mvvm;

namespace WinIsland.Core.Modules;

/// <summary>
/// A source of island content (clock, media, Claude Code…). Modules own their data and raise
/// <see cref="PresentationChanged"/> only when something that affects which module is shown,
/// or at what size, changes. All properties are read and written on the UI thread.
/// </summary>
public abstract class IslandModule : ObservableObject, IDisposable
{
    private string _compactText = string.Empty;
    private int _compactPriority = ModulePriority.Unavailable;
    private int _interactivePriority = ModulePriority.Unavailable;
    private bool _isAvailable;
    private bool _isViewActive;

    protected IslandModule(string id, string displayName, string glyph)
    {
        Id = id;
        DisplayName = displayName;
        Glyph = glyph;
    }

    /// <summary>Raised when availability or priorities change (i.e. the island may need to switch module/size).</summary>
    public event EventHandler? PresentationChanged;

    /// <summary>Raised when something happened that is worth briefly expanding the island for.</summary>
    public event EventHandler<TimeSpan>? AttentionRequested;

    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>Segoe Fluent Icons glyph representing the module.</summary>
    public string Glyph { get; }

    /// <summary>Short label shown in the compact pill.</summary>
    public string CompactText
    {
        get => _compactText;
        protected set => SetProperty(ref _compactText, value);
    }

    /// <summary>Priority for the compact (passive) pill. The highest available module wins.</summary>
    public int CompactPriority
    {
        get => _compactPriority;
        protected set
        {
            if (SetProperty(ref _compactPriority, value))
            {
                PresentationChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Priority used to pick the module shown when the user starts interacting.</summary>
    public int InteractivePriority
    {
        get => _interactivePriority;
        protected set
        {
            if (SetProperty(ref _interactivePriority, value))
            {
                PresentationChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Whether the module has anything to show at all.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        protected set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                PresentationChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The size used when the user interacts with this module.</summary>
    public virtual IslandSize InteractiveSize => IslandSize.Expanded;

    /// <summary>
    /// True while this module's expanded/large view is on screen. Modules use it to run
    /// view-only work (e.g. a progress bar tick) only while someone can actually see it.
    /// </summary>
    public bool IsViewActive
    {
        get => _isViewActive;
        set
        {
            if (SetProperty(ref _isViewActive, value))
            {
                OnViewActiveChanged(value);
            }
        }
    }

    public virtual DipSize GetSize(IslandSize size) => IslandMetrics.DefaultSize(size);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void OnViewActiveChanged(bool active)
    {
    }

    /// <summary>Call when <see cref="GetSize"/> would now return something different.</summary>
    protected void NotifyPresentationChanged() => PresentationChanged?.Invoke(this, EventArgs.Empty);

    protected void RequestAttention(TimeSpan? duration = null) =>
        AttentionRequested?.Invoke(this, duration ?? IslandMetrics.DefaultAttentionDuration);

    protected virtual void Dispose(bool disposing)
    {
    }
}

public static class ModulePriority
{
    public const int Unavailable = int.MinValue;
    public const int Background = -10;
    public const int Clock = 0;
    public const int Activity = 20;
    public const int Media = 30;
}
