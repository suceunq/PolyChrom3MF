using System.Windows;

namespace PolyChrom3MF.App;

public partial class WelcomeWindow : Window
{
    public bool ShowAtStartup => ShowAgain.IsChecked == true;
    public WelcomeWindow() => InitializeComponent();
    void Close_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
}
