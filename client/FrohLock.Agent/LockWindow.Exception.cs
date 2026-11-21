using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FrohLock.Agent;

/// <summary>
/// Ausnahme-Programm in der Sperre (Windows 0.31.0, Daniel 21.11.2026: „Wenn Renats Zeit rum ist, darf er nur
/// Evoluzzer spielen, alles andere bleibt gesperrt“).
///
/// Ablauf: Der Dienst sagt, ob die aktuelle Sperre das Programm erlaubt und ob es (mit passender Prüfsumme)
/// installiert ist. Der Knopf „🎮 … spielen“ holt den geprüften Pfad beim Dienst und startet das Programm in der
/// Sitzung des Kindes. Solange ein VOM DIENST GEPRÜFTER Prozess das Vordergrundfenster ist, liegt die Sperre als
/// bildschirmfüllender Hintergrund direkt dahinter (nicht mehr „immer oben“). Kommt irgendein anderes Fenster
/// nach vorn, wird das Spiel minimiert/beendet oder entzieht der Dienst die Freigabe, ist die Sperre sofort wieder
/// ganz vorn (Vordergrund-Ereignis + Prüfung alle 250 ms). Im Zweifel: volle Sperre.
/// </summary>
public partial class LockWindow
{
    private bool _exAllowed;
    private bool _exInstalled;
    private string _exName = "";
    private bool _passThrough;
    private IntPtr _passHwnd;
    private int _launchPid;
    private DateTime _launchGraceUntilUtc = DateTime.MinValue;
    private bool _launching;
    private readonly Dictionary<int, (Process proc, DateTime verifiedUtc)> _verified = new();
    private readonly Dictionary<int, DateTime> _checkRequested = new();
    private DispatcherTimer? _fgTimer;
    private ScreenCover.WinEventDelegate? _winEventProc;   // Verweis halten, sonst räumt der GC den Rückruf weg

    /// <summary>So lange gilt eine Freigabe des Dienstes für einen Prozess ohne neue Bestätigung.</summary>
    private static readonly TimeSpan VerifiedFor = TimeSpan.FromSeconds(6);

    /// <summary>%LOCALAPPDATA% des Kindes (Suchort für die Benutzer-Installation).</summary>
    public static string LocalAppDataDir
    {
        get
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); }
            catch { return ""; }
        }
    }

    /// <summary>Spielt das Kind gerade das geprüfte Programm (Sperre liegt dahinter)?</summary>
    public bool PassThroughActive => _passThrough;

    private void InitException()
    {
        _fgTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _fgTimer.Tick += (_, _) => CheckForeground();
        _fgTimer.Start();
        try
        {
            _winEventProc = (_, _, _, _, _, _, _) => Dispatcher.BeginInvoke(CheckForeground);
            ScreenCover.HookWinEvent(ScreenCover.EVENT_SYSTEM_FOREGROUND, _winEventProc);
            ScreenCover.HookWinEvent(ScreenCover.EVENT_SYSTEM_MINIMIZESTART, _winEventProc);
        }
        catch { /* ohne Ereignis bleibt die 250-ms-Prüfung */ }
    }

    /// <summary>
    /// Stand vom Dienst (jede Sekunde). <paramref name="verifiedPid"/> = Prozess, den der Dienst für den gemeldeten
    /// Vordergrund <paramref name="reportedPid"/> geprüft UND jetzt erlaubt hat (0 = nein).
    /// </summary>
    public void SetException(bool allowed, string name, bool installed, int verifiedPid, int reportedPid)
    {
        _exAllowed = allowed;
        _exInstalled = allowed && installed;
        _exName = string.IsNullOrWhiteSpace(name) ? "das Spiel" : name.Trim();

        if (!allowed)
        {
            DropAllVerified();
            if (_passThrough) EndPassThrough(assert: IsVisible);
        }
        else
        {
            if (verifiedPid > 0) AddVerified(verifiedPid);
            // Der Dienst hat den gemeldeten Prozess geprüft und NICHT freigegeben -> Freigabe entziehen.
            if (reportedPid > 0 && verifiedPid != reportedPid && _verified.ContainsKey(reportedPid))
            {
                DropVerified(reportedPid);
                if (_passThrough) EndPassThrough(assert: true);
            }
        }
        UpdatePlayPanel();
    }

    private void UpdatePlayPanel()
    {
        if (!_exAllowed)
        {
            PlayPanel.Visibility = Visibility.Collapsed;
            return;
        }
        PlayPanel.Visibility = Visibility.Visible;
        if (_exInstalled)
        {
            PlayButton.Visibility = Visibility.Visible;
            PlayButtonText.Text = $"🎮 {_exName} spielen";
            PlayButton.IsEnabled = !_launching;
            PlayInfo.Text = _launching ? $"{_exName} startet … 🚀" : $"{_exName} darfst du jetzt trotzdem spielen. Alles andere macht Pause.";
        }
        else
        {
            PlayButton.Visibility = Visibility.Collapsed;
            PlayInfo.Text = $"{_exName} ist noch nicht installiert.";
        }
    }

    private void AddVerified(int pid)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return;
        if (_verified.TryGetValue(pid, out var v))
        {
            _verified[pid] = (v.proc, DateTime.UtcNow);
            return;
        }
        try
        {
            // Handle offen halten: solange er offen ist, vergibt Windows die Prozess-Kennung nicht neu.
            var p = Process.GetProcessById(pid);
            _verified[pid] = (p, DateTime.UtcNow);
        }
        catch { }
    }

    private void DropVerified(int pid)
    {
        if (_verified.Remove(pid, out var v)) { try { v.proc.Dispose(); } catch { } }
    }

    private void DropAllVerified()
    {
        foreach (var pid in _verified.Keys.ToList()) DropVerified(pid);
        _launchPid = 0;
        _launchGraceUntilUtc = DateTime.MinValue;
    }

    private bool IsVerified(int pid)
    {
        if (!_verified.TryGetValue(pid, out var v)) return false;
        bool alive;
        try { alive = !v.proc.HasExited; } catch { alive = false; }
        if (!alive) { DropVerified(pid); return false; }
        // Freigabe veraltet (Dienst hat länger nicht bestätigt) -> nicht mehr frei, Prozess aber gemerkt (Knopf holt ihn zurück).
        return DateTime.UtcNow - v.verifiedUtc <= VerifiedFor;
    }

    private List<int> KnownAlivePids()
    {
        var list = new List<int>();
        foreach (var (pid, v) in _verified.ToList())
        {
            bool alive;
            try { alive = !v.proc.HasExited; } catch { alive = false; }
            if (alive) list.Add(pid); else DropVerified(pid);
        }
        return list;
    }

    private ICollection<int> AllowedPids()
        => _exAllowed ? _verified.Keys.Where(IsVerified).ToList() : Array.Empty<int>();

    /// <summary>Hat gerade das geprüfte Programm (oder das eben gestartete, noch in Prüfung) den Vordergrund?</summary>
    private bool ExceptionHoldsForeground()
    {
        if (!_exAllowed || !IsVisible) return false;
        var fg = ScreenCover.ForegroundWindow();
        int pid = ScreenCover.PidOf(fg);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        if (ScreenCover.IsMinimized(fg)) return false;
        return IsVerified(pid) || (pid == _launchPid && DateTime.UtcNow < _launchGraceUntilUtc);
    }

    /// <summary>Beim Einblenden der Sperre: ist gerade das geprüfte Programm vorn? (Dann nicht den Fokus nehmen.)</summary>
    public bool WouldPassThrough()
    {
        try
        {
            if (!_exAllowed) return false;
            var fg = ScreenCover.ForegroundWindow();
            int pid = ScreenCover.PidOf(fg);
            return pid > 4 && pid != Environment.ProcessId && !ScreenCover.IsMinimized(fg) && IsVerified(pid);
        }
        catch { return false; }
    }

    /// <summary>Kern: bei jedem Vordergrund-Wechsel und alle 250 ms.</summary>
    private void CheckForeground()
    {
        try
        {
            if (!IsVisible) { if (_passThrough) EndPassThrough(assert: false); return; }
            if (!_exAllowed) { if (_passThrough) EndPassThrough(assert: true); return; }

            var fg = ScreenCover.ForegroundWindow();
            if (fg == IntPtr.Zero) return;   // Wechsel läuft gerade
            int pid = ScreenCover.PidOf(fg);
            if (pid == Environment.ProcessId)
            {
                // Unsere Sperre (oder Anzeige) ist vorn: wieder ganz nach oben.
                if (_passThrough) EndPassThrough(assert: false);
                return;
            }
            if (IsVerified(pid) && !ScreenCover.IsMinimized(fg))
            {
                BeginPassThrough(fg);
                return;
            }
            if (pid == _launchPid && DateTime.UtcNow < _launchGraceUntilUtc)
            {
                // Eben vom Knopf gestartet, Prüfung durch den Dienst läuft – nicht wegnehmen, aber auch nicht freigeben:
                // die Sperre bleibt so lange oben.
                RequestCheck(pid);
                return;
            }
            // Irgendetwas anderes ist vorn (Explorer, Browser, Startmenü, ein zweites, ungeprüftes Programm …).
            RequestCheck(pid);
            EndPassThrough(assert: true);
            ShowFriendlyBypass();
        }
        catch
        {
            if (_passThrough) EndPassThrough(assert: true);
        }
    }

    private void BeginPassThrough(IntPtr gameHwnd)
    {
        var self = new WindowInteropHelper(this).Handle;
        if (self == IntPtr.Zero) return;
        if (!_passThrough)
        {
            _passThrough = true;
            Topmost = false;
        }
        if (_passHwnd != gameHwnd || !_placedBelow)
        {
            _passHwnd = gameHwnd;
            ScreenCover.PlaceBelow(self, gameHwnd);
            _placedBelow = true;
        }
    }

    private bool _placedBelow;

    private void EndPassThrough(bool assert)
    {
        bool was = _passThrough;
        _passThrough = false;
        _passHwnd = IntPtr.Zero;
        _placedBelow = false;
        if (!Topmost) Topmost = true;
        if (!IsVisible) return;
        if (assert) ForceFront();
        else if (was) EnsureCoverage(report: false);
    }

    /// <summary>
    /// Sperre sofort ganz nach vorn (Fläche, „immer oben“, Vordergrund, Tastatur) – OHNE die Umgehungs-Zählung
    /// (die läuft weiter nur im 700-ms-Takt, damit die schnelle Prüfung hier nicht früher zum Trennen der Sitzung führt).
    /// </summary>
    private void ForceFront()
    {
        try
        {
            EnsureCoverage(report: false);
            var self = new WindowInteropHelper(this).Handle;
            if (self != IntPtr.Zero) ScreenCover.ForceForeground(self); else Activate();
            PinBox.Focus();
        }
        catch { try { Activate(); } catch { } }
    }

    /// <summary>Beim Dienst nachfragen, ob der Prozess das geprüfte Programm ist (je Prozess höchstens alle 1,5 s).</summary>
    private void RequestCheck(int pid)
    {
        if (!_exAllowed || pid <= 4) return;
        var now = DateTime.UtcNow;
        if (_checkRequested.TryGetValue(pid, out var last) && now - last < TimeSpan.FromSeconds(1.5)) return;
        if (_checkRequested.Count > 64) _checkRequested.Clear();
        _checkRequested[pid] = now;
        _ = CheckAsync(pid);
    }

    private async Task<bool> CheckAsync(int pid)
    {
        try
        {
            var reply = await _ipc.CheckProgramAsync(pid);
            await Dispatcher.InvokeAsync(() =>
            {
                if (reply is { Success: true } && reply.ExceptionForegroundPid == pid && _exAllowed) AddVerified(pid);
                else DropVerified(pid);
                CheckForeground();
            });
            return reply is { Success: true } && reply.ExceptionForegroundPid == pid;
        }
        catch { return false; }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_exAllowed || !_exInstalled || _launching) return;
        try
        {
            // Läuft das geprüfte Programm schon (z. B. hinter die Sperre geraten)? Dann – nach erneuter Prüfung
            // durch den Dienst – nur nach vorn holen statt ein zweites Mal starten.
            foreach (var pid in KnownAlivePids())
            {
                if (!await CheckAsync(pid) || !_verified.TryGetValue(pid, out var v)) continue;
                v.proc.Refresh();
                var h = v.proc.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    ScreenCover.BringToFront(h);
                    return;
                }
            }

            _launching = true;
            UpdatePlayPanel();
            var reply = await _ipc.LaunchProgramAsync(LocalAppDataDir);
            var path = reply?.ProgramPath ?? "";
            if (reply is not { Success: true } || path.Length == 0 || !File.Exists(path))
            {
                PlayInfo.Text = $"{_exName} lässt sich gerade nicht starten. Frag Mama oder Papa. 💛";
                return;
            }
            // Start im Konto des Kindes (der Sperrbildschirm läuft als das Kind) – ohne Startparameter.
            var proc = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? ""
            });
            if (proc is null) { PlayInfo.Text = $"{_exName} ließ sich nicht starten."; return; }
            _launchPid = proc.Id;
            _launchGraceUntilUtc = DateTime.UtcNow.AddSeconds(4);
            proc.Dispose();

            // Dienst prüft den laufenden Prozess (Pfad, Prüfsumme, Startparameter) – erst dann darf er nach vorn.
            bool ok = false;
            for (int i = 0; i < 4 && !ok; i++)
            {
                if (i > 0) await Task.Delay(400);
                ok = await CheckAsync(_launchPid);
            }
            if (!ok)
            {
                _launchPid = 0;
                _launchGraceUntilUtc = DateTime.MinValue;
                PlayInfo.Text = $"{_exName} wurde nicht erkannt – darum bleibt alles gesperrt.";
                ForceFront();
            }
        }
        catch
        {
            PlayInfo.Text = $"{_exName} lässt sich gerade nicht starten.";
        }
        finally
        {
            _launching = false;
            if (_exAllowed && PlayInfo.Text.Contains("startet")) UpdatePlayPanel();
            else PlayButton.IsEnabled = true;
        }
    }
}
