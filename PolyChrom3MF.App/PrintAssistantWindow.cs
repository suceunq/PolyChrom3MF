using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace PolyChrom3MF.App;

public sealed class PrintAssistantWindow : Window
{
    public PrintAssistantWindow(ColorProposal proposal, AppSettings settings, string slicerName)
    {
        Title = "Assistant d’impression multicolore";
        Width = 720;
        Height = 680;
        MinWidth = 620;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var filaments = settings.FilamentColors.Select((hex, index) => new LoadedFilament(index + 1, $"Filament {index + 1}", hex)).ToList();
        var printer = new PrinterCapabilities(settings.PrinterName, settings.MaterialSlots, settings.NozzleDiameter, settings.LayerHeight, filaments);
        var preparation = new PrintAssistantService().Analyze(proposal, printer);

        var root = new DockPanel { Margin = new Thickness(22) };
        var close = new Button { Content = "Fermer", IsCancel = true, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "ASSISTANT D’IMPRESSION MULTICOLORE", FontSize = 22, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock
        {
            Text = $"{printer.Name} · {printer.MaterialSlots} emplacement(s) · buse {printer.NozzleDiameter:0.##} mm · couche {printer.LayerHeight:0.##} mm\nSlicer : {slicerName}",
            Margin = new Thickness(0, 7, 0, 18),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SecondaryText"]
        });
        panel.Children.Add(new TextBlock { Text = "Correspondance modèle → filaments chargés", FontSize = 16, FontWeight = FontWeights.SemiBold });
        foreach (var match in preparation.Matches)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            row.Children.Add(Swatch(match.ModelHex));
            row.Children.Add(new TextBlock { Text = $"Couleur {match.ColorIndex + 1}", Width = 100, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = "→", FontSize = 18, Margin = new Thickness(6, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Swatch(match.FilamentHex));
            row.Children.Add(new TextBlock { Text = $"Emplacement {match.Slot} · écart {match.Distance:0.0}", VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(row);
        }
        panel.Children.Add(new TextBlock { Text = "Analyse", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 5) });
        if (preparation.Warnings.Count == 0)
            panel.Children.Add(new TextBlock { Text = "✓ Configuration compatible avec la palette du modèle.", Foreground = Brushes.LightGreen });
        foreach (var warning in preparation.Warnings)
            panel.Children.Add(new TextBlock { Text = "⚠ " + warning, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Foreground = Brushes.Orange });
        if (preparation.UnusedFilaments.Count > 0)
            panel.Children.Add(new TextBlock { Text = $"Filaments non utilisés : {string.Join(", ", preparation.UnusedFilaments.Select(item => item.Name))}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    static Border Swatch(string hex) => new()
    {
        Width = 34,
        Height = 24,
        CornerRadius = new CornerRadius(4),
        Margin = new Thickness(0, 0, 8, 0),
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!)
    };
}
