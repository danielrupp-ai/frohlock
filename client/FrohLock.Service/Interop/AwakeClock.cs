using System.Runtime.InteropServices;

namespace FrohLock.Service.Interop;

/// <summary>
/// Windows-Uhr OHNE Schlafzeit (QueryUnbiasedInterruptTime, 100-ns-Einheiten seit Systemstart).
/// Steht still, solange der Laptop schläft – läuft weiter, solange er wach ist, auch wenn der
/// Dienst selbst hängt oder angehalten wurde. Damit lässt sich beides sicher unterscheiden.
/// </summary>
public static class AwakeClock
{
    public static long Now100ns => QueryUnbiasedInterruptTime(out ulong t) ? (long)t : Environment.TickCount64 * 10_000;

    public static double SecondsSince(long earlier100ns) => (Now100ns - earlier100ns) / 10_000_000.0;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
