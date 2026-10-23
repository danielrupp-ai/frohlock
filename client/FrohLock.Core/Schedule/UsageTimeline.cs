namespace FrohLock.Core.Schedule;

/// <summary>Ein zusammenhängender Nutzungsabschnitt (Vertrauenszeit, Unix-Sekunden).</summary>
public sealed class UsageSegment
{
    public long StartUnix { get; set; }
    public long EndUnix { get; set; }
    /// <summary>Tatsächlich gezählte Sekunden in diesem Abschnitt (Lücken zählen nicht).</summary>
    public int Seconds { get; set; }
}

/// <summary>Anzeigefertiger Abschnitt (lokale Uhrzeit) für Sperrbildschirm, Übersicht und Admin.</summary>
public sealed class UsageSegmentView
{
    public string Start { get; set; } = "";   // "HH:mm"
    public string End { get; set; } = "";     // "HH:mm"
    public int StartMinute { get; set; }      // Minute des Tages (0..1439)
    public int EndMinute { get; set; }        // Minute des Tages (1..1440)
    public int Minutes { get; set; }          // gezählte Nutzung in diesem Abschnitt
}

/// <summary>
/// Zeichnet auf, VON WANN BIS WANN genutzt wurde – damit Kinder sehen, dass die Zeit wirklich um ist.
/// Reine Logik (getestet); die Sekunden selbst zählt weiterhin der Dienst.
/// </summary>
public static class UsageTimeline
{
    /// <summary>Kurze Lücken (z. B. ein verpasster Tick) bilden keinen neuen Abschnitt.</summary>
    public const int MergeGapSeconds = 90;

    /// <summary>Für die Anzeige: Pausen bis zu dieser Länge werden zu einem Block zusammengefasst.</summary>
    public const int DisplayMergeGapSeconds = 5 * 60;

    public static void Accrue(List<UsageSegment> segments, long nowUnix, int seconds)
    {
        if (seconds <= 0) return;
        var last = segments.Count > 0 ? segments[^1] : null;
        if (last is not null && nowUnix >= last.EndUnix && nowUnix - last.EndUnix <= MergeGapSeconds)
        {
            last.EndUnix = nowUnix;
            last.Seconds += seconds;
            return;
        }
        segments.Add(new UsageSegment { StartUnix = nowUnix - seconds, EndUnix = nowUnix, Seconds = seconds });
        // Schutz gegen unbegrenztes Wachstum (z. B. ständiges Auf-/Zuklappen).
        if (segments.Count > 300) Compact(segments, DisplayMergeGapSeconds);
    }

    /// <summary>Fasst Abschnitte mit Pausen bis maxGapSeconds zusammen (Sekunden werden summiert).</summary>
    public static List<UsageSegment> Merge(IEnumerable<UsageSegment> segments, int maxGapSeconds)
    {
        var result = new List<UsageSegment>();
        foreach (var s in segments.OrderBy(s => s.StartUnix))
        {
            var last = result.Count > 0 ? result[^1] : null;
            if (last is not null && s.StartUnix - last.EndUnix <= maxGapSeconds)
            {
                last.EndUnix = Math.Max(last.EndUnix, s.EndUnix);
                last.Seconds += s.Seconds;
            }
            else
            {
                result.Add(new UsageSegment { StartUnix = s.StartUnix, EndUnix = s.EndUnix, Seconds = s.Seconds });
            }
        }
        return result;
    }

    private static void Compact(List<UsageSegment> segments, int maxGapSeconds)
    {
        var merged = Merge(segments, maxGapSeconds);
        segments.Clear();
        segments.AddRange(merged);
    }

    /// <summary>Anzeige-Abschnitte in lokaler Zeit (kurze Pausen zusammengefasst).</summary>
    public static List<UsageSegmentView> ToViews(IEnumerable<UsageSegment> segments, Func<long, DateTime> toLocal)
    {
        var views = new List<UsageSegmentView>();
        foreach (var s in Merge(segments, DisplayMergeGapSeconds))
        {
            var a = toLocal(s.StartUnix);
            var b = toLocal(s.EndUnix);
            int startMin = a.Hour * 60 + a.Minute;
            int endMin = b.Date > a.Date ? 1440 : b.Hour * 60 + b.Minute;
            if (endMin <= startMin) endMin = Math.Min(1440, startMin + 1);
            views.Add(new UsageSegmentView
            {
                Start = a.ToString("HH:mm"),
                End = b.Date > a.Date ? "24:00" : b.ToString("HH:mm"),
                StartMinute = startMin,
                EndMinute = endMin,
                Minutes = (int)Math.Round(s.Seconds / 60.0)
            });
        }
        return views;
    }
}

/// <summary>
/// Wann zählt Bildschirmzeit? Nur wenn jemand angemeldet ist, der Laptop entsperrt und
/// der Bildschirm wirklich AN ist (Deckel offen ODER ein externer Bildschirm läuft).
/// Zugeklappt ohne externen Bildschirm/Bildschirm aus/Windows gesperrt = keine Zeit.
/// </summary>
public static class UsageGate
{
    /// <summary>Ist der Bildschirm für das Kind sichtbar und nutzbar?</summary>
    /// <remarks>Zugeklappt + externer Monitor (Laptop als Desktop genutzt) zählt weiter.</remarks>
    public static bool ScreenOn(bool displayOn, bool lidOpen, bool externalDisplay, bool sessionLocked)
        => displayOn && (lidOpen || externalDisplay) && !sessionLocked;

    /// <summary>
    /// Zählt Bildschirmzeit? Fehlt das Overlay (abgestürzt oder beendet), weiß niemand, ob der
    /// Bildschirm an ist – dann wird gezählt, sonst ließe sich die Zeit durch Beenden des Overlays umgehen.
    /// </summary>
    public static bool ShouldCount(bool userSessionActive, bool agentAlive, bool deviceLocked, bool screenOn)
        => userSessionActive && !deviceLocked && (!agentAlive || screenOn);
}

/// <summary>
/// Einordnung der aktiven Bildschirmausgänge (Windows DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY):
/// eingebautes Laptop-Panel oder externer Monitor/Fernseher/Funk-/USB-Bildschirm?
/// </summary>
public static class DisplayOutput
{
    public const uint Lvds = 6;
    public const uint DisplayPortEmbedded = 11;
    public const uint UdiEmbedded = 13;
    public const uint Internal = 0x80000000;

    /// <summary>
    /// Alles außer dem eingebauten Panel zählt als extern (HDMI, DisplayPort, USB-C, Miracast,
    /// DisplayLink, virtuelle Bildschirme …). Im Zweifel extern, damit Spielzeit nicht verloren geht.
    /// </summary>
    public static bool IsExternal(uint outputTechnology)
        => outputTechnology is not (Lvds or DisplayPortEmbedded or UdiEmbedded or Internal);
}
