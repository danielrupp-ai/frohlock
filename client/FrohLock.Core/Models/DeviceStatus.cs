namespace FrohLock.Core.Models;

/// <summary>Status, den der Client im Heartbeat an den Server meldet.</summary>
public sealed class DeviceStatus
{
    public string DeviceId { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public long ConfigVersion { get; set; }
    public bool CurrentlyLocked { get; set; }
    public long TrustedTimeUnix { get; set; }
    public long TrustedTimeAgeSeconds { get; set; }
    public string LockReason { get; set; } = "";
    public int PinFailuresToday { get; set; }
    public long LastBootUnix { get; set; }

    // Tages-Gesamtnutzung / Limit.
    public int UsageMinutesToday { get; set; }
    public int DailyBudgetMinutes { get; set; }
    public string UsageDay { get; set; } = "";

    /// <summary>Sekunden seit letztem Overlay-Agent-Lebenszeichen; -1 = nie/kein Agent (Diagnose).</summary>
    public int AgentAliveAgeSeconds { get; set; } = -1;

    /// <summary>Letzte Ereignis-Protokollzeilen des Geräts (Diagnose, serverseitig einsehbar).</summary>
    public string RecentAudit { get; set; } = "";
}
