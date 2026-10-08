using System.Globalization;
using System.Text.Json;

namespace WinIsland.Core.Personal;

/// <summary>Current conditions (°C; converted for display).</summary>
public sealed record WeatherInfo(double TemperatureC, int Code, bool IsDay, double HighC, double LowC, string Place);

public interface IWeatherSource
{
    WeatherInfo? Current { get; }

    /// <summary>Raised on any thread.</summary>
    event EventHandler? Changed;
}

/// <summary>Open-Meteo (free, no key): forecast and place-name lookup.</summary>
public static class OpenMeteo
{
    public static Uri BuildForecastUri(double latitude, double longitude) => new(string.Create(
        CultureInfo.InvariantCulture,
        $"https://api.open-meteo.com/v1/forecast?latitude={latitude:0.###}&longitude={longitude:0.###}&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1"));

    public static Uri BuildGeocodeUri(string place) =>
        new($"https://geocoding-api.open-meteo.com/v1/search?count=1&format=json&name={Uri.EscapeDataString(place)}");

    public static (double Latitude, double Longitude, string Name)? ParseGeocode(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement first = results[0];
        return (first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble(), first.GetProperty("name").GetString() ?? string.Empty);
    }

    public static WeatherInfo? ParseForecast(string json, string place)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("current", out JsonElement current))
        {
            return null;
        }

        double High(string name) => root.TryGetProperty("daily", out JsonElement daily) && daily.TryGetProperty(name, out JsonElement values) && values.GetArrayLength() > 0
            ? values[0].GetDouble()
            : double.NaN;

        return new WeatherInfo(
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            !current.TryGetProperty("is_day", out JsonElement day) || day.GetInt32() == 1,
            High("temperature_2m_max"),
            High("temperature_2m_min"),
            place);
    }

    /// <summary>WMO weather code → (description, emoji).</summary>
    public static (string Text, string Emoji) Describe(int code, bool isDay) => code switch
    {
        0 => ("Clear", isDay ? "☀️" : "🌙"),
        1 => ("Mostly clear", isDay ? "🌤️" : "🌙"),
        2 => ("Partly cloudy", isDay ? "⛅" : "☁️"),
        3 => ("Cloudy", "☁️"),
        45 or 48 => ("Fog", "🌫️"),
        51 or 53 or 55 or 56 or 57 => ("Drizzle", "🌦️"),
        61 or 63 or 66 => ("Rain", "🌧️"),
        65 or 67 => ("Heavy rain", "🌧️"),
        71 or 73 or 75 or 77 => ("Snow", "🌨️"),
        80 or 81 or 82 => ("Showers", "🌦️"),
        85 or 86 => ("Snow showers", "🌨️"),
        95 or 96 or 99 => ("Thunderstorm", "⛈️"),
        _ => ("—", "🌡️"),
    };

    public static string Temperature(double celsius, bool fahrenheit) =>
        double.IsNaN(celsius) ? "–" : string.Create(CultureInfo.CurrentCulture, $"{Math.Round(fahrenheit ? (celsius * 9 / 5) + 32 : celsius):0}°");
}
