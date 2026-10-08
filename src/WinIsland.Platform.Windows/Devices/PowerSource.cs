using Windows.System.Power;
using WinIsland.Core.Devices;
using WinIsland.Core.Integrations;

namespace WinIsland.Platform.Windows.Devices;

/// <summary>Battery and charger state from <see cref="PowerManager"/> events.</summary>
public sealed class PowerSource : IPowerSource, IIntegration
{
    private PowerStatus _current = PowerStatus.None;
    private bool _subscribed;

    public event EventHandler? Changed;

    public string Name => "Power";

    public bool RequiresNetwork => false;

    public PowerStatus Current => Volatile.Read(ref _current);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            PowerManager.BatteryStatusChanged += OnChanged;
            PowerManager.PowerSupplyStatusChanged += OnChanged;
            PowerManager.RemainingChargePercentChanged += OnChanged;
            PowerManager.EnergySaverStatusChanged += OnChanged;
        }

        Update();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_subscribed)
        {
            _subscribed = false;
            PowerManager.BatteryStatusChanged -= OnChanged;
            PowerManager.PowerSupplyStatusChanged -= OnChanged;
            PowerManager.RemainingChargePercentChanged -= OnChanged;
            PowerManager.EnergySaverStatusChanged -= OnChanged;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void OnChanged(object? sender, object e) => Update();

    private void Update()
    {
        BatteryStatus battery = PowerManager.BatteryStatus;
        var status = new PowerStatus(
            HasBattery: battery != BatteryStatus.NotPresent,
            Percent: Math.Clamp(PowerManager.RemainingChargePercent, 0, 100),
            IsPluggedIn: PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent,
            IsCharging: battery == BatteryStatus.Charging,
            EnergySaver: PowerManager.EnergySaverStatus == EnergySaverStatus.On);

        if (Interlocked.Exchange(ref _current, status) != status)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
