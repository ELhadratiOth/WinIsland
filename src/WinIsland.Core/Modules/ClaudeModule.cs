using System.Collections.ObjectModel;
using WinIsland.Core.Claude;
using WinIsland.Core.Geometry;
using WinIsland.Core.Layout;
using WinIsland.Core.Mvvm;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Modules;

/// <summary>Local Claude Code sessions plus a prompt box. Uses the Large panel when interacting.</summary>
public sealed class ClaudeModule : IslandModule
{
    public const string ModuleId = "claude";

    private readonly ClaudeSessionMonitor _monitor;
    private readonly IClaudeMessenger _messenger;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private ClaudeSessionItem? _selectedSession;
    private string _draftMessage = string.Empty;
    private string _statusMessage = string.Empty;
    private string _attentionText = string.Empty;
    private int _activeCount;
    private bool _isSending;
    private readonly ClaudeUsageTracker? _usage;
    private string _usageToday = string.Empty;
    private string _usageWindow = string.Empty;
    private ClaudeRateLimits? _limits;
    private OneShotTimer? _limitsTimer;
    private readonly HashSet<(int Threshold, DateTimeOffset? Window)> _limitWarnings = [];

    public ClaudeModule(ClaudeSessionMonitor monitor, IClaudeMessenger messenger, IUiDispatcher dispatcher, TimeProvider? time = null, ClaudeUsageTracker? usage = null)
        : base(ModuleId, "Claude Code", "\uE99A")
    {
        _usage = usage;
        if (_usage is not null)
        {
            _usage.Changed += OnUsageChanged;
        }

        _time = time ?? TimeProvider.System;
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        SendCommand = new AsyncRelayCommand(SendAsync, CanSend, ex => StatusMessage = ex.Message);

        _monitor.SessionsChanged += OnSessionsChanged;
        _monitor.SessionBecameIdle += OnSessionBecameIdle;
        Apply(_monitor.Sessions);
    }

    public override IslandSize InteractiveSize => IslandSize.Large;

    public ObservableCollection<ClaudeSessionItem> Sessions { get; } = [];

    public ClaudeSessionItem? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value))
            {
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string DraftMessage
    {
        get => _draftMessage;
        set
        {
            if (SetProperty(ref _draftMessage, value ?? string.Empty))
            {
                SendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Text shown when the island briefly expands because a session went idle.</summary>
    public string AttentionText
    {
        get => _attentionText;
        private set => SetProperty(ref _attentionText, value);
    }

    public int ActiveCount
    {
        get => _activeCount;
        private set
        {
            if (SetProperty(ref _activeCount, value))
            {
                OnPropertyChanged(nameof(HasActiveSessions));
                OnPropertyChanged(nameof(ActiveSummary));
            }
        }
    }

    public bool HasActiveSessions => ActiveCount > 0;

    /// <summary>Badge text in the panel header, e.g. "2 working".</summary>
    public string ActiveSummary => ActiveCount == 0 ? "All idle" : $"{ActiveCount} working";

    public bool HasSessions => Sessions.Count > 0;

    public bool IsSending
    {
        get => _isSending;
        private set => SetProperty(ref _isSending, value);
    }

    public AsyncRelayCommand SendCommand { get; }

    /// <summary>"Today 3.4M tokens · ≈$5.20".</summary>
    public string UsageToday
    {
        get => _usageToday;
        private set => SetProperty(ref _usageToday, value);
    }

    /// <summary>"This window 1.1M · resets 3:00 PM".</summary>
    public string UsageWindow
    {
        get => _usageWindow;
        private set => SetProperty(ref _usageWindow, value);
    }

    public bool HasUsage => _usageToday.Length > 0;

    // ---- Plan limits (Pro / Max), from Claude Code's status line ----

    public bool HasLimits => _limits is { FiveHourPercent: not null } or { WeekPercent: not null };

    public bool HasFiveHourLimit => _limits?.FiveHourPercent is not null;

    public bool HasWeekLimit => _limits?.WeekPercent is not null;

    /// <summary>"23% · resets 3:00 PM".</summary>
    public string FiveHourLimitText => _limits is { FiveHourPercent: { } p } l ? $"{p:0}%{Resets(l.FiveHourResets, false)}" : string.Empty;

    /// <summary>"41% · resets Mon 9:00 AM".</summary>
    public string WeekLimitText => _limits is { WeekPercent: { } p } l ? $"{p:0}%{Resets(l.WeekResets, true)}" : string.Empty;

    /// <summary>0–1 for the bars.</summary>
    public double FiveHourFraction => Math.Clamp((_limits?.FiveHourPercent ?? 0) / 100, 0, 1);

    public double WeekFraction => Math.Clamp((_limits?.WeekPercent ?? 0) / 100, 0, 1);

    /// <summary>Claude orange, then yellow from 80% and red from 95% of the busier window.</summary>
    public uint LimitAccent => Math.Max(_limits?.FiveHourPercent ?? 0, _limits?.WeekPercent ?? 0) switch
    {
        >= 95 => Palette.Red,
        >= 80 => Palette.Yellow,
        _ => Palette.Claude,
    };

    /// <summary>Called (on the UI thread) with the limits Claude Code passed to its status line.</summary>
    public void ApplyLimits(ClaudeRateLimits limits)
    {
        bool had = HasLimits;
        _limits = limits;
        RaiseLimits();
        if (had != HasLimits)
        {
            NotifyPresentationChanged();
        }

        // Warn once per window when it passes 80% and 95%.
        foreach ((double? percent, DateTimeOffset? window, string label) in new[]
        {
            (limits.FiveHourPercent, limits.FiveHourResets, "5-hour"),
            (limits.WeekPercent, limits.WeekResets, "weekly"),
        })
        {
            foreach (int threshold in new[] { 95, 80 })
            {
                if (percent >= threshold && _limitWarnings.Add((threshold, window)))
                {
                    AttentionText = $"{percent:0}% of your {label} limit used{Resets(window, label == "weekly")}";
                    RequestAttention(TimeSpan.FromSeconds(6), threshold >= 95 ? AttentionPriority.Important : AttentionPriority.Normal);
                    break;
                }
            }
        }

        // Drop a window when it resets (Claude Code does the same).
        DateTimeOffset? next = new[] { limits.FiveHourResets, limits.WeekResets }.Where(r => r > _time.GetUtcNow()).Min();
        _limitsTimer ??= new OneShotTimer(_time, _dispatcher, ExpireLimits);
        if (next is { } at)
        {
            _limitsTimer.Start(at - _time.GetUtcNow());
        }
    }

    private void ExpireLimits()
    {
        if (_limits is not { } l)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        ApplyLimits(new ClaudeRateLimits(
            l.FiveHourResets > now ? l.FiveHourPercent : null,
            l.FiveHourResets > now ? l.FiveHourResets : null,
            l.WeekResets > now ? l.WeekPercent : null,
            l.WeekResets > now ? l.WeekResets : null));
    }

    private void RaiseLimits()
    {
        OnPropertyChanged(nameof(HasLimits));
        OnPropertyChanged(nameof(HasFiveHourLimit));
        OnPropertyChanged(nameof(HasWeekLimit));
        OnPropertyChanged(nameof(FiveHourLimitText));
        OnPropertyChanged(nameof(WeekLimitText));
        OnPropertyChanged(nameof(FiveHourFraction));
        OnPropertyChanged(nameof(WeekFraction));
        OnPropertyChanged(nameof(LimitAccent));
    }

    private string Resets(DateTimeOffset? at, bool withDay)
    {
        if (at is not { } when)
        {
            return string.Empty;
        }

        DateTimeOffset local = TimeZoneInfo.ConvertTime(when, _time.LocalTimeZone);
        // The weekly reset is days away: the day is enough (and fits the half-width bar label).
        string format = withDay ? "dddd" : "t";
        return $" · resets {local.ToString(format, System.Globalization.CultureInfo.CurrentCulture)}";
    }

    public override DipSize GetSize(IslandSize size) => size switch
    {
        IslandSize.Compact => IslandMetrics.CompactWide,
        IslandSize.Large => IslandMetrics.Large with { Height = Math.Clamp(168 + (HasUsage ? 24 : 0) + (HasLimits ? 44 : 0) + (Sessions.Count * 46), 260, 500) },
        _ => base.GetSize(size),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.SessionsChanged -= OnSessionsChanged;
            _monitor.SessionBecameIdle -= OnSessionBecameIdle;
            if (_usage is not null)
            {
                _usage.Changed -= OnUsageChanged;
            }

            _limitsTimer?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnViewActiveChanged(bool active)
    {
        // Day and window boundaries pass silently; refresh when the panel opens.
        if (active)
        {
            _usage?.Recompute();
        }
    }

    private void OnUsageChanged(object? sender, EventArgs e)
    {
        UsageSummary summary = _usage!.Summary;
        _dispatcher.TryEnqueue(() => ApplyUsage(summary));
    }

    private void ApplyUsage(UsageSummary summary)
    {
        bool had = HasUsage;
        UsageToday = summary.TodayTokens == 0
            ? string.Empty
            : $"Today {UsageText.Tokens(summary.TodayTokens)} tokens" + (summary.TodayCost is { } cost ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $" · ≈${cost:0.00}") : string.Empty);
        UsageWindow = summary.WindowResetsAt is { } resets
            ? $"5-hour window {UsageText.Tokens(summary.WindowTokens)} · resets {resets.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture)}"
            : string.Empty;
        OnPropertyChanged(nameof(HasUsage));
        if (had != HasUsage)
        {
            NotifyPresentationChanged();
        }
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        IReadOnlyList<ClaudeSessionInfo> sessions = _monitor.Sessions;
        _dispatcher.TryEnqueue(() => Apply(sessions));
    }

    private void OnSessionBecameIdle(object? sender, ClaudeSessionInfo session) =>
        _dispatcher.TryEnqueue(() =>
        {
            AttentionText = $"{session.ProjectName} is waiting for you";
            RequestAttention();
        });

    private void Apply(IReadOnlyList<ClaudeSessionInfo> sessions)
    {
        int oldCount = Sessions.Count;

        // Update in place: reuse rows by session id so selection and scroll position survive.
        for (int i = Sessions.Count - 1; i >= 0; i--)
        {
            if (!sessions.Any(s => s.SessionId == Sessions[i].SessionId))
            {
                Sessions.RemoveAt(i);
            }
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            ClaudeSessionInfo info = sessions[i];
            int existing = IndexOf(info.SessionId);
            if (existing < 0)
            {
                Sessions.Insert(i, new ClaudeSessionItem(info, _time.GetUtcNow()));
            }
            else
            {
                Sessions[existing].Update(info, _time.GetUtcNow());
                if (existing != i)
                {
                    Sessions.Move(existing, i);
                }
            }
        }

        if (SelectedSession is not null && IndexOf(SelectedSession.SessionId) < 0)
        {
            SelectedSession = null;
        }

        SelectedSession ??= Sessions.FirstOrDefault();

        ActiveCount = sessions.Count(s => s.IsActive);
        CompactText = ActiveCount switch
        {
            0 => string.Empty,
            1 => sessions.First(s => s.IsActive).ProjectName,
            _ => $"{ActiveCount} working",
        };

        IsAvailable = _monitor.IsInstalled || Sessions.Count > 0;
        CompactPriority = ActiveCount > 0 ? ModulePriority.Activity : ModulePriority.Unavailable;
        InteractivePriority = ActiveCount > 0 ? ModulePriority.Activity : ModulePriority.Background;

        if (oldCount != Sessions.Count)
        {
            OnPropertyChanged(nameof(HasSessions));
            NotifyPresentationChanged();
        }
    }

    private int IndexOf(string sessionId)
    {
        for (int i = 0; i < Sessions.Count; i++)
        {
            if (Sessions[i].SessionId == sessionId)
            {
                return i;
            }
        }

        return -1;
    }

    private bool CanSend() => !IsSending && SelectedSession is not null && !string.IsNullOrWhiteSpace(DraftMessage);

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        ClaudeSessionItem session = SelectedSession!;
        string message = DraftMessage.Trim();

        // Clear the box immediately; the CLI call runs in the background and may take minutes.
        DraftMessage = string.Empty;
        IsSending = true;
        StatusMessage = $"Sent to {session.ProjectName}…";
        try
        {
            await Task.Run(() => _messenger.SendAsync(session.Info, message, cancellationToken), cancellationToken);
            StatusMessage = $"{session.ProjectName} replied";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            DraftMessage = message;
        }
        finally
        {
            IsSending = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }
}
