using System.ComponentModel;
using FrohLock.Core;
using FrohLock.Core.Crypto;
using FrohLock.Core.Schedule;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FrohLock.Agent;

public partial class LockWindow : Window
{
    private readonly IpcClient _ipc;
    private bool _offline;

    /// <summary>Notfall-PIN hat lokal freigegeben, weil der Dienst nicht antwortet.</summary>
    public Action? OfflineMasterUnlocked { get; set; }

    /// <summary>Dienst antwortet nicht – PIN-Feld prüft dann lokal nur den Notfall-PIN.</summary>
    public void SetOffline(bool offline) => _offline = offline;
    private readonly DispatcherTimer _assertTimer;
    private readonly DispatcherTimer _messageTimer;
    private readonly DispatcherTimer _bubbleHideTimer;
    private readonly Random _rng = new();
    private bool _allowClose;
    private bool _busy;
    private DateTime _lastBubbleUtc = DateTime.MinValue;
    private int _msgIndex;
    private bool _budgetMode;
    private bool _penaltyMode;
    private long _penaltyBase;            // zuletzt vom Dienst gemeldete Restsekunden
    private DateTime _penaltyBaseAtUtc;   // wann gemeldet (für einen flüssigen Sekunden-Countdown)
    private int _penaltyTotalMinutes;
    private string _budgetLimitText = "Bildschirmzeit";
    private readonly UsageTimelineView _usageView = new();

    // Liebe, ruhige Nachrichten (rotieren).
    private static readonly string[] NightMessages =
    {
        "Jetzt ist Schlafenszeit. Der Laptop macht auch ein Nickerchen. 💤",
        "Die Sterne sind schon wach – Zeit zum Kuscheln! 🌟",
        "Morgen früh geht's weiter. Schlaf gut! 🛏️",
        "Der Mond passt auf dich auf. 🌙 Gute Nacht!",
        "Augen zu und träum was Schönes. 🐻",
        "Zähneputzen nicht vergessen! 🦷 Dann ab ins Bett.",
    };

    private static readonly string[] DayMessages =
    {
        "Kurze Pause für den Laptop! 🌼 Zeit für etwas anderes.",
        "Jetzt ist gerade Pause. Vielleicht ein bisschen spielen? 🧸",
        "Der Laptop ruht sich kurz aus. 🌈 Bis später!",
    };

    // Tageslimit erreicht (2 Stunden für heute aufgebraucht).
    private static readonly string[] BudgetMessages =
    {
        "Deine {L} für heute sind aufgebraucht. 🌟 Morgen gibt's wieder neue Zeit!",
        "Für heute ist genug! ⏰ Zeit für etwas anderes – bis morgen!",
        "Zeit ist um! 🎮➡️🌳 Morgen wartet frische Bildschirmzeit.",
        "Gut gespielt! 🙌 Das Tageslimit ist erreicht – morgen geht's weiter.",
    };

    // Auszeit: erklärt dem Kind ruhig, WARUM gesperrt ist.
    private static readonly string[] PenaltyMessages =
    {
        "Kein Problem – jeder vergisst mal eine Regel. Wir haben dich trotzdem lieb! 💛",
        "Bei einem Notfall darfst du IMMER kommen: wenn jemand verletzt ist, etwas kaputt geht oder du Angst hast. 🤗",
        "Beim nächsten Mal kurz überlegen: Ist es ein Notfall? Wenn nicht, einfach ein bisschen warten. ⏳",
        "Gleich geht's weiter. Vielleicht magst du solange etwas malen, lesen oder spielen? 🎨📖",
    };

    private static readonly string[] PenaltyBypassMessages =
    {
        "Die Auszeit läuft noch ein kleines bisschen. ⏳",
        "Gleich geht's weiter – die Uhr zählt schon runter! 😊",
        "Nur Mama oder Papa können die Auszeit früher beenden. 💛",
    };

    // Freundliche Sprüche, wenn ein Umgehungsversuch erkannt wird.
    private static readonly string[] BypassMessages =
    {
        "Psst… 🦉 der Laptop schläft jetzt wirklich!",
        "Gut versucht! 😄 Aber jetzt ist Schlafenszeit.",
        "Nix da 🌙 – morgen geht's weiter, versprochen!",
        "Der kleine Bär sagt: ab ins Bett! 🐻💤",
        "Nur Mama oder Papa haben den Schlüssel. Frag sie lieb. 💛",
        "Diese Taste macht jetzt auch Pause. ✨",
    };

    public LockWindow(IpcClient ipc)
    {
        InitializeComponent();
        _ipc = ipc;
        UsageHost.Content = _usageView;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += (_, _) => { PinBox.Focus(); UpdateClock(); UpdateHeading(); };
        // Werkzeugfenster erscheinen auf allen virtuellen Desktops.
        SourceInitialized += (_, _) => ScreenCover.MakeToolWindow(new WindowInteropHelper(this).Handle);

        _assertTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _assertTimer.Tick += (_, _) => { AssertForeground(); UpdateClock(); UpdateCountdown(); };
        _assertTimer.Start();

        // Nachrichten alle 6 s wechseln.
        _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _messageTimer.Tick += (_, _) => RotateMessage();
        _messageTimer.Start();

        _bubbleHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _bubbleHideTimer.Tick += (_, _) => { _bubbleHideTimer.Stop(); BubbleBorder.Visibility = Visibility.Collapsed; };
    }

    public void SetReason(string reason)
    {
        bool newPenalty = FrohLock.Core.Schedule.PenaltyPolicy.IsPenaltyReason(reason);
        bool newBudget = !newPenalty && !string.IsNullOrEmpty(reason) && reason.Contains("Tageslimit");
        if (newBudget) _budgetLimitText = LimitTextFrom(reason);
        bool modeChanged = newBudget != _budgetMode || newPenalty != _penaltyMode;
        _budgetMode = newBudget;
        _penaltyMode = newPenalty;
        UpdateHeading();
        if (modeChanged) { _msgIndex = -1; RotateMessage(); } // sofort passende Nachricht + Symbol
    }

    // Aus "Tageslimit erreicht (606/120 min)" den Limit-Text machen: 120 -> "2 Stunden".
    private static string LimitTextFrom(string reason)
    {
        try
        {
            int slash = reason.IndexOf('/');
            if (slash >= 0)
            {
                var digits = new string(reason[(slash + 1)..].SkipWhile(c => !char.IsDigit(c))
                    .TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, out int m) && m > 0)
                    return m % 60 == 0 ? (m / 60 == 1 ? "1 Stunde" : $"{m / 60} Stunden") : $"{m} Minuten";
            }
        }
        catch { }
        return "Bildschirmzeit";
    }

    /// <summary>Auszeit-Stand vom Dienst: Restsekunden, wie oft gestört, Minuten insgesamt.</summary>
    public void SetPenalty(long secondsRemaining, int count, int totalMinutes)
    {
        if (secondsRemaining != _penaltyBase)
        {
            _penaltyBase = secondsRemaining;
            _penaltyBaseAtUtc = DateTime.UtcNow;
        }
        _penaltyTotalMinutes = totalMinutes > 0 ? totalMinutes : (int)Math.Ceiling(secondsRemaining / 60.0);
        int minutes = _penaltyTotalMinutes;
        string why = "Mama oder Papa waren gerade beschäftigt, und es war zum Glück kein Notfall.\n"
                     + "Wir haben ausgemacht: Stören nur, wenn es wirklich wichtig ist. "
                     + $"Darum macht der Laptop jetzt {minutes} Minuten Pause. 💛";
        if (count > 1)
            why += minutes == count * 15
                ? $"\n(Das war jetzt {count}-mal – darum {count} × 15 Minuten zusammen.)"
                : $"\n(Das war jetzt {count}-mal – darum werden die Zeiten zusammengezählt.)";
        PenaltyInfoText.Text = why;
        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        if (!_penaltyMode) return;
        // Dienst zählt in 5-s-Schritten; dazwischen flüssig runterzählen (höchstens 6 s vorauslaufen).
        double since = (DateTime.UtcNow - _penaltyBaseAtUtc).TotalSeconds;
        long shown = Math.Max(0, _penaltyBase - (long)Math.Min(since, 6));
        CountdownText.Text = FrohLock.Core.Schedule.PenaltyPolicy.FormatCountdown(shown);
        double total = Math.Max(1, _penaltyTotalMinutes * 60.0);
        double frac = Math.Clamp(shown / total, 0, 1);
        PenaltyBarFill.Width = Math.Max(0, PenaltyBarBack.ActualWidth * frac);
    }

    /// <summary>Zeitleiste „von wann bis wann" – damit die Kinder sehen, dass die Zeit wirklich um ist.</summary>
    public void SetUsage(IReadOnlyList<FrohLock.Core.Schedule.UsageSegmentView>? segments, int usageMinutes, int budgetMinutes)
    {
        if (!_budgetMode) { UsageHost.Visibility = Visibility.Collapsed; return; }
        _usageView.Render(segments, usageMinutes, budgetMinutes, limitReached: true);
        UsageHost.Visibility = Visibility.Visible;
    }

    /// <summary>Zeigt Eltern den „PIN vergessen?"-Weg (kommt vom Dienst über IPC).</summary>
    public void SetForgot(string? forgotUrl)
    {
        if (string.IsNullOrWhiteSpace(forgotUrl))
        {
            ForgotText.Visibility = Visibility.Collapsed;
            return;
        }
        ForgotText.Text = $"Eltern: PIN vergessen? An einem anderen Gerät öffnen:\n{forgotUrl}";
        ForgotText.Visibility = Visibility.Visible;
    }

    private bool IsNight()
    {
        int h = DateTime.Now.Hour;
        return h >= 19 || h < 8;
    }

    private void UpdateHeading()
    {
        // Im Tageslimit-/Auszeit-Modus braucht der Inhalt Platz -> kleineres Symbol, keine Tiere.
        bool compact = _budgetMode || _penaltyMode;
        BigEmoji.FontSize = compact ? 72 : 120;
        CrittersText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (!_budgetMode) UsageHost.Visibility = Visibility.Collapsed;
        PenaltyPanel.Visibility = _penaltyMode ? Visibility.Visible : Visibility.Collapsed;

        if (_penaltyMode)
        {
            HeadingText.Text = "Kurze Auszeit";
            BigEmoji.Text = "⏸️";
        }
        else if (_budgetMode)
        {
            HeadingText.Text = "Zeit ist um für heute!";
            BigEmoji.Text = "⏰";
            CrittersText.Text = "🎮  ➡️  🌳";
        }
        else if (IsNight())
        {
            HeadingText.Text = "Gute Nacht!";
            BigEmoji.Text = "🌙";
            CrittersText.Text = "🦉  🐻  😴";
        }
        else
        {
            HeadingText.Text = "Kurze Pause!";
            BigEmoji.Text = "🌤️";
            CrittersText.Text = "🧸  🌈  🌼";
        }
    }

    private void RotateMessage()
    {
        var pool = _penaltyMode ? PenaltyMessages
            : _budgetMode ? BudgetMessages : (IsNight() ? NightMessages : DayMessages);
        _msgIndex = (_msgIndex + 1) % pool.Length;
        FriendlyText.Text = pool[_msgIndex].Replace("{L}", _budgetLimitText);
    }

    /// <summary>Wird vom Tastatur-Hook aufgerufen, wenn eine Umgehungstaste geschluckt wurde.</summary>
    public void ShowFriendlyBypass()
    {
        // Nicht öfter als alle 2,5 s, damit es nicht flackert.
        if ((DateTime.UtcNow - _lastBubbleUtc) < TimeSpan.FromSeconds(2.5)) return;
        _lastBubbleUtc = DateTime.UtcNow;
        Dispatcher.Invoke(() =>
        {
            var pool = _penaltyMode ? PenaltyBypassMessages : BypassMessages;
            BubbleText.Text = pool[_rng.Next(pool.Length)];
            BubbleBorder.Visibility = Visibility.Visible;
            (FindResource("BubbleSb") as Storyboard)?.Begin();
            _bubbleHideTimer.Stop();
            _bubbleHideTimer.Start();
        });
    }

    private void UpdateClock() => ClockText.Text = DateTime.Now.ToString("dddd, dd.MM.yyyy  HH:mm");

    public void AllowCloseOnce()
    {
        _allowClose = true;
        _assertTimer.Stop();
    }

    /// <summary>Meldet, wenn der Sperrbildschirm nicht alles abdeckte (Befund, Warnung ja/nein).</summary>
    public Action<string, bool>? CoverageProblem { get; set; }

    /// <summary>Ein fremdes Fenster lag über der Sperre bzw. hatte den Vordergrund (Befund, Eskalation nötig?).</summary>
    public Action<string, bool>? ForeignWindowOverLock { get; set; }

    private readonly BypassEscalation _escalation = new();
    private readonly System.Diagnostics.Stopwatch _guardClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Hat der Sperrbildschirm gerade den Windows-Vordergrund (Tastatur)?</summary>
    public bool HasForeground()
    {
        try { return ScreenCover.IsForeground(new WindowInteropHelper(this).Handle); } catch { return false; }
    }

    /// <summary>Beim Einblenden neu anfangen zu zählen.</summary>
    public void ResetGuard() => _escalation.Step(_guardClock.Elapsed.TotalSeconds + BypassEscalation.WindowSeconds + 1, false);

    /// <summary>
    /// Liegt ein fremdes Fenster über der Sperre (Vollbild-Spiel, Browser) oder hat es die Tastatur?
    /// Dann minimieren, Vordergrund zurückholen, melden – und bei Dauerzustand eskalieren.
    /// </summary>
    private void GuardAgainstForeignWindows(IntPtr hwnd)
    {
        var foreign = ScreenCover.FindForeign(hwnd);
        bool escalate = _escalation.Step(_guardClock.Elapsed.TotalSeconds, foreign is not null && !foreign.Shell);
        if (foreign is null) return;
        bool minimized = ScreenCover.Minimize(foreign);
        ScreenCover.ForceForeground(hwnd);
        ShowFriendlyBypass();
        string text = $"Fremdes Fenster über der Sperre: {foreign.Describe()}"
                      + (minimized ? " – minimiert" : "")
                      + (escalate ? $" – trotz Minimieren immer wieder ({_escalation.RecentHits}+ Treffer/Min) – Sitzung wird getrennt" : "");
        ForeignWindowOverLock?.Invoke(text, escalate);
    }

    /// <summary>
    /// Sperrfläche in echten Pixeln über ALLE aktuellen Bildschirme legen – bei jedem Takt und jedem
    /// Bildschirmwechsel. Vorher wurde die Fläche nur einmal beim Start gesetzt (Fehler 24.10.:
    /// externer Monitor ab-/angesteckt -> Laptop-Bildschirm blieb frei, obwohl „gesperrt").
    /// </summary>
    /// <param name="report">false beim Einblenden – dann ist das Nachziehen erwartet, kein Befund.</param>
    public void EnsureCoverage(bool report = true)
    {
        if (!IsVisible) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var problem = ScreenCover.Ensure(hwnd);
            if (problem is not null && report) CoverageProblem?.Invoke(problem, true);
            if (ScreenCover.IsCloaked(hwnd))
                CoverageProblem?.Invoke("Sperrbildschirm war unsichtbar (anderer virtueller Desktop o. Ä.)", true);
            if (report) GuardAgainstForeignWindows(hwnd);
        }
        catch { }
    }

    private void AssertForeground()
    {
        if (!IsVisible) return;
        EnsureCoverage();
        Topmost = true;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) ScreenCover.ForceForeground(hwnd); else Activate();
        }
        catch { Activate(); }
        try { PinBox.Focus(); } catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose) { e.Cancel = true; return; }
        base.OnClosing(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (IsVisible)
        {
            // Wer wegzuklicken versucht, bekommt eine freundliche Erinnerung.
            ShowFriendlyBypass();
            Dispatcher.BeginInvoke(AssertForeground);
        }
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e) => await TryUnlock();

    private async void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await TryUnlock();
    }

    private async Task TryUnlock()
    {
        if (_busy) return;
        _busy = true;
        UnlockButton.IsEnabled = false;
        FeedbackText.Text = "Prüfe …";
        try
        {
            var pin = PinBox.Password;
            if (string.IsNullOrEmpty(pin)) { FeedbackText.Text = "Bitte PIN eingeben."; return; }
            var reply = await _ipc.SubmitPinAsync(pin);
            PinBox.Clear();
            // Dienst antwortet nicht: nur der Master-/Notfall-PIN kann hier lokal freigeben.
            if (reply is null && _offline
                && await Task.Run(() => Branding.IsMasterPin(pin, PinHasher.Verify)))
            {
                FeedbackText.Foreground = System.Windows.Media.Brushes.LightGreen;
                FeedbackText.Text = "Entsperrt (Notfall-PIN). 🌈";
                OfflineMasterUnlocked?.Invoke();
                return;
            }
            if (reply is { Success: true })
            {
                FeedbackText.Foreground = System.Windows.Media.Brushes.LightGreen;
                FeedbackText.Text = "Entsperrt. 🌈";
            }
            else
            {
                FeedbackText.Foreground = System.Windows.Media.Brushes.MistyRose;
                int? remaining = reply?.PinFailuresRemaining;
                FeedbackText.Text = remaining is > 0
                    ? $"Das war leider nicht richtig. Noch {remaining} Versuche."
                    : "PIN nicht korrekt bzw. kurz gesperrt. Bitte Eltern fragen. 💛";
            }
        }
        finally
        {
            UnlockButton.IsEnabled = true;
            _busy = false;
        }
    }
}
