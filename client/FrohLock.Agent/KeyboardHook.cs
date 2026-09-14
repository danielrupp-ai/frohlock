using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace FrohLock.Agent;

/// <summary>
/// Low-Level-Tastatur-Hook, der während der Sperre gängige Umgehungstasten schluckt
/// (Alt+Tab, Win, Alt+F4, Strg+Esc). Hinweis: Strg+Alt+Entf (SAS) ist systemseitig NICHT
/// abfangbar – dafür sorgen Watchdog + Standardkonto + Fail-Secure (siehe SICHERHEIT.md).
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private volatile bool _active;

    public KeyboardHook() => _proc = HookCallback;

    /// <summary>Steuert, ob Tasten aktuell geschluckt werden (nur während Sperre).</summary>
    public bool Active { get => _active; set => _active = value; }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _active)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var key = KeyInterop.KeyFromVirtualKey((int)data.vkCode);
                bool alt = (data.flags & 0x20) != 0; // LLKHF_ALTDOWN
                bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;

                if (key is Key.LWin or Key.RWin) return (IntPtr)1;
                if (alt && key == Key.Tab) return (IntPtr)1;
                if (alt && key == Key.F4) return (IntPtr)1;
                if (alt && key == Key.Escape) return (IntPtr)1;
                if (ctrl && key == Key.Escape) return (IntPtr)1;
                if (key == Key.Apps) return (IntPtr)1; // Kontextmenü-Taste
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero) { UnhookWindowsHookEx(_hookId); _hookId = IntPtr.Zero; }
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
