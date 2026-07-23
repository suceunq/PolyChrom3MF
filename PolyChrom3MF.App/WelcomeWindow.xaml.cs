using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace PolyChrom3MF.App;

public partial class WelcomeWindow : Window
{
    public bool ShowAtStartup => ShowAgain.IsChecked == true;
    public WelcomeWindow() => InitializeComponent();
    void Close_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    void Donate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DonationService.Open();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d’ouvrir la page PayPal.\n\n{ex.Message}", "Soutenir PolyChrom 3MF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
