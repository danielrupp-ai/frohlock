namespace FrohLock.Core.Models;

/// <summary>
/// Vollständige, vom Server signierte Konfiguration eines Geräts.
/// Wird lokal nur als signiertes Paket gespeichert; ohne gültige Signatur -&gt; Fail-Secure.
/// </summary>
public sealed class LockConfig
{
    /// <summary>Monoton steigende Versionsnummer. Ältere Versionen werden nicht akzeptiert (Anti-Rollback).</summary>
    public long ConfigVersion { get; set; }

    public string DeviceId { get; set; } = "";

    /// <summary>Geplante Sperrfenster (Kernfunktion).</summary>
    public List<ScheduleWindow> Windows { get; set; } = new();

    /// <summary>Tägliches Gesamt-Zeitbudget in Minuten (0 = unbegrenzt). Zusätzlich zu den Sperrzeiten.</summary>
    public int DailyBudgetMinutes { get; set; }

    /// <summary>
    /// Optionales Budget je Wochentag (7 Einträge, Index 0=Sonntag .. 6=Samstag).
    /// Wert &gt; 0 überschreibt <see cref="DailyBudgetMinutes"/> an diesem Tag; sonst gilt der Standard.
    /// Leere Liste = überall Standard.
    /// </summary>
    public List<int> DailyBudgetByWeekday { get; set; } = new();

    /// <summary>Effektives Tagesbudget für einen Wochentag (Wochentag-Wert vor Standard).</summary>
    public int EffectiveDailyBudget(DayOfWeek day)
    {
        if (DailyBudgetByWeekday is { Count: 7 })
        {
            int v = DailyBudgetByWeekday[(int)day];
            if (v > 0) return v;
        }
        return DailyBudgetMinutes;
    }

    /// <summary>Wie lange eine PIN-Entsperrung gilt (Minuten), bevor der Zeitplan wieder greift.</summary>
    public int UnlockGraceMinutes { get; set; } = 60;

    /// <summary>PBKDF2-Hash des Eltern-PIN (Base64). Nie Klartext.</summary>
    public string PinHash { get; set; } = "";

    /// <summary>PBKDF2-Salt (Base64).</summary>
    public string PinSalt { get; set; } = "";

    public int PinIterations { get; set; } = 210_000;

    /// <summary>Server-Endpunkt der Brücke (HTTPS).</summary>
    public string ServerBaseUrl { get; set; } = "";

    /// <summary>SPKI-SHA256-Pins (Base64) für TLS-Pinning. Mind. ein Backup-Pin empfohlen.</summary>
    public List<string> TlsSpkiPins { get; set; } = new();

    /// <summary>Poll-Intervall gegen den Server in Sekunden.</summary>
    public int ServerPollSeconds { get; set; } = 60;

    /// <summary>Wenn keine Vertrauenszeit älter als dieser Wert (Minuten) verfügbar ist: Fail-Secure = gesperrt.</summary>
    public int MaxTrustedTimeStalenessMinutes { get; set; } = 720;

    /// <summary>Grundzustand, wenn gar keine Aussage möglich ist.</summary>
    public bool FailSecureLocked { get; set; } = true;
}
