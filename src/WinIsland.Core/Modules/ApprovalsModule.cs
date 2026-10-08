using WinIsland.Core.Claude;
using WinIsland.Core.Geometry;
using WinIsland.Core.Layout;
using WinIsland.Core.Mvvm;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Modules;

/// <summary>
/// Claude Code permission prompts answered from the island: the request takes the pill and
/// expands (nothing less important can replace it) until Allow, Deny or "Ask in terminal".
/// Also shows Claude's "waiting for your input" notes.
/// </summary>
public sealed class ApprovalsModule : IslandModule
{
    public const string ModuleId = "approvals";

    private static readonly TimeSpan HoldOpen = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(6);

    private readonly IUiDispatcher _dispatcher;
    private readonly List<ApprovalRequest> _pending = [];
    private readonly OneShotTimer _noticeTimer;
    private ClaudeNotice? _notice;

    public ApprovalsModule(IClaudeHookServer server, TimeProvider time, IUiDispatcher dispatcher)
        : base(ModuleId, "Approvals", "\uE8D7")
    {
        ArgumentNullException.ThrowIfNull(server);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _noticeTimer = new OneShotTimer(time, dispatcher, () =>
        {
            _notice = null;
            Changed();
        });
        AllowCommand = new RelayCommand(() => Resolve(ApprovalDecision.Allow), () => Current is not null);
        DenyCommand = new RelayCommand(() => Resolve(ApprovalDecision.Deny), () => Current is not null);
        AskCommand = new RelayCommand(() => Resolve(ApprovalDecision.Ask), () => Current is not null);
        AccentArgb = Palette.Claude;
        server.PermissionRequested += (_, request) => _dispatcher.TryEnqueue(() => Add(request));
        server.NoticeReceived += (_, notice) => _dispatcher.TryEnqueue(() => ShowNotice(notice));
    }

    public override bool AllowedInFocus => true;

    public ApprovalRequest? Current => _pending.Count > 0 ? _pending[0] : null;

    public bool HasRequest => Current is not null;

    public bool HasNoticeOnly => Current is null && _notice is not null;

    public string Title => Current?.Action ?? "Claude Code";

    public string ToolLabel => Current?.ToolLabel ?? string.Empty;

    public string Summary => Current?.Summary ?? _notice?.Message ?? string.Empty;

    public string Project => Current?.Project ?? _notice?.Project ?? string.Empty;

    /// <summary>"+2 more waiting".</summary>
    public string MoreText => _pending.Count > 1 ? $"+{_pending.Count - 1} more waiting" : string.Empty;

    public RelayCommand AllowCommand { get; }

    public RelayCommand DenyCommand { get; }

    public RelayCommand AskCommand { get; }

    public override DipSize GetSize(IslandSize size) => size switch
    {
        IslandSize.Compact => IslandMetrics.CompactWide,
        IslandSize.Expanded => HasRequest ? new DipSize(440, 176) : new DipSize(400, 84),
        _ => base.GetSize(size),
    };

    private void Add(ApprovalRequest request)
    {
        _pending.Add(request);

        // Claude Code gave up (timeout, the user answered in the terminal, session ended).
        request.Resolved += (_, _) => _dispatcher.TryEnqueue(() => Remove(request));
        Changed();
        RequestAttention(HoldOpen, AttentionPriority.Blocking);
    }

    private void Resolve(ApprovalDecision decision) => Current?.Resolve(decision);

    private void Remove(ApprovalRequest request)
    {
        if (_pending.Remove(request))
        {
            Changed();
            if (_pending.Count == 0)
            {
                EndAttention();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _noticeTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowNotice(ClaudeNotice notice)
    {
        _notice = notice;
        _noticeTimer.Start(NoticeDuration);
        Changed();
        if (_pending.Count == 0)
        {
            RequestAttention(NoticeDuration, AttentionPriority.Normal);
        }
    }

    private void Changed()
    {
        bool request = _pending.Count > 0;
        IsAvailable = request || _notice is not null;
        CompactPriority = request ? ModulePriority.Blocking : ModulePriority.Unavailable;
        InteractivePriority = request ? ModulePriority.Blocking : ModulePriority.Unavailable;
        CompactText = request ? "Claude needs approval" : "Claude is waiting";
        CompactDetail = request ? Current!.ToolLabel : string.Empty;
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(HasRequest));
        OnPropertyChanged(nameof(HasNoticeOnly));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolLabel));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(MoreText));
        AllowCommand.NotifyCanExecuteChanged();
        DenyCommand.NotifyCanExecuteChanged();
        AskCommand.NotifyCanExecuteChanged();
        NotifyPresentationChanged();
    }
}
