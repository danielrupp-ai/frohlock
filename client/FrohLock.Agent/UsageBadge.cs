using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace FrohLock.Agent;

/// <summary>
/// Kleine, immer sichtbare Zeit-Anzeige unten rechts: „Heute 45 von 120 Min · noch 1 Std 15 Min" mit Balken.
/// Damit das Kind jederzeit sieht, wie viel vom (gemeinsamen) Tageskonto schon verbraucht ist.
/// Nimmt nie den Fokus, steht nicht in Alt+Tab/Taskleiste, lässt sich verschieben (Position wird gemerkt),
/// Klick öffnet die Übersicht. Reine Anzeige – keine Sperrlogik.
/// </summary>
public sealed class UsageBadge : Window
{
    private readonly TextBlock _main = new() { Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.Bold };
    private readonly TextBlock _sub = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xE0, 0xFF)), FontSize = 12 };
    private readonly Border _barBack = new() { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 6, 0, 0),
        Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) };
    private readonly Border _barFill = new() { Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Border _card;
    private double _frac;
    private Point _downAt;
    private bool _placed;
    private readonly Action _openOverview;

    private static readonly string PosPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FrohLock", "zeit-anzeige.json");

    public UsageBadge(Action openOverview)
    {
        _openOverview = openOverview;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "FrohLock Zeit";
        Cursor = Cursors.Hand;
        ToolTip = "Deine Bildschirmzeit heute – klicken für die Übersicht, ziehen zum Verschieben";

        _barBack.Child = _barFill;
        _barBack.SizeChanged += (_, _) => UpdateBar();
        var stack = new StackPanel { MinWidth = 170 };
        stack.Children.Add(_main);
        stack.Children.Add(_sub);
        stack.Children.Add(_barBack);
        _card = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 12, 9), Margin = new Thickness(8),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x13, 0x29, 0x4B)),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Opacity = 0.45, Color = Colors.Black },
            Child = stack,
        };
        Content = _card;

        MouseLeftButtonDown += (_, e) =>
        {
            _downAt = new Point(Left, Top);
            try { DragMove(); } catch { }
            if (Math.Abs(Left - _downAt.X) < 3 && Math.Abs(Top - _downAt.Y) < 3) _openOverview();
            else SavePosition();
            e.Handled = true;
        };
        SourceInitialized += (_, _) => MakeToolWindow();
        SizeChanged += (_, _) => { if (!_placed) Place(); else KeepOnScreen(); };
    }

    /// <summary>Neuer Stand vom Dienst. used/budget = gemeinsames Tageskonto (alle Geräte des Kindes).</summary>
    public void Update(int used, int budget)
    {
        int rem = Math.Max(0, budget - used);
        _main.Text = $"⏱ Heute {Fmt(used)} von {Fmt(budget)}";
        _sub.Text = rem > 0 ? $"noch {Fmt(rem)} übrig" : "Tageszeit aufgebraucht";
        _frac = budget <= 0 ? 0 : Math.Min(1.0, (double)used / budget);
        _barFill.Background = rem <= 10 ? Brushes.IndianRed : (rem <= 30 ? Brushes.Goldenrod : Brushes.LimeGreen);
        _card.Background = new SolidColorBrush(rem <= 10
            ? Color.FromArgb(0xEE, 0x6B, 0x1E, 0x2A)
            : Color.FromArgb(0xE6, 0x13, 0x29, 0x4B));
        UpdateBar();
        // Andere „immer oben"-Fenster können uns überdecken -> Topmost alle 30 s frisch setzen.
        if (DateTime.UtcNow - _lastTopmost > TimeSpan.FromSeconds(30))
        {
            _lastTopmost = DateTime.UtcNow;
            Topmost = false;
            Topmost = true;
        }
    }

    private DateTime _lastTopmost = DateTime.MinValue;

    private void UpdateBar() => _barFill.Width = Math.Max(0, _barBack.ActualWidth * _frac);

    private static string Fmt(int m)
    {
        if (m < 60) return $"{m} Min";
        return m % 60 == 0 ? $"{m / 60} Std" : $"{m / 60} Std {m % 60} Min";
    }

    private void Place()
    {
        _placed = true;
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
        try
        {
            if (File.Exists(PosPath))
            {
                var p = JsonSerializer.Deserialize<double[]>(File.ReadAllText(PosPath));
                if (p is { Length: 2 }) { Left = p[0]; Top = p[1]; }
            }
        }
        catch { }
        KeepOnScreen();
    }

    /// <summary>Nach Bildschirmwechsel/Verschieben nie außerhalb des sichtbaren Bereichs.</summary>
    private void KeepOnScreen()
    {
        double l = SystemParameters.VirtualScreenLeft, t = SystemParameters.VirtualScreenTop;
        double r = l + SystemParameters.VirtualScreenWidth, b = t + SystemParameters.VirtualScreenHeight;
        Left = Math.Clamp(Left, l, Math.Max(l, r - ActualWidth));
        Top = Math.Clamp(Top, t, Math.Max(t, b - ActualHeight));
    }

    private void SavePosition()
    {
        try
        {
            KeepOnScreen();
            Directory.CreateDirectory(Path.GetDirectoryName(PosPath)!);
            File.WriteAllText(PosPath, JsonSerializer.Serialize(new[] { Left, Top }));
        }
        catch { }
    }

    private void MakeToolWindow()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        }
        catch { }
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
}
