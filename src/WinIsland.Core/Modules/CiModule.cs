using System.Collections.ObjectModel;
using WinIsland.Core.Geometry;
using WinIsland.Core.GitHub;
using WinIsland.Core.Helpers;
using WinIsland.Core.Layout;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Modules;

/// <summary>
/// GitHub Actions for the repositories you follow: a live pill while builds run, a notice
/// when one finishes (green or red), and the latest run of each workflow.
/// </summary>
public sealed class CiModule : IslandModule
{
    public const string ModuleId = "ci";
    private const int MaxRuns = 6;

    private readonly ICiSource _source;
    private readonly IShellLauncher _shell;
    private readonly TimeProvider _time;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<long, RunState> _known = [];
    private bool _primed;
    private WorkflowRun? _finished;

    public CiModule(ICiSource source, IShellLauncher shell, TimeProvider time, IUiDispatcher dispatcher)
        : base(ModuleId, "Builds", "\uE943")
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _source.Changed += OnSourceChanged;
        Apply(_source.Runs);
    }

    public ObservableCollection<RunItem> Runs { get; } = [];

    /// <summary>"Build passed" / "Build failed" for the notice; empty otherwise.</summary>
    public string Headline { get; private set; } = string.Empty;

    public bool HasHeadline => Headline.Length > 0;

    /// <summary>Green or red for the last finished build's headline.</summary>
    public uint HeadlineArgb { get; private set; } = Palette.Green;

    public override DipSize GetSize(IslandSize size) => size switch
    {
        IslandSize.Compact => IslandMetrics.CompactWide,
        IslandSize.Expanded => new DipSize(440, Math.Clamp(60 + (Runs.Count * 48) + (HasHeadline ? 22 : 0), 120, 380)),
        _ => base.GetSize(size),
    };

    public void Open(RunItem item) => _shell.Open(item.Url);

    protected override void OnViewActiveChanged(bool active)
    {
        if (active)
        {
            Apply(_source.Runs);
        }
        else if (HasHeadline)
        {
            Headline = string.Empty;
            OnPropertyChanged(nameof(Headline));
            OnPropertyChanged(nameof(HasHeadline));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Changed -= OnSourceChanged;
        }

        base.Dispose(disposing);
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        IReadOnlyList<WorkflowRun> runs = _source.Runs;
        _dispatcher.TryEnqueue(() => Apply(runs));
    }

    private void Apply(IReadOnlyList<WorkflowRun> runs)
    {
        // A run we saw queued/running that has now completed is worth a notice. The first
        // fetch only primes the state, so starting WinIsland doesn't announce old builds.
        WorkflowRun? finished = null;
        foreach (WorkflowRun run in runs)
        {
            if (_primed && _known.TryGetValue(run.Id, out RunState before) &&
                before is RunState.Queued or RunState.Running && !run.IsActive)
            {
                finished = run.State == RunState.Failed || finished is null ? run : finished;
            }

            _known[run.Id] = run.State;
        }

        _primed = runs.Count > 0 || _primed;

        DateTimeOffset now = _time.GetUtcNow();
        Runs.Clear();
        foreach (WorkflowRun run in WorkflowRunsParser.Latest(runs, MaxRuns))
        {
            Runs.Add(RunItem.From(run, now));
        }

        int active = runs.Count(r => r.IsActive);
        IsAvailable = Runs.Count > 0;
        CompactPriority = active > 0 ? ModulePriority.Activity : ModulePriority.Unavailable;
        InteractivePriority = active > 0 ? ModulePriority.Activity : ModulePriority.Background;
        WorkflowRun? first = runs.FirstOrDefault(r => r.IsActive);
        CompactText = active > 0 ? $"Building {RepoName(first!.Repo)}" : string.Empty;
        CompactDetail = active > 1 ? $"{active}" : string.Empty;
        AccentArgb = active > 0 ? Palette.Yellow : HeadlineArgb;

        if (finished is not null)
        {
            _finished = finished;
            bool failed = finished.State == RunState.Failed;
            Headline = $"{(failed ? "Build failed" : finished.State == RunState.Succeeded ? "Build passed" : "Build finished")} · {RepoName(finished.Repo)}";
            OnPropertyChanged(nameof(Headline));
            OnPropertyChanged(nameof(HasHeadline));
            HeadlineArgb = failed ? Palette.Red : Palette.Green;
            OnPropertyChanged(nameof(HeadlineArgb));
            AccentArgb = HeadlineArgb;
            RequestAttention(TimeSpan.FromSeconds(failed ? 8 : 5), failed ? AttentionPriority.Important : AttentionPriority.Normal);
        }

        NotifyPresentationChanged();
    }

    private static string RepoName(string repo) => repo.Split('/')[^1];
}

public sealed record RunItem(string Title, string Detail, string Url, uint StatusArgb, string StatusGlyph, bool IsActive)
{
    public static RunItem From(WorkflowRun run, DateTimeOffset now)
    {
        (uint color, string glyph) = run.State switch
        {
            RunState.Succeeded => (Palette.Green, "\uE73E"),
            RunState.Failed => (Palette.Red, "\uE711"),
            RunState.Running or RunState.Queued => (Palette.Yellow, "\uE916"),
            _ => (0xFF8E8E93u, "\uE738"),
        };

        string repo = run.Repo.Split('/')[^1];
        string age = SizeText.Ago(now - run.UpdatedAt);
        return new RunItem(
            string.IsNullOrWhiteSpace(run.Title) ? run.Workflow : run.Title,
            $"{repo} · {run.Workflow} · {run.Branch} · {(run.IsActive ? "running" : age)}",
            run.Url,
            color,
            glyph,
            run.IsActive);
    }
}
