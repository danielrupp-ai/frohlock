using FrohLock.Core;

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
    public void AddUsage(DateTime trustedLocalNow, int seconds)
    {
        var day = trustedLocalNow.ToString("yyyy-MM-dd");
        if (UsageDay != day) { UsageDay = day; UsageSecondsToday = 0; }
        UsageSecondsToday += seconds;
    }

    public int UsageMinutesToday(DateTime trustedLocalNow)
    {
        var day = trustedLocalNow.ToString("yyyy-MM-dd");
        if (UsageDay != day) return 0;
        return (int)(UsageSecondsToday / 60);
    }
}
