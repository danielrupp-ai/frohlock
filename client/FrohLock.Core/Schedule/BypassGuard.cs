namespace FrohLock.Core.Schedule;

/// <summary>Rechteck in echten Bildschirm-Pixeln (Win32-Koordinaten, rechts/unten exklusiv).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public bool IsEmpty => Width == 0 || Height == 0;

    /// <summary>Deckt dieses Rechteck <paramref name="other"/> komplett ab (mit kleiner Toleranz)?</summary>
    public bool Covers(PixelRect other, int tolerance = 2)
        => Left <= other.Left + tolerance && Top <= other.Top + tolerance
           && Right >= other.Right - tolerance && Bottom >= other.Bottom - tolerance;

    public override string ToString() => $"{Width}x{Height}@{Left},{Top}";
}

/// <summary>
/// Prüft, ob der Sperrbildschirm wirklich JEDEN Bildschirm abdeckt.
/// Hintergrund (24.10.): Die Fläche wurde nur einmal beim Start gesetzt – kam danach ein
/// externer Monitor dazu oder ging weg (oder änderte sich die Skalierung), blieb ein Bildschirm
/// frei, obwohl FrohLock korrekt „gesperrt" meldete.
/// </summary>
public static class CoverageRules
{
    /// <summary>Monitore, die das Sperrfenster nicht vollständig bedeckt.</summary>
    public static List<PixelRect> UncoveredMonitors(IEnumerable<PixelRect> monitors, PixelRect window)
        => monitors.Where(m => !window.Covers(m)).ToList();
}

/// <summary>
/// Eskalation, wenn während einer Sperre ein fremdes Fenster über dem Sperrbildschirm liegt
/// (Vollbild-Spiel/Browser holt sich den Vordergrund). Stufe 1 macht der Agent selbst
/// (Fenster minimieren, Vordergrund zurückholen). Kommt das Fenster trotzdem immer wieder,
/// meldet <see cref="Step"/> true -> der Dienst trennt die Windows-Sitzung (Anmeldebildschirm).
/// Zeit in Sekunden (monoton, vom Aufrufer).
/// </summary>
public sealed class BypassEscalation
{
    public const double WindowSeconds = 60;
    public const int EscalateAfterSamples = 20;   // ~14 s Fremdfenster (Takt 0,7 s) binnen 60 s
    public const double CooldownSeconds = 90;

    private readonly Queue<double> _hits = new();
    private double _lastEscalation = double.NegativeInfinity;

    /// <summary>Anzahl Treffer im gleitenden Fenster (für den Protokolltext).</summary>
    public int RecentHits => _hits.Count;

    public bool Step(double now, bool foreignOverLock)
    {
        while (_hits.Count > 0 && now - _hits.Peek() > WindowSeconds) _hits.Dequeue();
        if (!foreignOverLock) return false;
        _hits.Enqueue(now);
        if (_hits.Count < EscalateAfterSamples || now - _lastEscalation < CooldownSeconds) return false;
        _lastEscalation = now;
        _hits.Clear();
        return true;
    }
}

/// <summary>Welche fremden Fenster der Sperrbildschirm NICHT minimieren darf (Windows-Oberfläche selbst).</summary>
public static class ShellWindows
{
    private static readonly HashSet<string> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
        "Windows.UI.Core.CoreWindow", "Xaml_WindowedPopupClass", "ForegroundStaging",
        "MultitaskingViewFrame", "TaskListThumbnailWnd", "Shell_InputSwitchTopLevelWindow",
        "NotifyIconOverflowWindow", "tooltips_class32", "#32768"
    };

    public static bool IsShell(string? windowClass) => windowClass is not null && Classes.Contains(windowClass);
}
