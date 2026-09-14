using FrohLock.Core.Crypto;
using FrohLock.Core.Logging;
using FrohLock.Core.Models;
using FrohLock.Core.Schedule;
using FrohLock.Core.Time;

namespace FrohLock.Service;

/// <summary>
/// Zentraler, thread-sicherer Zustand des Dienstes. PIN-Prüfung und Sperr-Entscheidung
/// leben ausschließlich hier (LocalSystem), nie im Nutzerprozess.
/// </summary>
public sealed class EnforcementController
{
    private readonly object _gate = new();
    private readonly ITrustedClock _clock;
    private readonly AuditLog _audit;
    private readonly RuntimeState _state;

    private LockConfig? _config;
    private ScheduleEngine? _engine;
    private DateTime _lastAgentAliveUtc = DateTime.MinValue;

    public const int MaxPinFailuresBeforeLockout = 8;

    /// <summary>Watchdog: Zeitpunkt des letzten Agent-Lebenszeichens.</summary>
    public void MarkAgentAlive() { lock (_gate) _lastAgentAliveUtc = DateTime.UtcNow; }
    public TimeSpan AgentSilence { get { lock (_gate) return DateTime.UtcNow - _lastAgentAliveUtc; } }

    public EnforcementController(ITrustedClock clock, AuditLog audit, RuntimeState state)
    {
        _clock = clock;
        _audit = audit;
        _state = state;
    }

    public void SetConfig(LockConfig? config)
    {
        lock (_gate)
        {
            _config = config;
            _engine = config is null ? null : new ScheduleEngine(config);
        }
    }

    public LockConfig? Config { get { lock (_gate) return _config; } }

    /// <summary>Aktuelle Sperr-Entscheidung anhand Vertrauenszeit.</summary>
    public LockDecision Decide()
    {
        lock (_gate)
        {
            if (_config is null || _engine is null)
                return LockDecision.Locked("Keine gültige Konfiguration – Fail-Secure");

            var utc = _clock.UtcNow;
            var age = _clock.Age;
            var localNow = utc.ToLocalTime();
            DateTime? unlockUntilLocal = _state.UnlockUntilUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(_state.UnlockUntilUnix).LocalDateTime
                : null;

            return _engine.Decide(localNow, age, unlockUntilLocal);
        }
    }

    /// <summary>PIN-Versuch. Bei Erfolg temporäre Entsperrung. Gibt (ok, verbleibendeVersuche).</summary>
    public (bool ok, int remaining) SubmitPin(string pin)
    {
        lock (_gate)
        {
            if (_config is null) return (false, 0);

            if (_state.PinFailuresToday >= MaxPinFailuresBeforeLockout)
            {
                _audit.Write("PIN", "Abgelehnt: Tages-Fehlversuchslimit erreicht");
                return (false, 0);
            }

            bool ok = PinHasher.Verify(pin, _config.PinHash, _config.PinSalt, _config.PinIterations);
            if (ok)
            {
                var until = _clock.UtcNow.AddMinutes(Math.Max(1, _config.UnlockGraceMinutes));
                _state.UnlockUntilUnix = new DateTimeOffset(until).ToUnixTimeSeconds();
                _state.Save();
                _audit.Write("PIN", $"Korrekt – entsperrt bis {until:o}");
                return (true, MaxPinFailuresBeforeLockout - _state.PinFailuresToday);
            }

            _state.RegisterPinFailure(_clock.UtcNow);
            _state.Save();
            _audit.Write("PIN", $"Falsch (#{_state.PinFailuresToday})");
            return (false, Math.Max(0, MaxPinFailuresBeforeLockout - _state.PinFailuresToday));
        }
    }

    /// <summary>Sofort sperren (Fern-Befehl oder Overlay-Manipulation erkannt).</summary>
    public void ForceLock(string reason)
    {
        lock (_gate)
        {
            _state.UnlockUntilUnix = 0;
            _state.Save();
            _audit.Write("LOCK", $"Sofortsperre: {reason}");
        }
    }

    /// <summary>Fern-Entsperrung für n Minuten.</summary>
    public void RemoteUnlock(int minutes, string reason)
    {
        lock (_gate)
        {
            var until = _clock.UtcNow.AddMinutes(Math.Max(1, minutes));
            _state.UnlockUntilUnix = new DateTimeOffset(until).ToUnixTimeSeconds();
            _state.Save();
            _audit.Write("UNLOCK", $"Fern-Entsperrung {minutes} min: {reason}");
        }
    }

    public DeviceStatus BuildStatus(string appVersion)
    {
        lock (_gate)
        {
            var d = Decide();
            var age = _clock.Age;
            return new DeviceStatus
            {
                DeviceId = _config?.DeviceId ?? "",
                AppVersion = appVersion,
                ConfigVersion = _config?.ConfigVersion ?? 0,
                CurrentlyLocked = d.IsLocked,
                TrustedTimeUnix = new DateTimeOffset(_clock.UtcNow).ToUnixTimeSeconds(),
                TrustedTimeAgeSeconds = age == TimeSpan.MaxValue ? -1 : (long)age.TotalSeconds,
                LockReason = d.Reason,
                PinFailuresToday = _state.PinFailuresToday,
                LastBootUnix = _state.LastBootUnix
            };
        }
    }
}
