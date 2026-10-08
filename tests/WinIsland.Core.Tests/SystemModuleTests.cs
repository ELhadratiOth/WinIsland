using Microsoft.Extensions.Time.Testing;
using WinIsland.Core.Devices;
using WinIsland.Core.Layout;
using WinIsland.Core.Modules;

namespace WinIsland.Core.Tests;

public class SystemModuleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
    private readonly InlineDispatcher _dispatcher = new();
    private readonly FakeAudioEndpoint _speakers = new();
    private readonly FakeAudioEndpoint _mic = new();
    private readonly FakeBrightness _brightness = new();
    private readonly FakePower _power = new();

    private ControlsModule Controls(List<AttentionRequest> attention)
    {
        var module = new ControlsModule(_speakers, _mic, _brightness, _power, _dispatcher);
        module.AttentionRequested += (_, r) => attention.Add(r);
        return module;
    }

    [Fact]
    public void Volume_keys_show_the_compact_osd_but_the_islands_own_slider_does_not()
    {
        var attention = new List<AttentionRequest>();
        using ControlsModule controls = Controls(attention);
        Assert.Equal(50, controls.Volume);

        _speakers.Level = 0.72;
        _speakers.Raise(external: true);
        Assert.Equal(72, controls.Volume);
        Assert.Equal(OsdKind.Volume, controls.Osd);
        Assert.Equal("72", controls.OsdText);
        AttentionRequest request = Assert.Single(attention);
        Assert.Equal(IslandSize.Compact, request.Size);
        Assert.Equal(AttentionPriority.Feedback, request.Priority);

        controls.Volume = 30;
        Assert.Equal([0.3], _speakers.LevelRequests);
        Assert.Single(attention);
    }

    [Fact]
    public void Mute_shows_muted_glyph()
    {
        using ControlsModule controls = Controls([]);
        _speakers.IsMuted = true;
        _speakers.Raise(external: true);

        Assert.Equal("", controls.VolumeGlyph);
        Assert.Equal("Muted", controls.OsdText);
    }

    [Fact]
    public void Brightness_keys_show_the_osd()
    {
        var attention = new List<AttentionRequest>();
        using ControlsModule controls = Controls(attention);
        _brightness.Level = 65;
        _brightness.Raise(external: true);

        Assert.Equal(OsdKind.Brightness, controls.Osd);
        Assert.Equal(65, controls.Brightness);
        Assert.Single(attention);
    }

    [Fact]
    public void Plugging_in_and_low_battery_are_announced()
    {
        var attention = new List<AttentionRequest>();
        using ControlsModule controls = Controls(attention);

        _power.Set(new PowerStatus(true, 80, true, true, false));
        Assert.Equal(OsdKind.Battery, controls.Osd);
        Assert.Equal("Charging", controls.OsdLabel);
        Assert.Equal(Palette.Green, controls.AccentArgb);

        _power.Set(new PowerStatus(true, 21, false, false, false));
        _power.Set(new PowerStatus(true, 20, false, false, false));
        Assert.Equal(2, attention.Count);
        Assert.Equal(AttentionPriority.Important, attention[^1].Priority);
        Assert.Equal("Low battery", controls.OsdLabel);

        _power.Set(new PowerStatus(true, 19, false, false, false));
        Assert.Equal(2, attention.Count);
    }

    [Fact]
    public void Privacy_takes_the_pill_and_announces_new_recorders()
    {
        var privacy = new FakePrivacy();
        using var module = new PrivacyModule(privacy, _mic, _dispatcher);
        var attention = new List<AttentionRequest>();
        module.AttentionRequested += (_, r) => attention.Add(r);
        Assert.False(module.IsAvailable);

        privacy.Set(new SensorUse(SensorKind.Microphone, "Teams"));
        Assert.True(module.IsAvailable);
        Assert.Equal(ModulePriority.Live, module.CompactPriority);
        Assert.Equal("Teams", module.CompactText);
        Assert.Equal(Palette.Orange, module.AccentArgb);
        Assert.Single(attention);

        privacy.Set(new SensorUse(SensorKind.Microphone, "Teams"), new SensorUse(SensorKind.Camera, "Teams"));
        Assert.Equal(Palette.Green, module.AccentArgb);
        Assert.Equal("Camera · Mic", module.CompactDetail);
        Assert.Equal(2, attention.Count);

        module.ToggleMicCommand.Execute(null);
        Assert.True(_mic.IsMuted);
        Assert.True(module.IsMicMuted);

        privacy.Set();
        Assert.False(module.IsAvailable);
    }

    [Fact]
    public void Countdown_ticks_and_finishes_with_a_notice()
    {
        using var timer = new TimerModule(_time, _dispatcher);
        int finished = 0;
        var attention = new List<AttentionRequest>();
        timer.Finished += (_, _) => finished++;
        timer.AttentionRequested += (_, r) => attention.Add(r);

        timer.PresetCommand.Execute("1");
        Assert.True(timer.IsRunning);
        Assert.Equal("1:00", timer.DisplayText);
        Assert.Equal(ModulePriority.Live, timer.CompactPriority);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("0:59", timer.DisplayText);
        Assert.Equal("0:59", timer.CompactDetail);

        timer.StartPauseCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("0:59", timer.DisplayText);

        timer.StartPauseCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, finished);
        Assert.Equal("Done", timer.DisplayText);
        Assert.True(timer.IsIdle);
        Assert.Equal(AttentionPriority.Important, Assert.Single(attention).Priority);
    }

    [Fact]
    public void Pomodoro_rolls_into_a_break()
    {
        using var timer = new TimerModule(_time, _dispatcher);
        timer.SetModeCommand.Execute("Pomodoro");
        Assert.Equal("25:00", timer.DisplayText);
        Assert.Equal("Focus · round 1 of 4", timer.Caption);

        timer.StartPauseCommand.Execute(null);
        _time.Advance(TimerModule.PomodoroFocus);

        Assert.True(timer.IsRunning);
        Assert.Equal("Short break", timer.Caption);
        Assert.Equal("5:00", timer.DisplayText);
    }

    [Fact]
    public void Stopwatch_counts_up()
    {
        using var timer = new TimerModule(_time, _dispatcher);
        timer.Mode = TimerMode.Stopwatch;
        timer.StartPauseCommand.Execute(null);
        _time.Advance(TimeSpan.FromSeconds(75));

        Assert.Equal("1:15", timer.DisplayText);
        timer.ResetCommand.Execute(null);
        Assert.Equal("0:00", timer.DisplayText);
        Assert.True(timer.IsIdle);
    }
}
