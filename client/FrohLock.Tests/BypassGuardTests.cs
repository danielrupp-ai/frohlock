using FrohLock.Core.Schedule;
using Xunit;

namespace FrohLock.Tests;

public class BypassGuardTests
{
    private static readonly PixelRect Laptop = new(0, 0, 1920, 1200);
    private static readonly PixelRect External = new(1920, 0, 4480, 1440);

    [Fact]
    public void Window_sized_with_external_monitor_misses_laptop_after_unplugging()
    {
        // Fall 24.10.: Fläche berechnet, als nur der externe Monitor lief; danach nur noch das Laptop-Display.
        var stale = new PixelRect(-2560, 0, 0, 1440);
        Assert.Single(CoverageRules.UncoveredMonitors(new[] { Laptop }, stale));
    }

    [Fact]
    public void Window_sized_for_laptop_leaves_bigger_external_partly_free()
        => Assert.Single(CoverageRules.UncoveredMonitors(new[] { new PixelRect(0, 0, 2560, 1440) },
            new PixelRect(0, 0, 1280, 800)));

    [Fact]
    public void Virtual_screen_rect_covers_all_monitors()
        => Assert.Empty(CoverageRules.UncoveredMonitors(new[] { Laptop, External }, new PixelRect(0, 0, 4480, 1440)));
}

public class BypassEscalationTests
{
    [Fact]
    public void Covered_lock_never_escalates()
    {
        var e = new BypassEscalation();
        for (int s = 0; s < 120; s++) Assert.False(e.Step(s, false));
    }

    [Fact]
    public void Persistent_foreign_window_escalates_once_then_cools_down()
    {
        var e = new BypassEscalation();
        int escalations = 0;
        for (int i = 0; i < 200; i++) if (e.Step(i * 0.7, true)) escalations++;
        // 140 s Dauerumgehung: einmal sofort (nach ~14 s), dann frühestens nach 90 s Pause -> 2
        Assert.Equal(2, escalations);
    }

    [Fact]
    public void Short_flicker_does_not_escalate()
    {
        var e = new BypassEscalation();
        for (int i = 0; i < 10; i++) Assert.False(e.Step(i * 0.7, true));
        Assert.False(e.Step(100, true)); // alte Treffer sind abgelaufen
        Assert.Equal(1, e.RecentHits);
    }

    [Fact]
    public void Shell_windows_are_recognised()
    {
        Assert.True(ShellWindows.IsShell("Shell_TrayWnd"));
        Assert.True(ShellWindows.IsShell("Windows.UI.Core.CoreWindow"));
        Assert.False(ShellWindows.IsShell("ApplicationFrameWindow")); // UWP-Spiele (Minecraft) müssen weichen
        Assert.False(ShellWindows.IsShell("GLFW30"));
        Assert.False(ShellWindows.IsShell(null));
    }
}
