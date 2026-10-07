using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;

namespace WinIsland.Core.Claude;

public sealed record ClaudeSessionMonitorOptions
{
    /// <summary>Claude Code's transcript root (<c>~/.claude/projects</c> or <c>$CLAUDE_CONFIG_DIR/projects</c>).</summary>
    public string ProjectsDirectory { get; init; } = DefaultProjectsDirectory();

    /// <summary>Upper bound on retained sessions so memory stays flat over days of uptime.</summary>
    public int MaxSessions { get; init; } = 8;

    /// <summary>Sessions untouched for longer than this are forgotten.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(2);

    /// <summary>A session counts as working if its transcript was written within this window.</summary>
    public TimeSpan ActiveWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Coalesces bursts of transcript writes into a single rescan.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(400);

    public static string DefaultProjectsDirectory()
    {
        string? configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string root = !string.IsNullOrWhiteSpace(configDir)
            ? configDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(root, "projects");
    }
}

/// <summary>
/// Discovers local Claude Code sessions by watching transcript files. Works fully offline.
/// Driven by file-system events; the only timer is a one-shot scheduled for the moment the
/// next working session would turn idle.
/// </summary>
public sealed class ClaudeSessionMonitor : IIntegration
{
    private readonly ClaudeSessionMonitorOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private ITimer? _debounceTimer;
    private ITimer? _expiryTimer;
    private bool _running;
    private bool _publishedSinceStart;
    private IReadOnlyList<ClaudeSessionInfo> _sessions = [];

    public ClaudeSessionMonitor(ClaudeSessionMonitorOptions options, TimeProvider time)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Raised on a background thread when <see cref="Sessions"/> changes.</summary>
    public event EventHandler? SessionsChanged;

    /// <summary>Raised on a background thread when a working session stops writing (likely finished or waiting for input).</summary>
    public event EventHandler<ClaudeSessionInfo>? SessionBecameIdle;

    public string Name => "Claude Code sessions";

    public bool RequiresNetwork => false;

    /// <summary>False when no Claude Code transcript directory exists on this machine.</summary>
    public bool IsInstalled { get; private set; }

    /// <summary>Most recent sessions first.</summary>
    public IReadOnlyList<ClaudeSessionInfo> Sessions => Volatile.Read(ref _sessions);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_running)
            {
                return Task.CompletedTask;
            }

            _running = true;
            _publishedSinceStart = false;
            IsInstalled = Directory.Exists(_options.ProjectsDirectory);
            if (IsInstalled)
            {
                _watcher = new FileSystemWatcher(_options.ProjectsDirectory, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
                    InternalBufferSize = 32 * 1024,
                };
                _watcher.Changed += OnFileEvent;
                _watcher.Created += OnFileEvent;
                _watcher.Deleted += OnFileEvent;
                _watcher.Renamed += OnFileEvent;

                // Buffer overflow: we lost events, so just rescan everything.
                _watcher.Error += (_, _) => ScheduleRescan();
                _watcher.EnableRaisingEvents = true;
            }
        }

        Rescan();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _running = false;
            _watcher?.Dispose();
            _watcher = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _expiryTimer?.Dispose();
            _expiryTimer = null;
            _metadata.Clear();
            Publish([], detectIdle: false);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>Forces a rescan (used by tests and after resume from sleep).</summary>
    public void Rescan()
    {
        // Publishing under the lock keeps snapshots ordered when timer callbacks overlap.
        // Listeners only copy the list and post to the UI thread, so this never blocks long.
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            IReadOnlyList<ClaudeSessionInfo> sessions = IsInstalled ? Scan() : [];
            ScheduleExpiry(sessions);
            Publish(sessions, detectIdle: true);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleRescan();

    private void ScheduleRescan()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _debounceTimer ??= _time.CreateTimer(_ => Rescan(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(_options.Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    // Caller holds _gate.
    private List<ClaudeSessionInfo> Scan()
    {
        DateTimeOffset now = _time.GetUtcNow();
        var candidates = new List<(FileInfo File, string ProjectDirectory)>();
        try
        {
            foreach (string projectDirectory in Directory.EnumerateDirectories(_options.ProjectsDirectory))
            {
                // Top level only: sub-agent transcripts live in nested folders and are not sessions.
                foreach (string file in Directory.EnumerateFiles(projectDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    var info = new FileInfo(file);
                    if (now - info.LastWriteTimeUtc <= _options.MaxAge)
                    {
                        candidates.Add((info, projectDirectory));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn(nameof(ClaudeSessionMonitor), "Could not enumerate transcripts", ex);
        }

        var sessions = new List<ClaudeSessionInfo>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((FileInfo file, string projectDirectory) in candidates.OrderByDescending(c => c.File.LastWriteTimeUtc).Take(_options.MaxSessions))
        {
            keep.Add(file.FullName);
            string? cwd = GetWorkingDirectory(file);
            string projectName = cwd is not null
                ? Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } leaf ? leaf : cwd
                : ClaudeTranscriptReader.ProjectNameFromDirectory(Path.GetFileName(projectDirectory));

            DateTimeOffset lastActivity = new(file.LastWriteTimeUtc, TimeSpan.Zero);
            sessions.Add(new ClaudeSessionInfo(
                SessionId: Path.GetFileNameWithoutExtension(file.Name),
                ProjectName: projectName,
                ProjectPath: cwd,
                TranscriptPath: file.FullName,
                LastActivity: lastActivity,
                IsActive: now - lastActivity < _options.ActiveWindow));
        }

        // Forget metadata for sessions that dropped out, so nothing accumulates over time.
        foreach (string stale in _metadata.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _metadata.Remove(stale);
        }

        return sessions;
    }

    private string? GetWorkingDirectory(FileInfo file)
    {
        if (_metadata.TryGetValue(file.FullName, out CachedMetadata cached) && cached.Cwd is not null)
        {
            return cached.Cwd;
        }

        string? cwd = null;
        try
        {
            cwd = ClaudeTranscriptReader.ReadWorkingDirectory(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being written right now; we'll get another event shortly.
        }

        _metadata[file.FullName] = new CachedMetadata(cwd);
        return cwd;
    }

    // Caller holds _gate.
    private void ScheduleExpiry(IReadOnlyList<ClaudeSessionInfo> sessions)
    {
        DateTimeOffset? nextExpiry = null;
        foreach (ClaudeSessionInfo session in sessions)
        {
            if (session.IsActive)
            {
                DateTimeOffset expiry = session.LastActivity + _options.ActiveWindow;
                nextExpiry = nextExpiry is null || expiry < nextExpiry ? expiry : nextExpiry;
            }
        }

        if (nextExpiry is null)
        {
            _expiryTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        TimeSpan due = nextExpiry.Value - _time.GetUtcNow() + TimeSpan.FromMilliseconds(50);
        _expiryTimer ??= _time.CreateTimer(_ => Rescan(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _expiryTimer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    private void Publish(IReadOnlyList<ClaudeSessionInfo> sessions, bool detectIdle)
    {
        IReadOnlyList<ClaudeSessionInfo> previous = Interlocked.Exchange(ref _sessions, sessions);

        // Always announce the first scan so listeners learn IsInstalled even when there are no sessions.
        bool first = !_publishedSinceStart;
        _publishedSinceStart = true;
        if (!first && previous.SequenceEqual(sessions))
        {
            return;
        }

        SessionsChanged?.Invoke(this, EventArgs.Empty);

        if (detectIdle)
        {
            foreach (ClaudeSessionInfo before in previous.Where(p => p.IsActive))
            {
                ClaudeSessionInfo? after = sessions.FirstOrDefault(s => s.SessionId == before.SessionId);
                if (after is { IsActive: false })
                {
                    SessionBecameIdle?.Invoke(this, after);
                }
            }
        }
    }

    private readonly record struct CachedMetadata(string? Cwd);
}
