using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FrohLock.Core.Schedule;

namespace FrohLock.Agent;

/// <summary>
/// Kindgerechte Zeitleiste „Von wann bis wann warst du heute am Laptop?":
/// farbige Blöcke auf einer Tages-Uhrzeitachse, darunter die Zeiten als Liste
/// und die Rechnung, die zusammen das Tageslimit ergibt.
/// </summary>
public sealed class UsageTimelineView : UserControl
{
    private static readonly Color[] BlockColors =
    {
        Color.FromRgb(0xFF, 0xB7, 0x4D), // orange
        Color.FromRgb(0x4D, 0xD0, 0xE1), // türkis
        Color.FromRgb(0xF0, 0x62, 0x92), // pink
        Color.FromRgb(0x9C, 0xCC, 0x65), // grün
        Color.FromRgb(0xBA, 0x68, 0xC8), // lila
        Color.FromRgb(0xFF, 0xD5, 0x4F), // gelb
    };

    private const double TrackHeight = 34;

    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _total = new() { FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
    private readonly TextBlock _totalSub = new() { FontSize = 15, Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 0, 5) };
    private readonly Canvas _canvas = new() { Height = TrackHeight + 46, ClipToBounds = false };
    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _sum = new() { FontSize = 15, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x82)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    private List<UsageSegmentView> _segments = new();

    public UsageTimelineView()
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        head.Children.Add(_total);
        head.Children.Add(_totalSub);

        var root = new StackPanel();
        root.Children.Add(_title);
        root.Children.Add(head);
        root.Children.Add(_canvas);
        root.Children.Add(_chips);
        root.Children.Add(_sum);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(20, 16, 20, 16),
            Child = root
        };
        _canvas.SizeChanged += (_, _) => DrawTrack();
    }

    public void Render(IReadOnlyList<UsageSegmentView>? segments, int usageMinutes, int budgetMinutes, bool limitReached)
    {
        _segments = segments?.ToList() ?? new List<UsageSegmentView>();

        _title.Text = limitReached
            ? "So lange warst du heute schon am Laptop:"
            : "Deine Bildschirmzeit heute:";
        _total.Text = Fmt(usageMinutes);
        _totalSub.Text = budgetMinutes > 0 ? $"von {Fmt(budgetMinutes)} für heute" : "";

        // Liste der Abschnitte.
        _chips.Children.Clear();
        int i = 0;
        foreach (var s in _segments)
        {
            _chips.Children.Add(Chip($"🕐 {s.Start} – {s.End}", $"{Fmt(Math.Max(1, s.Minutes))}", BlockColors[i % BlockColors.Length]));
            i++;
        }
        int sumSegs = _segments.Sum(s => s.Minutes);
        int earlier = usageMinutes - sumSegs;
        if (earlier >= 2)
            _chips.Children.Add(Chip("⏳ früher heute", Fmt(earlier), Color.FromRgb(0x90, 0xA4, 0xAE)));

        // Die Rechnung: 50 Min + 40 Min + 30 Min = 2 Std.
        var parts = _segments.Select(s => Math.Max(1, s.Minutes)).ToList();
        if (earlier >= 2) parts.Insert(0, earlier);
        _sum.Text = parts.Count is >= 2 and <= 8
            ? "Zusammengerechnet: " + string.Join(" + ", parts.Select(Fmt)) + " = " + Fmt(usageMinutes)
            : (_segments.Count == 0 ? "Heute wurde noch keine Bildschirmzeit gezählt." : "");
        _sum.Visibility = string.IsNullOrEmpty(_sum.Text) ? Visibility.Collapsed : Visibility.Visible;

        DrawTrack();
    }

    private void DrawTrack()
    {
        _canvas.Children.Clear();
        double w = _canvas.ActualWidth;
        if (w <= 10) return;

        int nowMin = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        int first = _segments.Count > 0 ? _segments.Min(s => s.StartMinute) : nowMin;
        int last = _segments.Count > 0 ? _segments.Max(s => s.EndMinute) : nowMin;
        int axisStart = Math.Min(first, 8 * 60) / 60 * 60;
        int axisEnd = Math.Min(1440, (Math.Max(Math.Max(last, nowMin), 20 * 60) + 59) / 60 * 60);
        double span = Math.Max(60, axisEnd - axisStart);
        double X(int minute) => (minute - axisStart) / span * w;

        // Hintergrund-Spur (der Tag).
        var track = new Rectangle
        {
            Width = w, Height = TrackHeight, RadiusX = 10, RadiusY = 10,
            Fill = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF))
        };
        Canvas.SetTop(track, 0);
        _canvas.Children.Add(track);

        // Stundenmarken (alle 2 Std; bei kurzer Achse jede Stunde).
        int step = span > 10 * 60 ? 120 : 60;
        for (int m = axisStart; m <= axisEnd; m += step)
        {
            double x = X(m);
            var tick = new Rectangle { Width = 1, Height = 8, Fill = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)) };
            Canvas.SetLeft(tick, x); Canvas.SetTop(tick, TrackHeight + 2);
            _canvas.Children.Add(tick);
            var lbl = new TextBlock { Text = $"{m / 60} Uhr", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF)) };
            lbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(lbl, Math.Clamp(x - lbl.DesiredSize.Width / 2, 0, Math.Max(0, w - lbl.DesiredSize.Width)));
            Canvas.SetTop(lbl, TrackHeight + 10);
            _canvas.Children.Add(lbl);
        }

        // Nutzungs-Blöcke.
        int i = 0;
        foreach (var s in _segments)
        {
            var color = BlockColors[i % BlockColors.Length];
            double x1 = X(s.StartMinute), x2 = X(s.EndMinute);
            var block = new Rectangle
            {
                Width = Math.Max(4, x2 - x1), Height = TrackHeight - 6, RadiusX = 7, RadiusY = 7,
                Fill = new LinearGradientBrush(color, Color.FromArgb(0xFF, (byte)(color.R * 0.8), (byte)(color.G * 0.8), (byte)(color.B * 0.8)), 90),
                ToolTip = $"{s.Start} – {s.End} ({Fmt(s.Minutes)})"
            };
            Canvas.SetLeft(block, x1); Canvas.SetTop(block, 3);
            _canvas.Children.Add(block);
            i++;
        }

        // „Jetzt"-Markierung.
        if (nowMin >= axisStart && nowMin <= axisEnd)
        {
            double xn = X(nowMin);
            var now = new Rectangle { Width = 3, Height = TrackHeight + 8, Fill = Brushes.White, RadiusX = 1.5, RadiusY = 1.5 };
            Canvas.SetLeft(now, xn - 1.5); Canvas.SetTop(now, -4);
            _canvas.Children.Add(now);
            var nowLbl = new TextBlock { Text = "jetzt", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            nowLbl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(nowLbl, Math.Clamp(xn - nowLbl.DesiredSize.Width / 2, 0, Math.Max(0, w - nowLbl.DesiredSize.Width)));
            Canvas.SetTop(nowLbl, TrackHeight + 24);
            _canvas.Children.Add(nowLbl);
        }
    }

    private static Border Chip(string label, string value, Color color)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = label, FontSize = 15, Foreground = Brushes.White });
        sp.Children.Add(new TextBlock { Text = "  ·  " + value, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color) });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 8), Child = sp
        };
    }

    /// <summary>120 -> "2 Std", 75 -> "1 Std 15 Min", 40 -> "40 Min".</summary>
    public static string Fmt(int minutes)
    {
        if (minutes < 60) return $"{minutes} Min";
        int h = minutes / 60, m = minutes % 60;
        return m == 0 ? $"{h} Std" : $"{h} Std {m} Min";
    }
}
