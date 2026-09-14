using System.Windows;
using System.Windows.Threading;

namespace FrohLock.Agent;

/// <summary>
/// Overlay-Agent im Nutzerkontext. Fragt zyklisch den Dienst nach dem Sperrzustand,
/// zeigt/versteckt das Vollbild-Overlay, schluckt Umgehungstasten und sendet
/// Watchdog-Lebenszeichen. Enthält KEINE Sperrlogik/PIN – die liegt im Dienst.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstance;
    private readonly IpcClient _ipc = new();
    private KeyboardHook? _hook;
    private LockWindow? _lock;
    private DispatcherTimer? _poll;
    private DispatcherTimer? _alive;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Nur eine Instanz pro Sitzung.
        _singleInstance = new Mutex(true, "Local\\FrohLock.Agent.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _hook = new KeyboardHook();
        _hook.Install();

        _lock = new LockWindow(_ipc);

        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();

        _alive = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _alive.Tick += async (_, _) => { try { await _ipc.AliveAsync(); } catch { } };
        _alive.Start();

        // Erste Abfrage sofort.
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
            // Kein Dienst erreichbar? Fail-Secure: Overlay stehen lassen, wenn es schon sichtbar ist.
            bool locked = status?.Locked ?? (_lock?.IsVisible ?? false);

            if (locked) ShowLock(status?.Reason ?? "");
            else HideLock();
        }
        catch { /* im Zweifel Overlay stehen lassen */ }
        finally { _polling = false; }
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
