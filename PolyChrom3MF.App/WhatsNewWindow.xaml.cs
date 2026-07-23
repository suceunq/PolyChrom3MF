using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace PolyChrom3MF.App;

public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow(string version, string notes)
    {
        InitializeComponent();
        VersionText.Text = $"PolyChrom 3MF · version {version}";
        var lines = NormalizeNotes(notes);
        foreach (var line in lines)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(MediaColor.FromRgb(24, 127, 209)),
                Child = new TextBlock
                {
                    Text = "✓",
                    Foreground = MediaBrushes.White,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                }
            });
            var text = new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(226, 231, 239)),
                FontSize = 14,
                LineHeight = 21,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            NotesPanel.Children.Add(row);
        }
    }

    internal static IReadOnlyList<string> NormalizeNotes(string? notes)
    {
        var lines = (notes ?? "").Replace("\r", "").Split('\n')
            .Select(line => line.Trim().TrimStart('-', '*', '•').Trim())
            .Where(line => line.Length > 0)
            .Take(12)
            .Select(line => line.Length <= 300 ? line : line[..297] + "…")
            .ToList();
        return lines.Count > 0 ? lines : ["Améliorations générales et corrections de stabilité."];
    }

    void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    void Donate_Click(object sender, RoutedEventArgs e)
    {
        try { DonationService.Open(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossible d’ouvrir la page PayPal.\n\n{ex.Message}", "Soutenir PolyChrom 3MF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
