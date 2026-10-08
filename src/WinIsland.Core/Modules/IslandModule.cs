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
    private string _compactDetail = string.Empty;
    private uint _accentArgb = 0xFFF5F5F7;

    protected IslandModule(string id, string displayName, string glyph)
    {
        Id = id;
        DisplayName = displayName;
        Glyph = glyph;
    }

    /// <summary>Raised when availability or priorities change (i.e. the island may need to switch module/size).</summary>
    public event EventHandler? PresentationChanged;

    /// <summary>Raised when something happened that is worth briefly expanding the island for.</summary>
    public event EventHandler<AttentionRequest>? AttentionRequested;

    /// <summary>Raised when an earlier attention request no longer applies (e.g. an approval was answered).</summary>
    public event EventHandler? AttentionEnded;

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

    /// <summary>Secondary text on the right of the generic compact pill (e.g. "12:04", "76%").</summary>
    public string CompactDetail
    {
        get => _compactDetail;
        protected set => SetProperty(ref _compactDetail, value);
    }

    /// <summary>ARGB tint of the module's glyph and highlights.</summary>
    public uint AccentArgb
    {
        get => _accentArgb;
        protected set => SetProperty(ref _accentArgb, value);
    }

    /// <summary>False for modules that only appear on their own (notices) and shouldn't get a switcher chip.</summary>
    public virtual bool ShowInSwitcher => true;

    /// <summary>Whether the module may show while focus mode is on (timers, calls, approvals…).</summary>
    public virtual bool AllowedInFocus => false;

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

    /// <param name="duration">How long to stay expanded.</param>
    /// <param name="priority">A request never replaces a running one of higher priority (see <see cref="AttentionPriority"/>).</param>
    /// <param name="size">The size to expand to; the module's <see cref="GetSize"/> gives its dimensions.</param>
    protected void RequestAttention(TimeSpan? duration = null, int priority = AttentionPriority.Normal, IslandSize size = IslandSize.Expanded) =>
        AttentionRequested?.Invoke(this, new AttentionRequest(duration ?? IslandMetrics.DefaultAttentionDuration, priority, size));

    protected void EndAttention() => AttentionEnded?.Invoke(this, EventArgs.Empty);

    protected virtual void Dispose(bool disposing)
    {
    }
}

public sealed record AttentionRequest(TimeSpan Duration, int Priority, IslandSize Size);

public static class AttentionPriority
{
    /// <summary>Feedback for something the user just did (volume keys…).</summary>
    public const int Feedback = 10;
    public const int Normal = 20;
    public const int Important = 40;

    /// <summary>Waiting on the user (permission prompts).</summary>
    public const int Blocking = 60;
}

public static class ModulePriority
{
    public const int Unavailable = int.MinValue;
    public const int Background = -10;
    public const int Clock = 0;
    public const int Activity = 20;
    public const int Media = 30;

    /// <summary>Running timers, recording indicators: more important than music.</summary>
    public const int Live = 35;

    /// <summary>Something is waiting on the user.</summary>
    public const int Blocking = 50;
}
