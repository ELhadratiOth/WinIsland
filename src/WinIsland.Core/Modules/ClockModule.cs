using System.Globalization;
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

    public ClockModule(TimeProvider time, IUiDispatcher dispatcher)
        : base(ModuleId, "Clock", "")
    {
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

    /// <summary>Re-reads the time; also called when the system clock, time zone or locale changes.</summary>
    public void Refresh()
    {
        DateTimeOffset now = _time.GetLocalNow();
        TimeText = now.ToString("t", CultureInfo.CurrentCulture);
        DateText = now.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
        CompactText = TimeText;

        TimeSpan untilNextMinute = TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(now.TimeOfDay.Ticks % TimeSpan.TicksPerMinute);
        _minuteTimer.Start(untilNextMinute + TimeSpan.FromMilliseconds(20));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _minuteTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
