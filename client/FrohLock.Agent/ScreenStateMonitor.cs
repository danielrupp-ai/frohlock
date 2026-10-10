using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;

namespace FrohLock.Agent;

/// <summary>
/// Beobachtet, ob der Bildschirm der Sitzung wirklich an ist: Monitor an/aus,
/// Laptop-Deckel offen/zu und Windows-Sperre (Win+L). Nur dann zählt Bildschirmzeit.
/// Windows meldet den aktuellen Zustand direkt bei der Registrierung und danach jede Änderung.
/// </summary>
public sealed class ScreenStateMonitor : IDisposable
{
    private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private static readonly Guid GUID_LIDSWITCH_STATE_CHANGE = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    private HwndSource? _source;
    private IntPtr _displayReg = IntPtr.Zero;
    private IntPtr _lidReg = IntPtr.Zero;

    public bool DisplayOn { get; private set; } = true;
    public bool LidOpen { get; private set; } = true;
    public bool SessionLocked { get; private set; }

    /// <summary>Wird bei jeder Zustandsänderung ausgelöst (UI-Thread).</summary>
    public event Action? Changed;

    public void Start()
    {
        try
        {
            // Unsichtbares Nachrichtenfenster (nur für Power-Benachrichtigungen).
            var p = new HwndSourceParameters("FrohLockScreenState") { Width = 0, Height = 0, WindowStyle = 0 };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
            _displayReg = RegisterPowerSettingNotification(_source.Handle, GUID_CONSOLE_DISPLAY_STATE, DEVICE_NOTIFY_WINDOW_HANDLE);
            _lidReg = RegisterPowerSettingNotification(_source.Handle, GUID_LIDSWITCH_STATE_CHANGE, DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        catch { /* ohne Benachrichtigung bleibt es beim bisherigen Verhalten (zählt) */ }

        try { SystemEvents.SessionSwitch += OnSessionSwitch; } catch { }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                Set(sessionLocked: true);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
            case SessionSwitchReason.ConsoleConnect:
                Set(sessionLocked: false);
                break;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            try
            {
                var ps = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                if (ps.DataLength >= 4)
                {
                    int value = Marshal.ReadInt32(lParam, Marshal.OffsetOf<POWERBROADCAST_SETTING>(nameof(POWERBROADCAST_SETTING.Data)).ToInt32());
                    if (ps.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                        Set(displayOn: value != 0);      // 0 = aus, 1 = an, 2 = gedimmt (zählt als an)
                    else if (ps.PowerSetting == GUID_LIDSWITCH_STATE_CHANGE)
                        Set(lidOpen: value != 0);        // 0 = zugeklappt, 1 = offen
                }
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private void Set(bool? displayOn = null, bool? lidOpen = null, bool? sessionLocked = null)
    {
        bool changed = false;
        if (displayOn is { } d && d != DisplayOn) { DisplayOn = d; changed = true; }
        if (lidOpen is { } l && l != LidOpen) { LidOpen = l; changed = true; }
        if (sessionLocked is { } s && s != SessionLocked) { SessionLocked = s; changed = true; }
        if (changed) Changed?.Invoke();
    }

    public void Dispose()
    {
        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        if (_displayReg != IntPtr.Zero) UnregisterPowerSettingNotification(_displayReg);
        if (_lidReg != IntPtr.Zero) UnregisterPowerSettingNotification(_lidReg);
        _source?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, in Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
