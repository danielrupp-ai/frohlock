using FrohLock.Core.Schedule;
using Xunit;

namespace FrohLock.Tests;

public class ScreenLedgerTests
{
    private static long Min(int h, int m) => new DateTimeOffset(2026, 10, 18, h, m, 0, TimeSpan.Zero).ToUnixTimeSeconds() / 60;

    [Fact]
    public void Consecutive_minutes_form_one_range()
    {
        var r = new List<MinuteRange>();
        for (long m = Min(10, 0); m < Min(10, 30); m++) { ScreenLedger.Mark(r, m); ScreenLedger.Mark(r, m); }
        Assert.Single(r);
        Assert.Equal(Min(10, 0), r[0].Start);
        Assert.Equal(Min(10, 30), r[0].End);
    }

    [Fact]
    public void Screen_off_starts_new_range_and_old_minutes_drop_out()
    {
        var r = new List<MinuteRange>();
        ScreenLedger.Mark(r, Min(8, 0));
        ScreenLedger.Mark(r, Min(9, 0));
        Assert.Equal(2, r.Count);
        ScreenLedger.Mark(r, Min(9, 0) + ScreenLedger.KeepMinutes + 5);
        Assert.Single(r); // nach 24 h ist der Morgen weg
    }

    [Fact]
    public void Overlap_counts_only_screen_on_seconds_inside_the_gap()
    {
        // Bildschirm an 10:00–12:43, Dienst stand 09:30–12:43 still -> 2 Std 43 Min nachzählen.
        var r = new List<MinuteRange> { new() { Start = Min(10, 0), End = Min(12, 43) } };
        long gapFrom = Min(9, 30) * 60, gapTo = Min(12, 43) * 60;
        Assert.Equal(163 * 60, ScreenLedger.OverlapSeconds(r, gapFrom, gapTo));
        Assert.Equal(0, ScreenLedger.OverlapSeconds(r, Min(13, 0) * 60, Min(14, 0) * 60));
        Assert.Equal(0, ScreenLedger.OverlapSeconds(null, gapFrom, gapTo));
    }

    [Theory]
    [InlineData(5, 5, GapKind.None)]              // normaler Takt
    [InlineData(13 * 3600, 20, GapKind.Slept)]    // über Nacht geschlafen
    [InlineData(9600, 9590, GapKind.StalledAwake)] // 2 Std 40 Min wach, Dienst stand still
    [InlineData(600, 60, GapKind.Slept)]          // kurz wach im Standby (Wartung) – kein Alarm
    public void Classifies_gaps(double wall, double awake, GapKind expected)
        => Assert.Equal(expected, GapAnalysis.Classify(wall, awake));

    [Theory]
    [InlineData(45, "1 Min")]
    [InlineData(9780, "2 Std 43 Min")]
    [InlineData(7200, "2 Std")]
    public void Formats_duration(long seconds, string expected)
        => Assert.Equal(expected, GapAnalysis.Duration(seconds));
}
