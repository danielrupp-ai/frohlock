namespace FrohLock.Core.Schedule;

/// <summary>Zusammenhängende Minuten (Unix-Minuten, Ende exklusiv), in denen der Bildschirm an war.</summary>
public sealed class MinuteRange
{
    public long Start { get; set; }
    public long End { get; set; }
}

/// <summary>
/// Eigene Aufzeichnung des Overlays: wann war der Bildschirm wirklich an? Unabhängig vom Dienst –
/// stand der Dienst still, lässt sich damit die verpasste Bildschirmzeit nachzählen.
/// </summary>
public static class ScreenLedger
{
    /// <summary>So lange wird zurückgeschaut (ältere Minuten fallen weg).</summary>
    public const int KeepMinutes = 24 * 60;

    public static void Mark(List<MinuteRange> ranges, long unixMinute)
    {
        var last = ranges.Count > 0 ? ranges[^1] : null;
        if (last is not null && unixMinute >= last.Start && unixMinute < last.End) return;
        if (last is not null && unixMinute == last.End) last.End = unixMinute + 1;
        else ranges.Add(new MinuteRange { Start = unixMinute, End = unixMinute + 1 });

        long keepFrom = unixMinute - KeepMinutes;
        ranges.RemoveAll(r => r.End <= keepFrom);
        if (ranges.Count > 0 && ranges[0].Start < keepFrom) ranges[0].Start = keepFrom;
    }

    /// <summary>Sekunden im Zeitraum [fromUnix, toUnix), in denen der Bildschirm laut Aufzeichnung an war.</summary>
    public static long OverlapSeconds(IEnumerable<MinuteRange>? ranges, long fromUnix, long toUnix)
    {
        if (ranges is null || toUnix <= fromUnix) return 0;
        long sum = 0;
        foreach (var r in ranges)
        {
            long a = Math.Max(r.Start * 60, fromUnix);
            long b = Math.Min(r.End * 60, toUnix);
            if (b > a) sum += b - a;
        }
        return sum;
    }
}

/// <summary>Was ist in einer Lücke passiert, in der der Dienst nicht gezählt hat?</summary>
public enum GapKind
{
    /// <summary>Normaler Takt, keine Lücke.</summary>
    None,
    /// <summary>Der Laptop hat geschlafen (Windows-Uhr ohne Schlafzeit stand still) – alles in Ordnung.</summary>
    Slept,
    /// <summary>Der Laptop war wach, aber der Dienst lief nicht (hing, angehalten oder beendet).</summary>
    StalledAwake
}

/// <summary>
/// Vergleicht die normale Uhr mit der Windows-Uhr ohne Schlafzeit (QueryUnbiasedInterruptTime):
/// läuft nur die normale Uhr weiter, hat der Laptop geschlafen; laufen beide, war er wach.
/// </summary>
public static class GapAnalysis
{
    /// <summary>Ab so vielen Sekunden ohne Takt gilt es als Lücke.</summary>
    public const int GapSeconds = 45;

    /// <summary>Ab so viel WACHER Zeit ohne Dienst wird nachgezählt und gemeldet.</summary>
    public const int StalledAwakeSeconds = 120;

    public static GapKind Classify(double wallSeconds, double awakeSeconds)
    {
        if (wallSeconds <= GapSeconds && awakeSeconds <= GapSeconds) return GapKind.None;
        return awakeSeconds >= StalledAwakeSeconds ? GapKind.StalledAwake : GapKind.Slept;
    }

    public static string Duration(long seconds)
    {
        long min = Math.Max(0, (seconds + 30) / 60);
        if (min < 60) return $"{min} Min";
        return min % 60 == 0 ? $"{min / 60} Std" : $"{min / 60} Std {min % 60} Min";
    }
}
