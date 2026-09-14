using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FrohLock.Agent;

public partial class LockWindow : Window
{
    private readonly IpcClient _ipc;
    private readonly DispatcherTimer _assertTimer;
    private readonly DispatcherTimer _messageTimer;
    private readonly DispatcherTimer _bubbleHideTimer;
    private readonly Random _rng = new();
    private bool _allowClose;
    private bool _busy;
    private DateTime _lastBubbleUtc = DateTime.MinValue;
    private int _msgIndex;

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

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += (_, _) => { PinBox.Focus(); UpdateClock(); UpdateHeading(); };

        _assertTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _assertTimer.Tick += (_, _) => { AssertForeground(); UpdateClock(); };
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
        // Grund wird im Hintergrund gehalten, aber dem Kind zeigen wir freundliche Texte.
        UpdateHeading();
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
        if (IsNight())
        {
            HeadingText.Text = "Gute Nacht!";
        }
        else
        {
            HeadingText.Text = "Kurze Pause!";
            FriendlyText.Text = DayMessages[0];
        }
    }

    private void RotateMessage()
    {
        var pool = IsNight() ? NightMessages : DayMessages;
        _msgIndex = (_msgIndex + 1) % pool.Length;
        FriendlyText.Text = pool[_msgIndex];
    }

    /// <summary>Wird vom Tastatur-Hook aufgerufen, wenn eine Umgehungstaste geschluckt wurde.</summary>
    public void ShowFriendlyBypass()
    {
        // Nicht öfter als alle 2,5 s, damit es nicht flackert.
        if ((DateTime.UtcNow - _lastBubbleUtc) < TimeSpan.FromSeconds(2.5)) return;
        _lastBubbleUtc = DateTime.UtcNow;
        Dispatcher.Invoke(() =>
        {
            BubbleText.Text = BypassMessages[_rng.Next(BypassMessages.Length)];
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

    private void AssertForeground()
    {
        if (!IsVisible) return;
        Topmost = false; Topmost = true;
        if (WindowState != WindowState.Maximized) WindowState = WindowState.Normal;
        Activate();
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
