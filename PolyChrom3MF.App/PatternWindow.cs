using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Input;
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

public sealed class PatternWindow : System.Windows.Controls.UserControl
{
    readonly string _imagePath;
    readonly ComboBox _mode = new();
    readonly ComboBox _target = new();
    readonly ComboBox _logoColor = new();
    readonly ComboBox _alignment = new();
    readonly Slider _scale = Slider(10, 400, 100);
    readonly Slider _rotation = Slider(-180, 180, 0);
    readonly Slider _offsetX = Slider(-100, 100, 0);
    readonly Slider _offsetY = Slider(-100, 100, 0);
    readonly Slider _stretchX = Slider(10, 400, 100);
    readonly Slider _stretchY = Slider(10, 400, 100);
    readonly Slider _copies = Slider(1, 32, 1);
    readonly Slider _spacing = Slider(0, 300, 0);
    readonly Slider _logoThreshold = Slider(1, 254, 128);
    readonly Slider _tiltX = Slider(-70, 70, 0);
    readonly Slider _tiltY = Slider(-70, 70, 0);
    readonly Slider _relief = Slider(-2, 5, 0);
    readonly CheckBox _variants = new() { Content = new TextBlock { Text = "Créer quatre propositions avec quatre projections différentes", TextWrapping = TextWrapping.Wrap }, IsChecked = true };
    readonly CheckBox _monochromeLogo = new() { Content = new TextBlock { Text = "Mode logo monochrome — conserver uniquement les formes du logo", TextWrapping = TextWrapping.Wrap } };
    readonly CheckBox _invertLogo = new() { Content = new TextBlock { Text = "Logo sombre sur fond clair (inverser noir/blanc)", TextWrapping = TextWrapping.Wrap } };
    readonly CheckBox _repeatAcrossModel = new() { Content = new TextBlock { Text = "Répéter le motif pour couvrir toute la pièce", TextWrapping = TextWrapping.Wrap }, IsChecked = true };
    readonly CheckBox _mirrorX = new() { Content = "Miroir horizontal" };
    readonly CheckBox _mirrorY = new() { Content = "Miroir vertical" };
    readonly CheckBox _backFace = new() { Content = "Prévisualiser aussi les faces arrière", IsChecked = true };
    readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly TextBlock _previewStatus = new() { Text = "L’aperçu se met à jour au relâchement du curseur.", Margin = new Thickness(0, 7, 0, 0) };
    readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool _completed;
    bool _waitingForPlacement;
    bool _addingOccurrence;
    readonly List<PatternOccurrence> _occurrences = [];

    public PatternSettings Value { get; private set; }
    public bool WaitingForSurfacePlacement => _waitingForPlacement;
    public PatternMode CurrentMode => ((Option<PatternMode>)_mode.SelectedItem).Value;
    public event Action<PatternSettings>? PreviewRequested;
    public event Action<bool>? PlacementModeChanged;

    public PatternWindow(string imagePath, IReadOnlyList<ModelObject> objects, IReadOnlyList<PaletteColor> colors, PatternSettings? current = null, string? displayName = null)
    {
        _imagePath = imagePath;
        var triangleCount = objects.Sum(item => (long)item.Triangles.Count);
        _previewTimer.Interval = TimeSpan.FromMilliseconds(triangleCount switch
        {
            > 2_000_000 => 900,
            > 250_000 => 550,
            _ => 220
        });
        displayName ??= Path.GetFileName(imagePath);
        Value = current is null ? new PatternSettings(imagePath, DisplayName: displayName) : current with { ImagePath = imagePath, DisplayName = displayName };
        MinWidth = 430;
        _mode.ItemsSource = new[] { new Option<PatternMode>("Projection frontale", PatternMode.Front), new Option<PatternMode>("Enveloppement cylindrique", PatternMode.Cylindrical), new Option<PatternMode>("Motif répété", PatternMode.Repeated), new Option<PatternMode>("Projection triplanaire", PatternMode.Triplanar) }; _mode.DisplayMemberPath = nameof(Option<PatternMode>.Label); _mode.SelectedIndex = (int)Value.Mode;
        _alignment.ItemsSource = new[] { new Option<PatternAlignment>("Horizontal", PatternAlignment.Horizontal), new Option<PatternAlignment>("Vertical", PatternAlignment.Vertical), new Option<PatternAlignment>("Circulaire", PatternAlignment.Circular), new Option<PatternAlignment>("Placement manuel", PatternAlignment.Manual) };
        _alignment.DisplayMemberPath = nameof(Option<PatternAlignment>.Label);
        _alignment.SelectedIndex = (int)Value.Alignment;
        _occurrences.AddRange(Value.Occurrences ?? []);
        var targets = new List<Option<int>> { new("Toute la figurine", -1) }; targets.AddRange(objects.Select(obj => new Option<int>(obj.ToString(), obj.Index))); _target.ItemsSource = targets; _target.DisplayMemberPath = nameof(Option<int>.Label); _target.SelectedItem = targets.FirstOrDefault(item => item.Value == Value.TargetObject) ?? targets[0];
        var logoColors = colors.Select((color, index) => new Option<int>($"{color.Name}  {color.Hex}", index)).ToList();
        _logoColor.ItemsSource = logoColors; _logoColor.DisplayMemberPath = nameof(Option<int>.Label); _logoColor.SelectedIndex = Math.Clamp(Value.LogoColorIndex, 0, Math.Max(0, logoColors.Count - 1));
        _scale.Value = Value.Scale; _rotation.Value = Value.Rotation; _offsetX.Value = Value.OffsetX; _offsetY.Value = Value.OffsetY; _variants.IsChecked = Value.FourVariants;
        _monochromeLogo.IsChecked = Value.MonochromeLogo; _invertLogo.IsChecked = Value.InvertLogo; _logoThreshold.Value = Value.LogoThreshold;
        _repeatAcrossModel.IsChecked = Value.RepeatAcrossModel;
        _stretchX.Value = Value.StretchX; _stretchY.Value = Value.StretchY; _copies.Value = Value.Copies; _spacing.Value = Value.Spacing;
        _mirrorX.IsChecked = Value.MirrorX; _mirrorY.IsChecked = Value.MirrorY; _backFace.IsChecked = Value.BackFacePreview;
        _tiltX.Value = Value.TiltX; _tiltY.Value = Value.TiltY; _relief.Value = Value.ReliefDepth;
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); Value = ReadSettings(); PreviewRequested?.Invoke(Value); };
        _mode.SelectionChanged += (_, _) => QueuePreview(); _target.SelectionChanged += (_, _) => QueuePreview(); _logoColor.SelectionChanged += (_, _) => QueuePreview(); _alignment.SelectionChanged += (_, _) => QueuePreview();
        TrackSlider(_scale); TrackSlider(_rotation); TrackSlider(_offsetX); TrackSlider(_offsetY); TrackSlider(_stretchX); TrackSlider(_stretchY); TrackSlider(_copies); TrackSlider(_spacing); TrackSlider(_logoThreshold); TrackSlider(_tiltX); TrackSlider(_tiltY); TrackSlider(_relief);
        _variants.Checked += (_, _) => QueuePreview(); _variants.Unchecked += (_, _) => QueuePreview();
        _monochromeLogo.Checked += (_, _) => { UpdateLogoControls(); QueuePreview(); }; _monochromeLogo.Unchecked += (_, _) => { UpdateLogoControls(); QueuePreview(); };
        _invertLogo.Checked += (_, _) => QueuePreview(); _invertLogo.Unchecked += (_, _) => QueuePreview();
        _repeatAcrossModel.Checked += (_, _) => QueuePreview(); _repeatAcrossModel.Unchecked += (_, _) => QueuePreview();
        _mirrorX.Checked += (_, _) => QueuePreview(); _mirrorX.Unchecked += (_, _) => QueuePreview();
        _mirrorY.Checked += (_, _) => QueuePreview(); _mirrorY.Unchecked += (_, _) => QueuePreview();
        _backFace.Checked += (_, _) => QueuePreview(); _backFace.Unchecked += (_, _) => QueuePreview();
        Loaded += (_, _) => QueuePreview();
        Unloaded += (_, _) => _previewTimer.Stop();

        var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 12) };
        panel.Children.Add(new TextBlock { Text = "MOTIF IMAGE", FontSize = 22, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Le motif sera converti vers les couleurs de filament de chaque proposition. Les pixels transparents conservent la coloration existante.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 14), Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SecondaryText"] });
        var navigationPanel = new Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.Resources["InputBackground"],
            BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["Accent"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 12)
        };
        var navigationContent = new StackPanel();
        navigationContent.Children.Add(new TextBlock
        {
            Text = "Navigation 3D : clic gauche = tourner la pièce · clic droit = déplacer la vue · molette = zoomer. Utilisez « Placer sur la pièce », puis cliquez une fois sur la surface.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 7),
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SecondaryText"]
        });
        var reset = new Button { Content = "Réinitialiser le placement", HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => ResetPlacement();
        var placementActions = new WrapPanel();
        var place = new Button { Content = "Placer sur la pièce", MinWidth = 145 };
        place.Click += (_, _) => ArmPlacement(false);
        var add = new Button { Content = "Ajouter un tampon", MinWidth = 145 };
        add.Click += (_, _) => ArmPlacement(true);
        var remove = new Button { Content = "Retirer le dernier", MinWidth = 130 };
        remove.Click += (_, _) => { if (_occurrences.Count > 0) _occurrences.RemoveAt(_occurrences.Count - 1); QueuePreview(); };
        placementActions.Children.Add(place); placementActions.Children.Add(add); placementActions.Children.Add(remove);
        navigationContent.Children.Add(placementActions);
        navigationContent.Children.Add(reset);
        navigationPanel.Child = navigationContent;
        panel.Children.Add(navigationPanel);
        var preview = new Image { Source = LoadPreview(imagePath), Height = 145, Stretch = System.Windows.Media.Stretch.Uniform, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new Border { Background = (System.Windows.Media.Brush)Application.Current.Resources["InputBackground"], BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["PanelBorder"], BorderThickness = new Thickness(1), Padding = new Thickness(8), Child = preview });
        panel.Children.Add(Field("Application", _target)); panel.Children.Add(Field("Projection", _mode)); panel.Children.Add(_repeatAcrossModel); panel.Children.Add(_variants);
        var logoPanel = new StackPanel { Margin = new Thickness(0, 12, 0, 2) };
        logoPanel.Children.Add(_monochromeLogo);
        logoPanel.Children.Add(new TextBlock { Text = "Le fond et les autres pixels conservent la couleur actuelle du modèle. Idéal pour des symboles ou des logos noirs et blancs.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 4, 0, 5), Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SecondaryText"] });
        logoPanel.Children.Add(Field("Couleur de filament du logo", _logoColor));
        logoPanel.Children.Add(_invertLogo);
        logoPanel.Children.Add(SliderField("Seuil de détection du logo", _logoThreshold, ""));
        panel.Children.Add(logoPanel);
        panel.Children.Add(SliderField("Taille du motif", _scale, "%")); panel.Children.Add(SliderField("Largeur libre", _stretchX, "%")); panel.Children.Add(SliderField("Hauteur libre", _stretchY, "%")); panel.Children.Add(SliderField("Rotation", _rotation, "°")); panel.Children.Add(SliderField("Inclinaison horizontale", _tiltX, "°")); panel.Children.Add(SliderField("Inclinaison verticale", _tiltY, "°")); panel.Children.Add(SliderField("Profondeur / relief", _relief, " mm")); panel.Children.Add(SliderField("Décalage horizontal", _offsetX, "%")); panel.Children.Add(SliderField("Décalage vertical", _offsetY, "%")); panel.Children.Add(Field("Alignement des copies", _alignment)); panel.Children.Add(SliderField("Nombre de copies", _copies, "")); panel.Children.Add(SliderField("Espacement des copies", _spacing, "%")); panel.Children.Add(_mirrorX); panel.Children.Add(_mirrorY); panel.Children.Add(_backFace);
        _previewStatus.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SecondaryText"];
        panel.Children.Add(_previewStatus);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Annuler", MinWidth = 90 };
        cancel.Click += (_, _) => Complete(false);
        var apply = new Button { Content = "Appliquer le motif", IsDefault = true, MinWidth = 160 };
        apply.Click += Apply; buttons.Children.Add(cancel); buttons.Children.Add(apply);
        var footer = new Border
        {
            Padding = new Thickness(20, 12, 20, 16),
            Background = (System.Windows.Media.Brush)Application.Current.Resources["PanelBackground"],
            BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["PanelBorder"],
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = buttons
        };
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroller, 0); layout.Children.Add(scroller);
        Grid.SetRow(footer, 1); layout.Children.Add(footer);
        Content = layout;
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true;
            Complete(false);
        };
        UpdateLogoControls();
    }

    public Task<bool> ShowEditorAsync() => _completion.Task;

    void Apply(object sender, RoutedEventArgs e)
    {
        _previewTimer.Stop();
        Value = ReadSettings();
        Complete(true);
    }

    void ResetPlacement()
    {
        _scale.Value = 100;
        _stretchX.Value = 100;
        _stretchY.Value = 100;
        _rotation.Value = 0;
        _offsetX.Value = 0;
        _offsetY.Value = 0;
        _copies.Value = 1;
        _spacing.Value = 0;
        _tiltX.Value = _tiltY.Value = _relief.Value = 0;
        _occurrences.Clear();
        _mirrorX.IsChecked = false;
        _mirrorY.IsChecked = false;
        QueuePreview();
    }

    void ArmPlacement(bool add)
    {
        if (add && _occurrences.Count == 0)
            _occurrences.Add(new PatternOccurrence(Value.AnchorU, Value.AnchorV, TargetObject: Value.TargetObject));
        _waitingForPlacement = true;
        _addingOccurrence = add;
        PlacementModeChanged?.Invoke(true);
        _previewStatus.Text = add
            ? "Cliquez sur la surface pour ajouter un tampon indépendant."
            : "Cliquez sur la surface pour placer le tampon.";
    }

    public void PlaceOnSurface(
        int objectIndex,
        double x, double y, double z,
        double ux, double uy, double uz,
        double vx, double vy, double vz,
        double nx, double ny, double nz,
        double worldSize,
        bool force = false)
    {
        if (!_waitingForPlacement && !force) return;
        if (force) _addingOccurrence = false;
        Value = ReadSettings() with
        {
            TargetObject = objectIndex,
            AnchorU = .5,
            AnchorV = .5,
            HasSurfaceFrame = true,
            SurfaceX = x, SurfaceY = y, SurfaceZ = z,
            SurfaceUx = ux, SurfaceUy = uy, SurfaceUz = uz,
            SurfaceVx = vx, SurfaceVy = vy, SurfaceVz = vz,
            SurfaceNx = nx, SurfaceNy = ny, SurfaceNz = nz,
            SurfaceWorldSize = worldSize
        };
        if (_addingOccurrence)
        {
            _occurrences.Add(new PatternOccurrence(.5, .5, TargetObject: objectIndex));
            _alignment.SelectedIndex = (int)PatternAlignment.Manual;
            _copies.Value = Math.Max(1, _occurrences.Count);
        }
        else
        {
            var targets = _target.Items.Cast<Option<int>>().ToList();
            _target.SelectedItem = targets.FirstOrDefault(item => item.Value == objectIndex) ?? targets[0];
        }
        _waitingForPlacement = false;
        PlacementModeChanged?.Invoke(false);
        _previewStatus.Text = $"Tampon placé sur l’objet {objectIndex + 1} · aperçu actualisé.";
        QueuePreview();
    }

    public void AdjustFromGizmo(double dx, double dy, double scaleFactor, double rotationDelta, bool entireGroup, double viewportWidth, double viewportHeight, bool preview)
    {
        Value = ReadSettings();
        if (Math.Abs(scaleFactor - 1) > .0001)
            _scale.Value = Math.Clamp(_scale.Value * scaleFactor, _scale.Minimum, _scale.Maximum);
        if (Math.Abs(rotationDelta) > .0001)
            _rotation.Value = Math.Clamp(_rotation.Value + rotationDelta, _rotation.Minimum, _rotation.Maximum);
        if (Math.Abs(dx) > .0001 || Math.Abs(dy) > .0001)
        {
            var du = dx / Math.Max(200, viewportWidth);
            var dv = dy / Math.Max(200, viewportHeight);
            if (_alignment.SelectedIndex == (int)PatternAlignment.Manual && _occurrences.Count > 0)
            {
                if (entireGroup)
                    for (var index = 0; index < _occurrences.Count; index++)
                        _occurrences[index] = _occurrences[index] with { U = _occurrences[index].U + du, V = _occurrences[index].V + dv };
                else
                {
                    var index = _occurrences.Count - 1;
                    _occurrences[index] = _occurrences[index] with { U = _occurrences[index].U + du, V = _occurrences[index].V + dv };
                }
            }
            else Value = Value with { AnchorU = Value.AnchorU + du, AnchorV = Value.AnchorV + dv };
        }
        Value = ReadSettings() with { AnchorU = Value.AnchorU, AnchorV = Value.AnchorV, Occurrences = _occurrences.ToArray() };
        if (preview) QueuePreview();
    }

    public void CommitGizmoPreview()
    {
        Value = ReadSettings() with { AnchorU = Value.AnchorU, AnchorV = Value.AnchorV, Occurrences = _occurrences.ToArray() };
        QueuePreview();
    }

    public void DuplicateSelected(int total, bool entireGroup)
    {
        Value = ReadSettings();
        total = Math.Clamp(total, 1, 64);
        var sources = entireGroup && _occurrences.Count > 0
            ? _occurrences.ToArray()
            : [_occurrences.LastOrDefault() ?? new PatternOccurrence(Value.AnchorU, Value.AnchorV, TargetObject: Value.TargetObject)];
        _occurrences.Clear();
        for (var copy = 0; copy < total && _occurrences.Count < 64; copy++)
            foreach (var source in sources)
            {
                if (_occurrences.Count >= 64) break;
                _occurrences.Add(source with { U = source.U + copy * .04, V = source.V + copy * .04 });
            }
        _alignment.SelectedIndex = (int)PatternAlignment.Manual;
        _copies.Value = Math.Min(_copies.Maximum, _occurrences.Count);
        Value = ReadSettings() with { Occurrences = _occurrences.ToArray() };
        _previewStatus.Text = $"{_occurrences.Count} logo(s) dans le groupe · clic droit pour recommencer.";
        QueuePreview();
    }

    void Complete(bool accepted)
    {
        if (_completed) return;
        if (accepted)
        {
            Value = ReadSettings();
            if (!Value.HasSurfaceFrame && !Value.RepeatAcrossModel)
            {
                _previewStatus.Text = "Placez d’abord le logo sur la pièce, puis validez le motif.";
                ArmPlacement(false);
                return;
            }
        }
        _completed = true;
        _previewTimer.Stop();
        _completion.TrySetResult(accepted);
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
        RepeatAcrossModel: _repeatAcrossModel.IsChecked == true,
        StretchX: _stretchX.Value,
        StretchY: _stretchY.Value,
        Copies: (int)Math.Round(_copies.Value),
        Spacing: _spacing.Value,
        MirrorX: _mirrorX.IsChecked == true,
        MirrorY: _mirrorY.IsChecked == true,
        BackFacePreview: _backFace.IsChecked == true,
        AnchorU: Value.AnchorU,
        AnchorV: Value.AnchorV,
        TiltX: _tiltX.Value,
        TiltY: _tiltY.Value,
        ReliefDepth: _relief.Value,
        Alignment: ((Option<PatternAlignment>)_alignment.SelectedItem).Value,
        Occurrences: _occurrences.ToArray(),
        LocalSubdivision: true,
        HasSurfaceFrame: Value.HasSurfaceFrame,
        SurfaceX: Value.SurfaceX,
        SurfaceY: Value.SurfaceY,
        SurfaceZ: Value.SurfaceZ,
        SurfaceUx: Value.SurfaceUx,
        SurfaceUy: Value.SurfaceUy,
        SurfaceUz: Value.SurfaceUz,
        SurfaceVx: Value.SurfaceVx,
        SurfaceVy: Value.SurfaceVy,
        SurfaceVz: Value.SurfaceVz,
        SurfaceNx: Value.SurfaceNx,
        SurfaceNy: Value.SurfaceNy,
        SurfaceNz: Value.SurfaceNz,
        SurfaceWorldSize: Value.SurfaceWorldSize);

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
