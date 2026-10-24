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
    private long _lastAgentAliveAwake100ns;

    // Bildschirmzustand, vom Agent gemeldet (Standard: an, bis der Agent etwas anderes sagt).
    private bool _displayOn = true;
    private bool _lidOpen = true;
    private bool _externalDisplay;
    // Eigene Bildschirm-Aufzeichnung des Overlays (für das Nachzählen von Lücken).
    private List<MinuteRange> _screenLedger = new();
    private DateTime _screenLedgerUtc = DateTime.MinValue;
    private bool _sessionLocked;

    public const int MaxPinFailuresBeforeLockout = 8;

    /// <summary>Watchdog: Zeitpunkt des letzten Agent-Lebenszeichens.</summary>
    public void MarkAgentAlive() => MarkAgentAlive(-1, false, false);

    private readonly Dictionary<int, long> _aliveBySession = new();
    private DateTime _lastSessionMismatchUtc = DateTime.MinValue;
    private DateTime _lastHiddenLockUtc = DateTime.MinValue;

    /// <summary>
    /// Lebenszeichen des Sperrbildschirms. Zählt nur, wenn es aus der AKTIVEN Konsolen-Sitzung kommt:
    /// ein Sperrbildschirm in einer anderen (z. B. per „Benutzer wechseln" verlassenen) Sitzung
    /// darf den Wächter nicht beruhigen – dort sitzt niemand, und in der aktiven Sitzung fehlt er.
    /// </summary>
    public void MarkAgentAlive(int sessionId, bool lockVisible, bool foregroundOurs)
    {
        uint active = Interop.SessionLauncher.ActiveConsoleSessionId;
        bool fromActive = sessionId < 0 || active == 0xFFFFFFFF || (uint)sessionId == active;
        bool locked;
        lock (_gate)
        {
            if (sessionId >= 0) _aliveBySession[sessionId] = Interop.AwakeClock.Now100ns;
            if (fromActive)
            {
                _lastAgentAliveUtc = DateTime.UtcNow;
                _lastAgentAliveAwake100ns = Interop.AwakeClock.Now100ns;
            }
            locked = DecideLocked();
        }
        var now = DateTime.UtcNow;
        if (!fromActive && Interop.SessionLauncher.HasActiveUserSession() && now - _lastSessionMismatchUtc > TimeSpan.FromMinutes(5))
        {
            _lastSessionMismatchUtc = now;
            _audit.Write("UMGEHUNG", sessionId == 0
                ? $"⚠ Sperrbildschirm lief nur in der unsichtbaren Dienst-Sitzung 0 (Rest eines stillen Updates), aktiv ist Sitzung {active}"
                  + $" (Konto „{Interop.SessionLauncher.UserNameOfSession(active)}“) – wird dort nachgestartet"
                : $"⚠ Sperrbildschirm meldet sich aus Sitzung {sessionId}, aktiv ist Sitzung {active}"
                  + $" (Konto „{Interop.SessionLauncher.UserNameOfSession(active)}“) – anderes Benutzerkonto? Dort wird er nachgestartet");
        }
        if (fromActive && locked && (!lockVisible || !foregroundOurs) && now - _lastHiddenLockUtc > TimeSpan.FromMinutes(2))
        {
            _lastHiddenLockUtc = now;
            _audit.Write("UMGEHUNG", "⚠ Gesperrt, aber Sperrbildschirm "
                + (!lockVisible ? "nicht eingeblendet" : "ohne Vordergrund (Tastatur woanders)") + $" (Sitzung {sessionId})");
        }
    }

    private bool DecideLocked() { try { return Decide().IsLocked; } catch { return false; } }

    /// <summary>
    /// Wie lange schweigt der Agent – gemessen in WACHER Zeit (Schlaf zählt nicht), damit nach dem
    /// Aufwachen nicht fälschlich „Agent fehlt" erkannt wird, bevor er sich melden konnte.
    /// </summary>
    public TimeSpan AgentSilence
    {
        get
        {
            lock (_gate)
                return TimeSpan.FromSeconds(Math.Max(0, Interop.AwakeClock.SecondsSince(_lastAgentAliveAwake100ns)));
        }
    }

    /// <summary>Bildschirmzustand der Sitzung übernehmen (kommt mit jedem Agent-Lebenszeichen).</summary>
    public void SetScreenState(bool displayOn, bool lidOpen, bool externalDisplay, bool sessionLocked,
        List<MinuteRange>? screenLedger = null)
    {
        lock (_gate)
        {
            if (screenLedger is not null)
            {
                _screenLedger = screenLedger;
                _screenLedgerUtc = DateTime.UtcNow;
            }
            bool before = ScreenOnLocked();
            bool externalOnlyBefore = before && !_lidOpen;
            _displayOn = displayOn;
            _lidOpen = lidOpen;
            _externalDisplay = externalDisplay;
            _sessionLocked = sessionLocked;
            bool after = ScreenOnLocked();
            bool externalOnly = after && !lidOpen;
            if (before != after || externalOnlyBefore != externalOnly)
                _audit.Write("SCREEN", after
                    ? (externalOnly
                        ? "Deckel zu, aber externer Bildschirm an – Bildschirmzeit zählt"
                        : "Bildschirm an – Bildschirmzeit zählt")
                    : $"Pause: {(!displayOn ? "Bildschirm aus" : sessionLocked ? "Windows gesperrt" : "Deckel zu")} – zählt nicht");
        }
    }

    /// <summary>
    /// Takt der Hauptschleife festhalten (normale Uhr + Windows-Uhr ohne Schlafzeit), damit auch ein
    /// neu gestarteter Dienst erkennt, ob er bei wachem Laptop gefehlt hat.
    /// </summary>
    public void RecordTick(DateTime wallUtc, long awake100ns)
    {
        lock (_gate)
        {
            _state.LastTickWallUnix = new DateTimeOffset(wallUtc).ToUnixTimeSeconds();
            _state.LastTickAwake100ns = awake100ns;
        }
    }

    public (long wallUnix, long awake100ns) LastTick
    {
        get { lock (_gate) return (_state.LastTickWallUnix, _state.LastTickAwake100ns); }
    }

    /// <summary>
    /// Der Laptop war wach, aber der Dienst hat nicht gezählt. Merken, melden und – sobald das Overlay
    /// seine Bildschirm-Aufzeichnung schickt – die verpasste Bildschirmzeit nachzählen.
    /// </summary>
    public void RegisterStalledGap(long fromUnix, long toUnix, long awakeSeconds, string cause)
    {
        lock (_gate)
        {
            _audit.Write("LUECKE", $"⚠ FrohLock {(cause == "aus" ? "lief nicht" : "stand still")} " +
                $"{LocalHm(fromUnix)}–{LocalHm(toUnix)} ({GapAnalysis.Duration(awakeSeconds)}), obwohl der Laptop wach war – prüfe Bildschirmzeit");
            _state.PendingGaps.Add(new PendingGap
            {
                FromUnix = fromUnix, ToUnix = toUnix, AwakeSeconds = awakeSeconds,
                DetectedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Cause = cause
            });
            if (_state.PendingGaps.Count > 20) _state.PendingGaps.RemoveRange(0, _state.PendingGaps.Count - 20);
            _state.Save();
        }
    }

    /// <summary>
    /// Offene Lücken mit der Bildschirm-Aufzeichnung des Overlays abgleichen: war der Bildschirm an,
    /// wird die Zeit nachgezählt. Ohne frische Aufzeichnung wird nach 10 Minuten aufgegeben.
    /// </summary>
    public void ResolvePendingGaps()
    {
        lock (_gate)
        {
            if (_state.PendingGaps.Count == 0) return;
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool changed = false;
            foreach (var gap in _state.PendingGaps.ToList())
            {
                // Aufzeichnung muss NACH dem Ende der Lücke geschickt worden sein (sonst fehlt der Zeitraum).
                bool ledgerFresh = _screenLedgerUtc != DateTime.MinValue
                                   && new DateTimeOffset(_screenLedgerUtc).ToUnixTimeSeconds() >= gap.ToUnix;
                if (ledgerFresh)
                {
                    long screenSeconds = ScreenLedger.OverlapSeconds(_screenLedger, gap.FromUnix, gap.ToUnix);
                    if (screenSeconds >= 60 && _config is not null)
                    {
                        var utc = _clock.UtcNow;
                        long counted = _state.AddMissedUsage(utc.ToLocalTime(), gap.FromUnix, gap.ToUnix, screenSeconds);
                        _audit.Write("LUECKE", $"⚠ Bildschirm war {GapAnalysis.Duration(screenSeconds)} an, während FrohLock " +
                            $"nicht zählte ({LocalHm(gap.FromUnix)}–{LocalHm(gap.ToUnix)}) – {GapAnalysis.Duration(counted)} nachgezählt");
                    }
                    else
                    {
                        _audit.Write("LUECKE", $"Bildschirm war {LocalHm(gap.FromUnix)}–{LocalHm(gap.ToUnix)} aus – nichts nachzuzählen");
                    }
                    _state.PendingGaps.Remove(gap);
                    changed = true;
                }
                else if (nowUnix - gap.DetectedUnix > 600)
                {
                    _audit.Write("LUECKE", $"⚠ {LocalHm(gap.FromUnix)}–{LocalHm(gap.ToUnix)}: Overlay meldete sich nicht – " +
                        "Bildschirmzeit nicht nachzählbar");
                    _state.PendingGaps.Remove(gap);
                    changed = true;
                }
            }
            if (changed) _state.Save();
        }
    }

    private static string LocalHm(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("HH:mm");

    private bool ScreenOnLocked() => UsageGate.ScreenOn(_displayOn, _lidOpen, _externalDisplay, _sessionLocked);

    /// <summary>Bildschirm an, Deckel offen (oder externer Bildschirm), Windows entsperrt?</summary>
    public bool ScreenActive { get { lock (_gate) return ScreenOnLocked(); } }

    public EnforcementController(ITrustedClock clock, AuditLog audit, RuntimeState state)
    {
        // Kurze Schonfrist ab Dienststart (~10 s): nach einem Update fehlt der Sperrbildschirm in der
        // Sitzung des Kindes, der Wächter soll ihn sofort nachstarten statt 45 s zu warten.
        _lastAgentAliveAwake100ns = Interop.AwakeClock.Now100ns - 35L * 10_000_000L;
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

    /// <summary>Aktuelle Sperr-Entscheidung anhand Vertrauenszeit (inkl. offener Auszeit).</summary>
    public LockDecision Decide()
    {
        lock (_gate)
        {
            var d = DecideBase();
            if (_config is null || string.IsNullOrEmpty(_config.PinHash) || IsMasterUnlockActive()) return d;
            return PenaltyPolicy.Apply(d, _state.PenaltySecondsRemaining);
        }
    }

    private bool IsMasterUnlockActive()
        => _state.MasterUnlockUntilUnix > 0
           && _clock.UtcNow < DateTimeOffset.FromUnixTimeSeconds(_state.MasterUnlockUntilUnix).UtcDateTime;

    /// <summary>Entscheidung OHNE Auszeit (Sperrzeiten, Tageslimit, Entsperrungen).</summary>
    private LockDecision DecideBase()
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

            // „Sofort sperren" der Eltern: sperrt unabhängig von Uhrzeit und Tageslimit.
            if (_state.ForcedLock)
                return LockDecision.Locked("Mama oder Papa haben den Laptop gerade gesperrt. 💛");

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

    // ---------- Auszeit („gestört ohne Notfall") ----------

    /// <summary>Befehl schon ausgeführt? (Doppelt zugestellte Auszeiten dürfen sich nicht addieren.)</summary>
    public bool MarkCommandExecuted(string commandId)
    {
        lock (_gate)
        {
            bool fresh = _state.MarkCommandExecuted(commandId);
            if (fresh) _state.Save();
            return fresh;
        }
    }

    /// <summary>Auszeit hinzufügen – addiert sich zu einer noch offenen Auszeit.</summary>
    public void AddPenalty(int minutes, string reason)
    {
        lock (_gate)
        {
            if (minutes <= 0) return;
            _state.PenaltySecondsRemaining += minutes * 60L;
            _state.PenaltyCount++;
            _state.PenaltyTotalMinutes += minutes;
            _state.Save();
            _audit.Write("AUSZEIT", $"+{minutes} min ({reason}) – offen jetzt {PenaltyPolicy.FormatCountdown(_state.PenaltySecondsRemaining)}");
        }
    }

    public void ClearPenalty(string reason)
    {
        lock (_gate)
        {
            if (_state.PenaltySecondsRemaining <= 0 && _state.PenaltyCount == 0) return;
            _state.PenaltySecondsRemaining = 0;
            _state.PenaltyCount = 0;
            _state.PenaltyTotalMinutes = 0;
            _state.Save();
            _audit.Write("AUSZEIT", $"Aufgehoben ({reason})");
        }
    }

    /// <summary>Countdown weiterzählen – nur aufrufen, wenn die Auszeit sichtbar auf dem Bildschirm steht.</summary>
    public void TickPenalty(int seconds, bool userSessionActive, bool agentAlive, bool screenOn)
    {
        lock (_gate)
        {
            if (_state.PenaltySecondsRemaining <= 0) return;
            if (_config is null || string.IsNullOrEmpty(_config.PinHash)) return;
            if (!PenaltyPolicy.ShouldTick(!DecideBase().IsLocked, userSessionActive, agentAlive, screenOn)) return;
            _state.PenaltySecondsRemaining = Math.Max(0, _state.PenaltySecondsRemaining - seconds);
            if (_state.PenaltySecondsRemaining == 0)
            {
                _state.PenaltyCount = 0;
                _state.PenaltyTotalMinutes = 0;
                _state.Save();
                _audit.Write("AUSZEIT", "Abgelaufen – Laptop wieder frei");
            }
        }
    }

    public long PenaltySecondsRemaining { get { lock (_gate) return _state.PenaltySecondsRemaining; } }
    public int PenaltyCount { get { lock (_gate) return _state.PenaltyCount; } }
    public int PenaltyTotalMinutes { get { lock (_gate) return _state.PenaltyTotalMinutes; } }

    /// <summary>Zählt genutzte Zeit (nur aufrufen, wenn Gerät nutzbar + Kind angemeldet).</summary>
    public void AccrueUsage(int seconds)
    {
        lock (_gate)
        {
            if (_config is null) return;
            var utc = _clock.UtcNow;
            _state.AddUsage(utc.ToLocalTime(), new DateTimeOffset(utc).ToUnixTimeSeconds(), seconds);
        }
    }

    /// <summary>PIN-Versuch. Bei Erfolg temporäre Entsperrung. Gibt (ok, verbleibendeVersuche).</summary>
    public (bool ok, int remaining) SubmitPin(string pin)
    {
        lock (_gate)
        {
            // Master-/Notfall-PIN: entsperrt IMMER (auch ohne Config, auch bei Lockout/Fail-Secure).
            if (Branding.IsMasterPin(pin, PinHasher.Verify))
            {
                var masterUntil = _clock.UtcNow.AddHours(8);
                _state.MasterUnlockUntilUnix = new DateTimeOffset(masterUntil).ToUnixTimeSeconds();
                _state.UnlockUntilUnix = _state.MasterUnlockUntilUnix;
                _state.ForcedLock = false;
                _state.PinFailuresToday = 0;
                _state.PenaltySecondsRemaining = 0;
                _state.PenaltyCount = 0;
                _state.PenaltyTotalMinutes = 0;
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
                if (_state.ForcedLock) _audit.Write("LOCK", "Sofortsperre durch Eltern-PIN aufgehoben");
                _state.ForcedLock = false;
                if (_state.PenaltySecondsRemaining > 0) _audit.Write("AUSZEIT", "Durch Eltern-PIN aufgehoben");
                _state.PenaltySecondsRemaining = 0;
                _state.PenaltyCount = 0;
                _state.PenaltyTotalMinutes = 0;
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

    /// <summary>Von wann bis wann heute genutzt wurde (lokale Uhrzeit, Pausen ≤ 5 min zusammengefasst).</summary>
    public List<UsageSegmentView> TodayUsageSegments()
    {
        lock (_gate) return TodayUsageSegmentsNoLock();
    }

    private List<UsageSegmentView> TodayUsageSegmentsNoLock()
        => UsageTimeline.ToViews(_state.SegmentsToday(_clock.UtcNow.ToLocalTime()),
            unix => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime);

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

    /// <summary>Absturz des Overlays ins Geräte-Protokoll (Text gekürzt, kommt aus dem Nutzerprozess).</summary>
    public void ReportAgentCrash(string text)
    {
        var clean = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
        if (clean.Length > 300) clean = clean[..300];
        if (clean.Length > 0) _audit.Write("OVERLAY", $"⚠ Sperrbildschirm war abgestürzt: {clean}");
    }

    /// <summary>
    /// Sperrbildschirm deckte nicht alles ab (falsche Fläche, minimiert, unsichtbar) – ins Geräte-Protokoll,
    /// damit die Eltern im Admin sehen, WIE die Sperre ausgehebelt war. Text kommt aus dem Nutzerprozess.
    /// </summary>
    public void ReportCoverageProblem(string text, bool alert, bool escalate = false)
    {
        var clean = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ').Trim();
        if (clean.Length > 300) clean = clean[..300];
        if (clean.Length == 0) return;
        bool locked = Decide().IsLocked;
        _audit.Write("UMGEHUNG", (alert ? "⚠ " : "") + clean + (locked ? " (während Sperre)" : ""));

        // Fremdes Fenster kommt trotz Minimieren immer wieder: Windows-Sitzung trennen (Anmeldebildschirm).
        // Nur wenn wirklich gesperrt, höchstens alle 90 s – Programme laufen weiter, nur der Zugriff ist weg.
        if (!escalate || !locked) return;
        lock (_gate)
        {
            if ((DateTime.UtcNow - _lastBypassDisconnectUtc) < TimeSpan.FromSeconds(90)) return;
            _lastBypassDisconnectUtc = DateTime.UtcNow;
        }
        bool ok = Interop.SessionLauncher.DisconnectActiveSession();
        _audit.Write("UMGEHUNG", ok
            ? "⚠ Sperre wurde dauerhaft überdeckt – Windows-Sitzung getrennt (Anmeldebildschirm)"
            : "⚠ Sperre wurde dauerhaft überdeckt – Trennen der Sitzung fehlgeschlagen");
    }

    private DateTime _lastBypassDisconnectUtc = DateTime.MinValue;

    /// <summary>Sofort sperren (Fern-Befehl oder Overlay-Manipulation erkannt).</summary>
    public void ForceLock(string reason)
    {
        lock (_gate)
        {
            _state.UnlockUntilUnix = 0;
            _state.MasterUnlockUntilUnix = 0; // auch die Master-Entsperrung beenden (Admin gewinnt)
            _state.ForcedLock = true;          // bleibt gesperrt, bis Eltern freigeben
            _state.Save();
            _audit.Write("LOCK", $"Sofortsperre: {reason} – gesperrt bis zur Freigabe durch Eltern");
        }
    }

    /// <summary>Fern-Entsperrung für n Minuten.</summary>
    public void RemoteUnlock(int minutes, string reason)
    {
        lock (_gate)
        {
            var until = _clock.UtcNow.AddMinutes(Math.Max(1, minutes));
            _state.UnlockUntilUnix = new DateTimeOffset(until).ToUnixTimeSeconds();
            _state.ForcedLock = false;            // Fern-Entsperren hebt auch die Sofortsperre auf
            _state.PenaltySecondsRemaining = 0;   // Eltern entsperren -> Auszeit ist mit erledigt
            _state.PenaltyCount = 0;
            _state.PenaltyTotalMinutes = 0;
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
                UsageSegments = TodayUsageSegmentsNoLock(),
                ScreenActive = ScreenOnLocked(),
                PenaltySecondsRemaining = _state.PenaltySecondsRemaining,
                PenaltyCount = _state.PenaltyCount,
                PenaltyTotalMinutes = _state.PenaltyTotalMinutes,
                AgentAliveAgeSeconds = _lastAgentAliveUtc == DateTime.MinValue
                    ? -1 : (int)(DateTime.UtcNow - _lastAgentAliveUtc).TotalSeconds,
                RecentAudit = string.Join("\n", _audit.Tail(40))
            };
        }
    }
}
