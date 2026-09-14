namespace FrohLock.Core.Time;

public interface ITrustedClock
{
    /// <summary>Aktuelle Vertrauenszeit (UTC). Nutzt lokale Uhr NICHT für Enforcement.</summary>
    DateTime UtcNow { get; }
    /// <summary>Alter der letzten erfolgreichen Synchronisation.</summary>
    TimeSpan Age { get; }
    /// <summary>Wurde jemals eine Vertrauenszeit ermittelt (auch persistiert)?</summary>
    bool HasTime { get; }
}
