using System.Windows;
using System.Windows.Threading;

namespace FrohLock.Agent;

/// <summary>Read-only Übersicht: aktueller Zustand, heutige Sperrzeiten, Tageslimit/Restzeit.</summary>
public partial class StatusWindow : Window
{
    private readonly IpcClient _ipc;
    private readonly DispatcherTimer _timer;

    public StatusWindow(IpcClient ipc)
    {
        InitializeComponent();
        _ipc = ipc;
        Loaded += async (_, _) => await Refresh();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await Refresh();
        _timer.Start();
    }

    private async Task Refresh()
    {
        var s = await _ipc.GetStatusAsync();
        if (s is null)
        {
            StateText.Text = "Status nicht verfügbar";
            ReasonText.Text = "Der Schutzdienst antwortet gerade nicht.";
            FootText.Text = DateTime.Now.ToString("HH:mm:ss");
            return;
        }

        if (!s.Configured)
        {
            StateText.Text = "Noch nicht eingerichtet";
            ReasonText.Text = "FrohLock ist installiert, aber es wurden noch keine Sperrzeiten/PIN festgelegt.";
        }
        else if (s.Locked)
        {
            StateText.Text = "🔒 Jetzt gesperrt";
            ReasonText.Text = s.Reason;
        }
        else
        {
            StateText.Text = "✅ Jetzt frei";
            ReasonText.Text = s.Reason;
        }

        ScheduleText.Text = string.IsNullOrWhiteSpace(s.ScheduleSummary) ? "—" : s.ScheduleSummary;

        NextLockText.Text = s is { Locked: false, MinutesUntilLock: > 0 }
            ? $"Nächste Sperre in {FormatMinutes(s.MinutesUntilLock)}."
            : "";

        if (s.BudgetMinutes > 0)
        {
            int used = s.UsageMinutes;
            int rem = Math.Max(0, s.BudgetMinutes - used);
            BudgetText.Text = $"Heute genutzt: {used} min von {s.BudgetMinutes} min  ·  noch {rem} min";
            double frac = Math.Min(1.0, s.BudgetMinutes == 0 ? 0 : (double)used / s.BudgetMinutes);
            BarBack.Visibility = Visibility.Visible;
            BarBack.SizeChanged -= OnBarSize;
            BarBack.SizeChanged += OnBarSize;
            _lastFrac = frac;
            UpdateBar();
        }
        else
        {
            BudgetText.Text = $"Kein Tageslimit gesetzt. Heute genutzt: {s.UsageMinutes} min.";
            BarBack.Visibility = Visibility.Collapsed;
        }

        FootText.Text = "Stand: " + DateTime.Now.ToString("HH:mm:ss") + " · nur Ansicht (Änderungen über die Eltern-Verwaltung)";
    }

    private double _lastFrac;
    private void OnBarSize(object sender, SizeChangedEventArgs e) => UpdateBar();
    private void UpdateBar()
    {
        BarFill.Width = BarBack.ActualWidth * _lastFrac;
        BarFill.Background = _lastFrac >= 1.0
            ? System.Windows.Media.Brushes.IndianRed
            : (_lastFrac > 0.8 ? System.Windows.Media.Brushes.Goldenrod
                               : System.Windows.Media.Brushes.ForestGreen);
    }

    private static string FormatMinutes(int m)
    {
        if (m < 60) return $"{m} Min";
        return $"{m / 60} Std {m % 60} Min";
    }
}
