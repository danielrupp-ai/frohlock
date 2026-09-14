namespace FrohLock.Core.Models;

/// <summary>
/// Ein Sperrfenster: an den angegebenen Wochentagen ist von <see cref="StartMinute"/>
/// bis <see cref="EndMinute"/> (Minuten seit Mitternacht, lokale Uhrzeit) alles gesperrt.
/// Fenster über Mitternacht werden unterstützt (Start &gt; Ende).
/// </summary>
public sealed class ScheduleWindow
{
    /// <summary>Eindeutige Kennung (für Bearbeitung im Admin/Setup).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Anzeigename, optional.</summary>
    public string? Label { get; set; }

    /// <summary>Aktive Wochentage. Leer = alle Tage.</summary>
    public List<DayOfWeek> Days { get; set; } = new();

    /// <summary>Startzeit in Minuten seit Mitternacht (0..1439).</summary>
    public int StartMinute { get; set; }

    /// <summary>Endzeit in Minuten seit Mitternacht (0..1440). 1440 = Tagesende.</summary>
    public int EndMinute { get; set; }

    public bool Enabled { get; set; } = true;

    public bool AppliesTo(DayOfWeek day) => Days.Count == 0 || Days.Contains(day);
}
