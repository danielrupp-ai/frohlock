using FrohLock.Core;
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
            // 0) Master-/Notfall-Entsperrung hat ABSOLUTEN Vorrang (auch über Fail-Secure).
            if (_state.MasterUnlockUntilUnix > 0
                && _clock.UtcNow < DateTimeOffset.FromUnixTimeSeconds(_state.MasterUnlockUntilUnix).UtcDateTime)
                return LockDecision.Unlocked("Master-Entsperrung aktiv");

            // WICHTIG: Vor der Einrichtung NICHT sperren – sonst wäre man ohne PIN ausgesperrt.
            // Enforcement beginnt erst, wenn eine Konfiguration MIT gesetztem PIN vorliegt.
            if (_config is null || _engine is null || string.IsNullOrEmpty(_config.PinHash))
                return LockDecision.Unlocked("Noch nicht eingerichtet");

            var utc = _clock.UtcNow;
            var age = _clock.Age;
            var localNow = utc.ToLocalTime();
            DateTime? unlockUntilLocal = _state.UnlockUntilUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(_state.UnlockUntilUnix).LocalDateTime
                : null;

            var baseDec = _engine.Decide(localNow, age, unlockUntilLocal);

            // Optionales Tages-Gesamtlimit (Wochentag-Wert vor Standard).
            bool graceActive = unlockUntilLocal is { } u && localNow < u;
            return BudgetPolicy.Apply(baseDec, _config.EffectiveDailyBudget(localNow.DayOfWeek),
                _state.UsageMinutesToday(localNow), graceActive,
                unlockUntilLocal?.ToString("HH:mm"));
        }
    }

    /// <summary>Zählt genutzte Zeit (nur aufrufen, wenn Gerät nutzbar + Kind angemeldet).</summary>
    public void AccrueUsage(int seconds)
    {
        lock (_gate)
        {
            if (_config is null) return;
            _state.AddUsage(_clock.UtcNow.ToLocalTime(), seconds);
        }
    }

    /// <summary>PIN-Versuch. Bei Erfolg temporäre Entsperrung. Gibt (ok, verbleibendeVersuche).</summary>
    public (bool ok, int remaining) SubmitPin(string pin)
    {
        lock (_gate)
        {
            // Master-/Notfall-PIN: entsperrt IMMER (auch ohne Config, auch bei Lockout/Fail-Secure).
            if (PinHasher.Verify(pin, Branding.MasterUnlockHash, Branding.MasterUnlockSalt, Branding.MasterUnlockIterations))
            {
                var masterUntil = _clock.UtcNow.AddHours(8);
                _state.MasterUnlockUntilUnix = new DateTimeOffset(masterUntil).ToUnixTimeSeconds();
                _state.UnlockUntilUnix = _state.MasterUnlockUntilUnix;
                _state.PinFailuresToday = 0;
                _state.Save();
                _audit.Write("PIN", "MASTER-PIN akzeptiert – 8 h entsperrt");
                return (true, MaxPinFailuresBeforeLockout);
            }

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

    public bool IsConfigured
    {
        get { lock (_gate) return _config is not null && !string.IsNullOrEmpty(_config.PinHash); }
    }

    public int TodayUsageMinutes()
    {
        lock (_gate) return _state.UsageMinutesToday(_clock.UtcNow.ToLocalTime());
    }

    public int TodayBudgetMinutes()
    {
        lock (_gate)
        {
            if (_config is null) return 0;
            return _config.EffectiveDailyBudget(_clock.UtcNow.ToLocalTime().DayOfWeek);
        }
    }

    /// <summary>Menschenlesbare Sperrzeiten für heute (für die Status-Anzeige).</summary>
    public string TodayScheduleSummary()
    {
        lock (_gate)
        {
            if (_config is null) return "Noch nicht eingerichtet.";
            var today = _clock.UtcNow.ToLocalTime().DayOfWeek;
            var parts = new List<string>();
            foreach (var w in _config.Windows)
            {
                if (!w.Enabled) continue;
                if (w.Days.Count != 0 && !w.Days.Contains(today)) continue;
                parts.Add($"{w.StartMinute / 60:00}:{w.StartMinute % 60:00}–{w.EndMinute / 60:00}:{w.EndMinute % 60:00}");
            }
            return parts.Count == 0 ? "Heute keine feste Sperrzeit." : "Gesperrt: " + string.Join(", ", parts);
        }
    }

    /// <summary>Minuten bis zur nächsten Sperre (für die freundliche Erinnerung). -1 = keine/gesperrt.</summary>
    public int MinutesUntilLock()
    {
        lock (_gate)
        {
            if (_config is null || _engine is null) return -1;
            if (_clock.Age > TimeSpan.FromMinutes(_config.MaxTrustedTimeStalenessMinutes)) return -1;
            var localNow = _clock.UtcNow.ToLocalTime();

            int schedMin = _engine.MinutesUntilNextLockStart(localNow); // -1 = keine/schon Sperrzeit
            int budgetMin = -1;
            int budgetToday = _config.EffectiveDailyBudget(localNow.DayOfWeek);
            if (budgetToday > 0)
            {
                int rem = budgetToday - _state.UsageMinutesToday(localNow);
                budgetMin = rem > 0 ? rem : 0;
            }
            // Kleineren nicht-negativen Wert nehmen (was zuerst sperrt).
            if (schedMin < 0) return budgetMin;
            if (budgetMin < 0) return schedMin;
            return Math.Min(schedMin, budgetMin);
        }
    }

    /// <summary>Sofort sperren (Fern-Befehl oder Overlay-Manipulation erkannt).</summary>
    public void ForceLock(string reason)
    {
        lock (_gate)
        {
            _state.UnlockUntilUnix = 0;
            _state.MasterUnlockUntilUnix = 0; // auch die Master-Entsperrung beenden (Admin gewinnt)
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
            var localNow = _clock.UtcNow.ToLocalTime();
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
                LastBootUnix = _state.LastBootUnix,
                UsageMinutesToday = _state.UsageMinutesToday(localNow),
                DailyBudgetMinutes = _config?.EffectiveDailyBudget(localNow.DayOfWeek) ?? 0,
                UsageDay = localNow.ToString("yyyy-MM-dd"),
                AgentAliveAgeSeconds = _lastAgentAliveUtc == DateTime.MinValue
                    ? -1 : (int)(DateTime.UtcNow - _lastAgentAliveUtc).TotalSeconds
            };
        }
    }
}
