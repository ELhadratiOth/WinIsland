using Windows.Networking.Connectivity;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;

namespace WinIsland.Platform.Windows.Networking;

/// <summary>Internet connectivity from <see cref="NetworkInformation.NetworkStatusChanged"/> (event-driven, no polling).</summary>
public sealed class ConnectivityMonitor : IConnectivityMonitor, IDisposable
{
    private volatile bool _isOnline = true;
    private bool _subscribed;

    public event EventHandler<bool>? ConnectivityChanged;

    // Optimistic until the first check completes: offline-capable integrations don't care,
    // and network ones simply fail fast and retry if we were wrong.
    public bool IsOnline => _isOnline;

    /// <summary>Subscribes and performs the initial check off the UI thread.</summary>
    public void Start()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        _ = Task.Run(Update);
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
            _subscribed = false;
        }
    }

    private void OnNetworkStatusChanged(object sender) => Update();

    private void Update()
    {
        bool online;
        try
        {
            online = NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }
        catch (Exception ex)
        {
            AppLog.Warn(nameof(ConnectivityMonitor), "Connectivity check failed", ex);
            return;
        }

        if (online != _isOnline)
        {
            _isOnline = online;
            ConnectivityChanged?.Invoke(this, online);
        }
    }
}
