using FrohLock.Core.Schedule;
using Xunit;

namespace FrohLock.Tests;

/// <summary>„Sofort sperren“ der Eltern endet mit dem nächsten Tageswechsel; Manipulations-Sperre bleibt.</summary>
public class ForcedLockPolicyTests
{
    [Fact]
    public void Parent_lock_same_day_is_kept()
        => Assert.Equal(ForcedLockStep.Keep, ForcedLockPolicy.Check(true, true, "2026-11-24", "2026-11-24"));

    [Fact]
    public void Parent_lock_next_day_expires()
        => Assert.Equal(ForcedLockStep.Expire, ForcedLockPolicy.Check(true, true, "2026-11-24", "2026-11-25"));

    [Theory]
    [InlineData("2026-11-30", "2026-12-01")]   // Monatswechsel
    [InlineData("2026-12-31", "2027-01-01")]   // Jahreswechsel
    [InlineData("2027-02-28", "2027-03-01")]
    public void Parent_lock_expires_over_month_and_year_change(string lockDay, string today)
        => Assert.Equal(ForcedLockStep.Expire, ForcedLockPolicy.Check(true, true, lockDay, today));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Parent_lock_without_day_is_stamped_not_expired(string? lockDay)
        => Assert.Equal(ForcedLockStep.StampToday, ForcedLockPolicy.Check(true, true, lockDay, "2026-11-24"));

    [Fact]
    public void Tamper_lock_is_kept_even_days_later()
    {
        Assert.Equal(ForcedLockStep.Keep, ForcedLockPolicy.Check(true, false, "2026-11-24", "2026-11-24"));
        Assert.Equal(ForcedLockStep.Keep, ForcedLockPolicy.Check(true, false, "2026-11-24", "2026-11-25"));
        Assert.Equal(ForcedLockStep.Keep, ForcedLockPolicy.Check(true, false, "2026-11-24", "2027-01-01"));
        Assert.Equal(ForcedLockStep.Keep, ForcedLockPolicy.Check(true, false, "", "2026-11-25"));   // auch ohne Tag: nie ablaufen
    }

    [Fact]
    public void Clock_set_back_keeps_the_lock()
        => Assert.Equal(ForcedLockStep.Keep, ForcedLockPolicy.Check(true, true, "2026-11-24", "2026-11-23"));

    [Fact]
    public void No_lock_means_nothing_to_do()
    {
        Assert.Equal(ForcedLockStep.Keep, ForcedLockPolicy.Check(false, true, "2026-11-24", "2026-11-25"));
        Assert.Equal(ForcedLockStep.Keep, ForcedLockPolicy.Check(false, false, "", "2026-11-25"));
    }
}
