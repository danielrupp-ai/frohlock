using System.Windows;
using System.Windows.Input;

namespace FrohLock.Setup;

public partial class PinPromptWindow : Window
{
    public string Pin { get; private set; } = "";

    public PinPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PinBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Pin = PinBox.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Pin = PinBox.Password; DialogResult = true; }
        else if (e.Key == Key.Escape) DialogResult = false;
    }
}
