namespace FrohLock.Core.Schedule;

/// <summary>
/// Reine Logik für das optionale Tages-Gesamtlimit. Wird auf die Zeitplan-Entscheidung
/// aufgesetzt: ist der Zeitplan „frei", aber das Tagesbudget erschöpft, wird gesperrt –
/// außer eine (Eltern-)Entsperrung ist aktiv (Zusatzzeit).
/// </summary>
public static class BudgetPolicy
{
    /// <param name="usedMinutes">Gesamtnutzung heute – bei gemeinsamem Tageskonto inkl. der anderen Geräte.</param>
    /// <param name="otherDevicesMinutes">Davon auf den anderen Geräten des Kindes (nur für den Text).</param>
    public static LockDecision Apply(LockDecision baseDecision, int budgetMinutes, int usedMinutes,
        bool graceActive, string? graceUntilLabel, int otherDevicesMinutes = 0)
    {
        if (baseDecision.IsLocked) return baseDecision;   // Zeitplan/Fail-Secure hat Vorrang
        if (budgetMinutes <= 0) return baseDecision;       // kein Limit gesetzt
        if (usedMinutes < budgetMinutes) return baseDecision;

        return graceActive
            ? LockDecision.Unlocked($"Zusatzzeit bis {graceUntilLabel}")
            : LockDecision.Locked(otherDevicesMinutes > 0
                ? $"Tageslimit erreicht ({usedMinutes}/{budgetMinutes} min, davon {otherDevicesMinutes} min am anderen Gerät)"
                : $"Tageslimit erreicht ({usedMinutes}/{budgetMinutes} min)", LockKind.Budget);
    }
}
