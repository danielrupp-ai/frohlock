using FrohLock.Core.Schedule;
using Xunit;

namespace FrohLock.Tests;

public class PenaltyPolicyTests
{
    private static LockDecision Free => LockDecision.Unlocked("frei");

    [Fact]
    public void Pending_penalty_locks_free_device()
    {
        var d = PenaltyPolicy.Apply(Free, 15 * 60);
        Assert.True(d.IsLocked);
        Assert.True(PenaltyPolicy.IsPenaltyReason(d.Reason));
        Assert.Contains("15:00", d.Reason);
    }

    [Fact]
    public void No_penalty_keeps_decision()
        => Assert.False(PenaltyPolicy.Apply(Free, 0).IsLocked);

    [Fact]
    public void Night_or_budget_lock_wins_and_penalty_waits()
    {
        var night = LockDecision.Locked("Sperrzeit");
        Assert.Equal("Sperrzeit", PenaltyPolicy.Apply(night, 900).Reason);
        // ...und läuft dort nicht ab:
        Assert.False(PenaltyPolicy.ShouldTick(baseUnlocked: false, true, true, true));
    }

    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, true, false, false)]   // zugeklappt / Bildschirm aus
    [InlineData(true, false, true, true, false)]   // niemand angemeldet
    [InlineData(true, true, false, true, false)]   // Overlay läuft nicht -> Kind sieht nichts
    public void Countdown_runs_only_while_visible(bool baseUnlocked, bool session, bool agent, bool screen, bool expected)
        => Assert.Equal(expected, PenaltyPolicy.ShouldTick(baseUnlocked, session, agent, screen));

    [Fact]
    public void Countdown_format()
    {
        Assert.Equal("30:00", PenaltyPolicy.FormatCountdown(1800));
        Assert.Equal("0:09", PenaltyPolicy.FormatCountdown(9));
    }
}
