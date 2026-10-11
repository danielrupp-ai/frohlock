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

    public List<UsageSegment> SegmentsToday(DateTime trustedLocalNow)
        => UsageDay == trustedLocalNow.ToString("yyyy-MM-dd") ? UsageSegmentsToday : new List<UsageSegment>();

    public int UsageMinutesToday(DateTime trustedLocalNow)
    {
        var day = trustedLocalNow.ToString("yyyy-MM-dd");
        if (UsageDay != day) return 0;
        return (int)(UsageSecondsToday / 60);
    }
}
