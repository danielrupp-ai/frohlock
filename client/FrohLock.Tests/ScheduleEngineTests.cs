using FrohLock.Core.Models;
using FrohLock.Core.Schedule;
using Xunit;

namespace FrohLock.Tests;

public class ScheduleEngineTests
{
    private static LockConfig Cfg(params ScheduleWindow[] windows) => new()
    {
        Windows = windows.ToList(),
        MaxTrustedTimeStalenessMinutes = 720
    };

    private static ScheduleWindow Win(int startMin, int endMin, params DayOfWeek[] days) => new()
    {
        StartMinute = startMin,
        EndMinute = endMin,
        Days = days.ToList(),
        Enabled = true
    };

    private static DateTime At(int h, int m, DayOfWeek day = DayOfWeek.Wednesday)
    {
        // 2026-09-16 ist ein Mittwoch – Basis, dann auf gewünschten Tag verschieben.
        var d = new DateTime(2026, 9, 16, h, m, 0, DateTimeKind.Local);
        while (d.DayOfWeek != day) d = d.AddDays(1);
        return new DateTime(d.Year, d.Month, d.Day, h, m, 0, DateTimeKind.Local);
    }

    [Fact]
    public void Within_normal_window_is_locked()
    {
        var e = new ScheduleEngine(Cfg(Win(20 * 60, 22 * 60))); // 20:00-22:00
        Assert.True(e.Decide(At(21, 0), TimeSpan.Zero, null).IsLocked);
    }

    [Fact]
    public void Outside_window_is_unlocked()
    {
        var e = new ScheduleEngine(Cfg(Win(20 * 60, 22 * 60)));
        Assert.False(e.Decide(At(19, 59), TimeSpan.Zero, null).IsLocked);
        Assert.False(e.Decide(At(22, 0), TimeSpan.Zero, null).IsLocked); // Ende exklusiv
    }

    [Fact]
    public void Window_over_midnight_locks_both_sides()
    {
        var e = new ScheduleEngine(Cfg(Win(21 * 60, 7 * 60))); // 21:00-07:00
        Assert.True(e.Decide(At(23, 0), TimeSpan.Zero, null).IsLocked);   // abends
        Assert.True(e.Decide(At(6, 30), TimeSpan.Zero, null).IsLocked);   // morgens
        Assert.False(e.Decide(At(12, 0), TimeSpan.Zero, null).IsLocked);  // mittags frei
        Assert.False(e.Decide(At(7, 0), TimeSpan.Zero, null).IsLocked);   // Ende exklusiv
    }

    [Fact]
    public void Day_filter_respected()
    {
        var e = new ScheduleEngine(Cfg(Win(8 * 60, 22 * 60, DayOfWeek.Monday)));
        Assert.True(e.Decide(At(12, 0, DayOfWeek.Monday), TimeSpan.Zero, null).IsLocked);
        Assert.False(e.Decide(At(12, 0, DayOfWeek.Tuesday), TimeSpan.Zero, null).IsLocked);
    }

    [Fact]
    public void Stale_trusted_time_fails_secure_locked()
    {
        var e = new ScheduleEngine(Cfg(Win(20 * 60, 22 * 60)));
        // Mittags (kein Fenster), aber Zeit ist zu alt -> trotzdem gesperrt.
        var d = e.Decide(At(12, 0), TimeSpan.FromMinutes(721), null);
        Assert.True(d.IsLocked);
        Assert.Contains("Fail-Secure", d.Reason);
    }

    [Fact]
    public void Active_unlock_overrides_window()
    {
        var e = new ScheduleEngine(Cfg(Win(20 * 60, 22 * 60)));
        var now = At(21, 0);
        Assert.False(e.Decide(now, TimeSpan.Zero, now.AddMinutes(30)).IsLocked); // entsperrt bis 21:30
        Assert.True(e.Decide(now, TimeSpan.Zero, now.AddMinutes(-1)).IsLocked);  // Entsperrung abgelaufen
    }

    [Fact]
    public void NextBoundary_finds_window_start()
    {
        var e = new ScheduleEngine(Cfg(Win(20 * 60, 22 * 60)));
        var now = At(19, 30);
        var b = e.NextBoundary(now);
        Assert.Equal(20, b.Hour);
        Assert.Equal(0, b.Minute);
    }
}
