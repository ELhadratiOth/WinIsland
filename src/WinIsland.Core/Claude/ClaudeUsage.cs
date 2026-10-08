using System.Globalization;
using System.Text.Json;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;

namespace WinIsland.Core.Claude;

/// <summary>Tokens of one Claude API response, read from a Claude Code transcript.</summary>
public sealed record UsageEntry(
    string MessageId,
    DateTimeOffset Timestamp,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    /// <summary>Approximate cost at public API prices, or null for an unknown model.</summary>
    public decimal? Cost => ClaudePricing.Cost(this);
}

public sealed record UsageSummary(long TodayTokens, decimal? TodayCost, long WindowTokens, DateTimeOffset? WindowResetsAt)
{
    public static readonly UsageSummary Empty = new(0, null, 0, null);
}

/// <summary>Public per-million-token API prices by model family (an estimate for subscription users).</summary>
public static class ClaudePricing
{
    public static (decimal Input, decimal Output)? PricesFor(string model)
    {
        string m = model.ToLowerInvariant();
        if (m.Contains("opus-4-0", StringComparison.Ordinal) || m.Contains("opus-4-1", StringComparison.Ordinal) || m.Contains("opus-4-2025", StringComparison.Ordinal) || m.StartsWith("claude-3-opus", StringComparison.Ordinal))
        {
            return (15m, 75m);
        }

        if (m.Contains("opus", StringComparison.Ordinal))
        {
            return (5m, 25m);
        }

        if (m.Contains("sonnet", StringComparison.Ordinal))
        {
            return (3m, 15m);
        }

        if (m.Contains("haiku-3-5", StringComparison.Ordinal) || m.StartsWith("claude-3-5-haiku", StringComparison.Ordinal))
        {
            return (0.8m, 4m);
        }

        return m.Contains("haiku", StringComparison.Ordinal) ? (1m, 5m) : null;
    }

    public static decimal? Cost(UsageEntry e)
    {
        if (PricesFor(e.Model) is not { } p)
        {
            return null;
        }

        // Cache writes cost 1.25× input, cache reads 0.1× input.
        decimal input = (e.InputTokens + (e.CacheCreationTokens * 1.25m) + (e.CacheReadTokens * 0.1m)) * p.Input;
        return (input + (e.OutputTokens * p.Output)) / 1_000_000m;
    }
}

public static class UsageText
{
    public static string Tokens(long tokens) => tokens switch
    {
        < 1_000 => tokens.ToString(CultureInfo.CurrentCulture),
        < 1_000_000 => string.Create(CultureInfo.CurrentCulture, $"{tokens / 1_000.0:0.#}K"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{tokens / 1_000_000.0:0.#}M"),
    };
}

/// <summary>
/// Token usage from Claude Code's local transcripts: today's total and the current 5-hour
/// usage window (the window Claude subscription limits are counted in). Reads files
/// incrementally on change events and de-duplicates by message id (one response is split over
/// several transcript lines that repeat the same usage).
/// </summary>
public sealed class ClaudeUsageTracker : IIntegration
{
    public static readonly TimeSpan WindowLength = TimeSpan.FromHours(5);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(30);

    private readonly string _projectsDirectory;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UsageEntry> _entries = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private UsageSummary _summary = UsageSummary.Empty;

    public ClaudeUsageTracker(string projectsDirectory, TimeProvider time)
    {
        _projectsDirectory = projectsDirectory;
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Raised on a background thread when the summary changes.</summary>
    public event EventHandler? Changed;

    public string Name => "Claude Code usage";

    public bool RequiresNetwork => false;

    public UsageSummary Summary => Volatile.Read(ref _summary);

    public Task StartAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (!Directory.Exists(_projectsDirectory))
        {
            return;
        }

        DateTime cutoff = _time.GetUtcNow().UtcDateTime - Retention;
        foreach (string file in Directory.EnumerateFiles(_projectsDirectory, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(file) > cutoff)
            {
                ReadNew(file);
            }
        }

        lock (_gate)
        {
            _watcher = new FileSystemWatcher(_projectsDirectory, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };
            _watcher.Changed += (_, e) => OnFileChanged(e.FullPath);
            _watcher.Created += (_, e) => OnFileChanged(e.FullPath);
            _watcher.EnableRaisingEvents = true;
        }

        Recompute();
    }, cancellationToken);

    public Task StopAsync()
    {
        lock (_gate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>Re-evaluates today/window boundaries (e.g. after midnight or a window reset).</summary>
    public void Recompute()
    {
        UsageSummary summary;
        lock (_gate)
        {
            summary = Summarize([.. _entries.Values], _time.GetUtcNow(), TimeZoneInfo.Local);
        }

        if (Interlocked.Exchange(ref _summary, summary) != summary)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Parses one transcript line; null unless it is an assistant message carrying usage.</summary>
    public static UsageEntry? Parse(string line)
    {
        if (!line.Contains("\"usage\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("message", out JsonElement message) ||
                !message.TryGetProperty("usage", out JsonElement usage) ||
                !root.TryGetProperty("timestamp", out JsonElement ts) ||
                !DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset timestamp))
            {
                return null;
            }

            string id = message.TryGetProperty("id", out JsonElement mid) && mid.GetString() is { Length: > 0 } m ? m
                : root.TryGetProperty("requestId", out JsonElement rid) && rid.GetString() is { Length: > 0 } r ? r
                : root.TryGetProperty("uuid", out JsonElement uid) ? uid.GetString() ?? string.Empty : string.Empty;
            if (id.Length == 0)
            {
                return null;
            }

            static long Read(JsonElement usage, string name) =>
                usage.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

            string model = message.TryGetProperty("model", out JsonElement mo) ? mo.GetString() ?? string.Empty : string.Empty;
            return new UsageEntry(
                id,
                timestamp,
                model,
                Read(usage, "input_tokens"),
                Read(usage, "output_tokens"),
                Read(usage, "cache_creation_input_tokens"),
                Read(usage, "cache_read_input_tokens"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Today = the local calendar day. The 5-hour window starts at the hour of the first message
    /// after a gap (or after the previous window ended) and lasts five hours.
    /// </summary>
    public static UsageSummary Summarize(IReadOnlyList<UsageEntry> entries, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTime today = TimeZoneInfo.ConvertTime(now, zone).Date;
        long todayTokens = 0;
        decimal todayCost = 0;
        bool anyCost = false;
        foreach (UsageEntry e in entries)
        {
            if (TimeZoneInfo.ConvertTime(e.Timestamp, zone).Date == today)
            {
                todayTokens += e.TotalTokens;
                if (e.Cost is { } c)
                {
                    todayCost += c;
                    anyCost = true;
                }
            }
        }

        DateTimeOffset? windowStart = null;
        DateTimeOffset last = DateTimeOffset.MinValue;
        long windowTokens = 0;
        foreach (UsageEntry e in entries.OrderBy(e => e.Timestamp))
        {
            if (windowStart is null || e.Timestamp >= windowStart + WindowLength || e.Timestamp - last >= WindowLength)
            {
                windowStart = new DateTimeOffset(e.Timestamp.UtcDateTime.Date.AddHours(e.Timestamp.UtcDateTime.Hour), TimeSpan.Zero);
                windowTokens = 0;
            }

            windowTokens += e.TotalTokens;
            last = e.Timestamp;
        }

        bool windowActive = windowStart is { } start && now < start + WindowLength;
        return new UsageSummary(
            todayTokens,
            anyCost ? decimal.Round(todayCost, 2) : null,
            windowActive ? windowTokens : 0,
            windowActive ? windowStart + WindowLength : null);
    }

    private void OnFileChanged(string path)
    {
        try
        {
            ReadNew(path);
            Recompute();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn(nameof(ClaudeUsageTracker), "Reading a transcript failed", ex);
        }
    }

    /// <summary>Reads lines appended since the last read (only whole lines).</summary>
    private void ReadNew(string path)
    {
        long offset;
        lock (_gate)
        {
            offset = _offsets.GetValueOrDefault(path);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < offset)
        {
            offset = 0;
        }

        // Read what was appended in chunks and keep only complete lines; a line still being
        // written has no newline yet and is picked up by the next change event.
        stream.Seek(offset, SeekOrigin.Begin);
        var found = new List<UsageEntry>();
        var pending = new List<byte>();
        long consumed = offset;
        byte[] chunk = new byte[1 << 20];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            int start = 0;
            for (int i = 0; i < read; i++)
            {
                if (chunk[i] != (byte)'\n')
                {
                    continue;
                }

                pending.AddRange(new ArraySegment<byte>(chunk, start, i - start));
                consumed += pending.Count + 1;
                string line = System.Text.Encoding.UTF8.GetString([.. pending]).TrimEnd('\r');
                pending.Clear();
                start = i + 1;
                if (Parse(line) is { } entry)
                {
                    found.Add(entry);
                }
            }

            pending.AddRange(new ArraySegment<byte>(chunk, start, read - start));
        }

        DateTimeOffset cutoff = _time.GetUtcNow() - Retention;
        lock (_gate)
        {
            _offsets[path] = consumed;
            foreach (UsageEntry entry in found.Where(e => e.Timestamp > cutoff))
            {
                _entries[entry.MessageId] = entry;
            }

            foreach (string old in _entries.Where(e => e.Value.Timestamp <= cutoff).Select(e => e.Key).ToList())
            {
                _entries.Remove(old);
            }
        }
    }
}
