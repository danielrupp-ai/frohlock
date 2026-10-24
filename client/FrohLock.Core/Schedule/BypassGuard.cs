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
