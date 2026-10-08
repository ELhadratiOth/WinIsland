using System.Globalization;
using WinIsland.Core.Personal;
using WinIsland.Core.Threading;

namespace WinIsland.Core.Modules;

/// <summary>
/// The always-available fallback module. Wakes exactly once per minute, aligned to the minute
/// boundary, instead of ticking every second.
/// </summary>
public sealed class ClockModule : IslandModule
{
    public const string ModuleId = "clock";

    private readonly TimeProvider _time;
    private readonly OneShotTimer _minuteTimer;
    private string _timeText = string.Empty;
    private string _dateText = string.Empty;
    private string _weekdayText = string.Empty;
    private string _dayText = string.Empty;
    private readonly IWeatherSource? _weather;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<bool> _fahrenheit;
    private WeatherInfo? _weatherInfo;

    public ClockModule(TimeProvider time, IUiDispatcher dispatcher, IWeatherSource? weather = null, Func<bool>? fahrenheit = null)
        : base(ModuleId, "Clock", "\uE823")
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _weather = weather;
        _fahrenheit = fahrenheit ?? (() => false);
        if (_weather is not null)
        {
            _weather.Changed += OnWeatherChanged;
            _weatherInfo = _weather.Current;
        }

        _time = time ?? throw new ArgumentNullException(nameof(time));
        _minuteTimer = new OneShotTimer(time, dispatcher, Refresh);
        IsAvailable = true;
        CompactPriority = ModulePriority.Clock;
        InteractivePriority = ModulePriority.Clock;
        Refresh();
    }

    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    public string DateText
    {
        get => _dateText;
        private set => SetProperty(ref _dateText, value);
    }

    public override Geometry.DipSize GetSize(Layout.IslandSize size) => size switch
    {
        Layout.IslandSize.Expanded => new Geometry.DipSize(HasWeather ? 440 : 340, 96),
        Layout.IslandSize.Compact when HasWeather => new Geometry.DipSize(216, 36),
        _ => base.GetSize(size),
    };

    // ---- Weather (Open-Meteo) ----

    public bool HasWeather => _weatherInfo is not null;

    /// <summary>"18°".</summary>
    public string WeatherTemperature => _weatherInfo is { } w ? OpenMeteo.Temperature(w.TemperatureC, _fahrenheit()) : string.Empty;

    public string WeatherEmoji => _weatherInfo is { } w ? OpenMeteo.Describe(w.Code, w.IsDay).Emoji : string.Empty;

    /// <summary>"Partly cloudy · H 22° L 14°".</summary>
    public string WeatherSummary => _weatherInfo is { } w
        ? $"{OpenMeteo.Describe(w.Code, w.IsDay).Text} · H {OpenMeteo.Temperature(w.HighC, _fahrenheit())} L {OpenMeteo.Temperature(w.LowC, _fahrenheit())}"
        : string.Empty;

    public string WeatherPlace => _weatherInfo?.Place ?? string.Empty;

    /// <summary>Units changed in settings.</summary>
    public void RefreshWeather() => ApplyWeather(_weatherInfo);

    /// <summary>e.g. "Wednesday".</summary>
    public string WeekdayText
    {
        get => _weekdayText;
        private set => SetProperty(ref _weekdayText, value);
    }

    /// <summary>e.g. "October 7".</summary>
    public string DayText
    {
        get => _dayText;
        private set => SetProperty(ref _dayText, value);
    }

    /// <summary>Re-reads the time; also called when the system clock, time zone or locale changes.</summary>
    public void Refresh()
    {
        DateTimeOffset now = _time.GetLocalNow();
        TimeText = now.ToString("t", CultureInfo.CurrentCulture);
        DateText = now.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
        WeekdayText = now.ToString("dddd", CultureInfo.CurrentCulture);
        DayText = now.ToString(CultureInfo.CurrentCulture.DateTimeFormat.MonthDayPattern, CultureInfo.CurrentCulture);
        CompactText = TimeText;

        TimeSpan untilNextMinute = TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(now.TimeOfDay.Ticks % TimeSpan.TicksPerMinute);
        _minuteTimer.Start(untilNextMinute + TimeSpan.FromMilliseconds(20));
    }

    private void OnWeatherChanged(object? sender, EventArgs e)
    {
        WeatherInfo? info = _weather!.Current;
        _dispatcher.TryEnqueue(() => ApplyWeather(info));
    }

    private void ApplyWeather(WeatherInfo? info)
    {
        bool had = HasWeather;
        _weatherInfo = info;
        OnPropertyChanged(nameof(HasWeather));
        OnPropertyChanged(nameof(WeatherTemperature));
        OnPropertyChanged(nameof(WeatherEmoji));
        OnPropertyChanged(nameof(WeatherSummary));
        OnPropertyChanged(nameof(WeatherPlace));
        if (had != HasWeather)
        {
            NotifyPresentationChanged();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _minuteTimer.Dispose();
            if (_weather is not null)
            {
                _weather.Changed -= OnWeatherChanged;
            }
        }

        base.Dispose(disposing);
    }
}
