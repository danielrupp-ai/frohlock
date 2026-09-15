using FrohLock.Core.Models;

namespace FrohLock.Core.Schedule;

/// <summary>
/// Reine, deterministische Entscheidung: Ist das Gerät zum gegebenen (vertrauenswürdigen)
/// Zeitpunkt gesperrt? Kennt keinen Zustand -&gt; voll testbar.
/// </summary>
public sealed class ScheduleEngine
{
    private readonly LockConfig _config;

    public ScheduleEngine(LockConfig config) => _config = config;

    /// <param name="localNow">Vertrauenszeit, bereits in lokale Zeit umgerechnet.</param>
    /// <param name="trustedTimeAge">Alter der letzten erfolgreichen Zeitsynchronisation.</param>
    /// <param name="unlockUntilLocal">Aktive PIN-/Fern-Entsperrung gültig bis (lokal), oder null.</param>
    public LockDecision Decide(DateTime localNow, TimeSpan trustedTimeAge, DateTime? unlockUntilLocal)
    {
        // 1) Fail-Secure: Vertrauenszeit zu alt -> im Zweifel sperren.
        if (trustedTimeAge > TimeSpan.FromMinutes(_config.MaxTrustedTimeStalenessMinutes))
        {
            string alt = trustedTimeAge == TimeSpan.MaxValue
                ? "noch nie synchronisiert"
                : $"{(long)Math.Min(trustedTimeAge.TotalMinutes, 9_999_999)} min";
            return LockDecision.Locked($"Vertrauenszeit zu alt ({alt}) – Fail-Secure");
        }

        // 2) Aktive Entsperrung hat Vorrang, aber nur wenn nicht ohnehin sperrfrei.
        if (unlockUntilLocal is { } until && localNow < until)
        {
            // Trotzdem prüfen: läge sonst ein Sperrfenster vor? Nur dann ist die Entsperrung "aktiv".
            if (IsWithinAnyWindow(localNow, out _))
                return LockDecision.Unlocked($"Entsperrt bis {until:HH:mm}");
        }

        // 3) Sperrfenster (Kernfunktion).
        if (IsWithinAnyWindow(localNow, out var win))
            return LockDecision.Locked($"Sperrfenster {Fmt(win!.StartMinute)}–{Fmt(win.EndMinute)}");

        return LockDecision.Unlocked("Außerhalb aller Sperrfenster");
    }

    /// <summary>Nächster Zeitpunkt (lokal), an dem sich der Sperrzustand ändern könnte – für sparsames Timing.</summary>
    public DateTime NextBoundary(DateTime localNow)
    {
        // Prüfe die nächsten 48 Stunden minutengenau auf einen Zustandswechsel.
        var start = new DateTime(localNow.Year, localNow.Month, localNow.Day,
                                 localNow.Hour, localNow.Minute, 0, localNow.Kind);
        bool current = IsWithinAnyWindow(localNow, out _);
        for (int i = 1; i <= 48 * 60; i++)
        {
            var t = start.AddMinutes(i);
            if (IsWithinAnyWindow(t, out _) != current)
                return t;
        }
        return localNow.AddHours(24);
    }

    /// <summary>
    /// Minuten bis zum nächsten Sperrbeginn (Übergang „frei → gesperrt"), für die
    /// freundliche Schlafenszeit-Erinnerung. -1, wenn gerade schon eine Sperrzeit läuft
    /// oder in den nächsten 24 h keine beginnt.
    /// </summary>
    public int MinutesUntilNextLockStart(DateTime localNow)
    {
        if (IsWithinAnyWindow(localNow, out _)) return -1; // schon Sperrzeit
        var start = new DateTime(localNow.Year, localNow.Month, localNow.Day,
                                 localNow.Hour, localNow.Minute, 0, localNow.Kind);
        for (int i = 1; i <= 24 * 60; i++)
        {
            if (IsWithinAnyWindow(start.AddMinutes(i), out _))
                return i;
        }
        return -1;
    }

    private bool IsWithinAnyWindow(DateTime localNow, out ScheduleWindow? matched)
    {
        int minute = localNow.Hour * 60 + localNow.Minute;
        foreach (var w in _config.Windows)
        {
            if (!w.Enabled) continue;
            if (WindowActiveAt(w, localNow, minute))
            {
                matched = w;
                return true;
            }
        }
        matched = null;
        return false;
    }

    private static bool WindowActiveAt(ScheduleWindow w, DateTime localNow, int minute)
    {
        if (w.StartMinute == w.EndMinute) return false; // leeres Fenster

        if (w.StartMinute < w.EndMinute)
        {
            // Normalfall innerhalb eines Tages.
            return w.AppliesTo(localNow.DayOfWeek) && minute >= w.StartMinute && minute < w.EndMinute;
        }

        // Fenster über Mitternacht: z. B. 21:00–07:00.
        // Teil A: heute ab Start bis Mitternacht (Tag = Starttag).
        if (minute >= w.StartMinute && w.AppliesTo(localNow.DayOfWeek))
            return true;
        // Teil B: nach Mitternacht bis Ende (Tag = Starttag des Vortags).
        if (minute < w.EndMinute && w.AppliesTo(localNow.AddDays(-1).DayOfWeek))
            return true;
        return false;
    }

    private static string Fmt(int minute) => $"{minute / 60:00}:{minute % 60:00}";
}
