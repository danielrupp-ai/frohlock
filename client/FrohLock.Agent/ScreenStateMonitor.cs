using System.Runtime.InteropServices;
using System.Windows.Interop;
using FrohLock.Core.Schedule;
using Microsoft.Win32;

namespace FrohLock.Agent;

/// <summary>
/// Beobachtet, ob der Bildschirm der Sitzung wirklich an ist: Monitor an/aus,
/// Laptop-Deckel offen/zu, externer Monitor und Windows-Sperre (Win+L). Nur dann zählt Bildschirmzeit.
/// Zugeklappt mit externem Monitor (Laptop als Desktop) zählt weiter.
/// Windows meldet den aktuellen Zustand direkt bei der Registrierung und danach jede Änderung.
/// </summary>
public sealed class ScreenStateMonitor : IDisposable
{
    private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private static readonly Guid GUID_LIDSWITCH_STATE_CHANGE = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    private const int WM_DISPLAYCHANGE = 0x007E;

    private HwndSource? _source;
    private IntPtr _displayReg = IntPtr.Zero;
    private IntPtr _lidReg = IntPtr.Zero;

    public bool DisplayOn { get; private set; } = true;
    public bool LidOpen { get; private set; } = true;
    public bool SessionLocked { get; private set; }
    /// <summary>Ist gerade ein externer Bildschirm aktiv (HDMI, DisplayPort, USB-C, Miracast …)?</summary>
    public bool ExternalDisplay { get; private set; }

    /// <summary>Wird bei jeder Zustandsänderung ausgelöst (UI-Thread).</summary>
    public event Action? Changed;

    /// <summary>Monitore/Auflösung haben sich geändert (WM_DISPLAYCHANGE) – Sperrfläche nachziehen.</summary>
    public event Action? DisplayLayoutChanged;

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
        RefreshExternalDisplay();
    }

    /// <summary>Aktive Bildschirme neu abfragen (bei Bildschirmwechsel, Deckel und jedem Lebenszeichen).</summary>
    public void RefreshExternalDisplay() => Set(externalDisplay: DetectExternalDisplay());

    private static bool DetectExternalDisplay()
    {
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes) != 0)
                    return true;
                var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                var modes = new byte[Math.Max(1, numModes) * DisplayConfigModeInfoSize];
                int rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
                if (rc == ERROR_INSUFFICIENT_BUFFER) continue;   // Bildschirm kam gerade dazu
                if (rc != 0) return true;
                for (int i = 0; i < numPaths; i++)
                    if (DisplayOutput.IsExternal(paths[i].targetInfo.outputTechnology))
                        return true;
                return false;
            }
        }
        catch { }
        // Abfrage geht nicht: im Zweifel extern, damit Spielzeit am Monitor nicht verloren geht.
        return true;
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
        if (msg == WM_DISPLAYCHANGE)
        {
            RefreshExternalDisplay();
            try { DisplayLayoutChanged?.Invoke(); } catch { }
        }
        else if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
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
                        Set(lidOpen: value != 0, externalDisplay: DetectExternalDisplay()); // 0 = zugeklappt, 1 = offen
                }
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private void Set(bool? displayOn = null, bool? lidOpen = null, bool? sessionLocked = null, bool? externalDisplay = null)
    {
        bool changed = false;
        if (externalDisplay is { } x && x != ExternalDisplay) { ExternalDisplay = x; changed = true; }
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

    // --- Aktive Bildschirme (QueryDisplayConfig) ---
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const int DisplayConfigModeInfoSize = 64;   // sizeof(DISPLAYCONFIG_MODE_INFO), wird nicht ausgewertet

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateNumerator;
        public uint refreshRateDenominator;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] byte[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, in Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
