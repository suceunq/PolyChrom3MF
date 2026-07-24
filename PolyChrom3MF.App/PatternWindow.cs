using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    readonly ComboBox _logoColor = new();
    readonly Slider _scale = Slider(10, 400, 100);
    readonly Slider _rotation = Slider(-180, 180, 0);
    readonly Slider _offsetX = Slider(-100, 100, 0);
    readonly Slider _offsetY = Slider(-100, 100, 0);
    readonly Slider _logoThreshold = Slider(1, 254, 128);
    readonly CheckBox _variants = new() { Content = new TextBlock { Text = "Créer quatre propositions avec quatre projections différentes", TextWrapping = TextWrapping.Wrap }, IsChecked = true };
    readonly CheckBox _monochromeLogo = new() { Content = new TextBlock { Text = "Mode logo monochrome — conserver uniquement les formes du logo", TextWrapping = TextWrapping.Wrap } };
    readonly CheckBox _invertLogo = new() { Content = new TextBlock { Text = "Logo sombre sur fond clair (inverser noir/blanc)", TextWrapping = TextWrapping.Wrap } };
    readonly CheckBox _repeatAcrossModel = new() { Content = new TextBlock { Text = "Répéter le motif pour couvrir toute la pièce", TextWrapping = TextWrapping.Wrap }, IsChecked = true };
    readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly TextBlock _previewStatus = new() { Text = "L’aperçu se met à jour au relâchement du curseur.", Margin = new Thickness(0, 7, 0, 0) };

    public PatternSettings Value { get; private set; }
    public event Action<PatternSettings>? PreviewRequested;

    public PatternWindow(string imagePath, IReadOnlyList<ModelObject> objects, IReadOnlyList<PaletteColor> colors, PatternSettings? current = null, string? displayName = null)
    {
        _imagePath = imagePath;
        displayName ??= Path.GetFileName(imagePath);
        Value = current is null ? new PatternSettings(imagePath, DisplayName: displayName) : current with { ImagePath = imagePath, DisplayName = displayName };
        Title = "Appliquer un motif image"; Width = 650; MinHeight = 700; MaxHeight = Math.Max(700, SystemParameters.WorkArea.Height * .92); SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _mode.ItemsSource = new[] { new Option<PatternMode>("Projection frontale", PatternMode.Front), new Option<PatternMode>("Enveloppement cylindrique", PatternMode.Cylindrical), new Option<PatternMode>("Motif répété", PatternMode.Repeated), new Option<PatternMode>("Projection triplanaire", PatternMode.Triplanar) }; _mode.DisplayMemberPath = nameof(Option<PatternMode>.Label); _mode.SelectedIndex = (int)Value.Mode;
        var targets = new List<Option<int>> { new("Toute la figurine", -1) }; targets.AddRange(objects.Select(obj => new Option<int>(obj.ToString(), obj.Index))); _target.ItemsSource = targets; _target.DisplayMemberPath = nameof(Option<int>.Label); _target.SelectedItem = targets.FirstOrDefault(item => item.Value == Value.TargetObject) ?? targets[0];
        var logoColors = colors.Select((color, index) => new Option<int>($"{color.Name}  {color.Hex}", index)).ToList();
        _logoColor.ItemsSource = logoColors; _logoColor.DisplayMemberPath = nameof(Option<int>.Label); _logoColor.SelectedIndex = Math.Clamp(Value.LogoColorIndex, 0, Math.Max(0, logoColors.Count - 1));
        _scale.Value = Value.Scale; _rotation.Value = Value.Rotation; _offsetX.Value = Value.OffsetX; _offsetY.Value = Value.OffsetY; _variants.IsChecked = Value.FourVariants;
        _monochromeLogo.IsChecked = Value.MonochromeLogo; _invertLogo.IsChecked = Value.InvertLogo; _logoThreshold.Value = Value.LogoThreshold;
        _repeatAcrossModel.IsChecked = Value.RepeatAcrossModel;
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); Value = ReadSettings(); PreviewRequested?.Invoke(Value); };
        _mode.SelectionChanged += (_, _) => QueuePreview(); _target.SelectionChanged += (_, _) => QueuePreview(); _logoColor.SelectionChanged += (_, _) => QueuePreview();
        TrackSlider(_scale); TrackSlider(_rotation); TrackSlider(_offsetX); TrackSlider(_offsetY); TrackSlider(_logoThreshold);
        _variants.Checked += (_, _) => QueuePreview(); _variants.Unchecked += (_, _) => QueuePreview();
        _monochromeLogo.Checked += (_, _) => { UpdateLogoControls(); QueuePreview(); }; _monochromeLogo.Unchecked += (_, _) => { UpdateLogoControls(); QueuePreview(); };
        _invertLogo.Checked += (_, _) => QueuePreview(); _invertLogo.Unchecked += (_, _) => QueuePreview();
        _repeatAcrossModel.Checked += (_, _) => QueuePreview(); _repeatAcrossModel.Unchecked += (_, _) => QueuePreview();
        Loaded += (_, _) => QueuePreview();
        Closed += (_, _) => _previewTimer.Stop();

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "MOTIF IMAGE", FontSize = 22, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Le motif sera converti vers les couleurs de filament de chaque proposition. Les pixels transparents conservent la coloration existante.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 14), Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SecondaryText"] });
        panel.Children.Add(new TextBlock { Text = "Aperçu en direct : déplacez les curseurs pour voir le résultat sur la figurine derrière cette fenêtre.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Accent"] });
        var preview = new Image { Source = LoadPreview(imagePath), Height = 190, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new Border { Background = (System.Windows.Media.Brush)Application.Current.Resources["InputBackground"], BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["PanelBorder"], BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = preview });
        panel.Children.Add(Field("Application", _target)); panel.Children.Add(Field("Projection", _mode)); panel.Children.Add(_repeatAcrossModel); panel.Children.Add(_variants);
        var logoPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 2) };
        logoPanel.Children.Add(_monochromeLogo);
        logoPanel.Children.Add(new TextBlock { Text = "Le fond et les autres pixels conservent la couleur actuelle du modèle. Idéal pour des symboles ou des logos noirs et blancs.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 4, 0, 5), Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SecondaryText"] });
        logoPanel.Children.Add(Field("Couleur de filament du logo", _logoColor));
        logoPanel.Children.Add(_invertLogo);
        logoPanel.Children.Add(SliderField("Seuil de détection du logo", _logoThreshold, ""));
        panel.Children.Add(logoPanel);
        panel.Children.Add(SliderField("Taille du motif", _scale, "%")); panel.Children.Add(SliderField("Rotation", _rotation, "°")); panel.Children.Add(SliderField("Décalage horizontal", _offsetX, "%")); panel.Children.Add(SliderField("Décalage vertical", _offsetY, "%"));
        _previewStatus.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SecondaryText"];
        panel.Children.Add(_previewStatus);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 90 }; var apply = new Button { Content = "Appliquer le motif", IsDefault = true, MinWidth = 145 };
        apply.Click += Apply; buttons.Children.Add(cancel); buttons.Children.Add(apply); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        UpdateLogoControls();
    }

    void Apply(object sender, RoutedEventArgs e)
    {
        _previewTimer.Stop();
        Value = ReadSettings();
        DialogResult = true;
    }

    PatternSettings ReadSettings() => new(
        _imagePath,
        ((Option<PatternMode>)_mode.SelectedItem).Value,
        _scale.Value,
        _rotation.Value,
        _offsetX.Value,
        _offsetY.Value,
        ((Option<int>)_target.SelectedItem).Value,
        _variants.IsChecked == true,
        DisplayName: Value.DisplayName,
        MonochromeLogo: _monochromeLogo.IsChecked == true,
        LogoColorIndex: ((Option<int>?)_logoColor.SelectedItem)?.Value ?? 0,
        InvertLogo: _invertLogo.IsChecked == true,
        LogoThreshold: (byte)Math.Round(_logoThreshold.Value),
        RepeatAcrossModel: _repeatAcrossModel.IsChecked == true);

    void UpdateLogoControls()
    {
        var enabled = _monochromeLogo.IsChecked == true;
        _logoColor.IsEnabled = enabled;
        _invertLogo.IsEnabled = enabled;
        _logoThreshold.IsEnabled = enabled;
    }

    void QueuePreview()
    {
        if (!IsLoaded) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    void TrackSlider(Slider slider)
    {
        // A drag can emit dozens of ValueChanged events. Keep the thumb fluid and
        // calculate the expensive 3D preview only once when the user releases it.
        slider.ValueChanged += (_, _) => { if (!slider.IsMouseCaptureWithin) QueuePreview(); };
        slider.PreviewMouseLeftButtonUp += (_, _) => QueuePreview();
        slider.LostMouseCapture += (_, _) => QueuePreview();
    }

    public void SetPreviewStatus(string text, bool error = false)
    {
        _previewStatus.Text = text;
        _previewStatus.Foreground = error
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 115, 115))
            : (System.Windows.Media.Brush)Application.Current.Resources["SecondaryText"];
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
