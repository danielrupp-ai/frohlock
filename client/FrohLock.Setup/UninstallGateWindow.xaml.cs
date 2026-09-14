using System.Windows;
using System.Windows.Input;
using FrohLock.Core.Models;

namespace FrohLock.Setup;

/// <summary>
/// Vorgeschaltetes Deinstallations-Popup. Standard = „Nein, behalten".
/// Entfernen ist nur mit korrektem Eltern-/Master-PIN möglich.
/// </summary>
public partial class UninstallGateWindow : Window
{
    private readonly LockConfig? _config;

    /// <summary>True nur, wenn der PIN korrekt bestätigt wurde.</summary>
    public bool Allowed { get; private set; }

    public UninstallGateWindow(LockConfig? config)
    {
        InitializeComponent();
        _config = config;
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        Allowed = false;
        DialogResult = false;
    }

    private void ShowPin_Click(object sender, RoutedEventArgs e)
    {
        ChoicePanel.Visibility = Visibility.Collapsed;
        PinPanel.Visibility = Visibility.Visible;
        PinBox.Focus();
    }

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Confirm_Click(sender, e);
        else if (e.Key == Key.Escape) Keep_Click(sender, e);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (App.VerifyPinStatic(PinBox.Password, _config))
        {
            Allowed = true;
            DialogResult = true;
        }
        else
        {
            ErrText.Text = "Falscher PIN. Nur Eltern können FrohLock entfernen.";
            PinBox.Clear();
            PinBox.Focus();
        }
    }
}
