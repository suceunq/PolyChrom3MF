using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace PolyChrom3MF.App;

public sealed class PatternWindow : Window
{
    readonly string _imagePath;
    readonly ComboBox _mode = new();
    readonly ComboBox _target = new();
    readonly Slider _scale = Slider(10, 400, 100);
    readonly Slider _rotation = Slider(-180, 180, 0);
    readonly Slider _offsetX = Slider(-100, 100, 0);
    readonly Slider _offsetY = Slider(-100, 100, 0);
    readonly CheckBox _variants = new() { Content = new TextBlock { Text = "Créer quatre propositions avec quatre projections différentes", TextWrapping = TextWrapping.Wrap }, IsChecked = true };

    public PatternSettings Value { get; private set; }

    public PatternWindow(string imagePath, IReadOnlyList<ModelObject> objects, PatternSettings? current = null, string? displayName = null)
    {
        _imagePath = imagePath;
        displayName ??= Path.GetFileName(imagePath);
        Value = current is null ? new PatternSettings(imagePath, DisplayName: displayName) : current with { ImagePath = imagePath, DisplayName = displayName };
        Title = "Appliquer un motif image"; Width = 650; MinHeight = 700; MaxHeight = Math.Max(700, SystemParameters.WorkArea.Height * .92); SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _mode.ItemsSource = new[] { new Option<PatternMode>("Projection frontale", PatternMode.Front), new Option<PatternMode>("Enveloppement cylindrique", PatternMode.Cylindrical), new Option<PatternMode>("Motif répété", PatternMode.Repeated), new Option<PatternMode>("Projection triplanaire", PatternMode.Triplanar) }; _mode.DisplayMemberPath = nameof(Option<PatternMode>.Label); _mode.SelectedIndex = (int)Value.Mode;
        var targets = new List<Option<int>> { new("Toute la figurine", -1) }; targets.AddRange(objects.Select(obj => new Option<int>(obj.ToString(), obj.Index))); _target.ItemsSource = targets; _target.DisplayMemberPath = nameof(Option<int>.Label); _target.SelectedItem = targets.FirstOrDefault(item => item.Value == Value.TargetObject) ?? targets[0];
        _scale.Value = Value.Scale; _rotation.Value = Value.Rotation; _offsetX.Value = Value.OffsetX; _offsetY.Value = Value.OffsetY; _variants.IsChecked = Value.FourVariants;

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "MOTIF IMAGE", FontSize = 22, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Le motif sera converti vers les couleurs de filament de chaque proposition. Les pixels transparents conservent la coloration existante.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 14), Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SecondaryText"] });
        var preview = new Image { Source = LoadPreview(imagePath), Height = 190, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new Border { Background = (System.Windows.Media.Brush)Application.Current.Resources["InputBackground"], BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["PanelBorder"], BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = preview });
        panel.Children.Add(Field("Application", _target)); panel.Children.Add(Field("Projection", _mode)); panel.Children.Add(_variants);
        panel.Children.Add(SliderField("Taille du motif", _scale, "%")); panel.Children.Add(SliderField("Rotation", _rotation, "°")); panel.Children.Add(SliderField("Décalage horizontal", _offsetX, "%")); panel.Children.Add(SliderField("Décalage vertical", _offsetY, "%"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 90 }; var apply = new Button { Content = "Appliquer le motif", IsDefault = true, MinWidth = 145 };
        apply.Click += Apply; buttons.Children.Add(cancel); buttons.Children.Add(apply); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    void Apply(object sender, RoutedEventArgs e)
    {
        Value = new PatternSettings(_imagePath, ((Option<PatternMode>)_mode.SelectedItem).Value, _scale.Value, _rotation.Value, _offsetX.Value, _offsetY.Value, ((Option<int>)_target.SelectedItem).Value, _variants.IsChecked == true, DisplayName: Value.DisplayName);
        DialogResult = true;
    }

    static FrameworkElement Field(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) }); panel.Children.Add(control); return panel;
    }

    static FrameworkElement SliderField(string label, Slider slider, string suffix)
    {
        var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 55, TextAlignment = TextAlignment.Right };
        void Update() => value.Text = $"{slider.Value:0}{suffix}"; slider.ValueChanged += (_, _) => Update(); Update();
        var header = new DockPanel(); DockPanel.SetDock(value, Dock.Right); header.Children.Add(value); header.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold });
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(header); panel.Children.Add(slider); return panel;
    }

    static Slider Slider(double min, double max, double value) => new() { Minimum = min, Maximum = max, Value = value, TickFrequency = 10, IsSnapToTickEnabled = false };
    static BitmapImage LoadPreview(string path) { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.DecodePixelWidth = 500; image.EndInit(); image.Freeze(); return image; }
    sealed record Option<T>(string Label, T Value);
}
