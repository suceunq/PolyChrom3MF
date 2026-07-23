using System.Windows;

namespace PolyChrom3MF.App;

public partial class HelpGuideWindow : Window
{
    public HelpGuideWindow() => InitializeComponent();
    void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
