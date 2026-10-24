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
