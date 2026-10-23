using FrohLock.Core;
using FrohLock.Core.Schedule;

namespace FrohLock.Service;

/// <summary>
/// Persistierter Laufzeitzustand des Dienstes (überlebt Neustart).
/// Enthält KEINE Klartext-Geheimnisse.
/// </summary>
public sealed class RuntimeState
{
    public long ConfigVersion { get; set; }
    public long UnlockUntilUnix { get; set; }   // aktive Entsperrung (Vertrauenszeit), 0 = keine
    public long MasterUnlockUntilUnix { get; set; }  // Master-PIN-Override (überschreibt ALLES), 0 = keine
    /// <summary>„Sofort sperren" der Eltern: gilt, bis Eltern-PIN, Fern-Entsperren oder Master-PIN freigeben.</summary>
    public bool ForcedLock { get; set; }
    public int PinFailuresToday { get; set; }
    public string PinFailureDay { get; set; } = ""; // yyyy-MM-dd zur Tagesrückstellung
    public long LastBootUnix { get; set; }

    // Tages-Gesamtnutzung (für optionales Tageslimit).
    public string UsageDay { get; set; } = "";       // yyyy-MM-dd (Vertrauenszeit, lokal)
    public long UsageSecondsToday { get; set; }
    public List<UsageSegment> UsageSegmentsToday { get; set; } = new(); // von wann bis wann

    // Auszeit („gestört ohne Notfall"): Restsekunden, läuft nur bei sichtbarem Bildschirm ab.
    public long PenaltySecondsRemaining { get; set; }
    public int PenaltyCount { get; set; }               // wie oft (für den Text „2× gestört")
    public int PenaltyTotalMinutes { get; set; }        // verhängte Minuten insgesamt (15, 30, …)
    public List<string> ExecutedCommandIds { get; set; } = new(); // Schutz gegen doppelt zugestellte Befehle

    // Letzter Takt der Hauptschleife: normale Uhr + Windows-Uhr ohne Schlafzeit (100-ns-Einheiten).
    // Damit erkennt ein neu gestarteter Dienst, ob er bei WACHEM Laptop gefehlt hat.
    public long LastTickWallUnix { get; set; }
    public long LastTickAwake100ns { get; set; }
    /// <summary>Lücken, in denen der Laptop wach war, der Dienst aber nicht zählte (warten aufs Nachzählen).</summary>
    public List<PendingGap> PendingGaps { get; set; } = new();

    /// <summary>True, wenn der Befehl neu ist (und merkt ihn sich). Verhindert doppelte Auszeiten.</summary>
    public bool MarkCommandExecuted(string commandId)
    {
        if (string.IsNullOrEmpty(commandId)) return true;
        if (ExecutedCommandIds.Contains(commandId)) return false;
        ExecutedCommandIds.Add(commandId);
        if (ExecutedCommandIds.Count > 100) ExecutedCommandIds.RemoveRange(0, ExecutedCommandIds.Count - 100);
        return true;
    }

    public static RuntimeState Load()
    {
        try
        {
            if (File.Exists(AppPaths.StatePath))
            {
                var s = Json.Deserialize<RuntimeState>(File.ReadAllText(AppPaths.StatePath));
                if (s is not null) return s;
            }
        }
        catch { }
        return new RuntimeState();
    }

    public void Save()
    {
        try
        {
            AppPaths.EnsureDataDir();
            File.WriteAllText(AppPaths.StatePath, Json.Serialize(this));
        }
        catch { }
    }

    public void RegisterPinFailure(DateTime trustedNow)
    {
        var day = trustedNow.ToString("yyyy-MM-dd");
        if (PinFailureDay != day) { PinFailureDay = day; PinFailuresToday = 0; }
        PinFailuresToday++;
    }

    /// <summary>Zählt genutzte Sekunden für den aktuellen (Vertrauens-)Tag; setzt bei Tageswechsel zurück.</summary>
    public void AddUsage(DateTime trustedLocalNow, long trustedNowUnix, int seconds)
    {
        var day = trustedLocalNow.ToString("yyyy-MM-dd");
        if (UsageDay != day) { UsageDay = day; UsageSecondsToday = 0; UsageSegmentsToday.Clear(); }
        UsageSecondsToday += seconds;
        UsageTimeline.Accrue(UsageSegmentsToday, trustedNowUnix, seconds);
    }

    /// <summary>
    /// Zählt Bildschirmzeit nach, die in einer Lücke [fromUnix, toUnix) verpasst wurde
    /// (nur der Anteil von heute). Gibt die tatsächlich gezählten Sekunden zurück.
    /// </summary>
    public long AddMissedUsage(DateTime trustedLocalNow, long fromUnix, long toUnix, long seconds)
    {
        var day = trustedLocalNow.ToString("yyyy-MM-dd");
        if (UsageDay != day) { UsageDay = day; UsageSecondsToday = 0; UsageSegmentsToday.Clear(); }
        long midnight = new DateTimeOffset(trustedLocalNow.Date).ToUnixTimeSeconds();
        if (toUnix <= midnight || seconds <= 0) return 0;
        if (fromUnix < midnight)
        {
            // Nur den Anteil von heute nachzählen.
            seconds = (long)(seconds * (double)(toUnix - midnight) / Math.Max(1, toUnix - fromUnix));
            fromUnix = midnight;
        }
        seconds = Math.Min(seconds, toUnix - fromUnix);
        if (seconds <= 0) return 0;
        UsageSecondsToday += seconds;
        UsageSegmentsToday.Add(new UsageSegment { StartUnix = fromUnix, EndUnix = toUnix, Seconds = (int)seconds });
        UsageSegmentsToday.Sort((a, b) => a.StartUnix.CompareTo(b.StartUnix));
        return seconds;
    }

    public List<UsageSegment> SegmentsToday(DateTime trustedLocalNow)
        => UsageDay == trustedLocalNow.ToString("yyyy-MM-dd") ? UsageSegmentsToday : new List<UsageSegment>();

    public int UsageMinutesToday(DateTime trustedLocalNow)
    {
        var day = trustedLocalNow.ToString("yyyy-MM-dd");
        if (UsageDay != day) return 0;
        return (int)(UsageSecondsToday / 60);
    }
}

/// <summary>Zeitraum, in dem der Laptop wach war, der Dienst aber nicht gezählt hat.</summary>
public sealed class PendingGap
{
    public long FromUnix { get; set; }
    public long ToUnix { get; set; }
    public long AwakeSeconds { get; set; }
    /// <summary>Wann erkannt (normale Uhr) – ohne Overlay-Meldung wird nach 10 Min aufgegeben.</summary>
    public long DetectedUnix { get; set; }
    /// <summary>Wie erkannt: "hing" (Schleife stand) oder "aus" (Dienst lief gar nicht).</summary>
    public string Cause { get; set; } = "";
}
