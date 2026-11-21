using FrohLock.Core.Models;
using FrohLock.Core.Schedule;
using Xunit;

namespace FrohLock.Tests;

/// <summary>Programm-Ausnahme (Evoluzzer, Windows 0.31.0): Arten, erlaubte Sperr-Gründe, Zählen.</summary>
public class AppExceptionPolicyTests
{
    private static AppException Evo(string mode) => new() { Name = "Evoluzzer", Exe = "Evoluzzer.exe", Mode = mode };

    [Theory]
    [InlineData("offen", false, false)]
    [InlineData("sperre", true, false)]
    [InlineData("zeit", false, true)]
    [InlineData("beides", true, true)]
    [InlineData("BEIDES", true, true)]
    [InlineData("irgendwas", false, false)]
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    public void Modes_unknown_means_no_effect(string? mode, bool duringLock, bool uncounted)
    {
        Assert.Equal(duringLock, AppExceptionPolicy.AllowsDuringLock(mode));
        Assert.Equal(uncounted, AppExceptionPolicy.Uncounted(mode));
        Assert.Equal(duringLock || uncounted, AppExceptionPolicy.IsEffective(new AppException { Exe = "Evoluzzer.exe", Mode = mode ?? "" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Evoluzzer")]
    [InlineData("..\\Evoluzzer.exe")]
    [InlineData("C:\\Spiele\\Evoluzzer.exe")]
    public void Rule_without_valid_exe_has_no_effect(string exe)
        => Assert.False(AppExceptionPolicy.IsEffective(new AppException { Exe = exe, Mode = "beides" }));

    [Fact]
    public void Effective_tolerates_null_and_garbage()
    {
        Assert.Empty(AppExceptionPolicy.Effective(null));
        Assert.Empty(AppExceptionPolicy.Effective(new LockConfig { AppExceptions = null }));
        var cfg = new LockConfig { AppExceptions = new() { Evo("offen"), Evo("kaputt"), Evo("beides"), new AppException { Mode = "beides" } } };
        Assert.Single(AppExceptionPolicy.Effective(cfg));
    }

    [Fact]
    public void Lock_allows_exception_only_when_daily_budget_is_used_up()
    {
        Assert.False(AppExceptionPolicy.LockAllowsException(LockDecision.Locked("Sperrfenster", LockKind.Schedule), 0));   // Schlafenszeit bleibt streng
        Assert.True(AppExceptionPolicy.LockAllowsException(LockDecision.Locked("Tageslimit", LockKind.Budget), 0));
        Assert.False(AppExceptionPolicy.LockAllowsException(LockDecision.Locked("Eltern", LockKind.ParentLock), 0));     // „Sofort sperren“ bleibt streng

        Assert.False(AppExceptionPolicy.LockAllowsException(LockDecision.Locked("Auszeit", LockKind.Penalty), 900));
        Assert.False(AppExceptionPolicy.LockAllowsException(LockDecision.Locked("Fail-Secure", LockKind.FailSecure), 0));
        Assert.False(AppExceptionPolicy.LockAllowsException(LockDecision.Locked("Manipulation", LockKind.Tamper), 0));
        Assert.False(AppExceptionPolicy.LockAllowsException(LockDecision.Locked("unbekannt"), 0));   // Other = streng
        Assert.False(AppExceptionPolicy.LockAllowsException(LockDecision.Unlocked("frei"), 0));
        // Tageslimit, aber eine Auszeit wartet noch -> keine Ausnahme.
        Assert.False(AppExceptionPolicy.LockAllowsException(LockDecision.Locked("Tageslimit", LockKind.Budget), 60));
    }

    [Fact]
    public void Policies_tag_their_lock_kind()
    {
        var budget = BudgetPolicy.Apply(LockDecision.Unlocked("frei"), 120, 120, false, null);
        Assert.Equal(LockKind.Budget, budget.Kind);
        var shared = BudgetPolicy.Apply(LockDecision.Unlocked("frei"), 120, 125, false, null, 40);
        Assert.Equal(LockKind.Budget, shared.Kind);   // gemeinsames Tageskonto
        var penalty = PenaltyPolicy.Apply(LockDecision.Unlocked("frei"), 600);
        Assert.Equal(LockKind.Penalty, penalty.Kind);

        var cfg = new LockConfig
        {
            Windows = new() { new ScheduleWindow { StartMinute = 21 * 60, EndMinute = 7 * 60, Enabled = true } },
            MaxTrustedTimeStalenessMinutes = 60
        };
        var engine = new ScheduleEngine(cfg);
        var night = new DateTime(2026, 11, 21, 22, 0, 0, DateTimeKind.Local);
        Assert.Equal(LockKind.Schedule, engine.Decide(night, TimeSpan.Zero, null).Kind);
        Assert.Equal(LockKind.FailSecure, engine.Decide(night, TimeSpan.FromHours(5), null).Kind);
        Assert.False(AppExceptionPolicy.LockAllowsException(engine.Decide(night, TimeSpan.FromHours(5), null), 0));
    }

    [Fact]
    public void Tick_classification()
    {
        var zeit = Evo("zeit");
        var sperre = Evo("sperre");
        var beides = Evo("beides");

        // Frei + „zählt nicht“-Programm vorn -> getrennt.
        Assert.Equal(TickUse.Exempt, AppExceptionPolicy.ClassifyTick(true, true, false, true, zeit, false));
        Assert.Equal(TickUse.Exempt, AppExceptionPolicy.ClassifyTick(true, true, false, true, beides, false));
        // Frei + nur „in der Sperre erlaubt“ -> zählt ganz normal.
        Assert.Equal(TickUse.Counted, AppExceptionPolicy.ClassifyTick(true, true, false, true, sperre, false));
        // Frei, nichts Besonderes vorn -> normal.
        Assert.Equal(TickUse.Counted, AppExceptionPolicy.ClassifyTick(true, true, false, true, null, false));
        // Gesperrt + passende Sperre -> Spielzeit wird (nur zur Anzeige) mitgeführt.
        Assert.Equal(TickUse.Exempt, AppExceptionPolicy.ClassifyTick(true, true, true, true, beides, true));
        Assert.Equal(TickUse.Exempt, AppExceptionPolicy.ClassifyTick(true, true, true, true, sperre, true));
        // Gesperrt, aber Sperre erlaubt es nicht (Auszeit) -> nichts.
        Assert.Equal(TickUse.None, AppExceptionPolicy.ClassifyTick(true, true, true, true, beides, false));
        // Sperrbildschirm schweigt -> Vordergrund unbekannt -> normal zählen (wie bisher).
        Assert.Equal(TickUse.Counted, AppExceptionPolicy.ClassifyTick(true, false, false, true, zeit, false));
        // Bildschirm aus / niemand angemeldet -> nichts.
        Assert.Equal(TickUse.None, AppExceptionPolicy.ClassifyTick(true, true, false, false, zeit, false));
        Assert.Equal(TickUse.None, AppExceptionPolicy.ClassifyTick(false, true, false, true, zeit, false));
        // Unbekannte Art -> normal zählen.
        Assert.Equal(TickUse.Counted, AppExceptionPolicy.ClassifyTick(true, true, false, true, Evo("kaputt"), false));
    }

    [Fact]
    public void Foreground_allowed_matches_state()
    {
        Assert.True(AppExceptionPolicy.ForegroundAllowed(Evo("beides"), deviceLocked: true, lockAllowsException: true));
        Assert.False(AppExceptionPolicy.ForegroundAllowed(Evo("beides"), deviceLocked: true, lockAllowsException: false));
        Assert.False(AppExceptionPolicy.ForegroundAllowed(Evo("zeit"), deviceLocked: true, lockAllowsException: true));
        Assert.True(AppExceptionPolicy.ForegroundAllowed(Evo("zeit"), deviceLocked: false, lockAllowsException: false));
        Assert.False(AppExceptionPolicy.ForegroundAllowed(Evo("sperre"), deviceLocked: false, lockAllowsException: false));
        Assert.False(AppExceptionPolicy.ForegroundAllowed(null, deviceLocked: true, lockAllowsException: true));
    }

    [Fact]
    public void Exempt_time_does_not_reach_the_daily_budget()
    {
        // 2 h Limit, 100 Min schon genutzt. Jetzt 60 Min Evoluzzer („beides“) + 20 Min Browser.
        int budget = 120;
        long countedSeconds = 100 * 60;
        var exempt = new ExemptUsage();
        var evo = Evo("beides");
        for (int i = 0; i < 60 * 12; i++)   // 60 Min in 5-s-Takten
        {
            var use = AppExceptionPolicy.ClassifyTick(true, true, false, true, evo, false);
            if (use == TickUse.Counted) countedSeconds += 5;
            else if (use == TickUse.Exempt) exempt.Add("2026-11-21", evo.Name, 5);
        }
        var stillFree = BudgetPolicy.Apply(LockDecision.Unlocked("frei"), budget, (int)(countedSeconds / 60), false, null);
        Assert.False(stillFree.IsLocked);
        Assert.Equal(60, exempt.MinutesToday("2026-11-21"));

        for (int i = 0; i < 20 * 12; i++)
            if (AppExceptionPolicy.ClassifyTick(true, true, false, true, null, false) == TickUse.Counted) countedSeconds += 5;
        var locked = BudgetPolicy.Apply(LockDecision.Unlocked("frei"), budget, (int)(countedSeconds / 60), false, null);
        Assert.True(locked.IsLocked);
        Assert.Equal(LockKind.Budget, locked.Kind);
        // Und danach darf Evoluzzer weiter gespielt werden, Zeit läuft nur in der getrennten Anzeige weiter.
        Assert.True(AppExceptionPolicy.LockAllowsException(locked, 0));
        Assert.Equal(TickUse.Exempt, AppExceptionPolicy.ClassifyTick(true, true, true, true, evo, true));
    }

    [Fact]
    public void Exempt_usage_resets_per_day_and_lists_programs()
    {
        var u = new ExemptUsage();
        u.Add("2026-11-21", "Evoluzzer", 45 * 60);
        u.Add("2026-11-21", "Evoluzzer", 30);
        u.Add("2026-11-21", "", 120);
        Assert.Equal(47, u.MinutesToday("2026-11-21"));
        Assert.Equal(0, u.MinutesToday("2026-11-22"));
        var list = u.Today("2026-11-21");
        Assert.Equal("Evoluzzer", list[0].Name);
        Assert.Equal(45, list[0].Minutes);
        u.Add("2026-11-22", "Evoluzzer", 60);
        Assert.Equal(1, u.MinutesToday("2026-11-22"));
        Assert.Equal(0, u.MinutesToday("2026-11-21"));
    }
}
