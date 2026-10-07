namespace WinIsland.Core.Integrations;

/// <summary>Event-driven internet connectivity (no polling).</summary>
public interface IConnectivityMonitor
{
    bool IsOnline { get; }

    /// <summary>Raised on any thread when <see cref="IsOnline"/> flips.</summary>
    event EventHandler<bool>? ConnectivityChanged;
}

/// <summary>Used where no platform monitor exists (tests, non-Windows); always online.</summary>
public sealed class AlwaysOnlineConnectivityMonitor : IConnectivityMonitor
{
    public bool IsOnline => true;

    public event EventHandler<bool>? ConnectivityChanged
    {
        add { }
        remove { }
    }
}
