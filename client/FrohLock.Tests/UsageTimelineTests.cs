using FrohLock.Core.Schedule;
using Xunit;

namespace FrohLock.Tests;

public class UsageTimelineTests
{
    // Lokale Zeit = UTC, damit der Test zeitzonenunabhängig ist.
    private static DateTime ToLocal(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
    private static long At(int h, int m) => new DateTimeOffset(2026, 10, 10, h, m, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static void Use(List<UsageSegment> segs, long from, long to)
    {
        for (long t = from + 5; t <= to; t += 5) UsageTimeline.Accrue(segs, t, 5);
    }

    [Fact]
    public void Continuous_ticks_form_one_segment()
    {
        var segs = new List<UsageSegment>();
        Use(segs, At(9, 0), At(10, 30));
        Assert.Single(segs);
        Assert.Equal(90 * 60, segs[0].Seconds);
        var v = UsageTimeline.ToViews(segs, ToLocal).Single();
        Assert.Equal("09:00", v.Start);
        Assert.Equal("10:30", v.End);
        Assert.Equal(90, v.Minutes);
    }

    [Fact]
    public void Lid_closed_gap_splits_segments_and_does_not_count()
    {
        var segs = new List<UsageSegment>();
        Use(segs, At(9, 0), At(10, 0));
        // 9 Stunden zugeklappt: keine Ticks.
        Use(segs, At(19, 0), At(20, 0));
        var views = UsageTimeline.ToViews(segs, ToLocal);
        Assert.Equal(2, views.Count);
        Assert.Equal(120, views.Sum(v => v.Minutes));
        Assert.Equal("19:00", views[1].Start);
    }

    [Fact]
    public void Short_pauses_are_merged_for_display()
    {
        var segs = new List<UsageSegment>();
        Use(segs, At(14, 0), At(14, 30));
        Use(segs, At(14, 33), At(15, 0)); // 3 min Pause
        Assert.Equal(2, segs.Count);
        var v = UsageTimeline.ToViews(segs, ToLocal).Single();
        Assert.Equal("14:00", v.Start);
        Assert.Equal("15:00", v.End);
        Assert.Equal(57, v.Minutes); // Pause zählt nicht mit
    }

    [Fact]
    public void Segment_count_stays_bounded()
    {
        var segs = new List<UsageSegment>();
        long t = At(0, 0);
        for (int i = 0; i < 1000; i++) { t += 200; UsageTimeline.Accrue(segs, t, 5); }
        Assert.True(segs.Count <= 301);
        Assert.Equal(5000, segs.Sum(s => s.Seconds));
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)]  // Bildschirm aus
    [InlineData(true, false, false, false)]  // Deckel zu
    [InlineData(true, true, true, false)]    // Windows gesperrt (Win+L)
    public void Screen_must_be_visible(bool displayOn, bool lidOpen, bool sessionLocked, bool expected)
        => Assert.Equal(expected, UsageGate.ScreenOn(displayOn, lidOpen, sessionLocked));

    [Fact]
    public void Counts_only_when_everything_fits()
    {
        Assert.True(UsageGate.ShouldCount(true, true, false, true));
        Assert.False(UsageGate.ShouldCount(true, true, false, false)); // zugeklappt
        Assert.False(UsageGate.ShouldCount(true, true, true, true));   // schon gesperrt
        Assert.False(UsageGate.ShouldCount(true, false, false, true)); // Agent schweigt (Standby)
    }
}
