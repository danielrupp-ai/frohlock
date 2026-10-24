using FrohLock.Core.Schedule;

namespace FrohLock.Core.Ipc;

/// <summary>
/// Nachrichten über den Named Pipe zwischen Dienst (Server) und Session-Agent (Client).
/// Zeilenbasiert JSON. Der Agent fragt Status ab und meldet PIN-Versuche;
/// die PIN-Prüfung selbst passiert im Dienst (LocalSystem), nicht im Nutzerprozess.
/// </summary>
public enum IpcKind
{
    GetStatus,        // Agent -> Dienst: aktuellen Sperrzustand erfragen
    StatusReply,      // Dienst -> Agent
    SubmitPin,        // Agent -> Dienst: PIN-Versuch (nur zur Prüfung, nie gespeichert im Agent)
    PinResult,        // Dienst -> Agent
    AgentAlive,       // Agent -> Dienst: Heartbeat (Watchdog)
    RequestLock,      // Agent -> Dienst: erzwinge Sperre (z. B. Overlay ausgehebelt)
    AgentCrashReport, // Agent -> Dienst: letzter Absturz des Overlays (Reason = Fehlertext), fürs Protokoll
    BypassReport      // Agent -> Dienst: Sperrbildschirm deckte nicht alles ab (Reason = Befund), fürs Protokoll
}

public sealed class IpcMessage
{
    public IpcKind Kind { get; set; }
    public string? Pin { get; set; }
    public bool Locked { get; set; }
    public string Reason { get; set; } = "";
    public bool Success { get; set; }
    public int UnlockMinutes { get; set; }
    public long ServerTimeUnix { get; set; }
    public int PinFailuresRemaining { get; set; }
    /// <summary>Minuten bis zum nächsten Sperrbeginn (für die Schlafenszeit-Erinnerung), -1 = keine.</summary>
    public int MinutesUntilLock { get; set; } = -1;
    /// <summary>„PIN vergessen?"-Adresse (Server + /forgot), auf dem Sperrbildschirm anzeigbar.</summary>
    public string ForgotUrl { get; set; } = "";

    // Für die Benutzer-/Status-Übersicht (read-only Anzeige):
    public string ScheduleSummary { get; set; } = "";  // Sperrzeiten heute, menschenlesbar
    public int UsageMinutes { get; set; }               // heute genutzt
    public int BudgetMinutes { get; set; }              // heutiges Tageslimit (0 = keins)
    public bool Configured { get; set; }                // ist FrohLock eingerichtet (PIN gesetzt)?
    /// <summary>Von wann bis wann heute genutzt wurde (für die Zeitleiste).</summary>
    public List<UsageSegmentView> UsageSegments { get; set; } = new();
    /// <summary>Offene Auszeit in Sekunden (Countdown) und wie oft gestört wurde.</summary>
    public long PenaltySeconds { get; set; }
    public int PenaltyCount { get; set; }
    public int PenaltyTotalMinutes { get; set; }

    // Agent -> Dienst (mit AgentAlive): Bildschirmzustand der Sitzung. Nur bei Bildschirm AN,
    // Deckel offen (oder externem Bildschirm) und entsperrter Windows-Sitzung zählt Bildschirmzeit.
    public bool DisplayOn { get; set; } = true;
    public bool LidOpen { get; set; } = true;
    /// <summary>Läuft ein externer Monitor/Fernseher? Dann zählt es auch bei zugeklapptem Deckel.</summary>
    public bool ExternalDisplay { get; set; }
    /// <summary>Eigene Aufzeichnung des Overlays: wann war der Bildschirm an (letzte 24 h)? Für das Nachzählen.</summary>
    public List<MinuteRange>? ScreenOnMinutes { get; set; }
    public bool SessionLocked { get; set; }

    /// <summary>Mit BypassReport: true = Warnung für die Eltern (⚠), false = reine Diagnose.</summary>
    public bool Alert { get; set; }
    /// <summary>Mit BypassReport: Fremdfenster kommt trotz Minimieren immer wieder -> Dienst soll die Sitzung trennen.</summary>
    public bool Escalate { get; set; }
}
