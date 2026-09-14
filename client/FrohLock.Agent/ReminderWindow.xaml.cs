using System.Windows;
using System.Windows.Threading;

namespace FrohLock.Agent;

public partial class ReminderWindow : Window
{
    private readonly DispatcherTimer _autoClose;

    public ReminderWindow(int minutes)
    {
        InitializeComponent();

        TitleText.Text = minutes <= 1 ? "Gleich ist Schlafenszeit!" : "Bald ist Schlafenszeit!";
        BodyText.Text = minutes <= 1
            ? "Gleich macht der Laptop Pause. Sag noch schnell Gute Nacht! 🌟"
            : $"In {minutes} Minuten macht der Laptop Pause. Mach dich langsam bettfertig. 🦷🛏️";

        Loaded += (_, _) => PositionBottomRight();

        _autoClose = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _autoClose.Tick += (_, _) => { _autoClose.Stop(); Close(); };
        _autoClose.Start();
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 8;
        Top = area.Bottom - ActualHeight - 8;
    }
}
