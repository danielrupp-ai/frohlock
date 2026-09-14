using FrohLock.Core.Schedule;
using Xunit;

namespace FrohLock.Tests;

public class BudgetPolicyTests
{
    private static LockDecision Free => LockDecision.Unlocked("frei");
    private static LockDecision Locked => LockDecision.Locked("Sperrzeit");

    [Fact]
    public void No_budget_keeps_base_decision()
    {
        Assert.False(BudgetPolicy.Apply(Free, 0, 999, false, null).IsLocked);
    }

    [Fact]
    public void Under_budget_stays_free()
    {
        Assert.False(BudgetPolicy.Apply(Free, 120, 90, false, null).IsLocked);
    }

    [Fact]
    public void Over_budget_locks()
    {
        var d = BudgetPolicy.Apply(Free, 120, 120, false, null);
        Assert.True(d.IsLocked);
        Assert.Contains("Tageslimit", d.Reason);
    }

    [Fact]
    public void Over_budget_but_grace_gives_extra_time()
    {
        var d = BudgetPolicy.Apply(Free, 120, 150, true, "20:30");
        Assert.False(d.IsLocked);
        Assert.Contains("Zusatzzeit", d.Reason);
    }

    [Fact]
    public void Schedule_lock_wins_over_budget()
    {
        // Schon durch Zeitplan gesperrt -> bleibt gesperrt (nicht "Tageslimit").
        var d = BudgetPolicy.Apply(Locked, 120, 200, false, null);
        Assert.True(d.IsLocked);
        Assert.Equal("Sperrzeit", d.Reason);
    }
}
