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

    public ClaudeModule(ClaudeSessionMonitor monitor, IClaudeMessenger messenger, IUiDispatcher dispatcher, TimeProvider? time = null)
        : base(ModuleId, "Claude Code", "\uE99A")
    {
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

    public override DipSize GetSize(IslandSize size) => size switch
    {
        IslandSize.Compact => IslandMetrics.CompactWide,
        IslandSize.Large => IslandMetrics.Large with { Height = Math.Clamp(168 + (Sessions.Count * 46), 260, 440) },
        _ => base.GetSize(size),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.SessionsChanged -= OnSessionsChanged;
            _monitor.SessionBecameIdle -= OnSessionBecameIdle;
        }

        base.Dispose(disposing);
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
