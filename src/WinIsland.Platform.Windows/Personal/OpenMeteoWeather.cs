using Windows.Devices.Geolocation;
using WinIsland.Core.Diagnostics;
using WinIsland.Core.Integrations;
using WinIsland.Core.Personal;

namespace WinIsland.Platform.Windows.Personal;

/// <summary>
/// Weather from Open-Meteo (free, no account) for the city in settings, or the Windows location
/// when no city is set and location access is allowed. Refreshes every 30 minutes while online.
/// </summary>
public sealed class OpenMeteoWeather : IWeatherSource, IIntegration
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Func<bool> _enabled;
    private readonly Func<string> _place;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _wake;
    private WeatherInfo? _current;
    private (string Query, double Lat, double Lon, string Name)? _resolved;

    public OpenMeteoWeather(Func<bool> enabled, Func<string> place, TimeProvider time)
    {
        _enabled = enabled;
        _place = place;
        _time = time;
    }

    public event EventHandler? Changed;

    public string Name => "Weather";

    public bool RequiresNetwork => true;

    public WeatherInfo? Current => Volatile.Read(ref _current);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = LoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    /// <summary>Settings changed: refresh now.</summary>
    public void Refresh() => _wake?.Cancel();

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Publish(_enabled() ? await FetchAsync(token).ConfigureAwait(false) : null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                AppLog.Info(nameof(OpenMeteoWeather), $"Weather unavailable: {ex.GetType().Name}");
            }

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
            _wake = wake;
            try
            {
                await Task.Delay(Interval, _time, wake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _wake = null;
            }
        }
    }

    private async Task<WeatherInfo?> FetchAsync(CancellationToken token)
    {
        string query = _place().Trim();
        (double lat, double lon, string name)? location = null;
        if (query.Length > 0)
        {
            if (_resolved is { } r && r.Query == query)
            {
                location = (r.Lat, r.Lon, r.Name);
            }
            else if (OpenMeteo.ParseGeocode(await Http.GetStringAsync(OpenMeteo.BuildGeocodeUri(query), token).ConfigureAwait(false)) is { } found)
            {
                _resolved = (query, found.Latitude, found.Longitude, found.Name);
                location = found;
            }
        }
        else
        {
            location = await DeviceLocationAsync().ConfigureAwait(false);
        }

        if (location is not { } l)
        {
            return null;
        }

        string json = await Http.GetStringAsync(OpenMeteo.BuildForecastUri(l.lat, l.lon), token).ConfigureAwait(false);
        return OpenMeteo.ParseForecast(json, l.name);
    }

    /// <summary>Windows location, only if the user allows desktop apps to use it (city-level is enough).</summary>
    private static async Task<(double, double, string)?> DeviceLocationAsync()
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed)
            {
                return null;
            }

            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default, DesiredAccuracyInMeters = 5000 };
            Geoposition position = await locator.GetGeopositionAsync(TimeSpan.FromHours(1), TimeSpan.FromSeconds(10));
            return (position.Coordinate.Point.Position.Latitude, position.Coordinate.Point.Position.Longitude, string.Empty);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private void Publish(WeatherInfo? info)
    {
        if (Interlocked.Exchange(ref _current, info) != info)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
