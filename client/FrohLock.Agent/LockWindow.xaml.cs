using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FrohLock.Agent;

public partial class LockWindow : Window
{
    private readonly IpcClient _ipc;
    private readonly DispatcherTimer _assertTimer;
    private bool _allowClose;
    private bool _busy;

    public LockWindow(IpcClient ipc)
    {
        InitializeComponent();
        _ipc = ipc;

        // Über alle Monitore (virtueller Bildschirm) legen.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded += (_, _) => { PinBox.Focus(); UpdateClock(); };

        // Regelmäßig Vordergrund/Topmost erzwingen und Uhr aktualisieren.
        _assertTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _assertTimer.Tick += (_, _) => { AssertForeground(); UpdateClock(); };
        _assertTimer.Start();
    }

    public void SetReason(string reason) => ReasonText.Text = reason;

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
        // Schließen (auch Alt+F4) verhindern, solange nicht ausdrücklich erlaubt.
        if (!_allowClose) { e.Cancel = true; return; }
        base.OnClosing(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (IsVisible) Dispatcher.BeginInvoke(AssertForeground);
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
                FeedbackText.Text = "Entsperrt.";
                // App-Poll-Schleife blendet das Overlay aus.
            }
            else
            {
                FeedbackText.Foreground = System.Windows.Media.Brushes.Salmon;
                int? remaining = reply?.PinFailuresRemaining;
                FeedbackText.Text = remaining is > 0
                    ? $"Falscher PIN. Noch {remaining} Versuche."
                    : "Falscher PIN bzw. vorübergehend gesperrt.";
            }
        }
        finally
        {
            UnlockButton.IsEnabled = true;
            _busy = false;
        }
    }
}
