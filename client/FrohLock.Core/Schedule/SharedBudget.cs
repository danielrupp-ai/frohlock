namespace FrohLock.Core.Schedule;

/// <summary>
/// Gemeinsames Tageskonto (19.11.2026): Gehören mehrere Geräte demselben Kind, schickt der Server signiert,
/// wie viele Minuten die ANDEREN Geräte heute schon verbraucht haben. Für das Tageslimit zählt
/// eigene Zeit + diese Minuten – das Kind kann seine Zeit am Laptop oder am Handy nutzen, aber nicht doppelt.
/// </summary>
public sealed class SharedUsage
{
    /// <summary>Tag (yyyy-MM-dd, lokale Zeit), für den die Minuten gelten.</summary>
    public string Day { get; set; } = "";
    public int Minutes { get; set; }
    /// <summary>Klartext für die Anzeige, z. B. „renat handy 30 Min".</summary>
    public string Detail { get; set; } = "";
    /// <summary>Ausgabezeit des Server-Befehls – ein älterer (wieder eingespielter) Stand darf nicht überschreiben.</summary>
    public long IssuedUnix { get; set; }
}

public static class SharedBudget
{
    /// <summary>Minuten der anderen Geräte, die heute zählen (anderer Tag = 0).</summary>
    public static int OthersToday(SharedUsage? s, string todayKey)
        => s is not null && s.Day == todayKey ? Math.Max(0, s.Minutes) : 0;

    /// <summary>
    /// Neuen Stand übernehmen? Nur für HEUTE (Server und Gerät sind sich über den Tag einig) und nur,
    /// wenn er nicht älter ist als der gespeicherte. Liefert den neuen Stand oder null (= verwerfen).
    /// </summary>
    public static SharedUsage? Accept(SharedUsage? current, string day, int minutes, string detail,
        long issuedUnix, string todayKey)
    {
        if (string.IsNullOrEmpty(day) || day != todayKey) return null;
        if (minutes < 0 || minutes > 24 * 60 * 10) return null;
        if (current is not null && current.Day == day && issuedUnix < current.IssuedUnix) return null;
        return new SharedUsage { Day = day, Minutes = minutes, Detail = detail ?? "", IssuedUnix = issuedUnix };
    }
}
