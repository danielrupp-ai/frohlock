using FrohLock.Core.Models;

namespace FrohLock.Core.Schedule;

/// <summary>Wie ein 5-s-Takt Bildschirmzeit verbucht wird.</summary>
public enum TickUse
{
    /// <summary>Gar nicht (Bildschirm aus, niemand angemeldet, gesperrt …).</summary>
    None,
    /// <summary>Normale Bildschirmzeit (zählt aufs Tageslimit und ins gemeinsame Konto).</summary>
    Counted,
    /// <summary>Zeit im Ausnahme-Programm – getrennt mitgeführt, zählt NICHT aufs Limit.</summary>
    Exempt
}

/// <summary>
/// Reine Regeln für die Programm-Ausnahme (Windows 0.31.0, Daniel 21.11.2026: „Renat darf, wenn seine Zeit
/// rum ist, nur Evoluzzer spielen – Evoluzzer so lange er will“). Grundsatz: im Zweifel gesperrt bzw.
/// normal gezählt – jede unbekannte Art, jeder fehlende Wert führt zu WENIGER Freiheit, nie zu mehr.
/// </summary>
public static class AppExceptionPolicy
{
    public const string ModeOpen = "offen";
    public const string ModeLock = "sperre";
    public const string ModeTime = "zeit";
    public const string ModeBoth = "beides";

    private static string Norm(string? mode) => (mode ?? "").Trim().ToLowerInvariant();

    /// <summary>Darf das Programm auch während einer (passenden) Sperre gespielt werden?</summary>
    public static bool AllowsDuringLock(string? mode) => Norm(mode) is ModeLock or ModeBoth;

    /// <summary>Zählt die Zeit im Programm NICHT zur Bildschirmzeit?</summary>
    public static bool Uncounted(string? mode) => Norm(mode) is ModeTime or ModeBoth;

    /// <summary>Hat der Eintrag überhaupt eine Wirkung (gültige Art + Programmdatei)?</summary>
    public static bool IsEffective(AppException? e)
        => e is not null && ProgramVerifier.IsValidExeName(e.Exe) && (AllowsDuringLock(e.Mode) || Uncounted(e.Mode));

    /// <summary>Wirksame Einträge der Konfiguration (Null-/Müll-sicher).</summary>
    public static List<AppException> Effective(LockConfig? config)
        => (config?.AppExceptions ?? new List<AppException>()).Where(IsEffective).ToList();

    /// <summary>
    /// Erlaubt DIESE Sperre das Ausnahme-Programm? Nur Zeitplan, Tageslimit (auch gemeinsames Konto) und
    /// „Sofort sperren“ der Eltern – nie bei Auszeit (Strafe), Manipulation, Fail-Secure oder unbekanntem Grund.
    /// Eine noch offene Auszeit sperrt die Ausnahme ebenfalls (sie wartet nur auf das Ende der anderen Sperre).
    /// </summary>
    public static bool LockAllowsException(LockDecision decision, long penaltySecondsRemaining)
        => decision.IsLocked
           && penaltySecondsRemaining <= 0
           && decision.Kind is LockKind.Schedule or LockKind.Budget or LockKind.ParentLock;

    /// <summary>
    /// Einordnung eines Zähltakts.
    /// </summary>
    /// <param name="userSessionActive">Kind an der Konsole angemeldet.</param>
    /// <param name="agentAlive">Sperrbildschirm meldet sich (nur dann ist der Vordergrund bekannt).</param>
    /// <param name="deviceLocked">Laptop gerade gesperrt.</param>
    /// <param name="screenOn">Bildschirm sichtbar an.</param>
    /// <param name="foregroundException">Vom DIENST geprüftes Ausnahme-Programm im Vordergrund (frisch gemeldet), sonst null.</param>
    /// <param name="lockAllowsException">Ergebnis von <see cref="LockAllowsException"/> für die aktuelle Sperre.</param>
    public static TickUse ClassifyTick(bool userSessionActive, bool agentAlive, bool deviceLocked, bool screenOn,
        AppException? foregroundException, bool lockAllowsException)
    {
        if (!userSessionActive) return TickUse.None;
        if (foregroundException is not null && agentAlive && screenOn && IsEffective(foregroundException))
        {
            if (!deviceLocked && Uncounted(foregroundException.Mode)) return TickUse.Exempt;
            if (deviceLocked && lockAllowsException && AllowsDuringLock(foregroundException.Mode)) return TickUse.Exempt;
        }
        return UsageGate.ShouldCount(userSessionActive, agentAlive, deviceLocked, screenOn) ? TickUse.Counted : TickUse.None;
    }

    /// <summary>
    /// Ist ein geprüftes Ausnahme-Programm im JETZIGEN Zustand im Vordergrund erlaubt (Sperre nicht nach vorn holen
    /// bzw. Zeit nicht zählen)? Frei: bei „zeit/beides“. Gesperrt: bei „sperre/beides“ und passender Sperre.
    /// </summary>
    public static bool ForegroundAllowed(AppException? e, bool deviceLocked, bool lockAllowsException)
    {
        if (!IsEffective(e)) return false;
        return deviceLocked ? lockAllowsException && AllowsDuringLock(e!.Mode) : Uncounted(e!.Mode);
    }
}

/// <summary>Getrennt mitgeführte Zeit in Ausnahme-Programmen (zählt nicht), je Tag und Programm.</summary>
public sealed class ExemptUsage
{
    public string Day { get; set; } = "";
    public Dictionary<string, long> SecondsByProgram { get; set; } = new();

    public void Add(string dayKey, string program, long seconds)
    {
        if (seconds <= 0) return;
        if (Day != dayKey) { Day = dayKey; SecondsByProgram = new Dictionary<string, long>(); }
        SecondsByProgram ??= new Dictionary<string, long>();
        var name = string.IsNullOrWhiteSpace(program) ? "Ausnahme" : program.Trim();
        SecondsByProgram[name] = (SecondsByProgram.TryGetValue(name, out var s) ? s : 0) + seconds;
    }

    public int MinutesToday(string dayKey)
        => Day == dayKey && SecondsByProgram is not null ? (int)(SecondsByProgram.Values.Sum() / 60) : 0;

    public List<ExemptProgramView> Today(string dayKey)
        => Day == dayKey && SecondsByProgram is not null
            ? SecondsByProgram.OrderByDescending(kv => kv.Value)
                .Select(kv => new ExemptProgramView { Name = kv.Key, Minutes = (int)(kv.Value / 60) }).ToList()
            : new List<ExemptProgramView>();
}

/// <summary>Für Heartbeat/Anzeige: „Evoluzzer 45 Min“.</summary>
public sealed class ExemptProgramView
{
    public string Name { get; set; } = "";
    public int Minutes { get; set; }
}
