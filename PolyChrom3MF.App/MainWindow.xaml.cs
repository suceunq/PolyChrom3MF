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
    readonly ProjectService _projects = new();
    readonly SettingsService _settingsService = new();
    readonly SlicerDetectionService _slicerDetection = new();
    readonly UpdateService _updateService = new();
    readonly Dictionary<GeometryModel3D, int> _modelObjects = [];
    readonly Stack<EditorState> _undo = [];
    readonly Stack<EditorState> _redo = [];
    AppSettings _settings;
    ModelDocument? _doc;
    List<ColorProposal> _proposals = [];
    ColorProposal? _selected;
    System.Windows.Point _last;
    double _yaw = -40, _pitch = -25, _zoom = 1;
    bool _grid = true, _perspective = true, _dirty, _loadingControls, _funMode = true, _automaticUpdateChecked;
    int _generation, _colorCount = 4;
    string? _lastSlicerFile;
    Point3D _center;
    double _radius = 100;

    public MainWindow()
    {
        InitializeComponent();
        ApplyStandardMenuColors(MainMenu);
        _settings = _settingsService.Load();
        _colorCount = Math.Clamp(_settings.ColorCount, 4, 32);
        EnsurePreferredSlicer();
        BuildScene();
        ApplyTheme();
        ContentRendered += async (_, _) =>
        {
            if (_settings.ShowWelcome) ShowWelcome();
            ChooseColorCount(false);
            var startupFile = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
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
        IsEnabled = !busy;
    }

    static void ApplyStandardMenuColors(ItemsControl menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>()) { item.Foreground = System.Windows.Media.Brushes.Black; ApplyStandardMenuColors(item); }
    }

    void GenerateProposals()
    {
        if (_doc is null) return;
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
            _projects.Save(dialog.FileName, _doc, _proposals, _proposals.IndexOf(_selected!), _yaw, _pitch, _zoom, _generation, _funMode, _colorCount);
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
            }
            _yaw = project.Yaw; _pitch = project.Pitch; _zoom = project.Zoom; SelectProposal(project.SelectedProposal); RefreshBindings(); Render(); _dirty = false;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Projet impossible à ouvrir", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _doc = null; _lastSlicerFile = null; _proposals.Clear(); _selected = null; ObjectsList.ItemsSource = null; Proposals.ItemsSource = null; HintText.Visibility = Visibility.Visible; FileText.Text = "Aucun fichier chargé"; InfoText.Text = DimensionsText.Text = StatsText.Text = ""; ApplyButton.IsEnabled = false; OpenSlicerButton.IsEnabled = false; _dirty = false; Render();
    }

    bool ConfirmDiscard() => !_dirty || MessageBox.Show("Les modifications non enregistrées seront perdues. Continuer ?", "PolyChrom 3MF", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    void PushUndo() { if (_proposals.Count == 0) return; _undo.Push(Capture()); _redo.Clear(); }
    EditorState Capture() => new(_proposals.IndexOf(_selected!), _generation, _funMode, _colorCount, _proposals.Select(Clone).ToList());
    static ColorProposal Clone(ColorProposal p) => new(p.Name, p.Description, p.Colors.Select(c => new PaletteColor(c.Name, c.Hex)).ToList(), new Dictionary<int, int>(p.Assignments)) { TriangleAssignments = p.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone()) };
    void Restore(EditorState state) { _generation = state.Generation; _funMode = state.FunMode; _colorCount = state.ColorCount; ProposalsTitle.Text = $"4 PROPOSITIONS · {_colorCount} COULEURS"; _loadingControls = true; FunMode.IsChecked = _funMode; _loadingControls = false; _proposals = state.Proposals.Select(Clone).ToList(); SelectProposal(state.Selected); RefreshBindings(); Render(); _dirty = true; }
    void Undo_Click(object sender, RoutedEventArgs e) { if (_undo.Count == 0) return; _redo.Push(Capture()); Restore(_undo.Pop()); StatusText.Text = "Modification annulée."; }
    void Redo_Click(object sender, RoutedEventArgs e) { if (_redo.Count == 0) return; _undo.Push(Capture()); Restore(_redo.Pop()); StatusText.Text = "Modification rétablie."; }
    void RefreshBindings() { Proposals.ItemsSource = null; Proposals.ItemsSource = _proposals; ObjectColorCombo.ItemsSource = null; ObjectColorCombo.ItemsSource = _selected?.Colors; }

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
            for (var triangleIndex = 0; triangleIndex < obj.Triangles.Count; triangleIndex++)
            {
                var assigned = triangleColors is not null && triangleIndex < triangleColors.Length ? triangleColors[triangleIndex] : _selected?.Assignments.GetValueOrDefault(obj.Index, 0) ?? 0;
                assigned = Math.Clamp(assigned, 0, colorCount - 1);
                var triangle = obj.Triangles[triangleIndex];
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
    void Viewer_MouseWheel(object sender, MouseWheelEventArgs e) { _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? .88 : 1.14), .08, 20); UpdateCamera(); }
    void Viewer_MouseDown(object sender, MouseButtonEventArgs e) { _last = e.GetPosition(Viewer); Viewer.CaptureMouse(); if (e.ClickCount == 1) SelectFromView(_last); }
    void Viewer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) { var p = e.GetPosition(Viewer); if (e.LeftButton == MouseButtonState.Pressed) { _yaw += (p.X - _last.X) * .45; _pitch += (p.Y - _last.Y) * .45; } else if (e.RightButton == MouseButtonState.Pressed) { var scale = _radius * _zoom / Math.Max(200, Viewer.ActualWidth); _center.X -= (p.X - _last.X) * scale; _center.Z += (p.Y - _last.Y) * scale; } else return; _last = p; UpdateCamera(); }
    void Viewer_MouseUp(object sender, MouseButtonEventArgs e) => Viewer.ReleaseMouseCapture();
    void SelectFromView(System.Windows.Point point) { VisualTreeHelper.HitTest(Viewer, null, result => { if (result is RayMeshGeometry3DHitTestResult hit && hit.ModelHit is GeometryModel3D model && _modelObjects.TryGetValue(model, out var index)) { ObjectsList.SelectedIndex = index; ObjectsList.ScrollIntoView(ObjectsList.SelectedItem); return HitTestResultBehavior.Stop; } return HitTestResultBehavior.Continue; }, new PointHitTestParameters(point)); }

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
    async void Update_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(false);

    async Task CheckForUpdatesAsync(bool automatic)
    {
        if (!automatic) { IsEnabled = false; Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = true; StatusText.Text = "Recherche d’une mise à jour…"; }
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

            IsEnabled = false; Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = false; Progress.Minimum = 0; Progress.Maximum = 100; Progress.Value = 0;
            var progress = new Progress<double>(value => { Progress.Value = value; StatusText.Text = $"Téléchargement de la mise à jour… {value:0}%"; });
            var installer = await _updateService.DownloadInstallerAsync(update, progress);
            MessageBox.Show("La mise à jour a été téléchargée et vérifiée.\n\nPolyChrom 3MF va maintenant se fermer, installer la nouvelle version en silence, puis redémarrer automatiquement.", "Redémarrage après mise à jour", MessageBoxButton.OK, MessageBoxImage.Information);
            _ = Process.Start(new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                ArgumentList = { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS" }
            }) ?? throw new InvalidOperationException("Impossible de démarrer l’installateur de mise à jour.");
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Recherche de mise à jour impossible.";
            if (!automatic) MessageBox.Show($"Impossible de mettre à jour le logiciel pour le moment.\n\n{ex.Message}", "Mise à jour", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { IsEnabled = true; Progress.Visibility = Visibility.Collapsed; Progress.IsIndeterminate = true; }
    }
    void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
    void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return; if (e.Key == Key.O) Import_Click(sender, e); else if (e.Key == Key.S) SaveProject_Click(sender, e); else if (e.Key == Key.E) Export_Click(sender, e); else if (e.Key == Key.Z) Undo_Click(sender, e); else if (e.Key == Key.Y) Redo_Click(sender, e); else if (e.Key == Key.N) New_Click(sender, e); }
    void Window_Closing(object? sender, CancelEventArgs e) { if (!ConfirmDiscard()) e.Cancel = true; }
    void Quit_Click(object sender, RoutedEventArgs e) => Close();

    sealed record EditorState(int Selected, int Generation, bool FunMode, int ColorCount, List<ColorProposal> Proposals);
}
