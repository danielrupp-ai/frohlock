using System.IO;
using System.Windows;
using System.Windows.Threading;
using FrohLock.Core.Schedule;

namespace FrohLock.Agent;

/// <summary>
/// Overlay-Agent im Nutzerkontext. Fragt zyklisch den Dienst nach dem Sperrzustand,
/// zeigt/versteckt das kindgerechte Overlay, schluckt Umgehungstasten (mit freundlicher
/// Rückmeldung) und blendet vor der Sperre eine liebe Schlafenszeit-Erinnerung ein.
/// Enthält KEINE Sperrlogik/PIN – die liegt im Dienst.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstance;
    private readonly IpcClient _ipc = new();
    private KeyboardHook? _hook;
    private LockWindow? _lock;
    private DispatcherTimer? _poll;
    private DispatcherTimer? _alive;
    private readonly ScreenStateMonitor _screen = new();

    // Eigene Bildschirm-Aufzeichnung (unabhängig vom Dienst) – damit zählt der Dienst Lücken nach.
    private readonly List<MinuteRange> _screenLedger = new();

    // Sicherheitsnetz: antwortet der Schutzdienst bei eingeschaltetem Bildschirm minutenlang nicht,
    // sperrt das Overlay selbst (nur wenn FrohLock eingerichtet ist und der Dienst installiert ist).
    private const int ServiceSilentLockSeconds = 180;
    internal const string ServiceSilentReason =
        "Der Zeit-Wächter antwortet gerade nicht. Darum macht der Laptop zur Sicherheit eine Pause, " +
        "bis er wieder da ist. Mama oder Papa können helfen. 💛";
    private double _serviceSilentSeconds;
    private DateTime _lastPollUtc = DateTime.MinValue;
    private bool _serviceConfigured;
    private DateTime _offlineMasterUntilUtc = DateTime.MinValue;

    private const int ReminderThresholdMinutes = 15;
    private DateTime _lastReminderUtc = DateTime.MinValue;
    private int _lastReminderMinutes = -1;

    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrohLock", "agent-crash.txt");

    /// <summary>Absturz festhalten – der nächste Start meldet ihn dem Dienst (Diagnose für die Eltern).</summary>
    private static void WriteCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.WriteAllText(CrashLogPath, $"{DateTime.Now:dd.MM. HH:mm} {ex?.GetType().Name}: {ex?.Message} @ {ex?.TargetSite?.Name}");
        }
        catch { }
    }

    private async Task ReportPreviousCrashAsync()
    {
        try
        {
            if (!File.Exists(CrashLogPath)) return;
            var text = File.ReadAllText(CrashLogPath);
            var reply = await _ipc.SendAsync(new FrohLock.Core.Ipc.IpcMessage
            {
                Kind = FrohLock.Core.Ipc.IpcKind.AgentCrashReport, Reason = text
            });
            if (reply is not null) File.Delete(CrashLogPath);
        }
        catch { }
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, a) => WriteCrash(a.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, a) => WriteCrash(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { WriteCrash(a.Exception); a.SetObserved(); };

        // Reiner Anzeige-Modus (Benutzerübersicht) – kein Overlay, keine Sperre.
        if (e.Args.Any(a => string.Equals(a, "--status", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new StatusWindow(_ipc).Show();
            return;
        }

        // Sitzung 0 ist die unsichtbare Dienst-Sitzung: dort startet uns der Installer nach einem stillen
        // Fern-Update. Ein Sperrbildschirm ohne Bildschirm nützt nichts und hat (bis 0.25.0) den Wächter
        // beruhigt, während in der echten Sitzung keiner lief -> sofort beenden, der Dienst startet uns dort.
        if (SessionIdOfThisProcess() == 0) { Shutdown(); return; }

        _singleInstance = new Mutex(true, "Local\\FrohLock.Agent.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _hook = new KeyboardHook();
        _hook.Install();

        _lock = new LockWindow(_ipc);
        _lock.OfflineMasterUnlocked = () =>
        {
            _offlineMasterUntilUtc = DateTime.UtcNow.AddHours(8);
            _serviceSilentSeconds = 0;
            HideLock();
        };
        _hook.OnBlockedKey = () => _lock?.ShowFriendlyBypass();
        _lock.CoverageProblem = ReportCoverageProblem;
        _lock.ForeignWindowOverLock = ReportForeignWindow;

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();

        // Bildschirm an/aus, Deckel, externer Monitor, Windows-Sperre -> Dienst zählt nur bei sichtbarem Bildschirm.
        _screen.Start();
        _screen.Changed += async () => await SendAliveAsync();
        _screen.DisplayLayoutChanged += () => _lock?.EnsureCoverage();
        try { Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => _lock?.EnsureCoverage()); } catch { }

        _alive = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _alive.Tick += async (_, _) => await SendAliveAsync();
        _alive.Start();
        _ = SendAliveAsync();

        _ = PollAsync();
        _ = ReportPreviousCrashAsync();
    }

    private async Task SendAliveAsync()
    {
        try
        {
            _screen.RefreshExternalDisplay();
            // Minute merken, wenn der Bildschirm sichtbar an ist und das Kind ihn nutzen kann (kein Overlay).
            if (ScreenOnNow() && !(_lock?.IsVisible ?? false))
                ScreenLedger.Mark(_screenLedger, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60);
            var ledgerCopy = _screenLedger.Select(r => new MinuteRange { Start = r.Start, End = r.End }).ToList();
            bool visible = _lock?.IsVisible ?? false;
            await _ipc.AliveAsync(_screen.DisplayOn, _screen.LidOpen, _screen.ExternalDisplay, _screen.SessionLocked, ledgerCopy,
                lockVisible: visible, foregroundOurs: visible && (_lock?.HasForeground() ?? false));
        }
        catch { }
    }

    private bool ScreenOnNow()
        => UsageGate.ScreenOn(_screen.DisplayOn, _screen.LidOpen, _screen.ExternalDisplay, _screen.SessionLocked);

    private bool _polling;
    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var now = DateTime.UtcNow;
            double step = _lastPollUtc == DateTime.MinValue ? 0 : Math.Clamp((now - _lastPollUtc).TotalSeconds, 0, 5);
            _lastPollUtc = now;

            var status = await _ipc.GetStatusAsync();
            if (status is null)
            {
                if (_serviceConfigured && ScreenOnNow()) _serviceSilentSeconds += step;
                if (_serviceSilentSeconds >= ServiceSilentLockSeconds && now >= _offlineMasterUntilUtc && ServiceInstalled())
                {
                    _lock?.SetOffline(true);
                    ShowLock(ServiceSilentReason);
                    return;
                }
            }
            else
            {
                _serviceSilentSeconds = 0;
                _serviceConfigured = status.Configured;
                _lock?.SetOffline(false);
            }
            bool locked = status?.Locked ?? (_lock?.IsVisible ?? false);

            if (locked)
            {
                _lock?.SetForgot(status?.ForgotUrl);
                ShowLock(status?.Reason ?? "");
                if (status is not null)
                {
                    _lock?.SetUsage(status.UsageSegments, status.UsageMinutes, status.BudgetMinutes);
                    _lock?.SetPenalty(status.PenaltySeconds, status.PenaltyCount, status.PenaltyTotalMinutes);
                }
            }
            else
            {
                HideLock();
                MaybeShowReminder(status?.MinutesUntilLock ?? -1);
            }
        }
        catch { /* im Zweifel Overlay stehen lassen */ }
        finally { _polling = false; }
    }

    private void MaybeShowReminder(int minutesUntilLock)
    {
        if (minutesUntilLock <= 0 || minutesUntilLock > ReminderThresholdMinutes) return;

        // Sanft erinnern: beim Eintritt ins Fenster und dann nur alle ~5 Minuten erneut.
        bool crossedThreshold = _lastReminderMinutes < 0 || _lastReminderMinutes > ReminderThresholdMinutes;
        bool longEnoughSince = (DateTime.UtcNow - _lastReminderUtc) > TimeSpan.FromMinutes(4.5);
        _lastReminderMinutes = minutesUntilLock;

        if (!crossedThreshold && !longEnoughSince) return;
        _lastReminderUtc = DateTime.UtcNow;

        try { new ReminderWindow(minutesUntilLock).Show(); } catch { }
    }

    private void ShowLock(string reason)
    {
        if (_lock is null) return;
        _lock.SetReason(reason);
        if (!_lock.IsVisible)
        {
            _lock.Show();
            _lock.ResetGuard();
            _lock.EnsureCoverage(report: false);
            _lock.Activate();
        }
        if (_hook is not null) _hook.Active = true;
        _lastReminderMinutes = -1; // nach der Sperre wieder erinnern dürfen
    }

    private readonly Dictionary<string, DateTime> _lastCoverageReport = new();

    /// <summary>Befund ans Geräte-Protokoll (UMGEHUNG) – je Befundart höchstens alle 5 Minuten.</summary>
    private void ReportCoverageProblem(string text, bool alert)
    {
        var key = text.Length > 40 ? text[..40] : text;
        var now = DateTime.UtcNow;
        if (_lastCoverageReport.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(5)) return;
        _lastCoverageReport[key] = now;
        _ = _ipc.SendAsync(new FrohLock.Core.Ipc.IpcMessage
        {
            Kind = FrohLock.Core.Ipc.IpcKind.BypassReport, Reason = text, Alert = alert
        });
    }

    private readonly Dictionary<string, DateTime> _lastForeignReport = new();

    /// <summary>
    /// Fremdes Fenster über der Sperre -> UMGEHUNG ins Geräte-Protokoll (je Programm höchstens alle 2 Minuten,
    /// Eskalationen immer), damit die Eltern sehen, WELCHES Programm die Sperre überdeckt hat.
    /// </summary>
    private void ReportForeignWindow(string text, bool escalate)
    {
        var now = DateTime.UtcNow;
        int cut = text.IndexOf(" (", StringComparison.Ordinal);
        var key = cut > 0 ? text[..cut] : text;
        if (!escalate && _lastForeignReport.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(2)) return;
        _lastForeignReport[key] = now;
        _ = _ipc.SendAsync(new FrohLock.Core.Ipc.IpcMessage
        {
            Kind = FrohLock.Core.Ipc.IpcKind.BypassReport, Reason = text, Alert = true, Escalate = escalate
        });
    }

    private static int SessionIdOfThisProcess()
    {
        try { using var p = System.Diagnostics.Process.GetCurrentProcess(); return p.SessionId; } catch { return -1; }
    }

    private static bool ServiceInstalled()
    {
        try { return File.Exists(Path.Combine(AppContext.BaseDirectory, "FrohLockService.exe")); }
        catch { return true; }
    }

    private void HideLock()
    {
        if (_hook is not null) _hook.Active = false;
        if (_lock is { IsVisible: true }) _lock.Hide();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _hook?.Dispose();
        _screen.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch { }
    }
}
