namespace FrohLock.Core.Schedule;

/// <summary>
/// „Auszeit" (z. B. 15 min, weil das Kind ohne Notfall gestört hat). Mehrere Auszeiten addieren sich.
/// Die Auszeit läuft NUR ab, solange der Laptop sonst nutzbar wäre UND der Bildschirm sichtbar an ist –
/// Weggehen oder Zuklappen hält den Countdown an. Nachtsperre/Tageslimit haben Vorrang (dort wartet sie).
/// </summary>
public static class PenaltyPolicy
{
    public const string ReasonPrefix = "Auszeit";

    public static LockDecision Apply(LockDecision baseDecision, long penaltySecondsRemaining)
    {
        if (baseDecision.IsLocked) return baseDecision;   // Sperrzeit/Limit zuerst – Auszeit wartet
        if (penaltySecondsRemaining <= 0) return baseDecision;
        return LockDecision.Locked($"{ReasonPrefix}: noch {FormatCountdown(penaltySecondsRemaining)}");
    }

    /// <summary>Countdown läuft nur, wenn das Kind die Auszeit wirklich vor sich sieht.</summary>
    public static bool ShouldTick(bool baseUnlocked, bool userSessionActive, bool agentAlive, bool screenOn)
        => baseUnlocked && userSessionActive && agentAlive && screenOn;

    public static bool IsPenaltyReason(string? reason)
        => reason is not null && reason.StartsWith(ReasonPrefix, StringComparison.Ordinal);

    public static string FormatCountdown(long seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
