using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PolyChrom3MF.App;

public sealed class AboutWindow : Window
{
    const string TikTokUrl = "https://www.tiktok.com/@3d_ter?_r=1&_t=ZN-98EhsAU7jKY";

    public AboutWindow()
    {
        Title = "À propos de PolyChrom 3MF";
        Width = 560;
        MinHeight = 430;
        MaxHeight = Math.Max(430, SystemParameters.WorkArea.Height * .9);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(30) };
        panel.Children.Add(new TextBlock { Text = "PolyChrom 3MF", FontSize = 28, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Version 1.7.0", Foreground = (Brush)Application.Current.Resources["SecondaryText"], Margin = new Thickness(0, 2, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "Coloration procédurale de modèles 3MF et STL, de 4 à 32 couleurs.", TextWrapping = TextWrapping.Wrap });
        var credit = new Border { Background = (Brush)Application.Current.Resources["ControlBackground"], BorderBrush = (Brush)Application.Current.Resources["PanelBorder"], BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(16), Margin = new Thickness(0, 18, 0, 16) };
        var creditPanel = new StackPanel();
        creditPanel.Children.Add(new TextBlock { Text = "Sur une idée de 3D TER", FontSize = 18, FontWeight = FontWeights.Bold });
        var linkText = new TextBlock { Margin = new Thickness(0, 7, 0, 0) };
        var link = new Hyperlink(new Run("Découvrir 3D TER sur TikTok")) { NavigateUri = new Uri(TikTokUrl), Foreground = (Brush)Application.Current.Resources["Accent"] };
        link.RequestNavigate += OpenLink; linkText.Inlines.Add(link); creditPanel.Children.Add(linkText); credit.Child = creditPanel; panel.Children.Add(credit);
        panel.Children.Add(new TextBlock { Text = "Développement : bob59\n.NET 8 · WPF · format 3MF standard\nApplication locale, sans télémétrie.", Foreground = (Brush)Application.Current.Resources["SecondaryText"], TextWrapping = TextWrapping.Wrap });
        var close = new Button { Content = "Fermer", IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 18, 0, 0) };
        close.Click += (_, _) => Close(); panel.Children.Add(close);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true;
    }
}
