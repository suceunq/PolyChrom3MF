using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace PolyChrom3MF.App;

public sealed class ColorCountWindow : Window
{
    readonly Slider _slider;
    readonly TextBlock _value;
    public int ColorCount => (int)_slider.Value;

    public ColorCountWindow(int current)
    {
        Title = "Nombre de couleurs — PolyChrom 3MF";
        Width = 560;
        MinHeight = 390;
        MaxHeight = Math.Max(390, SystemParameters.WorkArea.Height * .9);
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "Combien de couleurs voulez-vous utiliser ?", FontSize = 20, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Minimum 4 couleurs · maximum 32 couleurs. Les quatre propositions et l’export 3MF utiliseront ce nombre.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SecondaryText"], Margin = new Thickness(0, 8, 0, 18) });
        _value = new TextBlock { FontSize = 28, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        _slider = new Slider { Minimum = 4, Maximum = 32, TickFrequency = 1, IsSnapToTickEnabled = true, Value = Math.Clamp(current, 4, 32), Margin = new Thickness(0, 8, 0, 10) };
        _slider.ValueChanged += (_, _) => _value.Text = $"{ColorCount} couleurs"; _value.Text = $"{ColorCount} couleurs";
        panel.Children.Add(_value); panel.Children.Add(_slider);
        var presets = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 18) };
        foreach (var count in new[] { 4, 6, 8, 12, 16, 24, 32 }) { var button = new Button { Content = count.ToString(), MinWidth = 46 }; button.Click += (_, _) => _slider.Value = count; presets.Children.Add(button); }
        panel.Children.Add(presets);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Utiliser ce nombre", IsDefault = true, Padding = new Thickness(16, 7, 16, 7) }; ok.Click += (_, _) => DialogResult = true;
        actions.Children.Add(ok); panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
}
