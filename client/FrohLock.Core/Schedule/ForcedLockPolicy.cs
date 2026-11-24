namespace FrohLock.Core.Schedule;

/// <summary>Was mit einer „Sofort sperren“-Sperre als Nächstes passiert.</summary>
public enum ForcedLockStep
{
    /// <summary>Nichts tun: Sperre bleibt (oder es gibt keine, oder sie ist streng).</summary>
    Keep,
    /// <summary>Sperre stammt aus einer älteren Version ohne Sperrtag: heutigen Tag eintragen (läuft zum nächsten Tageswechsel ab).</summary>
    StampToday,
    /// <summary>Neuer Tag: die Sofortsperre der Eltern ist abgelaufen.</summary>
    Expire,
}

/// <summary>
/// Reine Regel: Die „Sofortsperre“ der Eltern (Fernbefehl) endet von selbst mit dem nächsten Tageswechsel –
/// dann startet auch das Tageslimit neu. Eine strenge Sperre wegen Manipulation bleibt bis zur Freigabe durch Eltern.
/// Tage als <c>yyyy-MM-dd</c> (lokal, Vertrauenszeit).
/// </summary>
public static class ForcedLockPolicy
{
    /// <param name="forcedLock">Ist eine Sofortsperre aktiv?</param>
    /// <param name="byParents">true = Fernbefehl der Eltern, false = Manipulation (streng).</param>
    /// <param name="lockDay">Tag, an dem die Sperre gesetzt wurde (leer = unbekannt, ältere Version).</param>
    /// <param name="today">Heutiger Tag.</param>
    public static ForcedLockStep Check(bool forcedLock, bool byParents, string? lockDay, string today)
    {
        if (!forcedLock || !byParents) return ForcedLockStep.Keep;
        if (string.IsNullOrEmpty(lockDay)) return ForcedLockStep.StampToday;
        // Nur ein späterer Tag beendet die Sperre – eine zurückgedrehte Uhr hebt sie nie auf.
        return string.CompareOrdinal(today, lockDay) > 0 ? ForcedLockStep.Expire : ForcedLockStep.Keep;
    }
}
