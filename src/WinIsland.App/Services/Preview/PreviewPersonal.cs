using WinIsland.Core.Personal;

namespace WinIsland.App.Services.Preview;

internal sealed class PreviewWeather : IWeatherSource
{
    public WeatherInfo? Current { get; } = new(21.4, 2, true, 24.2, 15.8, "Casablanca, Morocco");

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

internal sealed class PreviewCalendar : ICalendarSource
{
    public IReadOnlyList<CalendarEvent> Events { get; private set; } = [];

    public event EventHandler? Changed;

    /// <summary>A meeting starting in five minutes, plus the rest of the day.</summary>
    public void MeetingSoon()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        DateTimeOffset start = now.AddMinutes(5).AddSeconds(-now.Second);
        Events =
        [
            new("Design review", start, start.AddMinutes(45), false, null, "https://teams.microsoft.com/l/meetup-join/19%3ameeting_preview"),
            new("1:1 with Sara", start.AddHours(2), start.AddHours(2.5), false, "Room 4B", null),
            new("Ship v0.2", start.AddHours(4), start.AddHours(5), false, null, "https://meet.google.com/abc-defg-hij"),
        ];
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
