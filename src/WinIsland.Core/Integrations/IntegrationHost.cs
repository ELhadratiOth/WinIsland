using WinIsland.Core.Diagnostics;

namespace WinIsland.Core.Integrations;

/// <summary>
/// Runs integrations in the background. Startup returns immediately so the island can render
/// first; each integration then starts on the thread pool with its own failure isolation,
/// exponential-backoff retries (one-shot timers, not polling) and offline handling.
/// </summary>
public sealed class IntegrationHost : IAsyncDisposable
{
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly List<Entry> _entries;
    private readonly IConnectivityMonitor _connectivity;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _started;

    public IntegrationHost(IEnumerable<IIntegration> integrations, IConnectivityMonitor connectivity, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(integrations);
        _entries = integrations.Select(i => new Entry(i)).ToList();
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Raised on a background thread whenever an integration changes status.</summary>
    public event EventHandler<IntegrationStatusChange>? StatusChanged;

    public IntegrationStatus GetStatus(string name) =>
        _entries.FirstOrDefault(e => e.Integration.Name == name)?.Status ?? IntegrationStatus.Stopped;

    /// <summary>Kicks off every integration and returns without waiting for any of them.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _connectivity.ConnectivityChanged += OnConnectivityChanged;
        foreach (Entry entry in _entries)
        {
            _ = Task.Run(() => StartEntryAsync(entry));
        }
    }

    /// <summary>Stops one integration (e.g. a module the user disabled) and releases its resources.</summary>
    public Task StopAsync(string name)
    {
        Entry? entry = _entries.FirstOrDefault(e => e.Integration.Name == name);
        return entry is null ? Task.CompletedTask : StopEntryAsync(entry, IntegrationStatus.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        _connectivity.ConnectivityChanged -= OnConnectivityChanged;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (Entry entry in _entries)
        {
            await StopEntryAsync(entry, IntegrationStatus.Stopped).ConfigureAwait(false);
            try
            {
                await entry.Integration.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Warn(nameof(IntegrationHost), $"Disposing {entry.Integration.Name} failed", ex);
            }

            entry.Gate.Dispose();
        }

        _shutdown.Dispose();
    }

    private async Task StartEntryAsync(Entry entry)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            entry.RetryTimer?.Dispose();
            entry.RetryTimer = null;

            if (entry.Status is IntegrationStatus.Running or IntegrationStatus.Starting || _shutdown.IsCancellationRequested)
            {
                return;
            }

            if (entry.Integration.RequiresNetwork && !_connectivity.IsOnline)
            {
                SetStatus(entry, IntegrationStatus.WaitingForNetwork);
                return;
            }

            SetStatus(entry, IntegrationStatus.Starting);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(StartTimeout);
            try
            {
                await entry.Integration.StartAsync(timeout.Token).ConfigureAwait(false);
                entry.Failures = 0;
                SetStatus(entry, IntegrationStatus.Running);
            }
            catch (Exception ex) when (!_shutdown.IsCancellationRequested)
            {
                entry.Failures++;
                TimeSpan delay = RetryDelay(entry.Failures);
                AppLog.Warn(nameof(IntegrationHost), $"{entry.Integration.Name} failed to start; retrying in {delay}", ex);
                await SafeStopAsync(entry).ConfigureAwait(false);
                SetStatus(entry, IntegrationStatus.Faulted, ex.Message);
                entry.RetryTimer = _time.CreateTimer(_ => _ = Task.Run(() => StartEntryAsync(entry)), null, delay, Timeout.InfiniteTimeSpan);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private async Task StopEntryAsync(Entry entry, IntegrationStatus finalStatus)
    {
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            entry.RetryTimer?.Dispose();
            entry.RetryTimer = null;
            if (entry.Status is IntegrationStatus.Running or IntegrationStatus.Starting or IntegrationStatus.Faulted)
            {
                await SafeStopAsync(entry).ConfigureAwait(false);
            }

            SetStatus(entry, finalStatus);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private void OnConnectivityChanged(object? sender, bool online)
    {
        foreach (Entry entry in _entries.Where(e => e.Integration.RequiresNetwork))
        {
            if (online)
            {
                entry.Failures = 0;
                _ = Task.Run(() => StartEntryAsync(entry));
            }
            else
            {
                _ = Task.Run(() => StopEntryAsync(entry, IntegrationStatus.WaitingForNetwork));
            }
        }
    }

    private static async Task SafeStopAsync(Entry entry)
    {
        try
        {
            await entry.Integration.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(IntegrationHost), $"Stopping {entry.Integration.Name} failed", ex);
        }
    }

    private void SetStatus(Entry entry, IntegrationStatus status, string? error = null)
    {
        if (entry.Status == status)
        {
            return;
        }

        entry.Status = status;
        StatusChanged?.Invoke(this, new IntegrationStatusChange(entry.Integration.Name, status, error));
    }

    internal static TimeSpan RetryDelay(int failures)
    {
        double seconds = MinRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(0, failures - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelay.TotalSeconds));
    }

    private sealed class Entry(IIntegration integration)
    {
        public IIntegration Integration { get; } = integration;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public volatile IntegrationStatus Status = IntegrationStatus.Pending;

        public int Failures;

        public ITimer? RetryTimer;
    }
}
