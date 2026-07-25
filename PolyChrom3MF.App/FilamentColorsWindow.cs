using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using ComboBox = System.Windows.Controls.ComboBox;
using MediaBrush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace PolyChrom3MF.App;

public sealed class FilamentColorsWindow : Window
{
    static readonly string[] Defaults =
    [
        "#E53935", "#1E88E5", "#43A047", "#FDD835", "#8E24AA", "#FB8C00", "#00ACC1", "#6D4C41",
        "#3949AB", "#7CB342", "#D81B60", "#5E35B1", "#00897B", "#C0CA33", "#F4511E", "#757575"
    ];

    readonly Slider _count;
    readonly TextBlock _countText;
    readonly WrapPanel _slots = new();
    readonly List<FilamentSlot> _values;

    public IReadOnlyList<string> Colors => _values.Take((int)_count.Value).Select(slot => slot.Hex).ToList();
    public IReadOnlyList<string> Materials => _values.Take((int)_count.Value).Select(slot => slot.Material).ToList();

    public FilamentColorsWindow(IReadOnlyList<string> colors, IReadOnlyList<string> materials)
    {
        Title = "Gérer mes filaments — PolyChrom 3MF";
        Width = 760; Height = 650; MinWidth = 650; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _values = Enumerable.Range(0, 32).Select(index => new FilamentSlot(
            index < colors.Count ? colors[index] : Defaults[index % Defaults.Length],
            index < materials.Count && materials[index].Equals("PETG", StringComparison.OrdinalIgnoreCase) ? "PETG" : "PLA")).ToList();

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "MES FILAMENTS", FontSize = 22, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock
        {
            Text = "Choisissez le nombre de couleurs, puis définissez leur teinte et leur matériau. Toutes les modifications seront enregistrées ensemble.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 16),
            Foreground = (MediaBrush)Application.Current.Resources["SecondaryText"]
        });
        _countText = new TextBlock { FontSize = 18, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        _count = new Slider { Minimum = 2, Maximum = 32, TickFrequency = 1, IsSnapToTickEnabled = true, Value = Math.Clamp(colors.Count, 2, 32), Margin = new Thickness(0, 8, 0, 6) };
        _count.ValueChanged += (_, _) => { UpdateCount(); RebuildSlots(); };
        panel.Children.Add(_countText); panel.Children.Add(_count);
        var presets = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        foreach (var count in new[] { 2, 4, 6, 8, 12, 16, 24, 32 })
        {
            var button = new Button { Content = count, MinWidth = 42 };
            button.Click += (_, _) => _count.Value = count;
            presets.Children.Add(button);
        }
        panel.Children.Add(presets);
        panel.Children.Add(new ScrollViewer { Content = _slots, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 385 });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(new Button { Content = "Annuler", IsCancel = true, MinWidth = 95 });
        var save = new Button { Content = "Enregistrer tous les filaments", IsDefault = true, MinWidth = 210 };
        save.Click += (_, _) => DialogResult = true;
        actions.Children.Add(save); panel.Children.Add(actions);
        Content = panel;
        UpdateCount(); RebuildSlots();
    }

    void UpdateCount() => _countText.Text = $"{(int)_count.Value} couleurs de filament";

    void RebuildSlots()
    {
        _slots.Children.Clear();
        for (var index = 0; index < (int)_count.Value; index++)
        {
            var captured = index;
            var slot = _values[index];
            var swatch = new Button { Width = 112, Height = 48, Margin = new Thickness(0, 0, 0, 5), ToolTip = "Cliquer pour choisir la couleur" };
            void RefreshSwatch() { swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(slot.Hex)!); swatch.Content = slot.Hex; swatch.Foreground = Contrast(slot.Hex); }
            RefreshSwatch();
            swatch.Click += (_, _) =>
            {
                using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
                var current = (Color)ColorConverter.ConvertFromString(slot.Hex)!;
                dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                _values[captured].Hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
                RefreshSwatch();
            };
            var material = new ComboBox { ItemsSource = new[] { "PLA", "PETG" }, SelectedItem = slot.Material, Width = 112 };
            material.SelectionChanged += (_, _) => _values[captured].Material = material.SelectedItem?.ToString() ?? "PLA";
            var card = new StackPanel { Width = 126, Margin = new Thickness(0, 0, 10, 12) };
            card.Children.Add(new TextBlock { Text = $"Filament {index + 1}", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            card.Children.Add(swatch); card.Children.Add(material);
            _slots.Children.Add(card);
        }
    }

    static MediaBrush Contrast(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex)!;
        return color.R * .299 + color.G * .587 + color.B * .114 > 150 ? Brushes.Black : Brushes.White;
    }

    sealed class FilamentSlot(string hex, string material)
    {
        public string Hex { get; set; } = hex;
        public string Material { get; set; } = material;
    }
}
