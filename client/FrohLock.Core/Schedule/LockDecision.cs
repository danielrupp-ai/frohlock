namespace FrohLock.Core.Schedule;

public enum LockState { Unlocked, Locked }

/// <summary>
/// WARUM gesperrt ist – entscheidet z. B., ob auf dem Sperrbildschirm ein Ausnahme-Programm (Evoluzzer)
/// angeboten werden darf. Unbekannt/Other gilt immer als „streng“ (keine Ausnahme).
/// </summary>
public enum LockKind
{
    /// <summary>Nicht gesperrt oder Grund nicht eingeordnet (streng behandeln).</summary>
    Other,
    /// <summary>Sperrfenster laut Zeitplan (Schlafenszeit, Schule …).</summary>
    Schedule,
    /// <summary>Tageslimit erreicht (auch über das gemeinsame Tageskonto).</summary>
    Budget,
    /// <summary>„Sofort sperren“ der Eltern aus der Ferne.</summary>
    ParentLock,
    /// <summary>Auszeit („gestört ohne Notfall“) – Strafsperre.</summary>
    Penalty,
    /// <summary>Vertrauenszeit zu alt – im Zweifel gesperrt.</summary>
    FailSecure,
    /// <summary>Manipulation erkannt (Sperre angefordert, Overlay ausgehebelt …).</summary>
    Tamper
}

public sealed record LockDecision(LockState State, string Reason, LockKind Kind = LockKind.Other)
{
    public bool IsLocked => State == LockState.Locked;
    public static LockDecision Locked(string reason, LockKind kind = LockKind.Other) => new(LockState.Locked, reason, kind);
    public static LockDecision Unlocked(string reason) => new(LockState.Unlocked, reason, LockKind.Other);
}
