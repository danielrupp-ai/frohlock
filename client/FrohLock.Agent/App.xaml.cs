using System.Windows;
using System.Windows.Threading;

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

    private const int ReminderThresholdMinutes = 15;
    private DateTime _lastReminderUtc = DateTime.MinValue;
    private int _lastReminderMinutes = -1;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "Local\\FrohLock.Agent.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _hook = new KeyboardHook();
        _hook.Install();

        _lock = new LockWindow(_ipc);
        _hook.OnBlockedKey = () => _lock?.ShowFriendlyBypass();

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();

        _alive = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _alive.Tick += async (_, _) => { try { await _ipc.AliveAsync(); } catch { } };
        _alive.Start();

        _ = PollAsync();
    }

    private bool _polling;
    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var status = await _ipc.GetStatusAsync();
            bool locked = status?.Locked ?? (_lock?.IsVisible ?? false);

            if (locked) ShowLock(status?.Reason ?? "");
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
            _lock.Activate();
        }
        if (_hook is not null) _hook.Active = true;
        _lastReminderMinutes = -1; // nach der Sperre wieder erinnern dürfen
    }

    private void HideLock()
    {
        if (_hook is not null) _hook.Active = false;
        if (_lock is { IsVisible: true }) _lock.Hide();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _hook?.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch { }
    }
}
