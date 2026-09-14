using FrohLock.Core.Models;
using Xunit;

namespace FrohLock.Tests;

public class WeekdayBudgetTests
{
    [Fact]
    public void No_weekday_list_uses_default()
    {
        var c = new LockConfig { DailyBudgetMinutes = 120 };
        Assert.Equal(120, c.EffectiveDailyBudget(DayOfWeek.Monday));
        Assert.Equal(120, c.EffectiveDailyBudget(DayOfWeek.Sunday));
    }

    [Fact]
    public void Weekday_value_overrides_default()
    {
        // Index 0=So .. 6=Sa. Mo=60, Sa/So=180, Rest 0 -> Standard 90.
        var wb = new List<int> { 180, 60, 0, 0, 0, 0, 180 };
        var c = new LockConfig { DailyBudgetMinutes = 90, DailyBudgetByWeekday = wb };
        Assert.Equal(60, c.EffectiveDailyBudget(DayOfWeek.Monday));   // idx1
        Assert.Equal(180, c.EffectiveDailyBudget(DayOfWeek.Saturday)); // idx6
        Assert.Equal(180, c.EffectiveDailyBudget(DayOfWeek.Sunday));   // idx0
        Assert.Equal(90, c.EffectiveDailyBudget(DayOfWeek.Wednesday)); // idx3 -> 0 -> Standard
    }

    [Fact]
    public void Malformed_list_falls_back_to_default()
    {
        var c = new LockConfig { DailyBudgetMinutes = 100, DailyBudgetByWeekday = new List<int> { 30 } }; // nicht 7
        Assert.Equal(100, c.EffectiveDailyBudget(DayOfWeek.Monday));
    }
}
