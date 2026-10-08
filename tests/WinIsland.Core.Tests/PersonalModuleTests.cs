using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Helpers;
using WinIsland.Core.Layout;
using WinIsland.Core.Modules;
using WinIsland.Core.Personal;

namespace WinIsland.Core.Tests;

public class PersonalModuleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
    private readonly InlineDispatcher _dispatcher = new();

    public PersonalModuleTests() => _time.SetLocalTimeZone(TimeZoneInfo.Utc);

    private const string Feed = """
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:standup
        SUMMARY:Daily standup
        DTSTART:20261005T093000Z
        DTEND:20261005T094500Z
        RRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR
        EXDATE:20261007T093000Z
        DESCRIPTION:Join: https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc%40thread.v2/0?context=x
          more text
        BEGIN:VALARM
        TRIGGER:-PT10M
        END:VALARM
        END:VEVENT
        BEGIN:VEVENT
        UID:review
        SUMMARY:Design review\, v2
        DTSTART:20261008T140000Z
        DURATION:PT1H
        LOCATION:https://us02web.zoom.us/j/123456789?pwd=abc
        END:VEVENT
        BEGIN:VEVENT
        UID:holiday
        SUMMARY:Team offsite
        DTSTART;VALUE=DATE:20261009
        DTEND;VALUE=DATE:20261010
        END:VEVENT
        BEGIN:VEVENT
        UID:gone
        SUMMARY:Cancelled thing
        STATUS:CANCELLED
        DTSTART:20261008T110000Z
        DTEND:20261008T120000Z
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Ics_feed_expands_recurrences_and_finds_join_links()
    {
        IReadOnlyList<CalendarEvent> events = IcsParser.Parse(Feed, _time.GetUtcNow().AddDays(-2), _time.GetUtcNow().AddDays(2));

        Assert.DoesNotContain(events, e => e.Title == "Cancelled thing");
        CalendarEvent[] standups = [.. events.Where(e => e.Title == "Daily standup")];
        Assert.Equal([6, 8, 9], standups.Select(e => e.Start.Day));
        Assert.All(standups, e => Assert.Equal(TimeSpan.FromMinutes(15), e.End - e.Start));
        Assert.StartsWith("https://teams.microsoft.com/l/meetup-join/", standups[0].JoinUrl, StringComparison.Ordinal);

        CalendarEvent review = Assert.Single(events, e => e.Title == "Design review, v2");
        Assert.Equal(TimeSpan.FromHours(1), review.End - review.Start);
        Assert.Equal("https://us02web.zoom.us/j/123456789?pwd=abc", review.JoinUrl);

        Assert.True(Assert.Single(events, e => e.Title == "Team offsite").AllDay);
    }

    [Fact]
    public void Calendar_shows_the_meeting_shortly_before_and_reminds_twice()
    {
        var source = new FakeCalendar(IcsParser.Parse(Feed, _time.GetUtcNow().AddDays(-1), _time.GetUtcNow().AddDays(2)));
        var shell = new RecordingShell();
        using var calendar = new CalendarModule(source, shell, _time, _dispatcher);
        var attention = new List<AttentionRequest>();
        calendar.AttentionRequested += (_, r) => attention.Add(r);

        Assert.Equal("Daily standup", calendar.NextTitle);
        Assert.Equal(ModulePriority.Unavailable, calendar.CompactPriority);

        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(ModulePriority.Live, calendar.CompactPriority);
        Assert.Equal("in 10 min", calendar.CompactDetail);
        Assert.Empty(attention);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(attention);
        Assert.Equal("in 5 min", calendar.NextWhen);

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(2, attention.Count);
        Assert.Equal("now", calendar.NextWhen);
        Assert.Equal("Join Teams", calendar.JoinLabel);
        calendar.JoinCommand.Execute(null);
        Assert.Single(shell.Opened);

        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("Design review, v2", calendar.NextTitle);
        Assert.Equal(ModulePriority.Unavailable, calendar.CompactPriority);
    }

    [Fact]
    public void Open_meteo_forecast_drives_the_clock_weather()
    {
        WeatherInfo? info = OpenMeteo.ParseForecast("""
            {"current":{"temperature_2m":18.4,"weather_code":2,"is_day":1},"daily":{"temperature_2m_max":[22.1],"temperature_2m_min":[13.6]}}
            """, "Casablanca");
        var weather = new FakeWeather(info);
        bool fahrenheit = false;
        using var clock = new ClockModule(_time, _dispatcher, weather, () => fahrenheit);

        Assert.Equal("18°", clock.WeatherTemperature);
        Assert.Equal("Partly cloudy · H 22° L 14°", clock.WeatherSummary);
        Assert.Equal(520, clock.GetSize(IslandSize.Expanded).Width);

        fahrenheit = true;
        clock.RefreshWeather();
        Assert.Equal("65°", clock.WeatherTemperature);
        Assert.Equal((33.57, -7.59, "Casablanca"), OpenMeteo.ParseGeocode("""{"results":[{"name":"Casablanca","latitude":33.57,"longitude":-7.59}]}"""));
    }

    private sealed class FakeCalendar(IReadOnlyList<CalendarEvent> events) : ICalendarSource
    {
        public IReadOnlyList<CalendarEvent> Events => events;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class FakeWeather(WeatherInfo? current) : IWeatherSource
    {
        public WeatherInfo? Current => current;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    private sealed class RecordingShell : IShellLauncher
    {
        public List<string> Opened { get; } = [];

        public void Open(string path) => Opened.Add(path);

        public void Reveal(string path)
        {
        }
    }
}
