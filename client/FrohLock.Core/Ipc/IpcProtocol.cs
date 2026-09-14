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
    RequestLock       // Agent -> Dienst: erzwinge Sperre (z. B. Overlay ausgehebelt)
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
}
