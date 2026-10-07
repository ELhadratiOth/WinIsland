namespace WinIsland.Core.Integrations;

/// <summary>
/// A background data source (media, Claude Code sessions, calendar, usage…). Integrations are
/// started off the UI thread after the island is already visible, are isolated from one another
/// (one failing never affects the rest) and network-bound ones are paused while offline.
/// </summary>
public interface IIntegration : IAsyncDisposable
{
    string Name { get; }

    /// <summary>When true the host only runs the integration while the machine is online.</summary>
    bool RequiresNetwork { get; }

    /// <summary>Starts listening. Must not block on slow work; throw to signal a failure (the host retries).</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops and releases event subscriptions, timers and cached data. May be restarted later.</summary>
    Task StopAsync();
}
