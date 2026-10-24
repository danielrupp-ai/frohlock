using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FrohLock.Core.Schedule;

namespace FrohLock.Agent;

/// <summary>
/// Legt den Sperrbildschirm in echten Pixeln über alle aktuellen Monitore und prüft die Lage
/// des eigenen Fensters. Der Agent ist Per-Monitor-DPI-aware (v2) – alle Werte hier sind Pixel.
/// </summary>
internal static class ScreenCover
{
    /// <summary>
    /// Zieht das Fenster auf die volle Fläche aller Monitore nach.
    /// Gibt einen Befund zurück, wenn es vorher NICHT alles abgedeckt hat (sonst null).
    /// </summary>
    public static string? Ensure(IntPtr hwnd)
    {
        var monitors = Monitors();
        var virt = new PixelRect(
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN));
        if (virt.IsEmpty) return null;

        GetWindowRect(hwnd, out var r);
        var current = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
        bool iconic = IsIconic(hwnd);
        var uncovered = CoverageRules.UncoveredMonitors(monitors, current);
        if (uncovered.Count == 0 && !iconic)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            return null;
        }

        if (iconic) ShowWindow(hwnd, SW_RESTORE);
        SetWindowPos(hwnd, HWND_TOPMOST, virt.Left, virt.Top, virt.Width, virt.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);

        return (iconic ? "Sperrbildschirm war minimiert" : "Sperrbildschirm deckte nicht alles ab")
               + $" – Fenster {current}, Monitore {string.Join(" + ", monitors)}, frei: {string.Join(" + ", uncovered)}"
               + " – korrigiert";
    }

    /// <summary>Ist das eigene Fenster von Windows ausgeblendet (z. B. anderer virtueller Desktop)?</summary>
    public static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch { return false; }
    }

    /// <summary>Werkzeugfenster: kein Taskleisten-/Alt+Tab-Eintrag, sichtbar auf allen virtuellen Desktops.</summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    private static List<PixelRect> Monitors()
    {
        var list = new List<PixelRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
                list.Add(new PixelRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Ein fremdes Fenster, das über dem Sperrbildschirm liegt.</summary>
    public sealed record ForeignWindow(IntPtr Handle, string Process, string Class, string Title, PixelRect Rect,
        bool Fullscreen, bool Shell, string How)
    {
        public string Describe()
            => $"{Process} ({Class}){(Title.Length > 0 ? $" „{Title}\"" : "")} {Rect}{(Fullscreen ? " Vollbild" : "")} – {How}";
    }

    /// <summary>
    /// Liegt an einem Stichpunkt eines Monitors ein fremdes Fenster über uns, oder hat ein fremdes
    /// Fenster den Vordergrund (Tastatur)? Prüft Mitte, Ecken und Kantenmitten jedes Monitors.
    /// </summary>
    public static ForeignWindow? FindForeign(IntPtr ownHwnd)
    {
        int ownPid = Environment.ProcessId;
        foreach (var m in Monitors())
        {
            foreach (var (x, y) in ProbePoints(m))
            {
                var h = WindowFromPoint(new POINT { X = x, Y = y });
                if (h == IntPtr.Zero) continue;
                h = GetAncestor(h, GA_ROOT);
                if (h == ownHwnd || h == IntPtr.Zero) continue;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == ownPid) continue;
                return Describe(h, pid, $"lag bei {x},{y} über der Sperre");
            }
        }
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != ownHwnd)
        {
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid != ownPid && pid != 0)
            {
                var d = Describe(fg, pid, "hatte den Vordergrund (Tastatur)");
                // Desktop/Taskleiste im Vordergrund sind kein Umgehungsweg – nur Programme.
                if (!d.Shell) return d;
            }
        }
        return null;
    }

    private static ForeignWindow Describe(IntPtr h, uint pid, string how)
    {
        var cls = new StringBuilder(128); GetClassName(h, cls, cls.Capacity);
        var title = new StringBuilder(128); GetWindowText(h, title, title.Capacity);
        string proc = "?";
        try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { }
        GetWindowRect(h, out var r);
        var rect = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
        bool fullscreen = Monitors().Any(m => rect.Covers(m, 8));
        string t = title.ToString().Trim();
        if (t.Length > 60) t = t[..60] + "…";
        return new ForeignWindow(h, proc, cls.ToString(), t, rect, fullscreen, ShellWindows.IsShell(cls.ToString()), how);
    }

    private static IEnumerable<(int X, int Y)> ProbePoints(PixelRect m)
    {
        int inset = Math.Max(4, Math.Min(m.Width, m.Height) / 12);
        int l = m.Left + inset, r = m.Right - 1 - inset, t = m.Top + inset, b = m.Bottom - 1 - inset;
        int cx = m.Left + m.Width / 2, cy = m.Top + m.Height / 2;
        return new[] { (cx, cy), (l, t), (r, t), (l, b), (r, b), (cx, t), (cx, b), (l, cy), (r, cy) };
    }

    public static bool IsForeground(IntPtr hwnd) => hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    /// <summary>Fremdes Fenster aus dem Weg: minimieren (nicht beenden). Windows-Oberfläche bleibt unangetastet.</summary>
    public static bool Minimize(ForeignWindow w)
    {
        if (w.Shell) return false;
        bool ok = ShowWindow(w.Handle, SW_FORCEMINIMIZE);
        PostMessage(w.Handle, WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);
        return ok;
    }

    /// <summary>
    /// Vordergrund (Tastatur) zurückholen. Windows verweigert SetForegroundWindow aus dem Hintergrund –
    /// darum erst an den Eingabe-Thread des Vordergrundfensters anhängen, zur Not Alt antippen.
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            var fg = GetForegroundWindow();
            if (fg == hwnd) return;
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                BringWindowToTop(hwnd);
                if (!SetForegroundWindow(hwnd))
                {
                    keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    SetForegroundWindow(hwnd);
                }
            }
            finally { if (attached) AttachThreadInput(me, fgThread, false); }
        }
        catch { }
    }

    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    private const int SW_RESTORE = 9;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000;
    private const int DWMWA_CLOAKED = 14;
    private const uint GA_ROOT = 2;
    private const int SW_FORCEMINIMIZE = 11;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr data);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int idx, IntPtr val);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
}
