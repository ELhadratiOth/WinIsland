using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Integrations;

namespace WinIsland.Core.Tests;

public class IntegrationHostTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task Start_returns_immediately_even_if_an_integration_is_slow()
    {
        var gate = new TaskCompletionSource();
        var slow = new FakeIntegration("slow") { OnStart = _ => gate.Task };
        await using var host = new IntegrationHost([slow], new FakeConnectivity(true), _time);

        host.Start();

        Assert.NotEqual(IntegrationStatus.Running, host.GetStatus("slow"));
        gate.SetResult();
        await Eventually.TrueAsync(() => host.GetStatus("slow") == IntegrationStatus.Running, "slow integration starts");
    }

    [Fact]
    public async Task One_failing_integration_does_not_affect_others_and_is_retried()
    {
        int attempts = 0;
        var flaky = new FakeIntegration("flaky")
        {
            OnStart = _ => ++attempts == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask,
        };
        var healthy = new FakeIntegration("healthy");
        await using var host = new IntegrationHost([flaky, healthy], new FakeConnectivity(true), _time);

        host.Start();
        await Eventually.TrueAsync(() => host.GetStatus("flaky") == IntegrationStatus.Faulted, "flaky faults");
        await Eventually.TrueAsync(() => host.GetStatus("healthy") == IntegrationStatus.Running, "healthy runs");

        _time.Advance(IntegrationHost.RetryDelay(1));
        await Eventually.TrueAsync(() => host.GetStatus("flaky") == IntegrationStatus.Running, "flaky recovers after backoff");
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Network_integrations_wait_for_connectivity_and_pause_when_offline()
    {
        var connectivity = new FakeConnectivity(false);
        var cloud = new FakeIntegration("cloud", requiresNetwork: true);
        var local = new FakeIntegration("local");
        await using var host = new IntegrationHost([cloud, local], connectivity, _time);

        host.Start();
        await Eventually.TrueAsync(() => host.GetStatus("local") == IntegrationStatus.Running, "local runs offline");
        await Eventually.TrueAsync(() => host.GetStatus("cloud") == IntegrationStatus.WaitingForNetwork, "cloud waits");
        Assert.Equal(0, cloud.Starts);

        connectivity.Set(true);
        await Eventually.TrueAsync(() => host.GetStatus("cloud") == IntegrationStatus.Running, "cloud starts online");

        connectivity.Set(false);
        await Eventually.TrueAsync(() => host.GetStatus("cloud") == IntegrationStatus.WaitingForNetwork, "cloud pauses offline");
        Assert.Equal(1, cloud.Stops);
        Assert.Equal(IntegrationStatus.Running, host.GetStatus("local"));
    }

    [Fact]
    public void Backoff_grows_and_is_capped()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), IntegrationHost.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(10), IntegrationHost.RetryDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(5), IntegrationHost.RetryDelay(50));
    }

    [Fact]
    public async Task Dispose_stops_and_disposes_integrations()
    {
        var integration = new FakeIntegration("x");
        var host = new IntegrationHost([integration], new FakeConnectivity(true), _time);
        host.Start();
        await Eventually.TrueAsync(() => host.GetStatus("x") == IntegrationStatus.Running, "running");

        await host.DisposeAsync();

        Assert.Equal(1, integration.Stops);
        Assert.True(integration.Disposed);
    }

    private sealed class FakeIntegration(string name, bool requiresNetwork = false) : IIntegration
    {
        private int _starts;
        private int _stops;

        public string Name => name;

        public bool RequiresNetwork => requiresNetwork;

        public Func<CancellationToken, Task>? OnStart { get; init; }

        public int Starts => Volatile.Read(ref _starts);

        public int Stops => Volatile.Read(ref _stops);

        public bool Disposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _starts);
            return OnStart?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stops);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeConnectivity(bool online) : IConnectivityMonitor
    {
        public bool IsOnline { get; private set; } = online;

        public event EventHandler<bool>? ConnectivityChanged;

        public void Set(bool value)
        {
            IsOnline = value;
            ConnectivityChanged?.Invoke(this, value);
        }
    }
}
