using System.Collections.ObjectModel;
using System.Globalization;
using WinIsland.Core.Geometry;
using WinIsland.Core.Helpers;
using WinIsland.Core.Layout;
using WinIsland.Core.Mvvm;
using WinIsland.Core.Personal;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Modules;

/// <summary>
/// The next meeting: in the pill from 10 minutes before until 5 minutes in, a notice at
/// 5 minutes and at the start, and a Join button for Teams / Zoom / Meet / Webex links.
/// Wakes exactly at those moments; nothing ticks in between.
/// </summary>
public sealed class CalendarModule : IslandModule
{
    public const string ModuleId = "calendar";

    private static readonly TimeSpan ShowBefore = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RemindBefore = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ShowAfterStart = TimeSpan.FromMinutes(5);

    private readonly ICalendarSource _source;
    private readonly IShellLauncher _shell;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly OneShotTimer _timer;
    private readonly HashSet<(string, DateTimeOffset, bool)> _announced = [];
    private IReadOnlyList<CalendarEvent> _events = [];
    private CalendarEvent? _next;

    public CalendarModule(ICalendarSource source, IShellLauncher shell, TimeProvider time, IUiDispatcher dispatcher)
        : base(ModuleId, "Calendar", "\uE787")
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _timer = new OneShotTimer(time, dispatcher, Update);
        JoinCommand = new RelayCommand(() => _shell.Open(_next!.JoinUrl!), () => _next?.JoinUrl is not null);
        AccentArgb = Palette.Red;
        _source.Changed += OnSourceChanged;
        _events = _source.Events;
        Update();
    }

    /// <summary>Meetings are like calls: they still show in focus mode.</summary>
    public override bool AllowedInFocus => true;

    public ObservableCollection<AgendaItem> Today { get; } = [];

    public string NextTitle => _next?.Title ?? string.Empty;

    /// <summary>"in 5 min", "now", "10:30 – 11:00".</summary>
    public string NextWhen { get; private set; } = string.Empty;

    public string NextLocation => _next?.Location is { } l && _next.JoinUrl is null ? l : string.Empty;

    public bool HasJoin => _next?.JoinUrl is not null;

    /// <summary>"Teams", "Zoom"…</summary>
    public string JoinLabel => _next?.JoinUrl switch
    {
        null => string.Empty,
        var u when u.Contains("teams", StringComparison.OrdinalIgnoreCase) => "Join Teams",
        var u when u.Contains("zoom", StringComparison.OrdinalIgnoreCase) => "Join Zoom",
        var u when u.Contains("meet.google", StringComparison.OrdinalIgnoreCase) => "Join Meet",
        var u when u.Contains("webex", StringComparison.OrdinalIgnoreCase) => "Join Webex",
        _ => "Join",
    };

    public bool HasNext => _next is not null;

    public RelayCommand JoinCommand { get; }

    public override DipSize GetSize(IslandSize size) => size switch
    {
        IslandSize.Compact => IslandMetrics.CompactWide,
        IslandSize.Expanded => new DipSize(420, Math.Clamp(104 + (Today.Count * 30), 120, 300)),
        _ => base.GetSize(size),
    };

    protected override void OnViewActiveChanged(bool active)
    {
        if (active)
        {
            Update();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Changed -= OnSourceChanged;
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        IReadOnlyList<CalendarEvent> events = _source.Events;
        _dispatcher.TryEnqueue(() =>
        {
            _events = events;
            Update();
        });
    }

    private void Update()
    {
        DateTimeOffset now = _time.GetUtcNow();
        DateTime today = _time.GetLocalNow().Date;

        // Next timed event that hasn't been running for more than 5 minutes.
        _next = _events.Where(e => !e.AllDay && e.End > now && now - e.Start < ShowAfterStart).MinBy(e => e.Start);

        Today.Clear();
        foreach (CalendarEvent e in _events.Where(e => e.Start.ToLocalTime().Date == today && e.End > now).Take(6))
        {
            Today.Add(new AgendaItem(e.Title, e.AllDay ? "All day" : $"{Time(e.Start)} – {Time(e.End)}", e == _next));
        }

        bool soon = _next is { } n && n.Start - now <= ShowBefore;
        NextWhen = _next is not { } next ? string.Empty
            : next.Start <= now ? "now"
            : next.Start - now < TimeSpan.FromMinutes(60) ? $"in {Math.Max(1, (int)Math.Ceiling((next.Start - now).TotalMinutes))} min"
            : $"{Time(next.Start)} – {Time(next.End)}";

        IsAvailable = Today.Count > 0 || _next is not null;
        CompactPriority = soon ? ModulePriority.Live : ModulePriority.Unavailable;
        InteractivePriority = soon ? ModulePriority.Live : ModulePriority.Background;
        CompactText = _next?.Title ?? string.Empty;
        CompactDetail = soon ? NextWhen : string.Empty;
        OnPropertyChanged(nameof(NextTitle));
        OnPropertyChanged(nameof(NextWhen));
        OnPropertyChanged(nameof(NextLocation));
        OnPropertyChanged(nameof(HasJoin));
        OnPropertyChanged(nameof(JoinLabel));
        OnPropertyChanged(nameof(HasNext));
        JoinCommand.NotifyCanExecuteChanged();
        NotifyPresentationChanged();

        if (_next is { } upcoming)
        {
            // Remind at 5 minutes and at the start, once each.
            bool atStart = now >= upcoming.Start;
            if ((atStart || upcoming.Start - now <= RemindBefore) && _announced.Add((upcoming.Title, upcoming.Start, atStart)))
            {
                RequestAttention(TimeSpan.FromSeconds(8), AttentionPriority.Important);
            }
        }

        ScheduleNextWake(now);
    }

    /// <summary>Wake at the next moment something visible changes (minute label, reminders, end).</summary>
    private void ScheduleNextWake(DateTimeOffset now)
    {
        var moments = new List<DateTimeOffset>();
        foreach (CalendarEvent e in _events.Where(e => !e.AllDay && e.End > now))
        {
            moments.AddRange([e.Start - ShowBefore, e.Start - RemindBefore, e.Start, e.Start + ShowAfterStart, e.End]);
        }

        if (_next is { } next && next.Start - now <= TimeSpan.FromMinutes(60) && next.Start > now)
        {
            // Keep "in N min" accurate.
            moments.Add(now + TimeSpan.FromSeconds(60 - now.Second));
        }

        // Midnight: "today" changes.
        moments.Add(new DateTimeOffset(_time.GetLocalNow().Date.AddDays(1), _time.GetLocalNow().Offset));

        DateTimeOffset wake = moments.Where(m => m > now).DefaultIfEmpty(now.AddHours(1)).Min();
        _timer.Start(wake - now);
    }

    private static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
}

public sealed record AgendaItem(string Title, string When, bool IsNext);
