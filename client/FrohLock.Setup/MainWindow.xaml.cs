using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using FrohLock.Core;
using FrohLock.Core.Crypto;
using FrohLock.Core.Models;
using FrohLock.Core.Server;

namespace FrohLock.Setup;

public partial class MainWindow : Window
{
    private static readonly (int day, string label)[] DayDefs =
    {
        (1, "Mo"), (2, "Di"), (3, "Mi"), (4, "Do"), (5, "Fr"), (6, "Sa"), (0, "So")
    };

    private readonly List<CheckBox> _dayChecks = new();
    private readonly ObservableCollection<WindowItem> _windows = new();

    private string? _deviceId;
    private string? _token;
    private string _serverUrl = "";

    public MainWindow()
    {
        InitializeComponent();
        foreach (var (day, label) in DayDefs)
        {
            var cb = new CheckBox { Content = label, Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(0, 0, 12, 0), Tag = day };
            _dayChecks.Add(cb);
            DaysPanel.Children.Add(cb);
        }
        WindowsList.ItemsSource = _windows;
        ServerUrlBox.Text = Branding.DefaultServerBaseUrl;
    }

    public sealed class WindowItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public List<int> Days { get; set; } = new();
        public int StartMinute { get; set; }
        public int EndMinute { get; set; }
        public string Display =>
            $"{Fmt(StartMinute)}–{Fmt(EndMinute)}   {(Days.Count == 0 ? "täglich" : string.Join(", ", Days.Select(DayName)))}";
        private static string Fmt(int m) => $"{m / 60:00}:{m % 60:00}";
        private static string DayName(int d) => new[] { "So", "Mo", "Di", "Mi", "Do", "Fr", "Sa" }[d];
    }

    private static bool TryParseTime(string s, out int minutes)
    {
        minutes = 0;
        var parts = s.Trim().Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m)
            && h is >= 0 and < 24 && m is >= 0 and < 60)
        {
            minutes = h * 60 + m;
            return true;
        }
        return false;
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        PairButton.IsEnabled = false;
        PairStatus.Text = "Koppeln …";
        try
        {
            _serverUrl = ServerUrlBox.Text.Trim().TrimEnd('/');
            var code = PairingBox.Text.Trim();
            var name = DeviceNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(_serverUrl) || string.IsNullOrWhiteSpace(code))
            {
                PairStatus.Text = "Bitte Server-Adresse und Kopplungscode angeben.";
                return;
            }

            using var http = TlsPinning.CreateClient(Array.Empty<string>(), TimeSpan.FromSeconds(20));
            var client = new ServerClient(http, _serverUrl, "", null);
            var reg = await client.RegisterAsync(code, name);
            if (reg is null)
            {
                PairStatus.Text = "Kopplung fehlgeschlagen (Code ungültig/abgelaufen oder Server nicht erreichbar).";
                return;
            }

            _deviceId = reg.DeviceId;
            _token = reg.Token;

            AppPaths.EnsureDataDir();
            File.WriteAllText(AppPaths.DeviceTokenPath, _token);
            File.WriteAllText(AppPaths.BootstrapPath, Json.Serialize(new BootstrapInfo
            {
                ServerBaseUrl = _serverUrl, DeviceId = _deviceId, TlsSpkiPins = new()
            }));

            PairStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            PairStatus.Text = $"Gekoppelt ✓  (Gerät {_deviceId[..8]}…)";
            SaveButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            PairStatus.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            PairButton.IsEnabled = true;
        }
    }

    private void AddWindow_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseTime(StartBox.Text, out int start) || !TryParseTime(EndBox.Text, out int end))
        {
            MessageBox.Show("Bitte Zeiten im Format HH:MM angeben.", "FrohLock");
            return;
        }
        if (start == end)
        {
            MessageBox.Show("Start- und Endzeit dürfen nicht gleich sein.", "FrohLock");
            return;
        }
        var days = _dayChecks.Where(c => c.IsChecked == true).Select(c => (int)c.Tag!).ToList();
        _windows.Add(new WindowItem { StartMinute = start, EndMinute = end, Days = days });
    }

    private void RemoveWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string id)
        {
            var item = _windows.FirstOrDefault(w => w.Id == id);
            if (item is not null) _windows.Remove(item);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceId is null || _token is null)
        {
            SaveStatus.Text = "Bitte zuerst koppeln.";
            return;
        }
        var pin = PinBox.Password;
        if (pin.Length < 4) { SaveStatus.Text = "PIN muss mindestens 4 Zeichen haben."; return; }
        if (pin != PinConfirmBox.Password) { SaveStatus.Text = "Die PINs stimmen nicht überein."; return; }
        if (_windows.Count == 0) { SaveStatus.Text = "Bitte mindestens eine Sperrzeit anlegen."; return; }

        SaveButton.IsEnabled = false;
        SaveStatus.Foreground = System.Windows.Media.Brushes.White;
        SaveStatus.Text = "Speichere …";
        try
        {
            var (hash, salt) = PinHasher.Hash(pin);
            var email = EmailBox.Text.Trim();
            var draft = new
            {
                windows = _windows.Select(w => new
                {
                    id = w.Id,
                    days = w.Days,
                    startMinute = w.StartMinute,
                    endMinute = w.EndMinute,
                    enabled = true
                }).ToList(),
                pinHash = hash,
                pinSalt = salt,
                pinIterations = PinHasher.DefaultIterations,
                unlockGraceMinutes = 60,
                dailyBudgetMinutes = 0,
                maxTrustedTimeStalenessMinutes = 720,
                resetEmail = email
            };

            using var http = TlsPinning.CreateClient(Array.Empty<string>(), TimeSpan.FromSeconds(20));
            var client = new ServerClient(http, _serverUrl, _deviceId, _token);
            bool ok = await client.SetConfigDraftAsync(draft);
            if (ok)
            {
                SaveStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                SaveStatus.Text = "Gespeichert ✓  Der Schutzdienst übernimmt die Einstellungen in Kürze. " +
                                  "Du kannst dieses Fenster schließen.";
                ForgotInfo.Text = string.IsNullOrWhiteSpace(email)
                    ? $"Tipp: Falls du den PIN vergisst, kannst du ihn hier zurücksetzen:\n{_serverUrl}/forgot"
                    : $"Falls du den PIN vergisst, setze ihn hier zurück – ein Link geht an {email}:\n{_serverUrl}/forgot";
                ForgotBox.Visibility = Visibility.Visible;
            }
            else
            {
                SaveStatus.Text = "Speichern fehlgeschlagen (Server nicht erreichbar?).";
                SaveButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            SaveStatus.Text = "Fehler: " + ex.Message;
            SaveButton.IsEnabled = true;
        }
    }
}
