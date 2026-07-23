using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using SystemColors = System.Windows.SystemColors;

namespace PolyChrom3MF.App;

public partial class MainWindow : Window
{
    readonly ThreeMfService _service = new();
    readonly StlService _stlService = new();
    readonly PaletteService _palettes = new();
    readonly PatternService _patternService = new();
    readonly ProjectService _projects = new();
    readonly SettingsService _settingsService = new();
    readonly SlicerDetectionService _slicerDetection = new();
    readonly UpdateService _updateService = new();
    readonly Dictionary<GeometryModel3D, int> _modelObjects = [];
    readonly Dictionary<int, HashSet<int>> _paintSelection = [];
    readonly Dictionary<int, Dictionary<(int A, int B, int C), int>> _triangleLookup = [];
    readonly Stack<EditorState> _undo = [];
    readonly Stack<EditorState> _redo = [];
    AppSettings _settings;
    readonly bool _settingsExistedAtStartup;
    ModelDocument? _doc;
    List<ColorProposal> _proposals = [];
    List<ColorProposal>? _beforePatternProposals;
    ColorProposal? _selected;
    PatternSettings? _pattern;
    System.Windows.Point _last;
    double _yaw = -40, _pitch = -25, _zoom = 1;
    bool _grid = true, _perspective = true, _dirty, _loadingControls, _funMode = true, _automaticUpdateChecked, _paintStroke, _paintStrokeChanged, _shutdownForUpdate;
    long _lastPaintSample;
    int _generation, _colorCount = 4;
    string? _lastSlicerFile;
    Point3D _center;
    double _radius = 100;

    public MainWindow()
    {
        InitializeComponent();
        ApplyStandardMenuColors(MainMenu);
        _settingsExistedAtStartup = File.Exists(_settingsService.FilePath);
        _settings = _settingsService.Load();
        if (!_settingsExistedAtStartup)
        {
            _settings.LastSeenVersion = UpdateService.CurrentVersion().ToString(3);
            _settings.LastReleaseNotes = UpdateService.BundledReleaseNotes;
            _settingsService.Save(_settings);
        }
        _colorCount = Math.Clamp(_settings.ColorCount, 4, 32);
        EnsurePreferredSlicer();
        BuildScene();
        ApplyTheme();
        Loaded += async (_, _) =>
        {
            ShowWhatsNewAfterUpdate();
            var startupFile = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
            if (startupFile is null)
            {
                if (_settings.ShowWelcome) ShowWelcome();
                ChooseColorCount(false);
            }
            if (startupFile is not null && new[] { ".3mf", ".stl", ".poly3mf" }.Contains(Path.GetExtension(startupFile), StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetExtension(startupFile).Equals(".poly3mf", StringComparison.OrdinalIgnoreCase)) await LoadProject(startupFile);
                else await LoadModel(startupFile);
            }
            if (!_automaticUpdateChecked) { _automaticUpdateChecked = true; await CheckForUpdatesAsync(true); }
        };
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Modèles 3D (*.3mf;*.stl)|*.3mf;*.stl|Fichiers 3MF (*.3mf)|*.3mf|Fichiers STL (*.stl)|*.stl", CheckFileExists = true };
        if (dialog.ShowDialog() == true && ConfirmDiscard()) await LoadModel(dialog.FileName);
    }

    async Task<bool> LoadModel(string path)
    {
        SetBusy(true, "Analyse du fichier 3MF en cours…");
        try
        {
            _doc = await Task.Run(() => Path.GetExtension(path).Equals(".stl", StringComparison.OrdinalIgnoreCase) ? _stlService.Read(path) : _service.Read(path));
            _pattern = null; _beforePatternProposals = null; _paintSelection.Clear(); _triangleLookup.Clear(); UpdatePatternText(); UpdatePaintSelectionText();
            _lastSlicerFile = path; OpenSlicerButton.IsEnabled = true; UpdateSlicerButton();
            FileText.Text = Path.GetFileName(path);
            InfoText.Text = $"Format : {_doc.SourceFormat}\n{_doc.Objects.Count} objet(s) · {_doc.TriangleCount:N0} triangles\nUnité : {_doc.Unit}\nComposants : {_doc.ComponentCount}";
            DimensionsText.Text = $"{_doc.SizeX:0.##} × {_doc.SizeY:0.##} × {_doc.SizeZ:0.##} mm";
            ObjectsList.ItemsSource = _doc.Objects;
            ObjectsList.SelectedIndex = 0;
            _generation = 0;
            GenerateProposals();
            HintText.Visibility = Visibility.Collapsed;
            StatsText.Text = $"{_doc.Objects.Count} objets · {_doc.TriangleCount:N0} triangles · {_selected?.Colors.Count ?? 0} couleurs";
            ComputeBounds();
            FitCamera();
            Render();
            _dirty = false;
            _undo.Clear(); _redo.Clear();
            StatusText.Text = _doc.Warning ?? "Analyse terminée.";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Import impossible", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Erreur d’importation.";
            return false;
        }
        finally { SetBusy(false); }
    }

    void SetBusy(bool busy, string? text = null)
    {
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = busy;
        if (text is not null) StatusText.Text = text;
        SetActivity(busy, text);
        IsEnabled = !busy;
    }

    void SetActivity(bool visible, string? text = null, bool determinate = false, double value = 0)
    {
        ActivityOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        if (!string.IsNullOrWhiteSpace(text)) ActivityText.Text = text;
        ActivityProgress.IsIndeterminate = !determinate;
        ActivityProgress.Value = Math.Clamp(value, 0, 100);
    }

    static void ApplyStandardMenuColors(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>()) { item.Foreground = System.Windows.Media.Brushes.Black; ApplyStandardMenuColors(item); }
    }

    void GenerateProposals()
    {
        if (_doc is null) return;
        _pattern = null; _beforePatternProposals = null; _paintSelection.Clear(); UpdatePatternText(); UpdatePaintSelectionText();
        var custom = UseFilaments.IsChecked == true ? _settings.FilamentColors : null;
        _proposals = _palettes.Create(_doc, custom, _generation, _funMode, _colorCount);
        ProposalsTitle.Text = $"4 PROPOSITIONS · {_colorCount} COULEURS";
        SelectProposal(0);
        Proposals.ItemsSource = null; Proposals.ItemsSource = _proposals;
        ApplyButton.IsEnabled = true;
    }

    void SelectProposal(int index)
    {
        if (_proposals.Count == 0) return;
        _selected = _proposals[Math.Clamp(index, 0, _proposals.Count - 1)];
        _loadingControls = true;
        ObjectColorCombo.ItemsSource = _selected.Colors;
        var paintColor = Math.Max(0, PaintColorCombo.SelectedIndex); PaintColorCombo.ItemsSource = _selected.Colors; PaintColorCombo.SelectedIndex = Math.Min(paintColor, _selected.Colors.Count - 1);
        if (ObjectsList.SelectedIndex >= 0)
            ObjectColorCombo.SelectedIndex = _selected.Assignments.GetValueOrDefault(ObjectsList.SelectedIndex, 0);
        _loadingControls = false;
    }

    void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1 || !new[] { ".3mf", ".stl", ".poly3mf" }.Contains(Path.GetExtension(files[0]), StringComparer.OrdinalIgnoreCase))
        {
            MessageBox.Show("Déposez un unique fichier .3mf, .stl ou .poly3mf.", "Format non pris en charge");
            return;
        }
        if (ConfirmDiscard())
        {
            if (Path.GetExtension(files[0]).Equals(".poly3mf", StringComparison.OrdinalIgnoreCase)) _ = LoadProject(files[0]);
            else _ = LoadModel(files[0]);
        }
    }

    void Proposal_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ColorProposal proposal) return;
        SelectProposal(_proposals.IndexOf(proposal));
        Render();
        StatusText.Text = $"{proposal.Name} sélectionnée.";
    }

    void Regenerate_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        PushUndo(); _generation++; GenerateProposals(); Render(); _dirty = true; StatusText.Text = $"Quatre nouvelles propositions générées (série {_generation + 1}).";
    }

    void ColorCount_Click(object sender, RoutedEventArgs e) => ChooseColorCount(true);

    void ChooseColorCount(bool regenerate)
    {
        var dialog = new ColorCountWindow(_colorCount) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var selected = dialog.ColorCount; if (selected == _colorCount) return;
        if (_doc is not null && regenerate) PushUndo();
        _colorCount = selected; _settings.ColorCount = selected; _settingsService.Save(_settings);
        ProposalsTitle.Text = $"4 PROPOSITIONS · {_colorCount} COULEURS";
        if (_doc is not null) { _generation++; GenerateProposals(); Render(); StatsText.Text = $"{_doc.Objects.Count} objets · {_doc.TriangleCount:N0} triangles · {_colorCount} couleurs"; _dirty = true; StatusText.Text = $"Quatre propositions générées avec {_colorCount} couleurs."; }
    }

    void FunMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingControls) return;
        _funMode = FunMode.IsChecked == true;
        if (_doc is null) return;
        PushUndo(); _generation++; GenerateProposals(); Render(); _dirty = true;
        StatusText.Text = _funMode ? "Quatre nouveaux motifs fun générés." : "Quatre styles classiques générés.";
    }

    void Filaments_Changed(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        PushUndo(); GenerateProposals(); Render(); _dirty = true;
    }

    void ManageFilaments_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        if (!_settings.FilamentColors.Contains(hex, StringComparer.OrdinalIgnoreCase)) _settings.FilamentColors.Add(hex);
        _settingsService.Save(_settings);
        if (UseFilaments.IsChecked == true && _doc is not null) { PushUndo(); GenerateProposals(); Render(); }
        StatusText.Text = $"Couleur de filament {hex} enregistrée localement.";
    }

    void EditSelectedColor_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || ObjectColorCombo.SelectedIndex < 0) return;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        var current = _selected.Colors[ObjectColorCombo.SelectedIndex].Color;
        dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        PushUndo();
        var index = ObjectColorCombo.SelectedIndex;
        var hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        _selected.Colors[index] = new PaletteColor("Personnalisée", hex);
        HexColorText.Text = hex;
        RefreshBindings(); Render(); _dirty = true;
    }

    void ApplyHex_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || ObjectColorCombo.SelectedIndex < 0) return;
        var value = HexColorText.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$")) { MessageBox.Show("Saisissez un code au format #RRGGBB.", "Couleur invalide"); return; }
        PushUndo(); var index = ObjectColorCombo.SelectedIndex; _selected.Colors[index] = new PaletteColor("Personnalisée", value.ToUpperInvariant()); RefreshBindings(); ObjectColorCombo.SelectedIndex = index; Render(); _dirty = true;
    }

    void ObjectColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls || _selected is null || ObjectsList.SelectedIndex < 0 || ObjectColorCombo.SelectedIndex < 0) return;
        PushUndo();
        _selected.Assignments[ObjectsList.SelectedIndex] = ObjectColorCombo.SelectedIndex;
        if (_selected.TriangleAssignments.TryGetValue(ObjectsList.SelectedIndex, out var triangleColors)) Array.Fill(triangleColors, ObjectColorCombo.SelectedIndex);
        Render(); _dirty = true;
    }

    void ObjectsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected is null || ObjectsList.SelectedIndex < 0) return;
        _loadingControls = true;
        ObjectColorCombo.SelectedIndex = _selected.Assignments.GetValueOrDefault(ObjectsList.SelectedIndex, 0);
        if (ObjectColorCombo.SelectedItem is PaletteColor color) HexColorText.Text = color.Hex;
        _loadingControls = false;
        Render();
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        StatusText.Text = $"{_selected.Name} appliquée. Le modèle est prêt à être exporté.";
        _dirty = true;
    }

    async void ImportPattern_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _selected is null) { MessageBox.Show("Importez d’abord un modèle 3MF ou STL.", "Motif image"); return; }
        var file = new OpenFileDialog { Filter = "Images compatibles (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Images PNG (*.png)|*.png|Images JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg", CheckFileExists = true, Title = "Choisir le motif à appliquer" };
        if (file.ShowDialog() != true) return;
        PatternWindow dialog;
        string preparedImage;
        try
        {
            PatternService.ValidateImage(file.FileName);
            var options = new ImageImportOptionsWindow(file.FileName) { Owner = this };
            if (options.ShowDialog() != true) return;
            // Read every WPF control value on the UI thread before starting image processing.
            var removeBackground = options.RemoveBackground;
            var tolerance = options.Tolerance;
            var sourceImage = file.FileName;
            preparedImage = await Task.Run(() => PatternService.PrepareImage(sourceImage, removeBackground, tolerance));
            dialog = new PatternWindow(preparedImage, _doc.Objects, _pattern, Path.GetFileName(file.FileName)) { Owner = this };
            if (dialog.ShowDialog() != true) return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Image impossible à ouvrir", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var selectedIndex = Math.Max(0, _proposals.IndexOf(_selected));
        PushUndo();
        var previousBeforePattern = _beforePatternProposals;
        _beforePatternProposals ??= _proposals.Select(Clone).ToList();
        var working = _beforePatternProposals.Select(Clone).ToList();
        var settings = dialog.Value;
        SetBusy(true, "Application du motif image sur les triangles…");
        try
        {
            var results = await Task.Run(() =>
            {
                var list = new List<PatternApplyResult>();
                if (settings.FourVariants)
                {
                    var modes = new[] { PatternMode.Front, PatternMode.Cylindrical, PatternMode.Repeated, PatternMode.Triplanar };
                    for (var i = 0; i < Math.Min(working.Count, modes.Length); i++)
                    {
                        list.Add(_patternService.Apply(_doc, working[i], settings, modes[i]));
                        working[i] = Rename(working[i], $"Image {i + 1} — {PatternModeName(modes[i])}", $"Motif {Path.GetFileName(file.FileName)} · {PatternModeName(modes[i]).ToLowerInvariant()}");
                    }
                }
                else { list.Add(_patternService.Apply(_doc, working[selectedIndex], settings)); working[selectedIndex] = Rename(working[selectedIndex], $"Image — {PatternModeName(settings.Mode)}", $"Motif {Path.GetFileName(file.FileName)} · {PatternModeName(settings.Mode).ToLowerInvariant()}"); }
                return list;
            });
            _proposals = working; _pattern = settings; SelectProposal(selectedIndex); RefreshBindings(); Render(); UpdatePatternText(); _dirty = true;
            var count = results.Sum(result => result.ColoredTriangles);
            StatusText.Text = $"Motif image appliqué sur {count:N0} triangles avec {_colorCount} couleurs imprimables.";
        }
        catch (Exception ex)
        {
            _beforePatternProposals = previousBeforePattern;
            MessageBox.Show(ex.Message, "Motif image impossible à appliquer", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Échec de l’application du motif image.";
        }
        finally { SetBusy(false); }
    }

    void RemovePattern_Click(object sender, RoutedEventArgs e)
    {
        if (_pattern is null) { StatusText.Text = "Aucun motif image à retirer."; return; }
        PushUndo(); var selectedIndex = Math.Max(0, _proposals.IndexOf(_selected!));
        if (_beforePatternProposals is not null) _proposals = _beforePatternProposals.Select(Clone).ToList();
        else { _generation++; GenerateProposals(); }
        _pattern = null; _beforePatternProposals = null; SelectProposal(Math.Min(selectedIndex, _proposals.Count - 1)); RefreshBindings(); Render(); UpdatePatternText(); _dirty = true; StatusText.Text = "Motif image retiré.";
    }

    static ColorProposal Rename(ColorProposal proposal, string name, string description) => new(name, description, proposal.Colors.Select(color => new PaletteColor(color.Name, color.Hex)).ToList(), new Dictionary<int, int>(proposal.Assignments)) { TriangleAssignments = proposal.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone()) };
    static string PatternModeName(PatternMode mode) => mode switch { PatternMode.Front => "Projection frontale", PatternMode.Cylindrical => "Enveloppement", PatternMode.Repeated => "Motif répété", _ => "Triplanaire" };
    void UpdatePatternText() { if (PatternText is null) return; PatternText.Text = _pattern is null ? "Aucun motif importé" : $"{(_pattern.DisplayName ?? Path.GetFileName(_pattern.ImagePath))} · {(_pattern.FourVariants ? "4 projections" : PatternModeName(_pattern.Mode))} · taille {_pattern.Scale:0}%"; }
    void PaintMode_Changed(object sender, RoutedEventArgs e)
    {
        if (PaintMode is null || PaintModeMenu is null) return;
        var enabled = sender == PaintModeMenu ? PaintModeMenu.IsChecked : PaintMode.IsChecked == true;
        if (PaintMode.IsChecked != enabled) PaintMode.IsChecked = enabled;
        if (PaintModeMenu.IsChecked != enabled) PaintModeMenu.IsChecked = enabled;
        if (!enabled)
        {
            _paintStroke = false;
            Viewer?.ReleaseMouseCapture();
        }
        StatusText.Text = enabled ? "Mode zones actif : choisissez Face par face ou Pinceau fluide. Clic droit pour tourner." : "Sélection de zones désactivée.";
    }

    async Task SelectPaintZone(RayMeshGeometry3DHitTestResult hit, GeometryModel3D model)
    {
        if (_doc is null || !_modelObjects.TryGetValue(model, out var objectIndex)) return;
        var obj = _doc.Objects.FirstOrDefault(item => item.Index == objectIndex); if (obj is null) return;
        var sourceTriangle = FindSourceTriangle(obj, hit);
        if (sourceTriangle < 0) return;
        var brushScale = PaintTool.SelectedIndex == 0 ? 0 : PaintBrushSize.SelectedIndex switch { 0 => .0015, 1 => .003, 2 => .008, 3 => .02, _ => .05 };
        SetBusy(true, "Sélection de la zone…");
        try
        {
            HashSet<int> selected;
            if (brushScale == 0) selected = [sourceTriangle];
            else
            {
                var diagonal = Math.Sqrt(Math.Pow(obj.Vertices.Max(v => v.X) - obj.Vertices.Min(v => v.X), 2) + Math.Pow(obj.Vertices.Max(v => v.Y) - obj.Vertices.Min(v => v.Y), 2) + Math.Pow(obj.Vertices.Max(v => v.Z) - obj.Vertices.Min(v => v.Z), 2));
                selected = await Task.Run(() => SelectNearbyTriangles(obj, sourceTriangle, hit.PointHit, Math.Max(.0001, diagonal * brushScale)));
            }
            if (!_paintSelection.TryGetValue(objectIndex, out var current)) _paintSelection[objectIndex] = current = [];
            foreach (var triangle in selected) current.Add(triangle);
            UpdatePaintSelectionText(); Render(); StatusText.Text = $"Zone ajoutée · {_paintSelection.Values.Sum(set => set.Count):N0} triangles sélectionnés.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Sélection de zone impossible", MessageBoxButton.OK, MessageBoxImage.Warning); StatusText.Text = "La zone n’a pas pu être sélectionnée."; }
        finally { SetBusy(false); }
    }

    internal static HashSet<int> SelectNearbyTriangles(ModelObject obj, int sourceTriangle, Point3D center, double radius)
    {
        var source = obj.Triangles[sourceTriangle]; var sa = obj.Vertices[source.A]; var sb = obj.Vertices[source.B]; var sc = obj.Vertices[source.C]; var sourceNormal = Normal(sa, sb, sc); var radiusSquared = radius * radius;
        var selected = new HashSet<int> { sourceTriangle };
        for (var i = 0; i < obj.Triangles.Count; i++)
        {
            var triangle = obj.Triangles[i]; var a = obj.Vertices[triangle.A]; var b = obj.Vertices[triangle.B]; var c = obj.Vertices[triangle.C];
            var x = (a.X + b.X + c.X) / 3; var y = (a.Y + b.Y + c.Y) / 3; var z = (a.Z + b.Z + c.Z) / 3;
            var dx = x - center.X; var dy = y - center.Y; var dz = z - center.Z;
            if (dx * dx + dy * dy + dz * dz > radiusSquared) continue;
            var normal = Normal(a, b, c); if (Math.Abs(Vector3D.DotProduct(sourceNormal, normal)) >= .35) selected.Add(i);
        }
        return selected;
    }

    static Vector3D Normal(Vertex a, Vertex b, Vertex c)
    {
        var normal = Vector3D.CrossProduct(new Vector3D(b.X - a.X, b.Y - a.Y, b.Z - a.Z), new Vector3D(c.X - a.X, c.Y - a.Y, c.Z - a.Z));
        if (normal.LengthSquared > 0) normal.Normalize(); return normal;
    }
    int FindSourceTriangle(ModelObject obj, RayMeshGeometry3DHitTestResult hit)
    {
        if (!_triangleLookup.TryGetValue(obj.Index, out var lookup))
        {
            lookup = new Dictionary<(int, int, int), int>(obj.Triangles.Count);
            for (var i = 0; i < obj.Triangles.Count; i++)
            {
                var triangle = obj.Triangles[i];
                lookup[TriangleKey(triangle.A, triangle.B, triangle.C)] = i;
            }
            _triangleLookup[obj.Index] = lookup;
        }
        return lookup.GetValueOrDefault(TriangleKey(hit.VertexIndex1, hit.VertexIndex2, hit.VertexIndex3), -1);
    }

    static (int A, int B, int C) TriangleKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    void ApplyPaintSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _selected is null || _selected.Colors.Count == 0 || _paintSelection.Count == 0) { MessageBox.Show("Activez la sélection de zones et cliquez sur le modèle avant d’appliquer une couleur.", "Coloration manuelle"); return; }
        var colorIndex = Math.Clamp(PaintColorCombo.SelectedIndex, 0, _selected.Colors.Count - 1); PushUndo();
        foreach (var pair in _paintSelection)
        {
            var obj = _doc!.Objects.First(item => item.Index == pair.Key);
            if (!_selected.TriangleAssignments.TryGetValue(pair.Key, out var assignments) || assignments.Length != obj.Triangles.Count) { assignments = new int[obj.Triangles.Count]; Array.Fill(assignments, _selected.Assignments.GetValueOrDefault(pair.Key, 0)); _selected.TriangleAssignments[pair.Key] = assignments; }
            foreach (var triangle in pair.Value.Where(index => index >= 0 && index < assignments.Length)) assignments[triangle] = colorIndex;
        }
        var count = _paintSelection.Values.Sum(set => set.Count); _paintSelection.Clear(); UpdatePaintSelectionText(); Render(); RefreshBindings(); _dirty = true; StatusText.Text = $"{_selected.Colors[colorIndex].Name} appliquée sur {count:N0} triangles.";
    }

    void ClearPaintSelection_Click(object sender, RoutedEventArgs e) { _paintSelection.Clear(); UpdatePaintSelectionText(); Render(); StatusText.Text = "Sélection de zones effacée."; }
    void UpdatePaintSelectionText() { if (PaintSelectionText is null) return; var count = _paintSelection.Values.Sum(set => set.Count); PaintSelectionText.Text = $"{count:N0} triangle{(count > 1 ? "s" : "")} sélectionné{(count > 1 ? "s" : "")}"; }

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _selected is null) { MessageBox.Show("Importez et sélectionnez une proposition avant l’export."); return; }
        var dialog = new SaveFileDialog { Filter = "Fichiers 3MF (*.3mf)|*.3mf", FileName = Path.GetFileNameWithoutExtension(_doc.Path) + $"_{_colorCount}Couleurs.3mf", InitialDirectory = Directory.Exists(_settings.ExportFolder) ? _settings.ExportFolder : null, OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        SetBusy(true, "Exportation et vérification du fichier 3MF…");
        try
        {
            var report = await Task.Run(() => _service.ExportAndValidate(_doc, _selected, dialog.FileName, _settings.VerifyAfterExport));
            StatusText.Text = _settings.VerifyAfterExport ? "Export terminé et vérifié avec succès." : "Export terminé avec succès.";
            _lastSlicerFile = dialog.FileName; OpenSlicerButton.IsEnabled = true; UpdateSlicerButton();
            var slicerName = PreferredSlicerName();
            var action = MessageBox.Show($"Export réussi.\n\n{report}\n\nOuvrir le fichier dans {slicerName} ?", "PolyChrom 3MF", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (_settings.OpenFolderAfterExport) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
            if (action == MessageBoxResult.Yes) OpenInSlicer(dialog.FileName);
            _dirty = false;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Échec de l’export", MessageBoxButton.OK, MessageBoxImage.Error); StatusText.Text = "Échec de l’export."; }
        finally { SetBusy(false); }
    }

    void OpenInSlicer(string path)
    {
        EnsurePreferredSlicer();
        var slicer = _settings.PreferredSlicer;
        if (!File.Exists(slicer)) { MessageBox.Show("Aucun slicer n’a été détecté. Choisissez-en un dans Paramètres > Slicer préféré."); return; }
        try { Process.Start(new ProcessStartInfo(slicer) { UseShellExecute = true, ArgumentList = { path } }); StatusText.Text = $"{Path.GetFileName(path)} ouvert dans {PreferredSlicerName()}."; }
        catch (Exception ex) { MessageBox.Show($"Impossible d’ouvrir {PreferredSlicerName()}.\n\n{ex.Message}", "Slicer impossible à lancer", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void OpenPreferredSlicer_Click(object sender, RoutedEventArgs e)
    {
        var path = File.Exists(_lastSlicerFile) ? _lastSlicerFile : _doc?.Path;
        if (path is null || !File.Exists(path)) { MessageBox.Show("Importez ou exportez d’abord un fichier 3MF ou STL."); return; }
        OpenInSlicer(path);
    }

    void EnsurePreferredSlicer()
    {
        if (File.Exists(_settings.PreferredSlicer)) { UpdateSlicerButton(); return; }
        var detected = _slicerDetection.Detect();
        _settings.PreferredSlicer = detected.FirstOrDefault()?.Path ?? "";
        if (_settings.PreferredSlicer.Length > 0) _settingsService.Save(_settings);
        UpdateSlicerButton();
    }

    string PreferredSlicerName() => _slicerDetection.Detect([_settings.PreferredSlicer]).FirstOrDefault(x => string.Equals(x.Path, _settings.PreferredSlicer, StringComparison.OrdinalIgnoreCase))?.Name ?? "le slicer préféré";
    void UpdateSlicerButton() { if (OpenSlicerButton is null) return; OpenSlicerButton.Content = File.Exists(_settings.PreferredSlicer) ? $"Ouvrir dans {PreferredSlicerName()}" : "Choisir un slicer…"; OpenSlicerButton.ToolTip = _settings.PreferredSlicer; }

    void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) { MessageBox.Show("Aucun modèle à enregistrer."); return; }
        var dialog = new SaveFileDialog { Filter = "Projet PolyChrom (*.poly3mf)|*.poly3mf", FileName = Path.GetFileNameWithoutExtension(_doc.Path) + ".poly3mf" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _projects.Save(dialog.FileName, _doc, _proposals, _proposals.IndexOf(_selected!), _yaw, _pitch, _zoom, _generation, _funMode, _colorCount, _pattern);
            _dirty = false; StatusText.Text = "Projet portable enregistré : modèle et styles sont réunis dans un seul fichier.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Projet impossible à enregistrer", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Projet PolyChrom (*.poly3mf)|*.poly3mf" };
        if (dialog.ShowDialog() != true || !ConfirmDiscard()) return;
        await LoadProject(dialog.FileName);
    }

    async Task LoadProject(string projectPath)
    {
        try
        {
            var project = _projects.Load(projectPath);
            if (!File.Exists(project.SourcePath)) throw new FileNotFoundException("Le fichier 3MF ou STL source du projet est introuvable.", project.SourcePath);
            if (!await LoadModel(project.SourcePath)) return;
            ProjectService.ValidateForDocument(project, _doc!);
            _generation = project.Generation; _funMode = project.FunMode; _colorCount = Math.Clamp(project.ColorCount, 4, 32); _settings.ColorCount = _colorCount;
            _loadingControls = true; FunMode.IsChecked = _funMode; _loadingControls = false;
            GenerateProposals();
            for (var p = 0; p < Math.Min(4, project.Proposals.Count); p++)
            {
                _proposals[p].Colors.Clear();
                _proposals[p].Colors.AddRange(project.Proposals[p].Select((hex, i) => new PaletteColor("Projet " + (i + 1), hex)));
                if (p < project.Assignments.Count) { _proposals[p].Assignments.Clear(); foreach (var pair in project.Assignments[p]) _proposals[p].Assignments[pair.Key] = pair.Value; }
                if (project.TriangleAssignments is not null && p < project.TriangleAssignments.Count)
                {
                    _proposals[p].TriangleAssignments.Clear();
                    foreach (var pair in project.TriangleAssignments[p]) _proposals[p].TriangleAssignments[pair.Key] = (int[])pair.Value.Clone();
                }
                if (project.ProposalNames is not null || project.ProposalDescriptions is not null)
                {
                    var proposal = _proposals[p];
                    _proposals[p] = Rename(proposal, project.ProposalNames?.ElementAtOrDefault(p) ?? proposal.Name, project.ProposalDescriptions?.ElementAtOrDefault(p) ?? proposal.Description);
                }
            }
            _pattern = project.Pattern; _beforePatternProposals = null; UpdatePatternText();
            _yaw = project.Yaw; _pitch = project.Pitch; _zoom = project.Zoom; SelectProposal(project.SelectedProposal); RefreshBindings(); Render(); _dirty = false;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Projet impossible à ouvrir", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _doc = null; _lastSlicerFile = null; _pattern = null; _beforePatternProposals = null; _paintSelection.Clear(); _triangleLookup.Clear(); _undo.Clear(); _redo.Clear(); UpdatePatternText(); UpdatePaintSelectionText(); _proposals.Clear(); _selected = null; ObjectsList.ItemsSource = null; Proposals.ItemsSource = null; HintText.Visibility = Visibility.Visible; FileText.Text = "Aucun fichier chargé"; InfoText.Text = DimensionsText.Text = StatsText.Text = ""; ApplyButton.IsEnabled = false; OpenSlicerButton.IsEnabled = false; _dirty = false; Render();
    }

    bool ConfirmDiscard() => !_dirty || MessageBox.Show("Les modifications non enregistrées seront perdues. Continuer ?", "PolyChrom 3MF", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    void PushUndo() { if (_proposals.Count == 0) return; _undo.Push(Capture()); _redo.Clear(); }
    EditorState Capture() => new(_proposals.IndexOf(_selected!), _generation, _funMode, _colorCount, _proposals.Select(Clone).ToList(), _pattern);
    static ColorProposal Clone(ColorProposal p) => new(p.Name, p.Description, p.Colors.Select(c => new PaletteColor(c.Name, c.Hex)).ToList(), new Dictionary<int, int>(p.Assignments)) { TriangleAssignments = p.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone()) };
    void Restore(EditorState state) { _generation = state.Generation; _funMode = state.FunMode; _colorCount = state.ColorCount; _pattern = state.Pattern; _beforePatternProposals = null; _paintSelection.Clear(); UpdatePatternText(); UpdatePaintSelectionText(); ProposalsTitle.Text = $"4 PROPOSITIONS · {_colorCount} COULEURS"; _loadingControls = true; FunMode.IsChecked = _funMode; _loadingControls = false; _proposals = state.Proposals.Select(Clone).ToList(); SelectProposal(state.Selected); RefreshBindings(); Render(); _dirty = true; }
    void Undo_Click(object sender, RoutedEventArgs e) { if (_undo.Count == 0) return; _redo.Push(Capture()); Restore(_undo.Pop()); StatusText.Text = "Modification annulée."; }
    void Redo_Click(object sender, RoutedEventArgs e) { if (_redo.Count == 0) return; _undo.Push(Capture()); Restore(_redo.Pop()); StatusText.Text = "Modification rétablie."; }
    void RefreshBindings() { Proposals.ItemsSource = null; Proposals.ItemsSource = _proposals; ObjectColorCombo.ItemsSource = null; ObjectColorCombo.ItemsSource = _selected?.Colors; PaintColorCombo.ItemsSource = null; PaintColorCombo.ItemsSource = _selected?.Colors; if (_selected is not null && _selected.Colors.Count > 0) PaintColorCombo.SelectedIndex = 0; }

    void BuildScene()
    {
        Viewer.Children.Add(new ModelVisual3D { Content = new AmbientLight(Color.FromRgb(145, 145, 145)) });
        Viewer.Children.Add(new ModelVisual3D { Content = new DirectionalLight(Colors.White, new Vector3D(-1, -1, -2)) });
        UpdateCamera();
    }

    void ComputeBounds()
    {
        if (_doc is null) return;
        var vertices = _doc.Objects.SelectMany(o => o.Vertices).ToArray();
        var minX = vertices.Min(v => v.X); var maxX = vertices.Max(v => v.X); var minY = vertices.Min(v => v.Y); var maxY = vertices.Max(v => v.Y); var minZ = vertices.Min(v => v.Z); var maxZ = vertices.Max(v => v.Z);
        _center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        _radius = Math.Max(1, Math.Sqrt(_doc.SizeX * _doc.SizeX + _doc.SizeY * _doc.SizeY + _doc.SizeZ * _doc.SizeZ) / 2);
    }

    void Render()
    {
        while (Viewer.Children.Count > 2) Viewer.Children.RemoveAt(2);
        _modelObjects.Clear();
        if (_doc is null) return;
        if (_grid) AddPlate();
        foreach (var obj in _doc.Objects)
        {
            var positions = new Point3DCollection(obj.Vertices.Select(v => new Point3D(v.X, v.Y, v.Z)));
            positions.Freeze();
            var triangleColors = _selected?.TriangleAssignments.GetValueOrDefault(obj.Index);
            var colorCount = Math.Max(1, _selected?.Colors.Count ?? 1);
            var capacity = Math.Max(3, obj.Triangles.Count * 3 / colorCount);
            var indicesByColor = Enumerable.Range(0, colorCount).Select(_ => new Int32Collection(capacity)).ToArray();
            var selectedIndices = new Int32Collection();
            for (var triangleIndex = 0; triangleIndex < obj.Triangles.Count; triangleIndex++)
            {
                var triangle = obj.Triangles[triangleIndex];
                if (_paintSelection.TryGetValue(obj.Index, out var selectedTriangles) && selectedTriangles.Contains(triangleIndex)) { selectedIndices.Add(triangle.A); selectedIndices.Add(triangle.B); selectedIndices.Add(triangle.C); continue; }
                var assigned = triangleColors is not null && triangleIndex < triangleColors.Length ? triangleColors[triangleIndex] : _selected?.Assignments.GetValueOrDefault(obj.Index, 0) ?? 0;
                assigned = Math.Clamp(assigned, 0, colorCount - 1);
                indicesByColor[assigned].Add(triangle.A); indicesByColor[assigned].Add(triangle.B); indicesByColor[assigned].Add(triangle.C);
            }
            for (var colorIndex = 0; colorIndex < colorCount; colorIndex++)
            {
                if (indicesByColor[colorIndex].Count == 0) continue;
                indicesByColor[colorIndex].Freeze();
                var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indicesByColor[colorIndex] };
                mesh.Freeze();
                var color = _selected?.Colors.ElementAtOrDefault(colorIndex)?.Color ?? Colors.SlateGray;
                if (ObjectsList.SelectedIndex == obj.Index) color = Color.Multiply(color, 1.12f);
                var brush = new SolidColorBrush(color); brush.Freeze();
                var material = new DiffuseMaterial(brush); material.Freeze();
                var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
                _modelObjects[model] = obj.Index;
                Viewer.Children.Add(new ModelVisual3D { Content = model });
            }
            if (selectedIndices.Count > 0)
            {
                selectedIndices.Freeze(); var selectionMesh = new MeshGeometry3D { Positions = positions, TriangleIndices = selectedIndices }; selectionMesh.Freeze();
                var selectionBrush = new SolidColorBrush(Color.FromRgb(255, 211, 45)); selectionBrush.Freeze(); var selectionMaterial = new DiffuseMaterial(selectionBrush); selectionMaterial.Freeze();
                var selectionModel = new GeometryModel3D(selectionMesh, selectionMaterial) { BackMaterial = selectionMaterial }; _modelObjects[selectionModel] = obj.Index; Viewer.Children.Add(new ModelVisual3D { Content = selectionModel });
            }
        }
        UpdateCamera();
    }

    void AddPlate()
    {
        var size = Math.Max(_radius * 2.4, 80); var z = _doc!.Objects.SelectMany(o => o.Vertices).Min(v => v.Z) - .4;
        var mesh = new MeshGeometry3D { Positions = new Point3DCollection([new(_center.X-size/2,_center.Y-size/2,z),new(_center.X+size/2,_center.Y-size/2,z),new(_center.X+size/2,_center.Y+size/2,z),new(_center.X-size/2,_center.Y+size/2,z)]), TriangleIndices = new Int32Collection([0,1,2,0,2,3]) };
        Viewer.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(80, 100, 110, 125)))) });
    }

    void UpdateCamera()
    {
        var yaw = _yaw * Math.PI / 180; var pitch = Math.Clamp(_pitch, -89, 89) * Math.PI / 180; var distance = _radius * 3.0 * _zoom;
        var direction = new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Cos(pitch) * Math.Cos(yaw), Math.Sin(pitch));
        var position = _center + direction * distance; var look = _center - position;
        Viewer.Camera = _perspective ? new PerspectiveCamera(position, look, new Vector3D(0, 0, 1), 42) : new OrthographicCamera(position, look, new Vector3D(0, 0, 1), _radius * 2.4 * _zoom);
    }

    void FitCamera() { _zoom = 1; _yaw = -40; _pitch = 25; UpdateCamera(); }
    void Viewer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var cursor = e.GetPosition(Viewer);
        var anchor = CursorPointOnPlane(Viewer.Camera, cursor, Viewer.ActualWidth, Viewer.ActualHeight, _center);
        var nextZoom = Math.Clamp(_zoom * (e.Delta > 0 ? .88 : 1.14), .08, 20);
        if (Math.Abs(nextZoom - _zoom) < double.Epsilon) return;
        _zoom = nextZoom;
        UpdateCamera();
        var movedAnchor = CursorPointOnPlane(Viewer.Camera, cursor, Viewer.ActualWidth, Viewer.ActualHeight, _center);
        if (anchor is Point3D before && movedAnchor is Point3D after)
        {
            _center += before - after;
            UpdateCamera();
        }
        e.Handled = true;
    }

    async void Viewer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _last = e.GetPosition(Viewer);
        if (PaintMode.IsChecked == true && e.ChangedButton == MouseButton.Left)
        {
            if (PaintTool.SelectedIndex == 1)
            {
                Viewer.CaptureMouse();
                _paintStroke = true;
                _paintStrokeChanged = false;
                _lastPaintSample = 0;
                SelectPaintBrushFromView(_last);
            }
            else await SelectPaintFromView(_last);
            e.Handled = true;
            return;
        }
        Viewer.CaptureMouse();
        if (PaintMode.IsChecked != true && e.ChangedButton == MouseButton.Left && e.ClickCount == 1) SelectFromView(_last);
        e.Handled = true;
    }

    void Viewer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var p = e.GetPosition(Viewer);
        var paintMode = PaintMode.IsChecked == true;
        if (paintMode && _paintStroke && e.LeftButton == MouseButtonState.Pressed)
        {
            var dx = p.X - _last.X;
            var dy = p.Y - _last.Y;
            if (dx * dx + dy * dy >= 4)
            {
                SelectPaintBrushFromView(p);
                _last = p;
            }
            return;
        }
        var rotate = paintMode ? e.RightButton == MouseButtonState.Pressed : e.LeftButton == MouseButtonState.Pressed;
        if (rotate)
        {
            _yaw += (p.X - _last.X) * .45;
            _pitch += (p.Y - _last.Y) * .45;
        }
        else if (!paintMode && e.RightButton == MouseButtonState.Pressed)
        {
            var scale = _radius * _zoom / Math.Max(200, Viewer.ActualWidth);
            _center.X -= (p.X - _last.X) * scale;
            _center.Z += (p.Y - _last.Y) * scale;
        }
        else return;
        _last = p;
        UpdateCamera();
    }
    void Viewer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _paintStroke)
        {
            _paintStroke = false;
            if (_paintStrokeChanged)
            {
                UpdatePaintSelectionText();
                Render();
                StatusText.Text = $"Trait terminé · {_paintSelection.Values.Sum(set => set.Count):N0} triangles sélectionnés.";
            }
        }
        Viewer.ReleaseMouseCapture();
    }

    internal static Point3D? CursorPointOnPlane(Camera? camera, System.Windows.Point cursor, double viewportWidth, double viewportHeight, Point3D planePoint)
    {
        if (camera is null || viewportWidth <= 0 || viewportHeight <= 0 || !double.IsFinite(cursor.X) || !double.IsFinite(cursor.Y)) return null;
        Point3D origin;
        Vector3D ray;
        Vector3D forward;
        Vector3D up;
        if (camera is PerspectiveCamera perspective)
        {
            origin = perspective.Position; forward = perspective.LookDirection; up = perspective.UpDirection;
            if (!NormalizeBasis(ref forward, ref up, out var right)) return null;
            var halfHeight = Math.Tan(perspective.FieldOfView * Math.PI / 360);
            var x = (cursor.X * 2 / viewportWidth - 1) * viewportWidth / viewportHeight * halfHeight;
            var y = (1 - cursor.Y * 2 / viewportHeight) * halfHeight;
            ray = forward + right * x + up * y;
        }
        else if (camera is OrthographicCamera orthographic)
        {
            origin = orthographic.Position; forward = orthographic.LookDirection; up = orthographic.UpDirection;
            if (!NormalizeBasis(ref forward, ref up, out var right)) return null;
            var x = (cursor.X * 2 / viewportWidth - 1) * orthographic.Width / 2;
            var y = (1 - cursor.Y * 2 / viewportHeight) * (orthographic.Width * viewportHeight / viewportWidth) / 2;
            origin += right * x + up * y;
            ray = forward;
        }
        else return null;
        var denominator = Vector3D.DotProduct(ray, forward);
        if (Math.Abs(denominator) < 1e-9) return null;
        var distance = Vector3D.DotProduct(planePoint - origin, forward) / denominator;
        return distance > 0 && double.IsFinite(distance) ? origin + ray * distance : null;
    }

    static bool NormalizeBasis(ref Vector3D forward, ref Vector3D up, out Vector3D right)
    {
        right = default;
        if (forward.LengthSquared < 1e-12 || up.LengthSquared < 1e-12) return false;
        forward.Normalize();
        up -= forward * Vector3D.DotProduct(up, forward);
        if (up.LengthSquared < 1e-12) return false;
        up.Normalize();
        right = Vector3D.CrossProduct(forward, up);
        if (right.LengthSquared < 1e-12) return false;
        right.Normalize();
        up = Vector3D.CrossProduct(right, forward);
        up.Normalize();
        return true;
    }

    void SelectFromView(System.Windows.Point point) { VisualTreeHelper.HitTest(Viewer, null, result => { if (result is RayMeshGeometry3DHitTestResult hit && hit.ModelHit is GeometryModel3D model && _modelObjects.TryGetValue(model, out var index)) { ObjectsList.SelectedIndex = index; ObjectsList.ScrollIntoView(ObjectsList.SelectedItem); return HitTestResultBehavior.Stop; } return HitTestResultBehavior.Continue; }, new PointHitTestParameters(point)); }
    async Task SelectPaintFromView(System.Windows.Point point)
    {
        RayMeshGeometry3DHitTestResult? selectedHit = null; GeometryModel3D? selectedModel = null;
        VisualTreeHelper.HitTest(Viewer, null, result => { if (result is RayMeshGeometry3DHitTestResult hit && hit.ModelHit is GeometryModel3D model && _modelObjects.ContainsKey(model)) { selectedHit = hit; selectedModel = model; return HitTestResultBehavior.Stop; } return HitTestResultBehavior.Continue; }, new PointHitTestParameters(point));
        if (selectedHit is not null && selectedModel is not null) await SelectPaintZone(selectedHit, selectedModel);
    }

    void SelectPaintBrushFromView(System.Windows.Point point)
    {
        var now = Environment.TickCount64;
        if (_lastPaintSample != 0 && now - _lastPaintSample < 20) return;
        _lastPaintSample = now;
        var radius = PaintBrushSize.SelectedIndex switch { 0 => 4d, 1 => 8d, 2 => 14d, 3 => 22d, _ => 32d };
        var offsets = new[]
        {
            new Vector(0, 0), new Vector(radius, 0), new Vector(-radius, 0), new Vector(0, radius), new Vector(0, -radius),
            new Vector(radius * .7, radius * .7), new Vector(-radius * .7, radius * .7),
            new Vector(radius * .7, -radius * .7), new Vector(-radius * .7, -radius * .7)
        };
        var added = 0;
        foreach (var offset in offsets)
        {
            RayMeshGeometry3DHitTestResult? selectedHit = null;
            GeometryModel3D? selectedModel = null;
            VisualTreeHelper.HitTest(Viewer, null, result =>
            {
                if (result is RayMeshGeometry3DHitTestResult hit && hit.ModelHit is GeometryModel3D model && _modelObjects.ContainsKey(model))
                {
                    selectedHit = hit;
                    selectedModel = model;
                    return HitTestResultBehavior.Stop;
                }
                return HitTestResultBehavior.Continue;
            }, new PointHitTestParameters(point + offset));
            if (selectedHit is null || selectedModel is null || _doc is null || !_modelObjects.TryGetValue(selectedModel, out var objectIndex)) continue;
            var obj = _doc.Objects.FirstOrDefault(item => item.Index == objectIndex);
            if (obj is null) continue;
            var triangle = FindSourceTriangle(obj, selectedHit);
            if (triangle < 0) continue;
            if (!_paintSelection.TryGetValue(objectIndex, out var current)) _paintSelection[objectIndex] = current = [];
            if (current.Add(triangle)) added++;
        }
        if (added == 0) return;
        _paintStrokeChanged = true;
        UpdatePaintSelectionText();
        StatusText.Text = $"Pinceau actif · {_paintSelection.Values.Sum(set => set.Count):N0} triangles sélectionnés.";
        if ((_doc?.TriangleCount ?? 0) <= 300_000 && now % 100 < 25) Render();
    }

    void ViewIso_Click(object sender, RoutedEventArgs e) { _yaw = -40; _pitch = 25; UpdateCamera(); }
    void ViewFront_Click(object sender, RoutedEventArgs e) { _yaw = 180; _pitch = 0; UpdateCamera(); }
    void ViewBack_Click(object sender, RoutedEventArgs e) { _yaw = 0; _pitch = 0; UpdateCamera(); }
    void ViewLeft_Click(object sender, RoutedEventArgs e) { _yaw = -90; _pitch = 0; UpdateCamera(); }
    void ViewRight_Click(object sender, RoutedEventArgs e) { _yaw = 90; _pitch = 0; UpdateCamera(); }
    void ViewTop_Click(object sender, RoutedEventArgs e) { _yaw = 0; _pitch = 89; UpdateCamera(); }
    void ViewBottom_Click(object sender, RoutedEventArgs e) { _yaw = 0; _pitch = -89; UpdateCamera(); }
    void Fit_Click(object sender, RoutedEventArgs e) => FitCamera();
    void ToggleGrid_Click(object sender, RoutedEventArgs e) { _grid = !_grid; Render(); StatusText.Text = _grid ? "Plateau affiché." : "Plateau masqué."; }
    void TogglePerspective_Click(object sender, RoutedEventArgs e) { _perspective = !_perspective; UpdateCamera(); }

    void Settings_Click(object sender, RoutedEventArgs e) { var dialog = new SettingsWindow(_settings) { Owner = this }; if (dialog.ShowDialog() == true) { _settings = dialog.Value; _settingsService.Save(_settings); EnsurePreferredSlicer(); ApplyTheme(); UpdateSlicerButton(); } }
    void ApplyTheme()
    {
        var light = _settings.Theme == "Clair" || (_settings.Theme == "Système" && SystemThemeIsLight());
        SetThemeBrush("AppBackground", light ? "#EEF1F6" : "#20242B");
        SetThemeBrush("PanelBackground", light ? "#FFFFFF" : "#2B313C");
        SetThemeBrush("ViewerBackground", light ? "#DCE3ED" : "#15191F");
        SetThemeBrush("ControlBackground", light ? "#E3E8F0" : "#3A4352");
        SetThemeBrush("ControlHover", light ? "#D3DAE6" : "#4A5870");
        SetThemeBrush("InputBackground", light ? "#FFFFFF" : "#171B22");
        SetThemeBrush("PrimaryText", light ? "#172033" : "#F4F6FA");
        SetThemeBrush("SecondaryText", light ? "#46546A" : "#C2CAD6");
        SetThemeBrush("MutedText", light ? "#65748A" : "#98A5B8");
        SetThemeBrush("PanelBorder", light ? "#AAB5C5" : "#566277");
        SetThemeBrush("Accent", light ? "#1267C8" : "#4B9CFF");
        var resources = System.Windows.Application.Current.Resources;
        resources[SystemColors.WindowBrushKey] = resources["InputBackground"];
        resources[SystemColors.ControlBrushKey] = resources["ControlBackground"];
        resources[SystemColors.ControlTextBrushKey] = resources["PrimaryText"];
        resources[SystemColors.WindowTextBrushKey] = resources["PrimaryText"];
        resources[SystemColors.GrayTextBrushKey] = resources["MutedText"];
        resources[SystemColors.MenuBrushKey] = resources["PanelBackground"];
        resources[SystemColors.MenuTextBrushKey] = resources["PrimaryText"];
        resources[SystemColors.HighlightBrushKey] = resources["Accent"];
        resources[SystemColors.HighlightTextBrushKey] = new SolidColorBrush(Colors.White);
    }

    static void SetThemeBrush(string key, string value) => System.Windows.Application.Current.Resources[key] = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(value)!);
    static bool SystemThemeIsLight()
    {
        try { return Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0)) != 0; }
        catch { return false; }
    }
    void ShowWelcome() { var dialog = new WelcomeWindow { Owner = this }; dialog.ShowDialog(); _settings.ShowWelcome = dialog.ShowAtStartup; _settingsService.Save(_settings); }
    void Welcome_Click(object sender, RoutedEventArgs e) => ShowWelcome();
    void HelpGuide_Click(object sender, RoutedEventArgs e) => new HelpGuideWindow { Owner = this }.ShowDialog();
    void WhatsNew_Click(object sender, RoutedEventArgs e) => ShowWhatsNew(
        UpdateService.CurrentVersion().ToString(3),
        string.IsNullOrWhiteSpace(_settings.LastReleaseNotes) ? UpdateService.BundledReleaseNotes : _settings.LastReleaseNotes);

    void ShowWhatsNewAfterUpdate()
    {
        if (!_settingsExistedAtStartup) return;
        var current = UpdateService.CurrentVersion().ToString(3);
        if (string.Equals(_settings.LastSeenVersion, current, StringComparison.OrdinalIgnoreCase)) return;
        var notes = string.Equals(_settings.PendingUpdateVersion, current, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_settings.PendingUpdateNotes)
            ? _settings.PendingUpdateNotes
            : UpdateService.BundledReleaseNotes;
        ShowWhatsNew(current, notes);
        _settings.LastSeenVersion = current;
        _settings.LastReleaseNotes = notes;
        _settings.PendingUpdateVersion = "";
        _settings.PendingUpdateNotes = "";
        _settingsService.Save(_settings);
    }

    void ShowWhatsNew(string version, string notes) => new WhatsNewWindow(version, notes) { Owner = this }.ShowDialog();
    void Donate_Click(object sender, RoutedEventArgs e)
    {
        try { DonationService.Open(); }
        catch (Exception ex) { MessageBox.Show($"Impossible d’ouvrir la page PayPal.\n\n{ex.Message}", "Soutenir PolyChrom 3MF", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    async void Update_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(false);

    async Task CheckForUpdatesAsync(bool automatic)
    {
        if (!automatic) { IsEnabled = false; Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = true; StatusText.Text = "Recherche d’une mise à jour…"; SetActivity(true, "Recherche d’une mise à jour…"); }
        try
        {
            var update = await _updateService.GetAvailableUpdateAsync();
            if (update is null)
            {
                StatusText.Text = "PolyChrom 3MF est à jour.";
                if (!automatic) MessageBox.Show($"Vous utilisez déjà la dernière version ({UpdateService.CurrentVersion().ToString(3)}).", "Mise à jour", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var install = MessageBox.Show($"La version {update.Version.ToString(3)} est disponible.\n\nNouveautés :\n{update.ReleaseNotes}\n\nLa télécharger et l’installer maintenant en arrière-plan ?", "Mise à jour disponible", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (install != MessageBoxResult.Yes) { StatusText.Text = $"Mise à jour {update.Tag} reportée."; return; }
            if (_dirty && MessageBox.Show("Le logiciel devra redémarrer. Les modifications non enregistrées seront perdues. Continuer ?", "Enregistrer avant la mise à jour", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            IsEnabled = false; Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = false; Progress.Minimum = 0; Progress.Maximum = 100; Progress.Value = 0; SetActivity(true, "Téléchargement de la mise à jour… 0 %", true, 0);
            var progress = new Progress<double>(value => { Progress.Value = value; StatusText.Text = $"Téléchargement de la mise à jour… {value:0}%"; SetActivity(true, $"Téléchargement de la mise à jour… {value:0} %", true, value); });
            var installer = await _updateService.DownloadInstallerAsync(update, progress);
            _settings.PendingUpdateVersion = update.Version.ToString(3);
            _settings.PendingUpdateNotes = update.ReleaseNotes;
            _settingsService.Save(_settings);
            MessageBox.Show("La mise à jour a été téléchargée et vérifiée.\n\nPolyChrom 3MF va maintenant se fermer, installer la nouvelle version en silence, puis redémarrer automatiquement.", "Redémarrage après mise à jour", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = Process.Start(new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                ArgumentList = { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS" }
            }) ?? throw new InvalidOperationException("Impossible de démarrer l’installateur de mise à jour.");
            _shutdownForUpdate = true;
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Recherche de mise à jour impossible.";
            if (!automatic) MessageBox.Show($"Impossible de mettre à jour le logiciel pour le moment.\n\n{ex.Message}", "Mise à jour", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; Progress.Visibility = Visibility.Collapsed; Progress.IsIndeterminate = true; SetActivity(false); }
    }
    void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
    void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return; if (e.Key == Key.O) Import_Click(sender, e); else if (e.Key == Key.S) SaveProject_Click(sender, e); else if (e.Key == Key.E) Export_Click(sender, e); else if (e.Key == Key.Z) Undo_Click(sender, e); else if (e.Key == Key.Y) Redo_Click(sender, e); else if (e.Key == Key.N) New_Click(sender, e); }
    void Window_Closing(object? sender, CancelEventArgs e) { if (!_shutdownForUpdate && !ConfirmDiscard()) e.Cancel = true; }
    void Quit_Click(object sender, RoutedEventArgs e) => Close();

    sealed record EditorState(int Selected, int Generation, bool FunMode, int ColorCount, List<ColorProposal> Proposals, PatternSettings? Pattern);
}
