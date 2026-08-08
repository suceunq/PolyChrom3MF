using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using PolyChrom3MF.Logos;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using SystemColors = System.Windows.SystemColors;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PolyChrom3MF.App;

public partial class MainWindow : Window
{
    // Keep the full source mesh for export, but cap the interactive WPF preview.
    // The voxel LOD preserves the overall shape while avoiding camera lag.
    const long FullDetailTriangleLimit = 500_000;
    const int LargeModelPreviewTarget = 300_000;
    const long MaxPaintRefinementTriangles = 750_000;
    const long MaxPaintRefinementGrowth = 250_000;
    // The legacy placement pipeline remains unreachable. Version 2.1 uses the
    // isolated, tested PolyChrom3MF.Logos engine behind the new workflow.
    static readonly bool ImageImportModuleEnabled = true;
    internal static bool ImageImportModuleAvailable => ImageImportModuleEnabled;
    readonly ThreeMfService _service = new();
    readonly StlService _stlService = new();
    readonly PaletteService _palettes = new();
    readonly PatternService _patternService = new();
    readonly PatternGeometryService _patternGeometryService = new();
    readonly LogoApplicationService _logoApplication = new();
    readonly SmartSelectionService _smartSelection = new();
    readonly LocalRefinementService _localRefinement = new();
    readonly LayerService _layerService = new();
    readonly ProjectService _projects = new();
    readonly SettingsService _settingsService = new();
    readonly SlicerDetectionService _slicerDetection = new();
    readonly UpdateService _updateService = new();
    readonly StyleLibraryService _styleLibrary = new();
    readonly TextPatternService _textPatterns = new();
    readonly GpuViewportHost _gpuViewport;
    readonly System.Windows.Threading.DispatcherTimer _layerPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly System.Windows.Threading.DispatcherTimer _navigationInertiaTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly Dictionary<GeometryModel3D, int> _modelObjects = [];
    readonly Dictionary<GeometryModel3D, Dictionary<(int A, int B, int C), int>> _renderTriangleLookup = [];
    readonly Dictionary<int, double> _objectDiagonals = [];
    readonly Dictionary<int, HashSet<int>> _paintSelection = [];
    readonly Dictionary<int, HashSet<int>> _heightRangePreview = [];
    readonly Dictionary<int, HashSet<int>> _hoverTrianglePreview = [];
    readonly HashSet<(int ObjectIndex, int TriangleIndex)> _triangleStrokePainted = [];
    DateTime _lastHeightPreviewUpdate = DateTime.MinValue;
    readonly Dictionary<int, Dictionary<(int A, int B, int C), int>> _triangleLookup = [];
    readonly List<System.Windows.Point> _paintStrokePoints = [];
    // A brush is first used to identify the small part of the source mesh that
    // needs resolution.  After local refinement it is evaluated again, so only
    // the refined fragments inside the visible stroke receive a colour.
    Func<ModelDocument, Dictionary<int, HashSet<int>>>? _pendingPrecisePaint;
    readonly Stack<EditorState> _undo = [];
    readonly Stack<EditorState> _redo = [];
    AppSettings _settings;
    readonly bool _settingsExistedAtStartup;
    ModelDocument? _doc;
    Dictionary<int, PreviewMesh> _previewMeshes = [];
    List<ColorProposal> _proposals = [];
    List<ColorProposal> _layerBases = [];
    List<List<ColorLayer>> _proposalLayers = [];
    LogoProject _logoProject = new();
    List<ColorProposal>? _logoBaseProposals;
    ColorProposal? _selected;
    PatternSettings? _pattern;
    PatternWindow? _activePatternEditor;
    System.Windows.Point _last;
    System.Windows.Point? _queuedTrianglePaintPoint;
    System.Windows.Point _mouseDownPoint;
    Vector _navigationVelocity;
    string? _patternGizmoMode;
    System.Windows.Point _patternGizmoLast;
    System.Windows.Point _patternGizmoCenter;
    Point3D? _patternGizmoWorldPoint;
    double _yaw = -40, _pitch = -25, _zoom = 1;
    bool _grid, _perspective = true, _dirty, _loadingControls, _funMode = true, _automaticUpdateChecked, _paintStroke, _trianglePaintStroke, _trianglePaintBusy, _paintModeBeforePattern, _shutdownForUpdate, _navigationMoved;
    bool _triangleStrokeUndoCaptured;
    int _generation, _colorCount = 4;
    int _lastSelectionObject = -1, _lastSelectionTriangle = -1;
    string? _lastSlicerFile;
    Point3D _center, _modelCenter;
    double _radius = 100, _modelMinZ;

    public MainWindow()
    {
        InitializeComponent();
        _gpuViewport = new GpuViewportHost();
        GpuHost.Content = _gpuViewport;
        _gpuViewport.Failed += () => { GpuHost.Visibility = Visibility.Collapsed; Viewer.Visibility = Visibility.Visible; Render(); StatusText.Text = "Moteur Direct3D indisponible : affichage compatible activé."; };
        _layerPreviewTimer.Tick += (_, _) => { _layerPreviewTimer.Stop(); RecomposeSelected(); };
        ApplyStandardMenuColors(MainMenu);
        _settingsExistedAtStartup = File.Exists(_settingsService.FilePath);
        _settings = _settingsService.Load();
        _grid = _settings.ShowBuildPlate;
        PlateMenuItem.IsChecked = _grid;
        InertiaMenuItem.IsChecked = _settings.NavigationInertia;
        _navigationInertiaTimer.Tick += NavigationInertiaTick;
        if (!_settingsExistedAtStartup)
        {
            _settings.LastSeenVersion = UpdateService.CurrentVersion().ToString(3);
            _settings.LastReleaseNotes = UpdateService.BundledReleaseNotes;
            _settingsService.Save(_settings);
        }
        _colorCount = Math.Clamp(_settings.ColorCount, 2, 32);
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
            if (_doc.TriangleCount > FullDetailTriangleLimit)
                await Task.Run(() => GC.Collect(2, GCCollectionMode.Aggressive, true, true));
            if (_doc.TriangleCount > FullDetailTriangleLimit)
                SetActivity(true, $"Optimisation de l’aperçu de {_doc.TriangleCount:N0} faces…");
            await RebuildPreviewMeshesAsync(_doc);
            _pattern = null; _logoProject = new LogoProject(); _logoBaseProposals = null;
            _paintSelection.Clear(); _triangleLookup.Clear(); _renderTriangleLookup.Clear(); UpdatePatternText(); UpdatePaintSelectionText();
            _lastSlicerFile = path; OpenSlicerButton.IsEnabled = true; UpdateSlicerButton();
            FileText.Text = Path.GetFileName(path);
            var displayedTriangles = _doc.Objects.Sum(obj => _previewMeshes.TryGetValue(obj.Index, out var preview) ? (long)preview.Triangles.Count : obj.Triangles.Count);
            var previewNote = displayedTriangles < _doc.TriangleCount
                ? $"\nAperçu optimisé : {displayedTriangles:N0} faces affichées · les {_doc.TriangleCount:N0} faces restent conservées pour l’export"
                : "";
            InfoText.Text = $"Format : {_doc.SourceFormat}\n{_doc.Objects.Count} objet(s) · {_doc.TriangleCount:N0} triangles\nUnité : {_doc.Unit}\nComposants : {_doc.ComponentCount}{previewNote}";
            DimensionsText.Text = $"{_doc.SizeX:0.##} × {_doc.SizeY:0.##} × {_doc.SizeZ:0.##} mm";
            ObjectsList.ItemsSource = _doc.Objects;
            ObjectsList.SelectedIndex = 0;
            _generation = 0;
            // Import must never silently apply the user's palette.  Proposals
            // remain available, but selecting one is an explicit user action.
            var preserveOriginal = true;
            if (preserveOriginal && _doc.OriginalColors is { Count: > 0 } && _doc.OriginalTriangleAssignments is { Count: > 0 })
            {
                // File has original colors → show them first, with proposals in background
                GenerateProposals();
                var originalProposal = new ColorProposal(
                    "Couleurs d'origine",
                    $"{_doc.OriginalColors.Count} couleur(s) conservée(s) du fichier importé",
                    _doc.OriginalColors,
                    _doc.Objects.ToDictionary(o => o.Index, _ => 0))
                {
                    TriangleAssignments = _doc.OriginalTriangleAssignments
                };
                _proposals.Insert(0, originalProposal);
                _layerBases.Insert(0, Clone(originalProposal));
                _proposalLayers.Insert(0, new List<ColorLayer> { _layerService.Create("Couleur de base", ColorLayerKind.BaseColor) });
                ProposalsTitle.Text = $"1 ORIGINALE + 4 PROPOSITIONS · {_colorCount} COULEURS";
                SelectProposal(0);
                Proposals.ItemsSource = null; Proposals.ItemsSource = _proposals;
            }
            else if (preserveOriginal)
            {
                // No original colors in file → show neutral gray, don't auto-generate proposals
                var neutralColor = new PaletteColor("Neutre", "#C0C0C0");
                var neutralProposal = new ColorProposal(
                    "Import neutre",
                    "Aucune couleur détectée — modèle affiché tel quel",
                    new List<PaletteColor> { neutralColor },
                    _doc.Objects.ToDictionary(o => o.Index, _ => 0));
                _proposals = new List<ColorProposal> { neutralProposal };
                _layerBases = new List<ColorProposal> { Clone(neutralProposal) };
                _proposalLayers = new List<List<ColorLayer>> { new() { _layerService.Create("Couleur de base", ColorLayerKind.BaseColor) } };
                ProposalsTitle.Text = "IMPORT NEUTRE · Aucune palette appliquée";
                SelectProposal(0);
                Proposals.ItemsSource = null; Proposals.ItemsSource = _proposals;
                ApplyButton.IsEnabled = true;
                StatusText.Text = "Modèle importé sans coloration. Utilisez Régénérer pour créer des propositions.";
            }
            else
            {
                GenerateProposals();
            }
            HintText.Visibility = Visibility.Collapsed;
            StatsText.Text = $"{_doc.Objects.Count} objets · {_doc.TriangleCount:N0} triangles · {_selected?.Colors.Count ?? 0} couleurs";
            RecenterView();
            _dirty = false;
            _undo.Clear(); _redo.Clear();
            StatusText.Text = _doc.Warning ?? (preserveOriginal
                ? (_doc.OriginalColors is { Count: > 0 }
                    ? "Couleurs d'origine conservées. Cliquez sur une proposition pour appliquer une palette."
                    : "Modèle importé sans coloration. Utilisez Régénérer pour créer des propositions.")
                : "Analyse terminée.");
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
        if (text is not null) StatusText.Text = text;
        SetActivity(busy, text);
        IsEnabled = !busy;
    }

    async void BeginnerMode_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dialog = new BeginnerWizardWindow(_settings, PreferredSlicerName()) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Choice is null) return;
        _colorCount = dialog.Choice.ColorCount;
        _generation = dialog.Choice.StyleIndex;
        _funMode = dialog.Choice.FunMode;
        _settings.ColorCount = _colorCount; _settingsService.Save(_settings);
        if (!await LoadModel(dialog.Choice.ModelPath)) return;
        _generation = dialog.Choice.StyleIndex;
        _loadingControls = true; FunMode.IsChecked = _funMode; _loadingControls = false;
        GenerateProposals(); SelectProposal(Math.Min(dialog.Choice.StyleIndex, _proposals.Count - 1)); RefreshBindings(); Render();
        StatusText.Text = "Mode débutant prêt : choisissez une proposition, prévisualisez puis exportez.";
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
        _pattern = null; _paintSelection.Clear(); HidePaintStrokeFill(); UpdatePatternText(); UpdatePaintSelectionText();
        var custom = UseFilaments.IsChecked == true ? _settings.FilamentColors : null;
        _proposals = _palettes.Create(_doc, custom, _generation, _funMode, _colorCount);
        ResetLayers();
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
        if (ObjectsList.SelectedItem is ModelObject selectedObject)
            ObjectColorCombo.SelectedIndex = _selected.Assignments.GetValueOrDefault(selectedObject.Index, 0);
        _loadingControls = false;
        UpdateSelectedObjectIndicator(false);
        RefreshLayers();
    }

    void ResetLayers()
    {
        _layerBases = _proposals.Select(Clone).ToList();
        _proposalLayers = _proposals.Select((_, index) => new List<ColorLayer>
        {
            _layerService.Create("Couleur de base", ColorLayerKind.BaseColor)
        }).ToList();
        RefreshLayers();
    }

    int SelectedProposalIndex() => _selected is null ? -1 : _proposals.IndexOf(_selected);

    void RefreshLayers(int selected = -1)
    {
        if (LayersList is null) return;
        var proposal = SelectedProposalIndex();
        var layers = proposal >= 0 && proposal < _proposalLayers.Count ? _proposalLayers[proposal] : [];
        _loadingControls = true;
        LayersList.ItemsSource = null;
        LayersList.ItemsSource = layers.AsEnumerable().Reverse().ToList();
        LayersList.SelectedIndex = layers.Count == 0 ? -1 : selected >= 0 ? Math.Min(selected, layers.Count - 1) : 0;
        if (LayersList.SelectedItem is ColorLayer { Pattern: { } selectedPattern }) _pattern = selectedPattern;
        UpdateLayerControls();
        _loadingControls = false;
        UpdatePatternText();
    }

    ColorLayer? SelectedLayer()
    {
        var proposal = SelectedProposalIndex();
        if (proposal < 0 || proposal >= _proposalLayers.Count || LayersList.SelectedItem is not ColorLayer selected) return null;
        return _proposalLayers[proposal].FirstOrDefault(layer => layer.Id == selected.Id);
    }

    void RecomposeSelected()
    {
        if (_doc is null) return;
        var index = SelectedProposalIndex();
        if (index < 0 || index >= _layerBases.Count || index >= _proposalLayers.Count) return;
        var composed = _layerService.Compose(_doc, _layerBases[index], _proposalLayers[index], previewOpacity: false);
        _proposals[index] = Rename(composed, _selected!.Name, _selected.Description);
        _selected = _proposals[index];
        RefreshBindings();
        Render();
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

    bool TryGetProposalColor(object sender, out int proposalIndex, out int colorIndex)
    {
        proposalIndex = -1;
        colorIndex = -1;
        if ((sender as FrameworkElement)?.DataContext is not PaletteColor color) return false;
        proposalIndex = _proposals.FindIndex(proposal => proposal.Colors.Any(candidate => ReferenceEquals(candidate, color)));
        if (proposalIndex < 0) return false;
        var proposal = _proposals[proposalIndex];
        colorIndex = proposal.Colors.FindIndex(candidate => ReferenceEquals(candidate, color));
        return colorIndex >= 0;
    }

    void ProposalColor_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetProposalColor(sender, out var proposalIndex, out var colorIndex)) return;
        e.Handled = true;
        SelectProposal(proposalIndex);
        EditProposalColor(proposalIndex, colorIndex);
    }

    void ProposalColor_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetProposalColor(sender, out var proposalIndex, out var colorIndex)) return;
        e.Handled = true;
        SelectProposal(proposalIndex);
        if (_proposals[proposalIndex].Colors.Count <= 2)
        {
            StatusText.Text = "Deux couleurs minimum doivent rester actives.";
            return;
        }
        DisableProposalColor(proposalIndex, colorIndex);
    }

    void EditProposalColor(int proposalIndex, int colorIndex)
    {
        if (proposalIndex < 0 || proposalIndex >= _proposals.Count) return;
        var proposal = _proposals[proposalIndex];
        if (colorIndex < 0 || colorIndex >= proposal.Colors.Count) return;
        var color = proposal.Colors[colorIndex];
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        dialog.Color = System.Drawing.Color.FromArgb(color.Color.R, color.Color.G, color.Color.B);
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        PushUndo();
        SelectProposal(proposalIndex);
        var hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        proposal.Colors[colorIndex] = new PaletteColor("Personnalisée", hex);
        if (proposalIndex < _layerBases.Count) _layerBases[proposalIndex].Colors[colorIndex] = new PaletteColor("Personnalisée", hex);
        RefreshBindings();
        Render();
        _dirty = true;
        StatusText.Text = $"Couleur {colorIndex + 1} de {proposal.Name} remplacée par {hex}.";
    }

    void DisableProposalColor(int proposalIndex, int colorIndex)
    {
        if (proposalIndex < 0 || proposalIndex >= _proposals.Count || _proposals[proposalIndex].Colors.Count <= 2) return;
        PushUndo();
        var proposal = _proposals[proposalIndex];
        var removedName = proposal.Colors[colorIndex].Name;
        var replacementOldIndex = PaletteService.FindReplacementColorIndex(proposal, colorIndex);
        if (!PaletteService.DisableColor(proposal, colorIndex)) return;
        if (proposalIndex < _layerBases.Count) PaletteService.DisableColor(_layerBases[proposalIndex], colorIndex);
        if (proposalIndex < _proposalLayers.Count)
            foreach (var layer in _proposalLayers[proposalIndex])
                foreach (var values in layer.TriangleOverrides.Values)
                    for (var index = 0; index < values.Length; index++)
                        values[index] = PaletteService.RemapColorIndex(values[index], colorIndex, replacementOldIndex);
        SelectProposal(proposalIndex);
        RefreshBindings();
        RefreshLayers();
        Render();
        _dirty = true;
        ProposalsTitle.Text = $"4 PROPOSITIONS · {proposal.Colors.Count} COULEURS ACTIVES";
        StatsText.Text = _doc is null ? StatsText.Text : $"{_doc.Objects.Count} objets · {_doc.TriangleCount:N0} triangles · {proposal.Colors.Count} couleurs";
        StatusText.Text = $"{removedName} désactivée. Les zones concernées ont été redistribuées automatiquement.";
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
        var dialog = new FilamentColorsWindow(_settings.FilamentColors, _settings.FilamentMaterials) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _settings.FilamentColors = dialog.Colors.ToList();
        _settings.FilamentMaterials = dialog.Materials.ToList();
        _colorCount = _settings.FilamentColors.Count;
        _settings.ColorCount = _colorCount;
        _settingsService.Save(_settings);
        if (UseFilaments.IsChecked == true && _doc is not null) { PushUndo(); GenerateProposals(); Render(); }
        StatusText.Text = $"{_settings.FilamentColors.Count} filaments PLA/PETG enregistrés ensemble.";
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
        var proposalIndex = SelectedProposalIndex();
        if (proposalIndex >= 0 && proposalIndex < _layerBases.Count) _layerBases[proposalIndex].Colors[index] = new PaletteColor("Personnalisée", hex);
        HexColorText.Text = hex;
        RefreshBindings(); Render(); _dirty = true;
    }

    void ApplyHex_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || ObjectColorCombo.SelectedIndex < 0) return;
        var value = HexColorText.Text.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$")) { MessageBox.Show("Saisissez un code au format #RRGGBB.", "Couleur invalide"); return; }
        PushUndo(); var index = ObjectColorCombo.SelectedIndex; _selected.Colors[index] = new PaletteColor("Personnalisée", value.ToUpperInvariant()); var proposalIndex = SelectedProposalIndex(); if (proposalIndex >= 0 && proposalIndex < _layerBases.Count) _layerBases[proposalIndex].Colors[index] = new PaletteColor("Personnalisée", value.ToUpperInvariant()); RefreshBindings(); ObjectColorCombo.SelectedIndex = index; Render(); _dirty = true;
    }

    void ObjectColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls || _selected is null || ObjectsList.SelectedItem is not ModelObject selectedObject || ObjectColorCombo.SelectedIndex < 0) return;
        PushUndo();
        _selected.Assignments[selectedObject.Index] = ObjectColorCombo.SelectedIndex;
        if (_selected.TriangleAssignments.TryGetValue(selectedObject.Index, out var triangleColors)) Array.Fill(triangleColors, ObjectColorCombo.SelectedIndex);
        var proposalIndex = SelectedProposalIndex();
        if (proposalIndex >= 0 && proposalIndex < _layerBases.Count)
        {
            _layerBases[proposalIndex].Assignments[selectedObject.Index] = ObjectColorCombo.SelectedIndex;
            if (_layerBases[proposalIndex].TriangleAssignments.TryGetValue(selectedObject.Index, out var baseTriangles)) Array.Fill(baseTriangles, ObjectColorCombo.SelectedIndex);
        }
        Render(); _dirty = true;
    }

    void ObjectsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected is null || ObjectsList.SelectedItem is not ModelObject selectedObject) return;
        _loadingControls = true;
        ObjectColorCombo.SelectedIndex = _selected.Assignments.GetValueOrDefault(selectedObject.Index, 0);
        if (ObjectColorCombo.SelectedItem is PaletteColor color) HexColorText.Text = color.Hex;
        _loadingControls = false;
        UpdateSelectedObjectIndicator(true);
        Render();
    }

    void UpdateSelectedObjectIndicator(bool updateStatus)
    {
        if (SelectedObjectText is null) return;
        if (ObjectsList.SelectedItem is not ModelObject selectedObject)
        {
            SelectedObjectText.Text = "Aucun objet sélectionné";
            return;
        }
        SelectedObjectText.Text = $"✓ Objet sélectionné : {selectedObject}";
        if (updateStatus) StatusText.Text = $"{selectedObject} sélectionné.";
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        StatusText.Text = $"{_selected.Name} appliquée. Le modèle est prêt à être exporté.";
        _dirty = true;
    }

    void PrintAssistant_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) { MessageBox.Show("Importez d’abord un modèle.", "Assistant impression"); return; }
        SelectExportProfile(_selected, true);
    }

    async void ImportPattern_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _selected is null)
        {
            MessageBox.Show("Importez d’abord un modèle 3MF ou STL.", "Logo ou image");
            return;
        }
        if (_activePatternEditor is not null)
        {
            _activePatternEditor.Focus();
            return;
        }
        var file = new OpenFileDialog
        {
            Filter = "Images compatibles (*.png;*.jpg;*.jpeg;*.webp;*.svg)|*.png;*.jpg;*.jpeg;*.webp;*.svg|PNG (*.png)|*.png|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg|WebP (*.webp)|*.webp|SVG (*.svg)|*.svg",
            CheckFileExists = true,
            Title = "Choisir le logo ou l’image"
        };
        if (file.ShowDialog() != true) return;

        var proposalIndex = SelectedProposalIndex();
        if (proposalIndex < 0) proposalIndex = 0;
        var proposalsBeforePreview = _proposals;
        var selectedBeforePreview = _selected;
        var documentBeforePreview = _doc;
        string? preparedImage = null;
        CancellationTokenSource? previewCancellation = null;
        var previewRevision = 0;
        PatternWindow? editor = null;
        LogoAsset? asset = null;
        try
        {
            var options = new ImageImportOptionsWindow(file.FileName) { Owner = this };
            if (options.ShowDialog() != true) return;
            preparedImage = options.PreparedImagePath ??
                            throw new InvalidDataException("Le détourage n’a produit aucune image.");
            var raster = new LogoImageImporter().Import(preparedImage);
            asset = new LogoAsset
            {
                Name = Path.GetFileName(file.FileName),
                Width = raster.Width,
                Height = raster.Height,
                SourceFormat = raster.SourceFormat,
                Png = LogoImageImporter.EncodePng(raster)
            };
            var aspect = asset.Width / (double)Math.Max(1, asset.Height);
            var initialStretchX = aspect >= 1 ? 100 : Math.Max(10, aspect * 100);
            var initialStretchY = aspect >= 1 ? Math.Max(10, 100 / aspect) : 100;
            var selectedObject = ObjectsList.SelectedItem as ModelObject ?? _doc.Objects.First();
            var initial = new PatternSettings(
                preparedImage,
                PatternMode.Front,
                TargetObject: selectedObject.Index,
                FourVariants: false,
                DisplayName: asset.Name,
                MonochromeLogo: true,
                LogoColorIndex: 0,
                RepeatAcrossModel: false,
                BackFacePreview: false,
                StretchX: initialStretchX,
                StretchY: initialStretchY);
            UpdatePatternGizmoImage(asset, initial);
            editor = new PatternWindow(preparedImage, _doc.Objects, _selected.Colors, initial, asset.Name);
            editor.PreviewRequested += PreviewLogo;
            editor.PlacementModeChanged += armed =>
            {
                PatternGizmo.Visibility = armed ? Visibility.Collapsed : Visibility.Visible;
                if (!armed) UpdatePatternGizmoProjection();
            };
            _activePatternEditor = editor;
            PatternEditorHost.Content = editor;
            SetPatternEditingUi(true);
            bool accepted;
            try { accepted = await editor.ShowEditorAsync(); }
            finally
            {
                _activePatternEditor = null;
                PatternEditorHost.Content = null;
                SetPatternEditingUi(false);
            }
            Interlocked.Increment(ref previewRevision);
            previewCancellation?.Cancel();
            editor.PreviewRequested -= PreviewLogo;
            _proposals = proposalsBeforePreview;
            _selected = selectedBeforePreview;
            RefreshBindings();
            Render();
            if (!accepted)
            {
                StatusText.Text = "Placement du logo annulé sans modifier le modèle.";
                return;
            }

            var instance = LogoApplicationService.FromPattern(
                asset.Id,
                editor.Value,
                Path.GetFileNameWithoutExtension(asset.Name),
                editor.Value.LogoColorIndex);
            var instances = editor.Value.Copies > 1
                ? LogoProjector.Duplicate(instance, editor.Value.Copies,
                    (float)(editor.Value.Spacing / 100 * Math.Max(.01, editor.Value.SurfaceWorldSize)),
                    editor.Value.Alignment switch
                    {
                        PatternAlignment.Vertical => LogoRepeatMode.Vertical,
                        PatternAlignment.Circular => LogoRepeatMode.Circular,
                        _ => LogoRepeatMode.Horizontal
                    })
                : [instance];
            var layer = new LogoLayer
            {
                Name = $"Logo · {Path.GetFileNameWithoutExtension(asset.Name)}",
                Order = _logoProject.Layers.Count,
                Instances = instances.ToList()
            };
            var nextProject = _logoProject with
            {
                Assets = [.. _logoProject.Assets, asset],
                Layers = [.. _logoProject.Layers, layer]
            };
            var baseProposals = _logoBaseProposals ?? proposalsBeforePreview.Select(Clone).ToList();
            PushUndo();
            SetBusy(true, "Subdivision locale et application précise du logo…");
            var result = await Task.Run(() =>
                _logoApplication.Apply(documentBeforePreview, baseProposals, nextProject));
            _doc = result.Document;
            _logoProject = nextProject;
            _logoBaseProposals = result.Bases.Select(Clone).ToList();
            _layerBases = result.Bases.Select(Clone).ToList();
            _proposals = result.Proposals;
            _proposalLayers = result.Proposals.Select((proposal, index) =>
            {
                var logoLayer = CreateDifferenceLayer(
                    layer.Name,
                    ColorLayerKind.MonochromeLogo,
                    result.Bases[index],
                    proposal);
                return new List<ColorLayer>
                {
                    _layerService.Create("Couleur de base", ColorLayerKind.BaseColor),
                    logoLayer
                };
            }).ToList();
            SetActivity(true, $"Optimisation de l’aperçu de {_doc.TriangleCount:N0} faces…");
            await RebuildPreviewMeshesAsync(_doc);
            SelectProposal(Math.Min(proposalIndex, _proposals.Count - 1));
            RefreshBindings();
            RefreshLayers();
            RefreshLogoLayers();
            Render();
            _dirty = true;
            StatusText.Text = result.AddedTriangles > 0
                ? $"Logo appliqué · {result.AddedTriangles:N0} triangles ajoutés uniquement sous le motif."
                : "Logo appliqué sans modifier les couleurs ni les motifs de fond.";
        }
        catch (OperationCanceledException)
        {
            _proposals = proposalsBeforePreview;
            _selected = selectedBeforePreview;
            Render();
            StatusText.Text = "Calcul du logo annulé.";
        }
        catch (Exception ex)
        {
            _proposals = proposalsBeforePreview;
            _selected = selectedBeforePreview;
            Render();
            MessageBox.Show(ex.Message, "Logo impossible à appliquer", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Le modèle n’a pas été modifié.";
        }
        finally
        {
            _activePatternEditor = null;
            PatternEditorHost.Content = null;
            SetPatternEditingUi(false);
            PatternGizmoImage.Source = null;
            previewCancellation?.Cancel();
            previewCancellation?.Dispose();
            SetBusy(false);
            try { if (preparedImage is not null && File.Exists(preparedImage)) File.Delete(preparedImage); } catch { }
        }

        async void PreviewLogo(PatternSettings settings)
        {
            var revision = Interlocked.Increment(ref previewRevision);
            var cancellation = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref previewCancellation, cancellation);
            previous?.Cancel();
            previous?.Dispose();
            try
            {
                if (!settings.HasSurfaceFrame)
                {
                    _proposals = proposalsBeforePreview;
                    _selected = selectedBeforePreview;
                    Render();
                    editor?.SetPreviewStatus("Cliquez sur « Placer sur la pièce », puis sur la surface.");
                    return;
                }
                if (asset is null) return;
                UpdatePatternGizmoImage(asset, settings);
                await Task.Yield();
                if (cancellation.IsCancellationRequested || revision != previewRevision) return;
                // The source mesh may be intentionally coarse. Recoloring its
                // whole triangles during placement produces a false, jagged
                // preview. The transparent gizmo above shows the exact raster;
                // printable geometry is generated only after validation.
                _proposals = proposalsBeforePreview;
                _selected = selectedBeforePreview;
                editor?.SetPreviewStatus("Aperçu fidèle du logo. La subdivision locale précise sera créée à la validation.");
                StatusText.Text = "Aperçu du logo actualisé.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (revision == previewRevision) editor?.SetPreviewStatus(ex.Message, true);
            }
        }
    }

    async void LegacyImportPattern_Click(object sender, RoutedEventArgs e)
    {
        if (!ImageImportModuleEnabled) return;
        if (_doc is null || _selected is null) { MessageBox.Show("Importez d’abord un modèle 3MF ou STL.", "Motif image"); return; }
        if (_activePatternEditor is not null)
        {
            _activePatternEditor.Focus();
            return;
        }
        var file = new OpenFileDialog { Filter = "Images compatibles (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|Images PNG (*.png)|*.png|Images JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg", CheckFileExists = true, Title = "Choisir le motif à appliquer" };
        if (file.ShowDialog() != true) return;
        PatternWindow dialog;
        string preparedImage;
        var selectedIndex = Math.Max(0, _proposals.IndexOf(_selected));
        var proposalsBeforePreview = _proposals;
        // Preview changes only the active proposal. Share the three untouched
        // proposals instead of cloning hundreds of megabytes of triangle arrays.
        var previewBase = _proposals;
        CancellationTokenSource? previewCancellation = null;
        var previewRevision = 0;
        try
        {
            PatternService.ValidateImage(file.FileName);
            var options = new ImageImportOptionsWindow(file.FileName) { Owner = this };
            if (options.ShowDialog() != true) return;
            preparedImage = options.PreparedImagePath ?? throw new InvalidDataException("Le détourage du logo n’a pas produit d’image valide.");
            var selectedObject = ObjectsList.SelectedItem as ModelObject;
            var initial = new PatternSettings(
                preparedImage,
                TargetObject: selectedObject?.Index ?? -1,
                FourVariants: false,
                DisplayName: Path.GetFileName(file.FileName),
                RepeatAcrossModel: false,
                BackFacePreview: false);
            dialog = new PatternWindow(preparedImage, _doc.Objects, _selected.Colors, initial, Path.GetFileName(file.FileName));
            dialog.PreviewRequested += PreviewPattern;
            dialog.PlacementModeChanged += armed => PatternGizmo.Visibility = armed ? Visibility.Collapsed : Visibility.Visible;
            _activePatternEditor = dialog;
            PatternEditorHost.Content = dialog;
            SetPatternEditingUi(true);
            bool accepted;
            try
            {
                accepted = await dialog.ShowEditorAsync();
            }
            finally
            {
                _activePatternEditor = null;
                PatternEditorHost.Content = null;
                SetPatternEditingUi(false);
            }
            Interlocked.Increment(ref previewRevision);
            previewCancellation?.Cancel();
            dialog.PreviewRequested -= PreviewPattern;
            _proposals = proposalsBeforePreview;
            SelectProposal(selectedIndex);
            RefreshBindings();
            if (!accepted)
            {
                Render();
                StatusText.Text = "Aperçu du motif annulé : la coloration précédente a été restaurée.";
                return;
            }
        }
        catch (Exception ex)
        {
            _activePatternEditor = null;
            PatternEditorHost.Content = null;
            SetPatternEditingUi(false);
            Interlocked.Increment(ref previewRevision);
            previewCancellation?.Cancel();
            _proposals = proposalsBeforePreview;
            SelectProposal(selectedIndex);
            RefreshBindings();
            Render();
            MessageBox.Show(ex.Message, "Image impossible à ouvrir", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        PushUndo();
        var working = _proposals.Select(Clone).ToList();
        var settings = dialog.Value;
        SetBusy(true, "Application du motif image sur les triangles…");
        try
        {
            var hasEditableLayers = _proposalLayers.Any(group => group.Any(layer => layer.Kind != ColorLayerKind.BaseColor));
            PatternGeometryResult geometry;
            if (!hasEditableLayers)
            {
                geometry = await Task.Run(() =>
                {
                    var modes = settings.FourVariants
                        ? new[] { PatternMode.Front, PatternMode.Cylindrical, PatternMode.Repeated, PatternMode.Triplanar }
                        : Enumerable.Repeat(settings.Mode, working.Count).ToArray();
                    IReadOnlySet<int>? targets = settings.FourVariants ? null : new HashSet<int> { selectedIndex };
                    return _patternGeometryService.Build(_doc, working, settings, modes, targetProposals: targets);
                });
                _doc = geometry.Document;
                SetActivity(true, $"Optimisation de l’affichage de {_doc.TriangleCount:N0} faces…");
                await RebuildPreviewMeshesAsync(_doc);
                _layerBases = geometry.BaseProposals.Select(Clone).ToList();
                _proposalLayers = _layerBases.Select(_ => new List<ColorLayer>
                {
                    _layerService.Create("Couleur de base", ColorLayerKind.BaseColor)
                }).ToList();
            }
            else
            {
                var applied = await Task.Run(() =>
                {
                    var results = working.Select(Clone).ToList();
                    for (var index = 0; index < results.Count; index++)
                    {
                        if (!settings.FourVariants && index != selectedIndex) continue;
                        var mode = settings.FourVariants
                            ? new[] { PatternMode.Front, PatternMode.Cylindrical, PatternMode.Repeated, PatternMode.Triplanar }[Math.Min(index, 3)]
                            : settings.Mode;
                        _patternService.Apply(_doc, results[index], settings, mode);
                    }
                    return results;
                });
                geometry = new PatternGeometryResult(_doc, working.Select(Clone).ToList(), applied, 0, 0);
            }

            var beforeNewLayer = hasEditableLayers ? working : geometry.BaseProposals;
            _proposals = geometry.Proposals;
            var modesForNames = new[] { PatternMode.Front, PatternMode.Cylindrical, PatternMode.Repeated, PatternMode.Triplanar };
            if (settings.FourVariants)
                for (var i = 0; i < _proposals.Count; i++) _proposals[i] = Rename(_proposals[i], $"Image {i + 1} — {PatternModeName(modesForNames[i])}", $"Motif {Path.GetFileName(file.FileName)} · {PatternModeName(modesForNames[i]).ToLowerInvariant()}");
            else _proposals[selectedIndex] = Rename(_proposals[selectedIndex], $"Image — {PatternModeName(settings.Mode)}", $"Motif {Path.GetFileName(file.FileName)} · {PatternModeName(settings.Mode).ToLowerInvariant()}");
            _pattern = settings;
            for (var index = 0; index < _proposals.Count; index++)
            {
                if (!settings.FourVariants && index != selectedIndex) continue;
                var targetName = settings.TargetObject < 0
                    ? "toute la figurine"
                    : _doc.Objects.FirstOrDefault(obj => obj.Index == settings.TargetObject)?.ToString() ?? $"objet {settings.TargetObject + 1}";
                var layerName = $"Motif · {Path.GetFileNameWithoutExtension(settings.DisplayName ?? settings.ImagePath)} · {targetName}";
                _proposalLayers[index].Add(CreateDifferenceLayer(layerName, settings.MonochromeLogo ? ColorLayerKind.MonochromeLogo : ColorLayerKind.Image, beforeNewLayer[index], _proposals[index], settings));
            }
            SelectProposal(selectedIndex); RefreshBindings(); RefreshLayers(); Render(); UpdatePatternText(); _dirty = true;
            StatusText.Text = geometry.AddedTriangles > 0
                ? $"Motif haute précision · {geometry.AddedTriangles:N0} triangles ajoutés localement (niveau {geometry.Levels})."
                : $"Motif ajouté indépendamment sur {(settings.TargetObject < 0 ? "toute la figurine" : "l’objet sélectionné")}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Motif image impossible à appliquer", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Échec de l’application du motif image.";
        }
        finally { SetBusy(false); }

        async void PreviewPattern(PatternSettings settings)
        {
            var revision = Interlocked.Increment(ref previewRevision);
            var cancellation = new CancellationTokenSource();
            var previousCancellation = previewCancellation;
            previewCancellation = cancellation;
            previousCancellation?.Cancel();
            try
            {
                if (!settings.HasSurfaceFrame && !settings.RepeatAcrossModel)
                {
                    var unchangedPreview = previewBase.ToList();
                    _proposals = unchangedPreview;
                    _selected = unchangedPreview[selectedIndex];
                    Render();
                    StatusText.Text = "Cliquez sur « Placer sur la pièce », puis choisissez la surface du logo.";
                    dialog.SetPreviewStatus("En attente du placement du logo sur la surface.");
                    return;
                }
                StatusText.Text = "Calcul de l’aperçu du motif…";
                dialog.SetPreviewStatus("Calcul de l’aperçu en cours…");
                var previewMode = settings.FourVariants
                    ? new[] { PatternMode.Front, PatternMode.Cylindrical, PatternMode.Repeated, PatternMode.Triplanar }[Math.Min(selectedIndex, 3)]
                    : settings.Mode;
                var previewResult = await Task.Run(() =>
                {
                    var proposal = Clone(previewBase[selectedIndex]);
                    var result = _patternService.Apply(_doc, proposal, settings, previewMode, cancellation.Token);
                    return (Proposal: proposal, Result: result);
                }, cancellation.Token);
                if (cancellation.IsCancellationRequested || revision != previewRevision) return;
                var preview = previewBase.ToList();
                preview[selectedIndex] = Rename(previewResult.Proposal, $"Aperçu — {PatternModeName(previewMode)}", $"Aperçu en direct · {PatternModeName(previewMode).ToLowerInvariant()}");
                _proposals = preview;
                _selected = preview[selectedIndex];
                Render();
                StatusText.Text = $"Aperçu actualisé sur {previewResult.Result.ColoredTriangles:N0} triangles.";
                dialog.SetPreviewStatus($"Aperçu actualisé · {previewResult.Result.ColoredTriangles:N0} triangles.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (revision == previewRevision)
                {
                    StatusText.Text = $"Aperçu impossible : {ex.Message}";
                    dialog.SetPreviewStatus($"Aperçu impossible : {ex.Message}", true);
                }
            }
            finally
            {
                if (ReferenceEquals(previewCancellation, cancellation)) previewCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    void RemovePattern_Click(object sender, RoutedEventArgs e)
    {
        if (_logoProject.Layers.Count > 0 && _logoBaseProposals is { Count: > 0 })
        {
            PushUndo();
            var logoSelectedIndex = SelectedProposalIndex();
            _proposals = _logoBaseProposals.Select(Clone).ToList();
            _layerBases = _proposals.Select(Clone).ToList();
            _proposalLayers = _proposals.Select(_ => new List<ColorLayer>
            {
                _layerService.Create("Couleur de base", ColorLayerKind.BaseColor)
            }).ToList();
            _logoProject = new LogoProject();
            _logoBaseProposals = null;
            _pattern = null;
            SelectProposal(Math.Clamp(logoSelectedIndex, 0, _proposals.Count - 1));
            RefreshBindings();
            RefreshLayers();
            RefreshLogoLayers();
            Render();
            _dirty = true;
            StatusText.Text = "Tous les logos ont été retirés. La coloration de fond est intacte.";
            return;
        }
        var selectedIndex = Math.Max(0, _proposals.IndexOf(_selected!));
        var selectedLayer = SelectedLayer();
        var pattern = selectedLayer?.Pattern ?? _pattern;
        if (pattern is null) { StatusText.Text = "Sélectionnez le calque du motif à retirer."; return; }
        PushUndo();
        var removed = 0;
        foreach (var layers in _proposalLayers)
            removed += layers.RemoveAll(layer => layer.Pattern is not null &&
                string.Equals(layer.Pattern.ImagePath, pattern.ImagePath, StringComparison.OrdinalIgnoreCase));
        for (var index = 0; index < _proposals.Count; index++)
            _proposals[index] = _layerService.Compose(_doc!, _layerBases[index], _proposalLayers[index]);
        _pattern = _proposalLayers[selectedIndex].LastOrDefault(layer => layer.Pattern is not null)?.Pattern;
        SelectProposal(Math.Min(selectedIndex, _proposals.Count - 1)); RefreshBindings(); RefreshLayers(); RecenterView(); UpdatePatternText(); _dirty = true;
        StatusText.Text = removed > 0 ? "Motif sélectionné retiré. Les autres motifs sont conservés." : "Aucun motif correspondant n’a été trouvé.";
    }

    void RefreshLogoLayers()
    {
        if (LogoLayersList is null) return;
        var selectedId = (LogoLayersList.SelectedItem as LogoInstance)?.Id;
        LogoLayersList.ItemsSource = null;
        LogoLayersList.ItemsSource = _logoProject.Layers
            .OrderBy(layer => layer.Order)
            .SelectMany(layer => layer.Instances.OrderBy(instance => instance.Order))
            .ToList();
        if (selectedId is Guid id)
            LogoLayersList.SelectedItem = LogoLayersList.Items.Cast<LogoInstance>().FirstOrDefault(instance => instance.Id == id);
    }

    void LogoLayersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogoLayersList.SelectedItem is LogoInstance instance)
            StatusText.Text = $"{instance.Name} sélectionné · objet {instance.Transform.Anchor.ObjectIndex + 1}.";
    }

    async void EditLogo_Click(object sender, RoutedEventArgs e)
    {
        if (LogoLayersList.SelectedItem is not LogoInstance selected || _doc is null || _selected is null) return;
        var asset = _logoProject.Assets.FirstOrDefault(item => item.Id == selected.AssetId);
        if (asset is null) return;
        var temporary = Path.Combine(Path.GetTempPath(), $"PolyChrom-edit-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(temporary, asset.Png);
        var current = PatternFromLogo(selected, temporary);
        var editor = new PatternWindow(temporary, _doc.Objects, _selected.Colors, current, asset.Name);
        var originalProject = CloneLogoProject(_logoProject);
        var originalProposals = _proposals;
        CancellationTokenSource? previewCancellation = null;
        var revision = 0;
        editor.PreviewRequested += Preview;
        editor.PlacementModeChanged += armed => PatternGizmo.Visibility = armed ? Visibility.Collapsed : Visibility.Visible;
        _activePatternEditor = editor;
        PatternEditorHost.Content = editor;
        SetPatternEditingUi(true);
        try
        {
            var accepted = await editor.ShowEditorAsync();
            Interlocked.Increment(ref revision);
            previewCancellation?.Cancel();
            _logoProject = originalProject;
            _proposals = originalProposals;
            _selected = _proposals[Math.Clamp(SelectedProposalIndex(), 0, _proposals.Count - 1)];
            if (!accepted) { Render(); return; }
            var replacement = LogoApplicationService.FromPattern(
                selected.AssetId, editor.Value, selected.Name, editor.Value.LogoColorIndex) with
            {
                Id = selected.Id,
                Order = selected.Order,
                Visible = selected.Visible,
                Locked = selected.Locked
            };
            PushUndo();
            ReplaceLogo(selected.Id, replacement);
            await ReapplyLogoProjectAsync("Logo modifié sans altérer le fond.");
        }
        catch (Exception ex)
        {
            _logoProject = originalProject;
            _proposals = originalProposals;
            Render();
            MessageBox.Show(ex.Message, "Modification du logo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _activePatternEditor = null;
            PatternEditorHost.Content = null;
            SetPatternEditingUi(false);
            previewCancellation?.Cancel();
            previewCancellation?.Dispose();
            try { File.Delete(temporary); } catch { }
        }

        async void Preview(PatternSettings settings)
        {
            if (!settings.HasSurfaceFrame) return;
            var currentRevision = Interlocked.Increment(ref revision);
            var cancellation = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref previewCancellation, cancellation);
            previous?.Cancel();
            previous?.Dispose();
            try
            {
                var previewProject = CloneLogoProject(originalProject);
                _logoProject = previewProject;
                ReplaceLogo(selected.Id, LogoApplicationService.FromPattern(
                    selected.AssetId, settings, selected.Name, settings.LogoColorIndex) with
                {
                    Id = selected.Id,
                    Order = selected.Order
                });
                var bases = _logoBaseProposals ?? originalProposals;
                var preview = await Task.Run(() => _logoApplication.Preview(_doc, bases, _logoProject, cancellation.Token), cancellation.Token);
                if (cancellation.IsCancellationRequested || currentRevision != revision) return;
                var selectedIndex = Math.Clamp(SelectedProposalIndex(), 0, preview.Count - 1);
                _proposals = preview;
                _selected = preview[selectedIndex];
                Render();
                editor.SetPreviewStatus("Aperçu exact de la modification.");
            }
            catch (OperationCanceledException) { }
        }
    }

    async void DuplicateLogo_Click(object sender, RoutedEventArgs e)
    {
        if (LogoLayersList.SelectedItem is not LogoInstance selected) return;
        var duplicate = LogoProjector.Duplicate(selected, 2, Math.Max(1, selected.Transform.WidthMm * .15f), LogoRepeatMode.Horizontal)[1];
        PushUndo();
        var layer = _logoProject.Layers.First(item => item.Instances.Any(instance => instance.Id == selected.Id));
        layer.Instances.Add(duplicate);
        await ReapplyLogoProjectAsync("Copie indépendante créée à côté du logo.");
        LogoLayersList.SelectedItem = duplicate;
    }

    async void ToggleLogo_Click(object sender, RoutedEventArgs e)
    {
        if (LogoLayersList.SelectedItem is not LogoInstance selected) return;
        PushUndo();
        ReplaceLogo(selected.Id, selected with { Visible = !selected.Visible });
        await ReapplyLogoProjectAsync(selected.Visible ? "Logo masqué." : "Logo affiché.");
    }

    async void DeleteLogo_Click(object sender, RoutedEventArgs e)
    {
        if (LogoLayersList.SelectedItem is not LogoInstance selected) return;
        PushUndo();
        foreach (var layer in _logoProject.Layers)
            layer.Instances.RemoveAll(instance => instance.Id == selected.Id);
        _logoProject.Layers.RemoveAll(layer => layer.Instances.Count == 0);
        await ReapplyLogoProjectAsync("Logo supprimé. Les autres logos et le fond sont conservés.");
    }

    void ReplaceLogo(Guid id, LogoInstance replacement)
    {
        foreach (var layer in _logoProject.Layers)
        {
            var index = layer.Instances.FindIndex(instance => instance.Id == id);
            if (index < 0) continue;
            layer.Instances[index] = replacement;
            return;
        }
        throw new InvalidDataException("Logo sélectionné introuvable.");
    }

    async Task ReapplyLogoProjectAsync(string status)
    {
        if (_doc is null || _logoBaseProposals is null) return;
        SetActivity(true, "Actualisation des calques de logos…");
        try
        {
            var selectedIndex = Math.Clamp(SelectedProposalIndex(), 0, _logoBaseProposals.Count - 1);
            _proposals = await Task.Run(() => _logoApplication.Preview(_doc, _logoBaseProposals, _logoProject));
            _layerBases = _logoBaseProposals.Select(Clone).ToList();
            _proposalLayers = _proposals.Select((proposal, index) => new List<ColorLayer>
            {
                _layerService.Create("Couleur de base", ColorLayerKind.BaseColor),
                CreateDifferenceLayer("Logos et images", ColorLayerKind.Image, _logoBaseProposals[index], proposal)
            }).ToList();
            SelectProposal(selectedIndex);
            RefreshBindings();
            RefreshLayers();
            RefreshLogoLayers();
            Render();
            _dirty = true;
            StatusText.Text = status;
        }
        finally { SetActivity(false); }
    }

    static PatternSettings PatternFromLogo(LogoInstance instance, string path)
    {
        var transform = instance.Transform;
        var size = Math.Max(.01f, transform.WidthMm);
        return new PatternSettings(
            path,
            transform.Projection switch
            {
                LogoProjectionMode.Cylindrical => PatternMode.Cylindrical,
                LogoProjectionMode.Conformal => PatternMode.Triplanar,
                _ => PatternMode.Front
            },
            100,
            transform.RotationDegrees,
            transform.OffsetUmm / size * 100,
            -transform.OffsetVmm / size * 100,
            transform.Anchor.ObjectIndex,
            false,
            DisplayName: instance.Name,
            MonochromeLogo: !instance.UseImageColors,
            LogoColorIndex: instance.FilamentIndex,
            RepeatAcrossModel: false,
            StretchX: 100,
            StretchY: transform.HeightMm / size * 100,
            MirrorX: transform.MirrorHorizontal,
            MirrorY: transform.MirrorVertical,
            BackFacePreview: false,
            TiltX: transform.TiltXDegrees,
            TiltY: transform.TiltYDegrees,
            ReliefDepth: transform.ReliefMm,
            HasSurfaceFrame: true,
            SurfaceX: transform.Anchor.Position.X,
            SurfaceY: transform.Anchor.Position.Y,
            SurfaceZ: transform.Anchor.Position.Z,
            SurfaceUx: transform.Anchor.Tangent.X,
            SurfaceUy: transform.Anchor.Tangent.Y,
            SurfaceUz: transform.Anchor.Tangent.Z,
            SurfaceVx: transform.Anchor.Bitangent.X,
            SurfaceVy: transform.Anchor.Bitangent.Y,
            SurfaceVz: transform.Anchor.Bitangent.Z,
            SurfaceNx: transform.Anchor.Normal.X,
            SurfaceNy: transform.Anchor.Normal.Y,
            SurfaceNz: transform.Anchor.Normal.Z,
            SurfaceWorldSize: size);
    }

    static ColorProposal Rename(ColorProposal proposal, string name, string description) => new(name, description, proposal.Colors.Select(color => new PaletteColor(color.Name, color.Hex)).ToList(), new Dictionary<int, int>(proposal.Assignments)) { TriangleAssignments = proposal.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone()) };
    ColorLayer CreateDifferenceLayer(string name, ColorLayerKind kind, ColorProposal basis, ColorProposal composed, PatternSettings? pattern = null)
    {
        var layer = _layerService.Create(name, kind) with { Pattern = pattern };
        if (_doc is null) return layer;
        foreach (var obj in _doc.Objects)
        {
            var before = basis.TriangleAssignments.GetValueOrDefault(obj.Index);
            var after = composed.TriangleAssignments.GetValueOrDefault(obj.Index);
            if (after is null) continue;
            var overrides = new int[obj.Triangles.Count];
            Array.Fill(overrides, -1);
            for (var triangle = 0; triangle < overrides.Length; triangle++)
            {
                var previous = before is null ? basis.Assignments.GetValueOrDefault(obj.Index, 0) : before[triangle];
                if (after[triangle] != previous) overrides[triangle] = after[triangle];
            }
            if (overrides.Any(value => value >= 0)) layer.TriangleOverrides[obj.Index] = overrides;
        }
        return layer;
    }
    static string PatternModeName(PatternMode mode) => mode switch { PatternMode.Front => "Projection frontale", PatternMode.Cylindrical => "Enveloppement", PatternMode.Repeated => "Motif répété", _ => "Triplanaire" };
    void UpdatePatternText()
    {
        if (PatternText is null) return;
        PatternText.Text = "";
        if (PatternTransformPanel is not null) PatternTransformPanel.Visibility = Visibility.Collapsed;
    }

    async void PatternTransform_Click(object sender, RoutedEventArgs e)
    {
        if (!ImageImportModuleEnabled) return;
        var selectedPatternLayer = SelectedLayer();
        var activePattern = selectedPatternLayer?.Pattern ?? _pattern;
        if (activePattern is null || _doc is null || sender is not FrameworkElement { Tag: string action }) return;
        var changed = action switch
        {
            "left" => activePattern with { OffsetX = Math.Max(-200, activePattern.OffsetX - 3) },
            "right" => activePattern with { OffsetX = Math.Min(200, activePattern.OffsetX + 3) },
            "up" => activePattern with { OffsetY = Math.Min(200, activePattern.OffsetY + 3) },
            "down" => activePattern with { OffsetY = Math.Max(-200, activePattern.OffsetY - 3) },
            "rotateleft" => activePattern with { Rotation = Math.Max(-180, activePattern.Rotation - 5) },
            "rotateright" => activePattern with { Rotation = Math.Min(180, activePattern.Rotation + 5) },
            "smaller" => activePattern with { Scale = Math.Max(10, activePattern.Scale - 5) },
            "larger" => activePattern with { Scale = Math.Min(400, activePattern.Scale + 5) },
            "mirrorx" => activePattern with { MirrorX = !activePattern.MirrorX },
            "mirrory" => activePattern with { MirrorY = !activePattern.MirrorY },
            "copy" => activePattern with { Copies = Math.Min(32, activePattern.Copies + 1) },
            _ => activePattern
        };
        if (changed == activePattern) return;
        await ApplyPatternLayerChangeAsync(activePattern, changed);
    }

    async void EditPatternLayer_Click(object sender, RoutedEventArgs e)
    {
        if (!ImageImportModuleEnabled) return;
        var layer = SelectedLayer();
        if (_doc is null || _selected is null || layer?.Pattern is not { } current || layer.IsLocked) return;
        if (_activePatternEditor is not null) return;
        var editor = new PatternWindow(current.ImagePath, _doc.Objects, _selected.Colors, current, current.DisplayName);
        editor.PlacementModeChanged += armed => PatternGizmo.Visibility = armed ? Visibility.Collapsed : Visibility.Visible;
        _activePatternEditor = editor;
        PatternEditorHost.Content = editor;
        SetPatternEditingUi(true);
        bool accepted;
        try { accepted = await editor.ShowEditorAsync(); }
        finally
        {
            _activePatternEditor = null;
            PatternEditorHost.Content = null;
            SetPatternEditingUi(false);
        }
        if (accepted) await ApplyPatternLayerChangeAsync(current, editor.Value);
    }

    async Task ApplyPatternLayerChangeAsync(PatternSettings activePattern, PatternSettings changed)
    {
        if (_doc is null) return;
        PushUndo(); _pattern = changed; SetActivity(true, "Actualisation du motif dans la vue 3D…");
        try
        {
            var selectedIndex = SelectedProposalIndex();
            var sourcePath = activePattern.ImagePath;
            List<(int LayerIndex, ColorLayer? Layer)> updated = await Task.Run(() => _proposalLayers.Select((layers, index) =>
            {
                var layerIndex = layers.FindIndex(layer => layer.Pattern is not null &&
                    string.Equals(layer.Pattern.ImagePath, sourcePath, StringComparison.OrdinalIgnoreCase));
                if (layerIndex < 0) return (LayerIndex: -1, Layer: (ColorLayer?)null);
                var lower = _layerService.Compose(_doc, _layerBases[index], layers.Take(layerIndex).ToList());
                var result = Clone(lower);
                var mode = changed.FourVariants
                    ? new[] { PatternMode.Front, PatternMode.Cylindrical, PatternMode.Repeated, PatternMode.Triplanar }[Math.Min(index, 3)]
                    : changed.Mode;
                _patternService.Apply(_doc, result, changed, mode);
                var replacement = CreateDifferenceLayer(layers[layerIndex].Name, layers[layerIndex].Kind, lower, result, changed) with
                {
                    Id = layers[layerIndex].Id,
                    IsVisible = layers[layerIndex].IsVisible,
                    IsLocked = layers[layerIndex].IsLocked,
                    PreviewOpacity = layers[layerIndex].PreviewOpacity
                };
                return (LayerIndex: layerIndex, Layer: (ColorLayer?)replacement);
            }).ToList());
            for (var index = 0; index < updated.Count; index++)
            {
                if (updated[index].LayerIndex < 0 || updated[index].Layer is not { } replacement) continue;
                _proposalLayers[index][updated[index].LayerIndex] = replacement;
                _proposals[index] = _layerService.Compose(_doc, _layerBases[index], _proposalLayers[index]);
            }
            SelectProposal(selectedIndex); RefreshBindings(); RefreshLayers(); Render(); UpdatePatternText(); _dirty = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Transformation du motif", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { SetActivity(false); }
    }

    void LayersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;
        UpdateLayerControls();
        if (SelectedLayer()?.Pattern is { } pattern)
        {
            _pattern = pattern;
            UpdatePatternText();
        }
    }

    void LayersList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!ImageImportModuleEnabled) return;
        if (SelectedLayer()?.Pattern is null) return;
        EditPatternLayer_Click(sender, e);
        e.Handled = true;
    }

    void UpdateLayerControls()
    {
        if (LayerVisible is null || LayerLocked is null) return;
        var layer = SelectedLayer();
        LayerVisible.IsEnabled = LayerLocked.IsEnabled = layer is not null;
        LayerVisible.IsChecked = layer?.IsVisible ?? false;
        LayerLocked.IsChecked = layer?.IsLocked ?? false;
    }

    void AddLayer_Click(object sender, RoutedEventArgs e)
    {
        var proposal = SelectedProposalIndex();
        if (proposal < 0) return;
        PushUndo();
        _proposalLayers[proposal].Add(_layerService.Create($"Peinture {_proposalLayers[proposal].Count}", ColorLayerKind.Paint));
        RefreshLayers();
        _dirty = true;
    }

    void DuplicateLayer_Click(object sender, RoutedEventArgs e)
    {
        var proposal = SelectedProposalIndex();
        var layer = SelectedLayer();
        if (proposal < 0 || layer is null) return;
        PushUndo();
        _proposalLayers[proposal].Add(layer.Duplicate(layer.Name + " copie"));
        RecomposeSelected();
        RecenterView();
        RefreshLayers();
        _dirty = true;
    }

    void RenameLayer_Click(object sender, RoutedEventArgs e)
    {
        var proposal = SelectedProposalIndex(); var layer = SelectedLayer();
        if (proposal < 0 || layer is null) return;
        var value = PromptText("Renommer le calque", "Nom du calque", layer.Name);
        if (value is null) return;
        PushUndo();
        var index = _proposalLayers[proposal].FindIndex(item => item.Id == layer.Id);
        _proposalLayers[proposal][index] = layer with { Name = LayerService.SafeName(value) };
        RefreshLayers(); _dirty = true;
    }

    void MergeLayer_Click(object sender, RoutedEventArgs e)
    {
        var proposal = SelectedProposalIndex(); var upper = SelectedLayer();
        if (proposal < 0 || upper is null) return;
        var layers = _proposalLayers[proposal]; var upperIndex = layers.FindIndex(item => item.Id == upper.Id);
        if (upperIndex <= 0) { MessageBox.Show("Sélectionnez un calque placé au-dessus d’un autre calque."); return; }
        try
        {
            PushUndo(); var merged = _layerService.Merge(layers[upperIndex - 1], upper);
            layers.RemoveAt(upperIndex); layers[upperIndex - 1] = merged;
            RecomposeSelected(); RefreshLayers(); _dirty = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Fusion impossible", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    async void TextLayer_Click(object sender, RoutedEventArgs e)
    {
        if (!ImageImportModuleEnabled) return;
        if (_doc is null || _selected is null) return;
        var text = PromptText("Ajouter un calque de texte", "Texte à projeter", "PolyChrom");
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var image = _textPatterns.Render(text);
            var color = Math.Clamp(PaintColorCombo.SelectedIndex, 0, _selected.Colors.Count - 1);
            var settings = new PatternSettings(image, PatternMode.Front, 100, FourVariants: false, DisplayName: text,
                MonochromeLogo: true, LogoColorIndex: color, RepeatAcrossModel: false);
            var style = new PolyStyleData($"Texte {text}", "Calque de texte projeté", _selected.Colors.Select(item => item.Hex).ToList(),
                settings, [ColorLayerKind.Text], _settings.PrinterName, _settings.MaterialSlots, DateTimeOffset.UtcNow);
            await ApplyStyle(style);
            var proposal = SelectedProposalIndex();
            var imageLayer = _proposalLayers[proposal].FindIndex(layer => layer.Kind == ColorLayerKind.Image);
            if (imageLayer >= 0) _proposalLayers[proposal][imageLayer] = _proposalLayers[proposal][imageLayer] with { Name = $"Texte : {text}", Kind = ColorLayerKind.Text };
            RefreshLayers();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Texte impossible à ajouter", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void EffectLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _selected is null) return;
        PushUndo(); var proposal = SelectedProposalIndex();
        var layer = _layerService.Create("Effet dégradé vertical", ColorLayerKind.Effect);
        var minZ = _doc.Objects.SelectMany(obj => obj.Vertices).Min(vertex => vertex.Z);
        var height = Math.Max(.000001, _doc.SizeZ);
        foreach (var obj in _doc.Objects)
        {
            var overrides = new int[obj.Triangles.Count];
            for (var index = 0; index < obj.Triangles.Count; index++)
            {
                var triangle = obj.Triangles[index];
                var z = (obj.Vertices[triangle.A].Z + obj.Vertices[triangle.B].Z + obj.Vertices[triangle.C].Z) / 3;
                overrides[index] = Math.Clamp((int)(((z - minZ) / height) * _selected.Colors.Count), 0, _selected.Colors.Count - 1);
            }
            layer.TriangleOverrides[obj.Index] = overrides;
        }
        _proposalLayers[proposal].Add(layer); RecomposeSelected(); RefreshLayers(); _dirty = true;
    }

    void DeleteLayer_Click(object sender, RoutedEventArgs e)
    {
        var proposal = SelectedProposalIndex();
        var layer = SelectedLayer();
        if (proposal < 0 || layer is null || layer.Kind == ColorLayerKind.BaseColor) return;
        PushUndo();
        _proposalLayers[proposal].RemoveAll(item => item.Id == layer.Id);
        RecomposeSelected();
        RefreshLayers();
        _pattern = _proposalLayers[proposal].LastOrDefault(item => item.Pattern is not null)?.Pattern;
        UpdatePatternText();
        _dirty = true;
    }

    void MoveLayerUp_Click(object sender, RoutedEventArgs e) => MoveSelectedLayer(1);
    void MoveLayerDown_Click(object sender, RoutedEventArgs e) => MoveSelectedLayer(-1);

    void MoveSelectedLayer(int delta)
    {
        var proposal = SelectedProposalIndex();
        var layer = SelectedLayer();
        if (proposal < 0 || layer is null) return;
        var layers = _proposalLayers[proposal];
        var from = layers.FindIndex(item => item.Id == layer.Id);
        var to = Math.Clamp(from + delta, 0, layers.Count - 1);
        if (from == to) return;
        PushUndo();
        layers.RemoveAt(from);
        layers.Insert(to, layer);
        RecomposeSelected();
        RefreshLayers();
        _dirty = true;
    }

    void LayerProperty_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingControls) return;
        var proposal = SelectedProposalIndex();
        var layer = SelectedLayer();
        if (proposal < 0 || layer is null) return;
        var index = _proposalLayers[proposal].FindIndex(item => item.Id == layer.Id);
        _proposalLayers[proposal][index] = layer with { IsVisible = LayerVisible.IsChecked == true, IsLocked = LayerLocked.IsChecked == true };
        RecomposeSelected();
        RefreshLayers();
        _dirty = true;
    }

    async void PaintMode_Changed(object sender, RoutedEventArgs e)
    {
        if (PaintMode is null || PaintModeMenu is null) return;
        var enabled = sender == PaintModeMenu ? PaintModeMenu.IsChecked : PaintMode.IsChecked == true;
        if (PaintMode.IsChecked != enabled) PaintMode.IsChecked = enabled;
        if (PaintModeMenu.IsChecked != enabled) PaintModeMenu.IsChecked = enabled;
        if (!enabled)
        {
            _paintStroke = false;
            Viewer?.ReleaseMouseCapture();
            HidePaintStrokeFill();
        }
        else if (_doc is not null && _doc.TriangleCount > FullDetailTriangleLimit && _previewMeshes.Count == 0)
        {
            SetBusy(true, "Préparation du niveau de détail pour la peinture…");
            try { _previewMeshes = await Task.Run(() => BuildPreviewMeshes(_doc)); }
            finally { SetBusy(false); }
        }
        UpdateBrushCursorVisibility();
        Render();
        StatusText.Text = enabled ? "Mode zones actif : choisissez Face par face ou Pinceau fluide. Clic droit pour tourner." : "Sélection de zones désactivée.";
    }

    void PaintTool_Changed(object sender, SelectionChangedEventArgs e)
    {
        _heightRangePreview.Clear();
        _hoverTrianglePreview.Clear();
        UpdateBrushCursorVisibility();
        Render();
    }
    void UpdateBrushCursorVisibility()
    {
        if (PaintBrushCursor is null || PaintMode is null || PaintTool is null) return;
        PaintBrushCursor.Visibility = PaintMode.IsChecked == true && PaintTool.SelectedIndex is 1 or 4 && Viewer.IsMouseOver ? Visibility.Visible : Visibility.Collapsed;
    }

    async Task SelectPaintZone(RayMeshGeometry3DHitTestResult hit, GeometryModel3D model)
    {
        if (_doc is null || !_modelObjects.TryGetValue(model, out var objectIndex)) return;
        var obj = _doc.Objects.FirstOrDefault(item => item.Index == objectIndex); if (obj is null) return;
        var sourceTriangle = FindSourceTriangle(obj, hit);
        if (sourceTriangle < 0) return;
        _lastSelectionObject = objectIndex;
        _lastSelectionTriangle = sourceTriangle;
        var brushScale = PaintTool.SelectedIndex is 0 or 5 ? 0 : PaintBrushSize.SelectedIndex switch { 0 => .0015, 1 => .003, 2 => .008, 3 => .02, _ => .05 };
        SetBusy(true, "Sélection de la zone…");
        try
        {
            HashSet<int> selected;
            if (PaintTool.SelectedIndex == 5)
            {
                var diagonal = _objectDiagonals.GetValueOrDefault(obj.Index, _radius * 2);
                var halfRange = Math.Max(.001, diagonal * PaintHeightRange.Value / 100d / 2d);
                selected = obj.Triangles.Select((triangle, index) => (triangle, index))
                    .Where(item => Math.Abs((obj.Vertices[item.triangle.A].Z + obj.Vertices[item.triangle.B].Z + obj.Vertices[item.triangle.C].Z) / 3d - hit.PointHit.Z) <= halfRange)
                    .Select(item => item.index).ToHashSet();
            }
            else if (brushScale == 0) selected = [sourceTriangle];
            else
            {
                var diagonal = _objectDiagonals.GetValueOrDefault(obj.Index, _radius * 2);
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
        if (hit.ModelHit is GeometryModel3D renderedModel &&
            _renderTriangleLookup.TryGetValue(renderedModel, out var renderedLookup))
            return renderedLookup.GetValueOrDefault(TriangleKey(hit.VertexIndex1, hit.VertexIndex2, hit.VertexIndex3), -1);
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

    async void ApplyPaintSelection_Click(object sender, RoutedEventArgs e) => await CommitPaintSelectionAsync();

    async Task<bool> CommitPaintSelectionAsync(bool captureUndo = true)
    {
        if (_doc is null || _selected is null || _selected.Colors.Count == 0 || _paintSelection.Count == 0) { MessageBox.Show("Activez la sélection de zones et cliquez sur le modèle avant d’appliquer une couleur.", "Coloration manuelle"); return false; }
        var selectedLayer = SelectedLayer();
        if (selectedLayer is { Kind: not ColorLayerKind.BaseColor, IsLocked: true })
        {
            MessageBox.Show("Ce calque est verrouillé.", "Coloration manuelle");
            return false;
        }
        var colorIndex = Math.Clamp(PaintColorCombo.SelectedIndex, 0, _selected.Colors.Count - 1);
        if (captureUndo) PushUndo();
        var proposalIndex = SelectedProposalIndex();
        var selectionCount = _paintSelection.Values.Sum(set => set.Count);
        // Aggressive local subdivision under the brush — makes painted boundaries smooth
        // like in Orca/Bambu Studio. Levels are capped by selection size to keep performance.
        // The Triangle tool intentionally paints one existing face. Refining
        // it into 1,024 children provides no precision benefit and used to
        // destabilise the next click. Local refinement is reserved for
        // continuous tools (circle, sphere, rectangle and lasso).
        var levels = PaintRefinementLevels(_doc.TriangleCount, selectionCount, PaintTool.SelectedIndex);
        if (levels > 0)
        {
            SetBusy(true, "Subdivision locale sous la zone peinte…");
            try
            {
                var refinement = await Task.Run(() => _localRefinement.Refine(_doc, _proposals, _layerBases, _proposalLayers, _paintSelection, levels));
                _doc = refinement.Document; _proposals = refinement.Proposals; _layerBases = refinement.Bases; _proposalLayers = refinement.Layers;
                // Topology changed: cached vertex-triplet → triangle indexes
                // belong to the previous mesh and would make every following
                // click target the wrong face (or none at all).
                _triangleLookup.Clear();
                _renderTriangleLookup.Clear();
                ObjectsList.ItemsSource = _doc.Objects;
                ObjectsList.SelectedIndex = Math.Clamp(ObjectsList.SelectedIndex, 0, Math.Max(0, _doc.Objects.Count - 1));
                _paintSelection.Clear(); foreach (var pair in refinement.Selection) _paintSelection[pair.Key] = pair.Value;
                // Subdivision only inserts points on existing edges; it never
                // changes the bounding box. Do not recompute bounds here: that
                // would reset the current camera target after every stroke.
                SelectProposal(proposalIndex);
                if (refinement.AddedTriangles > 0) StatusText.Text = $"{refinement.AddedTriangles:N0} triangles ajoutés uniquement sous la peinture.";
            }
            finally { SetBusy(false); }
        }
        if (_pendingPrecisePaint is { } precisePaint)
        {
            // Do not turn every coarse source face touched by the cursor into a
            // solid block.  Re-evaluate the same visible stroke on the locally
            // subdivided mesh; this is the committed paint mask.
            _paintSelection.Clear();
            foreach (var pair in precisePaint(_doc!)) _paintSelection[pair.Key] = pair.Value;
        }
        _pendingPrecisePaint = null;
        _selected = _proposals[proposalIndex];
        var layer = SelectedLayer();
        if (layer is null || layer.Kind == ColorLayerKind.BaseColor)
        {
            layer = _layerService.Create($"Peinture {_proposalLayers[proposalIndex].Count}", ColorLayerKind.Paint);
            _proposalLayers[proposalIndex].Add(layer);
        }
        if (layer.IsLocked) { MessageBox.Show("Ce calque est verrouillé.", "Coloration manuelle"); return false; }
        foreach (var pair in _paintSelection)
        {
            var obj = _doc!.Objects.First(item => item.Index == pair.Key);
            if (!layer.TriangleOverrides.TryGetValue(pair.Key, out var assignments) || assignments.Length != obj.Triangles.Count)
            {
                assignments = new int[obj.Triangles.Count];
                Array.Fill(assignments, -1);
                layer.TriangleOverrides[pair.Key] = assignments;
            }
            foreach (var triangle in pair.Value.Where(index => index >= 0 && index < assignments.Length)) assignments[triangle] = colorIndex;
        }
        var count = _paintSelection.Values.Sum(set => set.Count);
        _paintSelection.Clear();
        HidePaintStrokeFill();
        UpdatePaintSelectionText();
        RecomposeSelected();
        RefreshLayers();
        _dirty = true;
        StatusText.Text = $"{_selected.Colors[colorIndex].Name} appliquée sur {count:N0} triangles dans le calque « {layer.Name} ».";
        return true;
    }

    internal static int PaintRefinementLevels(long triangleCount, int selectionCount, int toolIndex)
    {
        if (toolIndex == 0 || triangleCount < 0 || selectionCount <= 0) return 0;
        var levels = selectionCount switch
        {
            < 500 => triangleCount < 200_000 ? 5 : 4,
            < 2000 => triangleCount < 200_000 ? 4 : 3,
            < 10000 => 3,
            _ => 2
        };
        var availableGrowth = Math.Min(MaxPaintRefinementGrowth, Math.Max(0, MaxPaintRefinementTriangles - triangleCount));
        while (levels > 0)
        {
            var childrenPerTriangle = 1L << (levels * 2);
            var estimatedGrowth = (long)selectionCount * (childrenPerTriangle - 1);
            if (estimatedGrowth <= availableGrowth) break;
            levels--;
        }
        return levels;
    }

    void ClearPaintSelection_Click(object sender, RoutedEventArgs e) { _paintSelection.Clear(); HidePaintStrokeFill(); UpdatePaintSelectionText(); Render(); StatusText.Text = "Sélection de zones effacée."; }

    void ErasePaintSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _paintSelection.Count == 0) { StatusText.Text = "Sélectionnez une zone à gommer."; return; }
        var proposalIndex = SelectedProposalIndex();
        var layer = SelectedLayer();
        if (proposalIndex < 0 || layer is null || layer.Kind == ColorLayerKind.BaseColor || layer.IsLocked)
        {
            MessageBox.Show("Sélectionnez d’abord un calque de peinture non verrouillé.", "Gomme");
            return;
        }
        PushUndo();
        var erased = 0;
        foreach (var pair in _paintSelection)
            if (layer.TriangleOverrides.TryGetValue(pair.Key, out var values))
                foreach (var triangle in pair.Value.Where(index => index >= 0 && index < values.Length)) { values[triangle] = -1; erased++; }
        _paintSelection.Clear(); HidePaintStrokeFill(); UpdatePaintSelectionText(); RecomposeSelected(); RefreshLayers(); _dirty = true;
        StatusText.Text = $"Gomme : {erased:N0} fragment(s) restauré(s) depuis le calque inférieur.";
    }

    void PickPaintColor_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _selected is null || _lastSelectionObject < 0 || _lastSelectionTriangle < 0) { StatusText.Text = "Cliquez d’abord sur une face avec l’outil Face par face."; return; }
        var composed = FinalProposal(SelectedProposalIndex());
        if (!composed.TriangleAssignments.TryGetValue(_lastSelectionObject, out var values) || _lastSelectionTriangle >= values.Length) return;
        PaintColorCombo.SelectedIndex = Math.Clamp(values[_lastSelectionTriangle], 0, _selected.Colors.Count - 1);
        StatusText.Text = $"Pipette : {_selected.Colors[PaintColorCombo.SelectedIndex].Name}.";
    }

    async void FillVisibleRegion_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _lastSelectionObject < 0 || _lastSelectionTriangle < 0) { StatusText.Text = "Cliquez d’abord sur une face de l’îlot à remplir."; return; }
        var obj = _doc.Objects.FirstOrDefault(item => item.Index == _lastSelectionObject);
        if (obj is null) return;
        SetBusy(true, "Recherche de l’îlot visible…");
        try
        {
            var island = await Task.Run(() => _smartSelection.ConnectedIsland(obj, _lastSelectionTriangle));
            _paintSelection[obj.Index] = island;
            await CommitPaintSelectionAsync();
        }
        finally { SetBusy(false); }
    }

    void UpdateHeightRangePreview(System.Windows.Point point)
    {
        if (_doc is null || DateTime.UtcNow - _lastHeightPreviewUpdate < TimeSpan.FromMilliseconds(180)) return;
        _lastHeightPreviewUpdate = DateTime.UtcNow;
        RayMeshGeometry3DHitTestResult? surface = null;
        GeometryModel3D? geometry = null;
        VisualTreeHelper.HitTest(Viewer, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult hit && hit.ModelHit is GeometryModel3D model && _modelObjects.ContainsKey(model))
            {
                surface = hit; geometry = model;
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
        if (surface is null || geometry is null || !_modelObjects.TryGetValue(geometry, out var objectIndex)) return;
        var obj = _doc.Objects.FirstOrDefault(item => item.Index == objectIndex);
        if (obj is null) return;
        var diagonal = _objectDiagonals.GetValueOrDefault(obj.Index, _radius * 2);
        var halfRange = Math.Max(.001, diagonal * PaintHeightRange.Value / 100d / 2d);
        var projection = CapturePaintProjection();
        if (projection is null) return;
        var preview = SelectVisibleTrianglesByHeight(_doc, obj.Index, surface.PointHit.Z, halfRange, projection.Value);
        if (_heightRangePreview.Count == 1 && _heightRangePreview.TryGetValue(obj.Index, out var existing) && existing.SetEquals(preview)) return;
        _heightRangePreview.Clear();
        if (preview.Count > 0) _heightRangePreview[obj.Index] = preview;
        Render();
        StatusText.Text = $"Aperçu de la plage de hauteur : {preview.Count:N0} triangles. Cliquez pour sélectionner, puis appliquez la couleur.";
    }

    void UpdateTrianglePreview(System.Windows.Point point)
    {
        RayMeshGeometry3DHitTestResult? surface = null;
        GeometryModel3D? geometry = null;
        VisualTreeHelper.HitTest(Viewer, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult hit && hit.ModelHit is GeometryModel3D model && _modelObjects.ContainsKey(model))
            {
                surface = hit; geometry = model;
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
        if (_doc is null || surface is null || geometry is null || !_modelObjects.TryGetValue(geometry, out var objectIndex)) return;
        var obj = _doc.Objects.FirstOrDefault(item => item.Index == objectIndex);
        if (obj is null) return;
        var triangle = FindSourceTriangle(obj, surface);
        if (triangle < 0) return;
        if (_hoverTrianglePreview.TryGetValue(objectIndex, out var current) && current.SetEquals([triangle])) return;
        _hoverTrianglePreview.Clear();
        _hoverTrianglePreview[objectIndex] = [triangle];
        Render();
        StatusText.Text = "Aperçu : cliquez pour appliquer directement la couleur choisie à ce triangle.";
    }

    async void ResetColoring_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        var source = _doc.Path;
        if (!File.Exists(source))
        {
            MessageBox.Show("Le fichier importé d’origine n’est plus accessible. Réouvrez-le pour réinitialiser sa coloration.", "Réinitialiser la coloration", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show("Retrouver exactement les couleurs et le maillage du fichier importé ? Les calques de peinture actuels seront retirés.", "Réinitialiser la coloration", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await LoadModel(source);
    }

    async void SmartSelect_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _selected is null || sender is not FrameworkElement { Tag: string action }) return;
        if (action is "island" or "angle" && (_lastSelectionObject < 0 || _lastSelectionTriangle < 0))
        {
            MessageBox.Show("Cliquez d’abord sur une face du modèle pour définir le point de départ.", "Sélection intelligente");
            return;
        }
        var selectedColorIndex = Math.Clamp(PaintColorCombo.SelectedIndex, 0, _selected.Colors.Count - 1);
        var cameraLookDirection = (Viewer.Camera as ProjectionCamera)?.LookDirection;
        var selectedObjectIndex = ObjectsList.SelectedItem is ModelObject selectedObject ? selectedObject.Index : _lastSelectionObject;
        SetBusy(true, "Analyse intelligente des surfaces…");
        try
        {
            var additions = await Task.Run(() =>
            {
                var result = new Dictionary<int, HashSet<int>>();
                if (action is "island" or "angle")
                {
                    var obj = _doc.Objects.First(item => item.Index == _lastSelectionObject);
                    result[obj.Index] = action == "island"
                        ? _smartSelection.ConnectedIsland(obj, _lastSelectionTriangle)
                        : _smartSelection.SimilarFaces(obj, _lastSelectionTriangle, 25, true);
                }
                else if (action == "color")
                {
                    foreach (var obj in _doc.Objects) result[obj.Index] = _smartSelection.ByColor(obj, _selected, selectedColorIndex);
                }
                else if (action == "object")
                {
                    var obj = _doc.Objects.FirstOrDefault(item => item.Index == selectedObjectIndex) ?? _doc.Objects[0];
                    result[obj.Index] = Enumerable.Range(0, obj.Triangles.Count).ToHashSet();
                }
                else if (cameraLookDirection is Vector3D lookDirection)
                {
                    foreach (var obj in _doc.Objects) result[obj.Index] = _smartSelection.FacingCamera(obj, lookDirection);
                }
                return result;
            });
            foreach (var pair in additions)
            {
                if (!_paintSelection.TryGetValue(pair.Key, out var current)) _paintSelection[pair.Key] = current = [];
                current.UnionWith(pair.Value);
            }
            UpdatePaintSelectionText();
            Render();
            StatusText.Text = $"Sélection intelligente terminée · {_paintSelection.Values.Sum(set => set.Count):N0} triangles.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Sélection intelligente impossible", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    async void SemanticSelect_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || SemanticRegionCombo.SelectedItem is not ComboBoxItem item) return;
        var region = item.Content?.ToString() ?? "";
        SetBusy(true, $"Détection automatique : {region.ToLowerInvariant()}…");
        try
        {
            var additions = await Task.Run(() => _smartSelection.SemanticRegion(_doc, region));
            foreach (var pair in additions)
            {
                if (!_paintSelection.TryGetValue(pair.Key, out var current)) _paintSelection[pair.Key] = current = [];
                current.UnionWith(pair.Value);
            }
            UpdatePaintSelectionText(); Render();
            StatusText.Text = additions.Count == 0
                ? $"Aucune zone « {region} » détectée automatiquement."
                : $"{region} détecté(s) · {_paintSelection.Values.Sum(set => set.Count):N0} triangles sélectionnés. Vérifiez puis appliquez.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Détection automatique", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { SetBusy(false); }
    }
    void UpdatePaintSelectionText() { if (PaintSelectionText is null) return; var count = _paintSelection.Values.Sum(set => set.Count); PaintSelectionText.Text = $"{count:N0} triangle{(count > 1 ? "s" : "")} sélectionné{(count > 1 ? "s" : "")}"; }

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _selected is null) { MessageBox.Show("Importez et sélectionnez une proposition avant l’export."); return; }
        var exportProposal = FinalProposal(SelectedProposalIndex());
        var exportProfile = SelectExportProfile(exportProposal, false);
        if (exportProfile is null) return;
        var dialog = new SaveFileDialog { Filter = "Fichiers 3MF (*.3mf)|*.3mf", FileName = ExportFileName(_doc.Path, exportProfile, _colorCount), InitialDirectory = Directory.Exists(_settings.ExportFolder) ? _settings.ExportFolder : null, OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        SetBusy(true, "Exportation et vérification du fichier 3MF…");
        try
        {
            var materials = MaterialsFor(exportProposal);
            var report = await Task.Run(() => _service.ExportAndValidate(_doc, exportProposal, dialog.FileName, _settings.VerifyAfterExport, materials, exportProfile));
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

    async void OpenPreferredSlicer_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null || _selected is null) { MessageBox.Show("Importez et sélectionnez une proposition avant d’ouvrir le slicer."); return; }
        var proposal = FinalProposal(SelectedProposalIndex());
        var exportProfile = SelectExportProfile(proposal, false);
        if (exportProfile is null) return;
        SetBusy(true, $"Préparation du modèle coloré pour {PreferredSlicerName()}…");
        try
        {
            var previewFolder = Path.Combine(Path.GetTempPath(), "PolyChrom3MF", "SlicerPreview");
            Directory.CreateDirectory(previewFolder);
            var path = Path.Combine(previewFolder, ExportFileName(_doc.Path, exportProfile, _colorCount));
            var materials = MaterialsFor(proposal);
            await Task.Run(() => _service.ExportAndValidate(_doc, proposal, path, true, materials, exportProfile));
            _lastSlicerFile = path;
            OpenInSlicer(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Le modèle coloré n’a pas pu être préparé pour {PreferredSlicerName()}.\n\n{ex.Message}", "Ouverture dans le slicer impossible", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Préparation pour le slicer impossible.";
        }
        finally { SetBusy(false); }
    }

    static string ExportFileName(string sourcePath, ExportProfileSettings profile, int colorCount)
    {
        var source = Path.GetFileNameWithoutExtension(sourcePath);
        var markers = new[] { "_AMS", "+AMS", "_Bambu", "+Bambu", "_BBL", "+BBL", "_X1C", "+X1C" };
        var markerIndex = markers
            .Select(marker => source.IndexOf(marker, StringComparison.OrdinalIgnoreCase))
            .Where(index => index > 0)
            .DefaultIfEmpty(source.Length)
            .Min();
        source = source[..markerIndex].Trim(' ', '_', '-', '+');
        if (source.Length == 0) source = "Modele";
        var printer = string.IsNullOrWhiteSpace(profile.PrinterPreset) ? profile.SlicerName : profile.PrinterPreset;
        string Safe(string value) => string.Concat(value
            .Take(80)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return $"{Safe(source)}_{Safe(printer)}_{colorCount}Couleurs.3mf";
    }

    ExportProfileSettings? SelectExportProfile(ColorProposal proposal, bool force)
    {
        var existing = _settings.ExportProfiles.FirstOrDefault(profile =>
            profile.Name.Equals(_settings.DefaultExportProfile, StringComparison.OrdinalIgnoreCase));
        if (!force && !_settings.AlwaysConfirmExportProfile && existing is not null && File.Exists(existing.SlicerPath))
        {
            _settings.PreferredSlicer = existing.SlicerPath;
            return existing;
        }
        var detected = _slicerDetection.Detect([_settings.PreferredSlicer]);
        var dialog = new ExportProfileWindow(_settings, proposal, detected) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null) return null;
        _settingsService.Save(_settings);
        EnsurePreferredSlicer();
        UpdateSlicerButton();
        return dialog.Result;
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
            _projects.Save(dialog.FileName, _doc, _proposals, _proposals.IndexOf(_selected!), _yaw, _pitch, _zoom, _generation, _funMode, _colorCount, _pattern, _proposalLayers, _layerBases, _logoProject);
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
            _generation = project.Generation; _funMode = project.FunMode; _colorCount = Math.Clamp(project.ColorCount, 2, 32); _settings.ColorCount = _colorCount;
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
            _pattern = project.Pattern;
            _logoProject = DecodeLogoProject(project.LogoArchive);
            UpdatePatternText();
            if (project.Layers is not null && project.Layers.Count == _proposals.Count)
                _proposalLayers = project.Layers.Select(group => group.Select(layer => layer.Duplicate(layer.Name) with { Id = layer.Id }).ToList()).ToList();
            else _proposalLayers = _proposals.Select(_ => new List<ColorLayer> { _layerService.Create("Couleur de base", ColorLayerKind.BaseColor) }).ToList();
            _layerBases = _proposals.Select(Clone).ToList();
            if (project.LayerBaseAssignments is not null && project.LayerBaseTriangles is not null)
                for (var p = 0; p < Math.Min(_layerBases.Count, Math.Min(project.LayerBaseAssignments.Count, project.LayerBaseTriangles.Count)); p++)
                {
                    _layerBases[p].Assignments.Clear();
                    foreach (var pair in project.LayerBaseAssignments[p]) _layerBases[p].Assignments[pair.Key] = pair.Value;
                    _layerBases[p].TriangleAssignments.Clear();
                    foreach (var pair in project.LayerBaseTriangles[p]) _layerBases[p].TriangleAssignments[pair.Key] = (int[])pair.Value.Clone();
                }
            _logoBaseProposals = _logoProject.Layers.Count > 0 ? _layerBases.Select(Clone).ToList() : null;
            _yaw = project.Yaw; _pitch = project.Pitch; _zoom = project.Zoom; SelectProposal(project.SelectedProposal); RefreshBindings(); RefreshLogoLayers(); Render(); _dirty = false;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Projet impossible à ouvrir", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _doc = null; _lastSlicerFile = null; _pattern = null; _logoProject = new LogoProject(); _logoBaseProposals = null; _paintSelection.Clear(); _triangleLookup.Clear(); _renderTriangleLookup.Clear(); _previewMeshes.Clear(); _undo.Clear(); _redo.Clear(); UpdatePatternText(); UpdatePaintSelectionText(); _proposals.Clear(); _layerBases.Clear(); _proposalLayers.Clear(); RefreshLayers(); RefreshLogoLayers(); _selected = null; ObjectsList.ItemsSource = null; Proposals.ItemsSource = null; SelectedObjectText.Text = "Aucun objet sélectionné"; HintText.Visibility = Visibility.Visible; FileText.Text = "Aucun fichier chargé"; InfoText.Text = DimensionsText.Text = StatsText.Text = ""; ApplyButton.IsEnabled = false; OpenSlicerButton.IsEnabled = false; _dirty = false; Render();
    }

    bool ConfirmDiscard() => !_dirty || MessageBox.Show("Les modifications non enregistrées seront perdues. Continuer ?", "PolyChrom 3MF", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    void PushUndo() { if (_proposals.Count == 0) return; _undo.Push(Capture()); _redo.Clear(); }
    EditorState Capture() => new(_proposals.IndexOf(_selected!), _generation, _funMode, _colorCount, _proposals.Select(Clone).ToList(), _pattern, _doc, _layerBases.Select(Clone).ToList(), CloneLayers(_proposalLayers), CloneLogoProject(_logoProject), _logoBaseProposals?.Select(Clone).ToList());
    static ColorProposal Clone(ColorProposal p) => new(p.Name, p.Description, p.Colors.Select(c => new PaletteColor(c.Name, c.Hex)).ToList(), new Dictionary<int, int>(p.Assignments)) { TriangleAssignments = p.TriangleAssignments.ToDictionary(pair => pair.Key, pair => (int[])pair.Value.Clone()) };
    static List<List<ColorLayer>> CloneLayers(IEnumerable<IEnumerable<ColorLayer>> groups) => groups.Select(group => group.Select(layer => layer.Duplicate(layer.Name) with { Id = layer.Id }).ToList()).ToList();
    void Restore(EditorState state) { _generation = state.Generation; _funMode = state.FunMode; _colorCount = state.ColorCount; _pattern = state.Pattern; _doc = state.Document; _logoProject = CloneLogoProject(state.Logos); _logoBaseProposals = state.LogoBases?.Select(Clone).ToList(); _paintSelection.Clear(); UpdatePatternText(); UpdatePaintSelectionText(); ProposalsTitle.Text = $"4 PROPOSITIONS · {_colorCount} COULEURS"; _loadingControls = true; FunMode.IsChecked = _funMode; _loadingControls = false; _proposals = state.Proposals.Select(Clone).ToList(); _layerBases = state.LayerBases.Select(Clone).ToList(); _proposalLayers = CloneLayers(state.Layers); SelectProposal(state.Selected); RefreshBindings(); ComputeBounds(); Render(); _dirty = true; }
    void Undo_Click(object sender, RoutedEventArgs e) { if (_undo.Count == 0) return; _redo.Push(Capture()); Restore(_undo.Pop()); StatusText.Text = "Modification annulée."; }
    void Redo_Click(object sender, RoutedEventArgs e) { if (_redo.Count == 0) return; _undo.Push(Capture()); Restore(_redo.Pop()); StatusText.Text = "Modification rétablie."; }
    void RefreshBindings()
    {
        var selectedPaintColor = PaintColorCombo.SelectedIndex;
        Proposals.ItemsSource = null; Proposals.ItemsSource = _proposals;
        ObjectColorCombo.ItemsSource = null; ObjectColorCombo.ItemsSource = _selected?.Colors;
        PaintColorCombo.ItemsSource = null; PaintColorCombo.ItemsSource = _selected?.Colors;
        if (_selected is not null && _selected.Colors.Count > 0)
            PaintColorCombo.SelectedIndex = Math.Clamp(selectedPaintColor < 0 ? 0 : selectedPaintColor, 0, _selected.Colors.Count - 1);
    }

    void BuildScene()
    {
        Viewer.Children.Add(new ModelVisual3D { Content = new AmbientLight(Color.FromRgb(55, 55, 55)) });
        Viewer.Children.Add(new ModelVisual3D { Content = new DirectionalLight(Color.FromRgb(220, 220, 220), new Vector3D(-1, -1, -2)) });
        UpdateCamera();
    }

    void ComputeBounds()
    {
        if (_doc is null) return;
        var minX = double.MaxValue; var minY = double.MaxValue; var minZ = double.MaxValue;
        var maxX = double.MinValue; var maxY = double.MinValue; var maxZ = double.MinValue;
        _objectDiagonals.Clear();
        foreach (var obj in _doc.Objects)
        {
            var objectMinX = double.MaxValue; var objectMinY = double.MaxValue; var objectMinZ = double.MaxValue;
            var objectMaxX = double.MinValue; var objectMaxY = double.MinValue; var objectMaxZ = double.MinValue;
            foreach (var vertex in obj.Vertices)
            {
                minX = Math.Min(minX, vertex.X); minY = Math.Min(minY, vertex.Y); minZ = Math.Min(minZ, vertex.Z);
                maxX = Math.Max(maxX, vertex.X); maxY = Math.Max(maxY, vertex.Y); maxZ = Math.Max(maxZ, vertex.Z);
                objectMinX = Math.Min(objectMinX, vertex.X); objectMinY = Math.Min(objectMinY, vertex.Y); objectMinZ = Math.Min(objectMinZ, vertex.Z);
                objectMaxX = Math.Max(objectMaxX, vertex.X); objectMaxY = Math.Max(objectMaxY, vertex.Y); objectMaxZ = Math.Max(objectMaxZ, vertex.Z);
            }
            _objectDiagonals[obj.Index] = Math.Sqrt(Math.Pow(objectMaxX - objectMinX, 2) + Math.Pow(objectMaxY - objectMinY, 2) + Math.Pow(objectMaxZ - objectMinZ, 2));
        }
        _modelMinZ = minZ;
        _modelCenter = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        _center = _modelCenter;
        _radius = Math.Max(1, Math.Sqrt(_doc.SizeX * _doc.SizeX + _doc.SizeY * _doc.SizeY + _doc.SizeZ * _doc.SizeZ) / 2);
    }

    // Recalculate the framing after geometry or coloration changes. The deferred
    // pass is important for the GPU viewport: its swap chain can finish sizing
    // after the import task, otherwise the first frame may appear off-centre.
    void RecenterView()
    {
        if (_doc is null) return;
        ComputeBounds();
        FitCamera();
        Render();
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            if (_doc is null) return;
            ComputeBounds();
            FitCamera();
            Render();
        }));
    }

    ColorProposal FinalProposal(int index) => _doc is not null && index >= 0 && index < _layerBases.Count && index < _proposalLayers.Count
        ? Rename(_layerService.Compose(_doc, _layerBases[index], _proposalLayers[index]), _proposals[index].Name, _proposals[index].Description)
        : _selected ?? throw new InvalidOperationException("Aucune proposition sélectionnée.");

    IReadOnlyList<string> MaterialsFor(ColorProposal proposal)
    {
        var byColor = _settings.FilamentColors
            .Select((hex, index) => (Hex: hex, Material: _settings.FilamentMaterials.ElementAtOrDefault(index) ?? "PLA"))
            .GroupBy(item => item.Hex, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Material, StringComparer.OrdinalIgnoreCase);
        return proposal.Colors.Select(color => byColor.GetValueOrDefault(color.Hex, "PLA")).ToList();
    }

    string? PromptText(string title, string label, string initial)
    {
        var input = new TextBox { Text = initial, MinWidth = 300, Margin = new Thickness(0, 6, 0, 12) };
        var window = new Window { Title = title, Owner = this, Width = 390, Height = 180, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 90 }; var ok = new Button { Content = "Valider", IsDefault = true, MinWidth = 90 };
        ok.Click += (_, _) => window.DialogResult = true; actions.Children.Add(cancel); actions.Children.Add(ok); panel.Children.Add(actions); window.Content = panel;
        return window.ShowDialog() == true ? input.Text : null;
    }

    void SaveStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) { MessageBox.Show("Sélectionnez une proposition avant d’enregistrer un style."); return; }
        var dialog = new SaveFileDialog
        {
            Filter = "Style PolyChrom (*.polystyle)|*.polystyle",
            FileName = _selected.Name.Replace(' ', '_') + ".polystyle",
            InitialDirectory = _styleLibrary.LibraryFolder
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var proposal = SelectedProposalIndex();
            var style = new PolyStyleData(_selected.Name, _selected.Description, _selected.Colors.Select(color => color.Hex).ToList(), null,
                proposal >= 0 && proposal < _proposalLayers.Count ? _proposalLayers[proposal].Select(layer => layer.Kind).ToList() : [],
                _settings.PrinterName, _settings.MaterialSlots, DateTimeOffset.UtcNow);
            _styleLibrary.Save(dialog.FileName, style);
            StatusText.Text = $"Style partagé enregistré : {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Style impossible à enregistrer", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    async void StyleGallery_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new StyleGalleryWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedStyle is null) return;
        if (_doc is null) { MessageBox.Show("Importez d’abord un modèle sur lequel appliquer ce style."); return; }
        await ApplyStyle(dialog.SelectedStyle);
    }

    Task ApplyStyle(PolyStyleData style)
    {
        if (_doc is null || _selected is null) return Task.CompletedTask;
        PushUndo();
        var proposalIndex = SelectedProposalIndex();
        _colorCount = style.Colors.Count;
        _settings.ColorCount = _colorCount;
        for (var index = 0; index < style.Colors.Count; index++)
        {
            var color = new PaletteColor($"Style {index + 1}", style.Colors[index]);
            if (index < _selected.Colors.Count) _selected.Colors[index] = color; else _selected.Colors.Add(color);
        }
        if (_selected.Colors.Count > style.Colors.Count) _selected.Colors.RemoveRange(style.Colors.Count, _selected.Colors.Count - style.Colors.Count);
        if (proposalIndex < _layerBases.Count)
        {
            _layerBases[proposalIndex].Colors.Clear();
            _layerBases[proposalIndex].Colors.AddRange(_selected.Colors.Select(color => new PaletteColor(color.Name, color.Hex)));
        }
        SelectProposal(proposalIndex); RefreshBindings(); RecenterView(); UpdatePatternText();
        ProposalsTitle.Text = $"4 PROPOSITIONS · {_colorCount} COULEURS";
        _dirty = true; StatusText.Text = style.Pattern is null
            ? $"Style « {style.Name} » appliqué."
            : $"Palette du style « {style.Name} » appliquée. Son ancien motif image a été ignoré dans cette version stable.";
        return Task.CompletedTask;
    }

    internal static Dictionary<int, PreviewMesh> BuildPreviewMeshes(ModelDocument document)
    {
        var result = new Dictionary<int, PreviewMesh>();
        if (document.TriangleCount <= FullDetailTriangleLimit) return result;
        foreach (var obj in document.Objects)
        {
            var target = Math.Max(200, (int)Math.Round(LargeModelPreviewTarget * (obj.Triangles.Count / (double)document.TriangleCount)));
            if (obj.Triangles.Count <= target * 1.15) continue;
            result[obj.Index] = SimplifyForPreview(obj, target);
        }
        return result;
    }

    async Task RebuildPreviewMeshesAsync(ModelDocument document)
    {
        _previewMeshes = document.TriangleCount > FullDetailTriangleLimit
            ? await Task.Run(() => BuildPreviewMeshes(document))
            : [];
    }

    internal static PreviewMesh SimplifyForPreview(ModelObject obj, int targetTriangles)
    {
        if (obj.Vertices.Count == 0 || obj.Triangles.Count == 0) return new PreviewMesh([], []);
        var minX = double.MaxValue; var minY = double.MaxValue; var minZ = double.MaxValue;
        var maxX = double.MinValue; var maxY = double.MinValue; var maxZ = double.MinValue;
        foreach (var vertex in obj.Vertices)
        {
            minX = Math.Min(minX, vertex.X); minY = Math.Min(minY, vertex.Y); minZ = Math.Min(minZ, vertex.Z);
            maxX = Math.Max(maxX, vertex.X); maxY = Math.Max(maxY, vertex.Y); maxZ = Math.Max(maxZ, vertex.Z);
        }
        var largestSide = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
        if (largestSide <= 1e-12)
            return new PreviewMesh(obj.Vertices.ToList(), obj.Triangles.Select((triangle, index) => new PreviewTriangle(triangle.A, triangle.B, triangle.C, index)).ToList());
        var gridResolution = Math.Clamp((int)Math.Sqrt(Math.Max(200, targetTriangles) / 6d), 18, 420);
        var cellSize = largestSide / gridResolution;
        var voxelIndices = new Dictionary<VoxelKey, int>(Math.Min(obj.Vertices.Count, Math.Max(1024, targetTriangles)));
        var vertices = new List<Vertex>(Math.Min(obj.Vertices.Count, Math.Max(1024, targetTriangles)));
        var triangles = new List<PreviewTriangle>(Math.Min(obj.Triangles.Count, Math.Max(1024, targetTriangles)));
        var uniqueTriangles = new HashSet<(int A, int B, int C)>();

        int VertexIndex(Vertex vertex)
        {
            var key = new VoxelKey(
                (int)Math.Floor((vertex.X - minX) / cellSize),
                (int)Math.Floor((vertex.Y - minY) / cellSize),
                (int)Math.Floor((vertex.Z - minZ) / cellSize));
            if (voxelIndices.TryGetValue(key, out var existing)) return existing;
            var index = vertices.Count;
            vertices.Add(vertex);
            voxelIndices[key] = index;
            return index;
        }

        for (var sourceIndex = 0; sourceIndex < obj.Triangles.Count; sourceIndex++)
        {
            var source = obj.Triangles[sourceIndex];
            var a = VertexIndex(obj.Vertices[source.A]);
            var b = VertexIndex(obj.Vertices[source.B]);
            var c = VertexIndex(obj.Vertices[source.C]);
            if (a == b || b == c || a == c) continue;
            var key = TriangleKey(a, b, c);
            if (!uniqueTriangles.Add(key)) continue;
            triangles.Add(new PreviewTriangle(a, b, c, sourceIndex));
        }
        return new PreviewMesh(vertices, triangles);
    }

    internal sealed record PreviewMesh(List<Vertex> Vertices, List<PreviewTriangle> Triangles);
    internal sealed record PreviewTriangle(int A, int B, int C, int SourceIndex);
    readonly record struct VoxelKey(int X, int Y, int Z);

    void Render()
    {
        // Always use the solid compatibility renderer. The previous Direct3D
        // path produced a dotted/translucent result on dense models.
        GpuHost.Visibility = Visibility.Collapsed;
        Viewer.Visibility = Visibility.Visible;
        while (Viewer.Children.Count > 2) Viewer.Children.RemoveAt(2);
        _modelObjects.Clear();
        _renderTriangleLookup.Clear();
        if (_doc is null) return;
        if (_grid) AddPlate();
        foreach (var obj in _doc.Objects)
        {
            _previewMeshes.TryGetValue(obj.Index, out var preview);
            var renderVertices = preview?.Vertices ?? obj.Vertices;
            var renderTriangleCount = preview?.Triangles.Count ?? obj.Triangles.Count;
            var positions = new Point3DCollection(renderVertices.Select(v => new Point3D(v.X, v.Y, v.Z)));
            positions.Freeze();

            // Compute per-vertex normals for smooth shading (averaged face normals).
            // This eliminates the "flat triangle" look and makes painted areas appear smooth.
            var normalSums = new Vector3D[renderVertices.Count];
            var fullTriangleCount = preview?.Triangles.Count ?? obj.Triangles.Count;
            for (var ti = 0; ti < fullTriangleCount; ti++)
            {
                var pt = preview?.Triangles[ti];
                var t = pt is null ? obj.Triangles[ti] : new Triangle(pt.A, pt.B, pt.C);
                if (t.A >= renderVertices.Count || t.B >= renderVertices.Count || t.C >= renderVertices.Count) continue;
                var va = renderVertices[t.A]; var vb = renderVertices[t.B]; var vc = renderVertices[t.C];
                var faceNormal = Vector3D.CrossProduct(
                    new Vector3D(vb.X - va.X, vb.Y - va.Y, vb.Z - va.Z),
                    new Vector3D(vc.X - va.X, vc.Y - va.Y, vc.Z - va.Z));
                if (faceNormal.LengthSquared < 1e-12) continue;
                faceNormal.Normalize();
                normalSums[t.A] += faceNormal;
                normalSums[t.B] += faceNormal;
                normalSums[t.C] += faceNormal;
            }
            var normals = new Vector3DCollection(renderVertices.Count);
            for (var ni = 0; ni < normalSums.Length; ni++)
            {
                var n = normalSums[ni];
                if (n.LengthSquared > 1e-12) n.Normalize();
                else n = new Vector3D(0, 0, 1);
                normals.Add(n);
            }
            normals.Freeze();
            var triangleColors = _selected?.TriangleAssignments.GetValueOrDefault(obj.Index);
            var colorCount = Math.Max(1, _selected?.Colors.Count ?? 1);
            var capacity = Math.Max(3, renderTriangleCount * 3 / colorCount);
            var indicesByColor = Enumerable.Range(0, colorCount).Select(_ => new Int32Collection(capacity)).ToArray();
            var selectedIndices = new Int32Collection();
            var lookupByColor = preview is null ? null : Enumerable.Range(0, colorCount).Select(_ => new Dictionary<(int, int, int), int>()).ToArray();
            Dictionary<(int, int, int), int>? selectedLookup = preview is null ? null : [];
            for (var renderTriangleIndex = 0; renderTriangleIndex < renderTriangleCount; renderTriangleIndex++)
            {
                var previewTriangle = preview?.Triangles[renderTriangleIndex];
                var sourceTriangleIndex = previewTriangle?.SourceIndex ?? renderTriangleIndex;
                var triangle = previewTriangle is null
                    ? obj.Triangles[renderTriangleIndex]
                    : new Triangle(previewTriangle.A, previewTriangle.B, previewTriangle.C);
                var isSelected = _paintSelection.TryGetValue(obj.Index, out var selectedTriangles) && selectedTriangles.Contains(sourceTriangleIndex);
                var isHeightPreview = !isSelected && _heightRangePreview.TryGetValue(obj.Index, out var previewTriangles) && previewTriangles.Contains(sourceTriangleIndex);
                var isTrianglePreview = !isSelected && !isHeightPreview && _hoverTrianglePreview.TryGetValue(obj.Index, out var hoverTriangles) && hoverTriangles.Contains(sourceTriangleIndex);
                if (isSelected || isHeightPreview || isTrianglePreview)
                {
                    selectedIndices.Add(triangle.A); selectedIndices.Add(triangle.B); selectedIndices.Add(triangle.C);
                    if (selectedLookup is not null) selectedLookup[TriangleKey(triangle.A, triangle.B, triangle.C)] = sourceTriangleIndex;
                    continue;
                }
                var assigned = triangleColors is not null && sourceTriangleIndex < triangleColors.Length ? triangleColors[sourceTriangleIndex] : _selected?.Assignments.GetValueOrDefault(obj.Index, 0) ?? 0;
                assigned = Math.Clamp(assigned, 0, colorCount - 1);
                indicesByColor[assigned].Add(triangle.A); indicesByColor[assigned].Add(triangle.B); indicesByColor[assigned].Add(triangle.C);
                lookupByColor?[assigned].Add(TriangleKey(triangle.A, triangle.B, triangle.C), sourceTriangleIndex);
            }
            for (var colorIndex = 0; colorIndex < colorCount; colorIndex++)
            {
                if (indicesByColor[colorIndex].Count == 0) continue;
                indicesByColor[colorIndex].Freeze();
                var mesh = new MeshGeometry3D { Positions = positions, Normals = normals, TriangleIndices = indicesByColor[colorIndex] };
                mesh.Freeze();
                var color = _selected?.Colors.ElementAtOrDefault(colorIndex)?.Color ?? Colors.SlateGray;
                var brush = new SolidColorBrush(color); brush.Freeze();
                // Do not tint the complete selected object. The former blue
                // emissive highlight turned imported black into navy and faded
                // reds, so the preview no longer matched the slicer.
                var diffuse = new DiffuseMaterial(brush); diffuse.Freeze();
                Material material = diffuse;
                var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
                _modelObjects[model] = obj.Index;
                if (lookupByColor is not null) _renderTriangleLookup[model] = lookupByColor[colorIndex];
                Viewer.Children.Add(new ModelVisual3D { Content = model });
            }
            if (selectedIndices.Count > 0 && PaintStrokeFill.Visibility != Visibility.Visible)
            {
                selectedIndices.Freeze(); var selectionMesh = new MeshGeometry3D { Positions = positions, Normals = normals, TriangleIndices = selectedIndices }; selectionMesh.Freeze();
                // Semi-transparent golden glow — blends smoothly instead of showing hard triangle edges
                var glowBrush = new SolidColorBrush(Color.FromArgb(100, 255, 211, 45)); glowBrush.Freeze();
                var baseBrush = new SolidColorBrush(Color.FromArgb(140, 60, 45, 15)); baseBrush.Freeze();
                var selectionGroup = new MaterialGroup();
                selectionGroup.Children.Add(new DiffuseMaterial(baseBrush));
                selectionGroup.Children.Add(new EmissiveMaterial(glowBrush));
                selectionGroup.Freeze();
                var selectionModel = new GeometryModel3D(selectionMesh, selectionGroup) { BackMaterial = selectionGroup }; _modelObjects[selectionModel] = obj.Index;
                if (selectedLookup is not null) _renderTriangleLookup[selectionModel] = selectedLookup;
                Viewer.Children.Add(new ModelVisual3D { Content = selectionModel });
            }
        }
        UpdateCamera();
    }

    void AddPlate()
    {
        var size = Math.Max(_radius * 2.4, 80); var z = _modelMinZ - .4;
        var mesh = new MeshGeometry3D { Positions = new Point3DCollection([new(_modelCenter.X-size/2,_modelCenter.Y-size/2,z),new(_modelCenter.X+size/2,_modelCenter.Y-size/2,z),new(_modelCenter.X+size/2,_modelCenter.Y+size/2,z),new(_modelCenter.X-size/2,_modelCenter.Y+size/2,z)]), TriangleIndices = new Int32Collection([0,1,2,0,2,3]) };
        Viewer.Children.Add(new ModelVisual3D { Content = new GeometryModel3D(mesh, new DiffuseMaterial(new SolidColorBrush(Color.FromArgb(80, 100, 110, 125)))) });
    }

    void UpdateCamera()
    {
        (_yaw, _pitch) = (WrapAngle(_yaw), WrapAngle(_pitch));
        var (direction, up) = OrbitFrame(_yaw, _pitch);
        var distance = _radius * 3.0 * _zoom;
        var position = _center + direction * distance; var look = _center - position;
        if (_perspective)
        {
            if (Viewer.Camera is not PerspectiveCamera camera || camera.IsFrozen)
                Viewer.Camera = new PerspectiveCamera(position, look, up, 42);
            else
            {
                camera.Position = position;
                camera.LookDirection = look;
                camera.UpDirection = up;
                camera.FieldOfView = 42;
            }
        }
        else
        {
            var width = _radius * 2.4 * _zoom;
            if (Viewer.Camera is not OrthographicCamera camera || camera.IsFrozen)
                Viewer.Camera = new OrthographicCamera(position, look, up, width);
            else
            {
                camera.Position = position;
                camera.LookDirection = look;
                camera.UpDirection = up;
                camera.Width = width;
            }
        }
        if (GpuHost.Visibility == Visibility.Visible)
            _gpuViewport.SetCamera(_center, _radius, _yaw, _pitch, _zoom);
        UpdatePatternGizmoProjection();
    }

    internal static (Vector3D Direction, Vector3D Up) OrbitFrame(double yawDegrees, double pitchDegrees)
    {
        var yaw = yawDegrees * Math.PI / 180;
        var pitch = pitchDegrees * Math.PI / 180;
        var direction = new Vector3D(Math.Cos(pitch) * Math.Sin(yaw), Math.Cos(pitch) * Math.Cos(yaw), Math.Sin(pitch));
        var up = new Vector3D(-Math.Sin(pitch) * Math.Sin(yaw), -Math.Sin(pitch) * Math.Cos(yaw), Math.Cos(pitch));
        direction.Normalize();
        up.Normalize();
        return (direction, up);
    }

    static double WrapAngle(double angle)
    {
        angle %= 360;
        return angle > 180 ? angle - 360 : angle <= -180 ? angle + 360 : angle;
    }

    void FitCamera() { _center = _modelCenter; _zoom = 1; _yaw = -40; _pitch = 25; UpdateCamera(); }
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
        _navigationInertiaTimer.Stop();
        _navigationVelocity = default;
        _last = e.GetPosition(Viewer);
        _mouseDownPoint = _last;
        _navigationMoved = false;
        Viewer.Focus();
        if (_activePatternEditor is { WaitingForSurfacePlacement: true } editor && e.ChangedButton == MouseButton.Left)
        {
            PlacePatternFromView(_last, editor);
            e.Handled = true;
            return;
        }
        if (_activePatternEditor is not null &&
            e.ChangedButton is MouseButton.Left or MouseButton.Right)
        {
            Viewer.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (PaintMode.IsChecked == true && e.ChangedButton == MouseButton.Left)
        {
            if (PaintTool.SelectedIndex == 5)
            {
                await SelectPaintFromView(_last);
            }
            else if (PaintTool.SelectedIndex == 0)
            {
                Viewer.CaptureMouse();
                _trianglePaintStroke = true;
                _triangleStrokeUndoCaptured = false;
                _triangleStrokePainted.Clear();
                await PaintTriangleAt(_last);
            }
            else if (PaintTool.SelectedIndex >= 1)
            {
                Viewer.CaptureMouse();
                _paintStroke = true;
                _paintStrokePoints.Clear();
                _paintStrokePoints.Add(_last);
                PaintStrokePreview.Points = new PointCollection([_last]);
                PaintStrokePreview.StrokeThickness = PaintTool.SelectedIndex is 1 or 4 ? BrushRadiusPixels() * 2 : 2;
                PaintStrokePreview.Visibility = Visibility.Visible;
            }
            e.Handled = true;
            return;
        }
        Viewer.CaptureMouse();
        if (PaintMode.IsChecked != true && e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            SetDynamicPivot(_last);
            _navigationMoved = true;
        }
        e.Handled = true;
    }

    void Viewer_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var p = e.GetPosition(Viewer);
        UpdateBrushCursor(p);
        var paintMode = PaintMode.IsChecked == true;
        if (paintMode && !_paintStroke && PaintTool.SelectedIndex == 5 && e.LeftButton != MouseButtonState.Pressed)
            UpdateHeightRangePreview(p);
        else if (paintMode && !_paintStroke && PaintTool.SelectedIndex == 0 && e.LeftButton != MouseButtonState.Pressed)
            UpdateTrianglePreview(p);
        if (paintMode && _trianglePaintStroke && e.LeftButton == MouseButtonState.Pressed)
        {
            _ = PaintTriangleAt(p);
            return;
        }
        if (paintMode && _paintStroke && e.LeftButton == MouseButtonState.Pressed)
        {
            if (PaintTool.SelectedIndex == 2)
            {
                PaintStrokePreview.Points = new PointCollection([
                    _paintStrokePoints[0], new(p.X, _paintStrokePoints[0].Y), p,
                    new(_paintStrokePoints[0].X, p.Y), _paintStrokePoints[0]
                ]);
                _last = p;
                return;
            }
            var dx = p.X - _last.X;
            var dy = p.Y - _last.Y;
            if (dx * dx + dy * dy >= 2.25)
            {
                _paintStrokePoints.Add(p);
                PaintStrokePreview.Points.Add(p);
                _last = p;
            }
            return;
        }
        var rotate = paintMode ? e.RightButton == MouseButtonState.Pressed : e.LeftButton == MouseButtonState.Pressed;
        if (rotate)
        {
            var dx = p.X - _last.X;
            var dy = p.Y - _last.Y;
            _yaw += dx * .35;
            _pitch += dy * .35;
            _navigationVelocity = new Vector(dx * .35, dy * .35);
        }
        else if (!paintMode && e.RightButton == MouseButtonState.Pressed)
        {
            var dx = p.X - _last.X;
            var dy = p.Y - _last.Y;
            var (_, up) = OrbitFrame(_yaw, _pitch);
            var look = _center - ((ProjectionCamera)Viewer.Camera).Position;
            look.Normalize();
            var right = Vector3D.CrossProduct(look, up);
            right.Normalize();
            var scale = _radius * _zoom * 2.2 / Math.Max(240, Viewer.ActualWidth);
            _center += right * (-dx * scale) + up * (dy * scale);
            _navigationVelocity = default;
        }
        else return;
        if ((p - _mouseDownPoint).LengthSquared > 9) _navigationMoved = true;
        _last = p;
        UpdateCamera();
    }
    async void Viewer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _trianglePaintStroke)
        {
            _trianglePaintStroke = false;
            _triangleStrokePainted.Clear();
            _queuedTrianglePaintPoint = null;
            Viewer.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Left && _paintStroke)
        {
            _paintStroke = false;
            Viewer.ReleaseMouseCapture();
            var points = PaintTool.SelectedIndex == 2
                ? new[] { _paintStrokePoints[0], e.GetPosition(Viewer) }
                : _paintStrokePoints.ToArray();
            _paintStrokePoints.Clear();
            if (points.Length > 0 && _doc is not null && CapturePaintProjection() is { } projection)
            {
                // Capture every UI-owned value before leaving the dispatcher thread.
                var document = _doc;
                var brushRadius = BrushRadiusPixels();
                SetBusy(true, "Application du trait de pinceau…");
                try
                {
                    var tool = PaintTool.SelectedIndex;
                    var selection = await Task.Run(() => tool is 1 or 4
                        ? SelectTrianglesFromScreenStroke(document, points, brushRadius, projection)
                        : SelectTrianglesFromScreenRegion(document, points, tool == 2, projection));
                    foreach (var pair in selection)
                    {
                        if (!_paintSelection.TryGetValue(pair.Key, out var current)) _paintSelection[pair.Key] = current = [];
                        current.UnionWith(pair.Value);
                    }
                    if (selection.Count == 0)
                    {
                        StatusText.Text = "Le trait n’a rencontré aucune surface visible.";
                    }
                    else
                    {
                        // Commit directly. The second projection runs only after
                        // local subdivision and therefore preserves the circular
                        // contour instead of the original large triangles.
                        _pendingPrecisePaint = refined => tool is 1 or 4
                            ? SelectTrianglesFromScreenStroke(refined, points, brushRadius, projection, precise: true)
                            : SelectTrianglesFromScreenRegion(refined, points, tool == 2, projection, precise: true);
                        await CommitPaintSelectionAsync();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Le trait n’a pas pu être appliqué.\n\n{ex.Message}", "Pinceau", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    _pendingPrecisePaint = null;
                    PaintStrokePreview.Visibility = Visibility.Collapsed;
                    PaintStrokePreview.Points.Clear();
                    SetBusy(false);
                    UpdateBrushCursor(e.GetPosition(Viewer));
                }
            }
            else PaintStrokePreview.Visibility = Visibility.Collapsed;
            return;
        }
        Viewer.ReleaseMouseCapture();
        if (PaintMode.IsChecked != true && e.ChangedButton == MouseButton.Left)
        {
            if (!_navigationMoved && (e.GetPosition(Viewer) - _mouseDownPoint).LengthSquared <= 9)
                SelectFromView(e.GetPosition(Viewer));
            else if (_settings.NavigationInertia && _navigationVelocity.Length > .18)
                _navigationInertiaTimer.Start();
        }
        e.Handled = true;
    }

    void NavigationInertiaTick(object? sender, EventArgs e)
    {
        _navigationVelocity *= .86;
        if (_navigationVelocity.Length < .04)
        {
            _navigationInertiaTimer.Stop();
            return;
        }
        _yaw += _navigationVelocity.X;
        _pitch += _navigationVelocity.Y;
        UpdateCamera();
    }

    void SetDynamicPivot(System.Windows.Point point)
    {
        Point3D? pivot = null;
        VisualTreeHelper.HitTest(Viewer, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult hit)
            {
                pivot = hit.PointHit;
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
        if (pivot is null) return;
        _center = pivot.Value;
        UpdateCamera();
        StatusText.Text = "Pivot de rotation placé sur la surface sélectionnée.";
    }

    void PlacePatternFromView(System.Windows.Point point, PatternWindow editor, bool force = false)
    {
        if (_doc is null) return;
        RayMeshGeometry3DHitTestResult? surface = null;
        VisualTreeHelper.HitTest(Viewer, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult hit &&
                hit.ModelHit is GeometryModel3D model &&
                _modelObjects.ContainsKey(model))
            {
                surface = hit;
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
        if (surface is null || surface.ModelHit is not GeometryModel3D geometry || !_modelObjects.TryGetValue(geometry, out var objectIndex))
        {
            StatusText.Text = "Cliquez directement sur une surface visible du modèle.";
            return;
        }
        var hitPoint = surface.PointHit;
        var p1 = surface.MeshHit.Positions[surface.VertexIndex1];
        var p2 = surface.MeshHit.Positions[surface.VertexIndex2];
        var p3 = surface.MeshHit.Positions[surface.VertexIndex3];
        var normal = Vector3D.CrossProduct(p2 - p1, p3 - p1);
        if (normal.LengthSquared < 1e-16) return;
        normal.Normalize();
        if (Viewer.Camera is ProjectionCamera camera)
        {
            var towardCamera = camera.Position - hitPoint;
            if (Vector3D.DotProduct(normal, towardCamera) < 0) normal = -normal;
        }
        var cameraLook = (Viewer.Camera as ProjectionCamera)?.LookDirection ?? new Vector3D(0, 1, 0);
        var cameraUp = (Viewer.Camera as ProjectionCamera)?.UpDirection ?? new Vector3D(0, 0, 1);
        cameraLook.Normalize(); cameraUp.Normalize();
        var cameraRight = Vector3D.CrossProduct(cameraLook, cameraUp);
        cameraRight.Normalize();
        var tangentU = cameraRight - normal * Vector3D.DotProduct(cameraRight, normal);
        if (tangentU.LengthSquared < 1e-12) tangentU = Vector3D.CrossProduct(cameraUp, normal);
        tangentU.Normalize();
        var tangentV = Vector3D.CrossProduct(normal, tangentU);
        tangentV.Normalize();
        if (Vector3D.DotProduct(tangentV, cameraUp) < 0) tangentV = -tangentV;
        // A logo starts at a useful "stamp" size instead of spanning most of
        // the object. The user can then enlarge it with the visible handles.
        var worldSize = Math.Max(.01, _objectDiagonals.GetValueOrDefault(objectIndex, _radius * 2) * .18);
        editor.PlaceOnSurface(
            objectIndex,
            hitPoint.X, hitPoint.Y, hitPoint.Z,
            tangentU.X, tangentU.Y, tangentU.Z,
            tangentV.X, tangentV.Y, tangentV.Z,
            normal.X, normal.Y, normal.Z,
            worldSize,
            force);
        _patternGizmoWorldPoint = hitPoint;
        _patternGizmoCenter = point;
        UpdatePatternGizmoProjection();
        ObjectsList.SelectedItem = _doc.Objects.FirstOrDefault(obj => obj.Index == objectIndex);
        StatusText.Text = "Tampon placé : vous pouvez encore régler sa taille, sa rotation et son inclinaison.";
    }

    void PatternGizmo_MoveDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _patternGizmoMode = "move";
        _patternGizmoLast = e.GetPosition(PatternGizmoCanvas);
        PatternGizmo.CaptureMouse();
        e.Handled = true;
    }

    void PatternGizmo_HandleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not FrameworkElement { Tag: string mode }) return;
        _patternGizmoMode = mode;
        _patternGizmoLast = e.GetPosition(PatternGizmoCanvas);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    void PatternGizmo_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_patternGizmoMode is null || e.LeftButton != MouseButtonState.Pressed || _activePatternEditor is null) return;
        var point = e.GetPosition(PatternGizmoCanvas);
        var delta = point - _patternGizmoLast;
        var group = PatternGizmoGroupMode.IsChecked == true;
        if (_patternGizmoMode == "move")
        {
            _patternGizmoCenter += delta;
            // Moving the frame is intentionally visual-only during the drag.
            // The expensive mesh projection is committed once on mouse-up.
            Canvas.SetLeft(PatternGizmo, _patternGizmoCenter.X - PatternGizmo.Width / 2);
            Canvas.SetTop(PatternGizmo, _patternGizmoCenter.Y - PatternGizmo.Height / 2);
        }
        else if (_patternGizmoMode == "resize")
        {
            var center = _patternGizmoCenter;
            var oldDistance = Math.Max(1, (_patternGizmoLast - center).Length);
            var newDistance = Math.Max(1, (point - center).Length);
            var factor = Math.Clamp(newDistance / oldDistance, .5, 2);
            _activePatternEditor.AdjustFromGizmo(0, 0, factor, 0, group, Viewer.ActualWidth, Viewer.ActualHeight, preview: false);
        }
        else if (_patternGizmoMode == "rotate")
        {
            var oldAngle = Math.Atan2(_patternGizmoLast.Y - _patternGizmoCenter.Y, _patternGizmoLast.X - _patternGizmoCenter.X);
            var newAngle = Math.Atan2(point.Y - _patternGizmoCenter.Y, point.X - _patternGizmoCenter.X);
            _activePatternEditor.AdjustFromGizmo(0, 0, 1, (newAngle - oldAngle) * 180 / Math.PI, group, Viewer.ActualWidth, Viewer.ActualHeight, preview: false);
        }
        _patternGizmoLast = point;
        if (_patternGizmoMode != "move") UpdatePatternGizmoProjection();
        e.Handled = true;
    }

    void PatternGizmo_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_patternGizmoMode is null) return;
        var completedMode = _patternGizmoMode;
        Mouse.Capture(null);
        _patternGizmoMode = null;
        if (_activePatternEditor is not null)
        {
            if (completedMode == "move") PlacePatternFromView(_patternGizmoCenter, _activePatternEditor, force: true);
            else _activePatternEditor.CommitGizmoPreview();
        }
        e.Handled = true;
    }

    void PatternGizmo_RightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activePatternEditor is null) return;
        PatternGizmoMenu.PlacementTarget = PatternGizmo;
        PatternGizmoMenu.IsOpen = true;
        e.Handled = true;
    }

    void PatternGizmo_Duplicate(object sender, RoutedEventArgs e)
    {
        PatternGizmoMenu.IsOpen = false;
        if (_activePatternEditor is null || sender is not FrameworkElement { Tag: string value }) return;
        var count = value == "custom"
            ? int.TryParse(PromptText("Dupliquer le logo", "Nombre total de logos (1 à 64)", "4"), out var requested) ? Math.Clamp(requested, 1, 64) : 0
            : int.Parse(value);
        if (count <= 0) return;
        _activePatternEditor.DuplicateSelected(count, PatternGizmoGroupMode.IsChecked == true);
        StatusText.Text = $"{count} occurrences du logo prêtes à être déplacées ensemble ou séparément.";
    }

    void UpdatePatternGizmo(double scale)
    {
        if (_activePatternEditor is null || PatternGizmoCanvas.Visibility != Visibility.Visible) return;
        var size = Math.Clamp(90 + scale * .8, 90, 360);
        PatternGizmo.Width = PatternGizmo.Height = size;
        PatternGizmo.RenderTransform = Transform.Identity;
        Canvas.SetLeft(PatternGizmo, _patternGizmoCenter.X - size / 2);
        Canvas.SetTop(PatternGizmo, _patternGizmoCenter.Y - size / 2);
    }

    void UpdatePatternGizmoImage(LogoAsset asset, PatternSettings settings)
    {
        var raster = LogoImageImporter.DecodeNormalizedPng(asset.Png, asset.Name);
        var pixels = new byte[raster.PixelCount * 4];
        byte tintRed = 255, tintGreen = 255, tintBlue = 255;
        if (settings.MonochromeLogo &&
            _selected is not null &&
            settings.LogoColorIndex >= 0 &&
            settings.LogoColorIndex < _selected.Colors.Count)
        {
            var hex = _selected.Colors[settings.LogoColorIndex].Hex.TrimStart('#');
            if (hex.Length == 6)
            {
                byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out tintRed);
                byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out tintGreen);
                byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out tintBlue);
            }
        }

        for (var index = 0; index < raster.PixelCount; index++)
        {
            var source = index * 4;
            var target = source;
            pixels[target] = settings.MonochromeLogo ? tintBlue : raster.Rgba[source + 2];
            pixels[target + 1] = settings.MonochromeLogo ? tintGreen : raster.Rgba[source + 1];
            pixels[target + 2] = settings.MonochromeLogo ? tintRed : raster.Rgba[source];
            pixels[target + 3] = raster.Rgba[source + 3];
        }

        var bitmap = BitmapSource.Create(
            raster.Width,
            raster.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            raster.Width * 4);
        bitmap.Freeze();
        PatternGizmoImage.Source = bitmap;
    }

    void UpdatePatternGizmoProjection()
    {
        if (_patternGizmoWorldPoint is not Point3D world ||
            PatternGizmoCanvas.Visibility != Visibility.Visible ||
            Viewer.Camera is not ProjectionCamera camera ||
            _activePatternEditor is null) return;
        var settings = _activePatternEditor.Value;
        if (!TryProjectToViewport(world, camera, out var center))
        {
            PatternGizmo.Visibility = Visibility.Collapsed;
            return;
        }
        PatternGizmo.Visibility = Visibility.Visible;
        _patternGizmoCenter = center;
        if (!settings.HasSurfaceFrame)
        {
            UpdatePatternGizmo(settings.Scale);
            return;
        }
        var tangentU = new Vector3D(settings.SurfaceUx, settings.SurfaceUy, settings.SurfaceUz);
        var tangentV = new Vector3D(settings.SurfaceVx, settings.SurfaceVy, settings.SurfaceVz);
        if (tangentU.LengthSquared < 1e-12 || tangentV.LengthSquared < 1e-12)
        {
            UpdatePatternGizmo(settings.Scale);
            return;
        }
        tangentU.Normalize();
        tangentV.Normalize();
        var halfWidthWorld = settings.SurfaceWorldSize * settings.Scale / 100d * settings.StretchX / 100d / 2;
        var halfHeightWorld = settings.SurfaceWorldSize * settings.Scale / 100d * settings.StretchY / 100d / 2;
        if (!TryProjectToViewport(world + tangentU * halfWidthWorld, camera, out var edgeU) ||
            !TryProjectToViewport(world + tangentV * halfHeightWorld, camera, out var edgeV))
        {
            UpdatePatternGizmo(settings.Scale);
            return;
        }
        var projectedU = edgeU - center;
        var projectedV = edgeV - center;
        var width = Math.Clamp(projectedU.Length * 2, 24, Math.Max(24, Viewer.ActualWidth * 1.5));
        var height = Math.Clamp(projectedV.Length * 2, 24, Math.Max(24, Viewer.ActualHeight * 1.5));
        PatternGizmo.Width = width;
        PatternGizmo.Height = height;
        PatternGizmo.RenderTransformOrigin = new System.Windows.Point(.5, .5);
        var angle = Math.Atan2(projectedU.Y, projectedU.X) * 180 / Math.PI - settings.Rotation;
        PatternGizmo.RenderTransform = new RotateTransform(angle);
        Canvas.SetLeft(PatternGizmo, center.X - width / 2);
        Canvas.SetTop(PatternGizmo, center.Y - height / 2);
    }

    bool TryProjectToViewport(Point3D world, ProjectionCamera camera, out System.Windows.Point point)
    {
        point = default;
        var forward = camera.LookDirection; var up = camera.UpDirection;
        if (!NormalizeBasis(ref forward, ref up, out var right)) return false;
        var relative = world - camera.Position;
        var depth = Vector3D.DotProduct(relative, forward);
        if (depth <= 1e-8) return false;
        double x; double y;
        if (camera is PerspectiveCamera perspective)
        {
            // WPF defines PerspectiveCamera.FieldOfView horizontally.
            var halfWidth = Math.Tan(perspective.FieldOfView * Math.PI / 360) * depth;
            var halfHeight = halfWidth * Viewer.ActualHeight / Math.Max(1, Viewer.ActualWidth);
            x = Vector3D.DotProduct(relative, right) / Math.Max(1e-8, halfWidth);
            y = Vector3D.DotProduct(relative, up) / Math.Max(1e-8, halfHeight);
        }
        else if (camera is OrthographicCamera orthographic)
        {
            var halfWidth = orthographic.Width / 2;
            var halfHeight = halfWidth * Viewer.ActualHeight / Math.Max(1, Viewer.ActualWidth);
            x = Vector3D.DotProduct(relative, right) / Math.Max(1e-8, halfWidth);
            y = Vector3D.DotProduct(relative, up) / Math.Max(1e-8, halfHeight);
        }
        else return false;
        point = new System.Windows.Point((x + 1) * Viewer.ActualWidth / 2, (1 - y) * Viewer.ActualHeight / 2);
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    void Viewer_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_paintStroke && PaintBrushCursor is not null) PaintBrushCursor.Visibility = Visibility.Collapsed;
        if (_heightRangePreview.Count > 0 || _hoverTrianglePreview.Count > 0) { _heightRangePreview.Clear(); _hoverTrianglePreview.Clear(); Render(); }
    }

    void UpdateBrushCursor(System.Windows.Point point)
    {
        if (PaintBrushCursor is null || PaintMode.IsChecked != true || PaintTool.SelectedIndex is not (1 or 4))
        {
            if (PaintBrushCursor is not null) PaintBrushCursor.Visibility = Visibility.Collapsed;
            return;
        }
        var radius = BrushRadiusPixels();
        PaintBrushCursor.Width = PaintBrushCursor.Height = radius * 2;
        Canvas.SetLeft(PaintBrushCursor, point.X - radius);
        Canvas.SetTop(PaintBrushCursor, point.Y - radius);
        PaintBrushCursor.Visibility = Visibility.Visible;
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
            var halfWidth = Math.Tan(perspective.FieldOfView * Math.PI / 360);
            var halfHeight = halfWidth * viewportHeight / viewportWidth;
            var x = (cursor.X * 2 / viewportWidth - 1) * halfWidth;
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

    async Task PaintTriangleAt(System.Windows.Point point)
    {
        if (_doc is null || _selected is null) return;
        if (_trianglePaintBusy) { _queuedTrianglePaintPoint = point; return; }
        RayMeshGeometry3DHitTestResult? hit = null;
        GeometryModel3D? model = null;
        VisualTreeHelper.HitTest(Viewer, null, result =>
        {
            if (result is RayMeshGeometry3DHitTestResult candidate && candidate.ModelHit is GeometryModel3D geometry && _modelObjects.ContainsKey(geometry))
            {
                hit = candidate; model = geometry;
                return HitTestResultBehavior.Stop;
            }
            return HitTestResultBehavior.Continue;
        }, new PointHitTestParameters(point));
        if (hit is null || model is null || !_modelObjects.TryGetValue(model, out var objectIndex)) return;
        var obj = _doc.Objects.FirstOrDefault(item => item.Index == objectIndex);
        if (obj is null) return;
        var triangle = FindSourceTriangle(obj, hit);
        if (triangle < 0 || !_triangleStrokePainted.Add((objectIndex, triangle))) return;
        _trianglePaintBusy = true;
        try
        {
            _lastSelectionObject = objectIndex;
            _lastSelectionTriangle = triangle;
            _hoverTrianglePreview.Clear();
            _paintSelection.Clear();
            _paintSelection[objectIndex] = [triangle];
            if (await CommitPaintSelectionAsync(captureUndo: !_triangleStrokeUndoCaptured))
                _triangleStrokeUndoCaptured = true;
        }
        finally
        {
            _trianglePaintBusy = false;
            if (_trianglePaintStroke && _queuedTrianglePaintPoint is System.Windows.Point queued)
            {
                _queuedTrianglePaintPoint = null;
                await PaintTriangleAt(queued);
            }
        }
    }

    double BrushRadiusPixels() => PaintBrushSize.SelectedIndex switch { 0 => 4d, 1 => 8d, 2 => 14d, 3 => 22d, _ => 32d };

    void UpdatePaintStrokeFill(IReadOnlyList<System.Windows.Point> points)
    {
        if (PaintStrokeFill is null || points.Count < 2) return;
        try
        {
            var radius = BrushRadiusPixels();
            // Build a filled polygon from the stroke points by offsetting perpendicularly
            var left = new List<System.Windows.Point>();
            var right = new List<System.Windows.Point>();
            for (var i = 0; i < points.Count; i++)
            {
                var prev = points[Math.Max(0, i - 1)];
                var next = points[Math.Min(points.Count - 1, i + 1)];
                var dirX = next.X - prev.X;
                var dirY = next.Y - prev.Y;
                var len = Math.Sqrt(dirX * dirX + dirY * dirY);
                if (len < 0.01) { dirX = 1; dirY = 0; len = 1; }
                var perpX = -dirY / len * radius;
                var perpY = dirX / len * radius;
                left.Add(new System.Windows.Point(points[i].X + perpX, points[i].Y + perpY));
                right.Add(new System.Windows.Point(points[i].X - perpX, points[i].Y - perpY));
            }
            // Build polygon: left side forward + right side backward
            var polygon = new PointCollection(left.Count + right.Count);
            foreach (var pt in left) polygon.Add(pt);
            for (var i = right.Count - 1; i >= 0; i--) polygon.Add(right[i]);

            // Use selected paint color for the fill
            var paintColor = _selected?.Colors.ElementAtOrDefault(
                Math.Clamp(PaintColorCombo?.SelectedIndex ?? 0, 0, (_selected?.Colors.Count ?? 1) - 1));
            var hex = paintColor?.Hex ?? "#FFD32D";
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var opacity = PaintPreviewOpacity is null ? .65 : PaintPreviewOpacity.Value / 100d;
            PaintStrokeFill.Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)Math.Clamp(255 * opacity, 0, 255), c.R, c.G, c.B));
            PaintStrokeFill.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)Math.Clamp(255 * Math.Max(opacity, .8), 0, 255), c.R, c.G, c.B));
            PaintStrokeFill.Points = polygon;
            PaintStrokeFill.Visibility = Visibility.Visible;
        }
        catch { PaintStrokeFill.Visibility = Visibility.Collapsed; }
    }

    void HidePaintStrokeFill()
    {
        if (PaintStrokeFill is null) return;
        PaintStrokeFill.Visibility = Visibility.Collapsed;
        PaintStrokeFill.Points.Clear();
    }

    internal static IReadOnlyList<System.Windows.Point> BrushStrokeCenters(System.Windows.Point from, System.Windows.Point to, double spacing)
    {
        spacing = Math.Max(1, spacing);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var steps = Math.Max(1, (int)Math.Ceiling(distance / spacing));
        return Enumerable.Range(0, steps + 1).Select(step => new System.Windows.Point(from.X + dx * step / steps, from.Y + dy * step / steps)).ToArray();
    }

    PaintProjection? CapturePaintProjection()
    {
        if (Viewer.Camera is not ProjectionCamera camera || Viewer.ActualWidth <= 0 || Viewer.ActualHeight <= 0) return null;
        var forward = camera.LookDirection;
        var up = camera.UpDirection;
        if (!NormalizeBasis(ref forward, ref up, out var right)) return null;
        return camera switch
        {
            PerspectiveCamera perspective => new PaintProjection(camera.Position, forward, up, right, true, perspective.FieldOfView, Viewer.ActualWidth, Viewer.ActualHeight),
            OrthographicCamera orthographic => new PaintProjection(camera.Position, forward, up, right, false, orthographic.Width, Viewer.ActualWidth, Viewer.ActualHeight),
            _ => null
        };
    }

    internal static Dictionary<int, HashSet<int>> SelectTrianglesFromScreenStroke(
        ModelDocument document,
        IReadOnlyList<System.Windows.Point> stroke,
        double radius,
        PaintProjection projection,
        bool precise = false)
    {
        var result = new Dictionary<int, HashSet<int>>();
        if (stroke.Count == 0 || radius <= 0 || projection.Width <= 0 || projection.Height <= 0) return result;

        const int cellSize = 3;
        var gridWidth = Math.Max(1, (int)Math.Ceiling(projection.Width / cellSize));
        var gridHeight = Math.Max(1, (int)Math.Ceiling(projection.Height / cellSize));
        var mask = new bool[gridWidth * gridHeight];
        var radiusCells = Math.Max(1, (int)Math.Ceiling(radius / cellSize));
        var centers = new List<System.Windows.Point>();
        if (stroke.Count == 1) centers.Add(stroke[0]);
        else
            for (var index = 1; index < stroke.Count; index++)
                centers.AddRange(BrushStrokeCenters(stroke[index - 1], stroke[index], Math.Max(1.5, radius * .25)));

        foreach (var center in centers)
        {
            var centerX = (int)Math.Floor(center.X / cellSize);
            var centerY = (int)Math.Floor(center.Y / cellSize);
            for (var y = Math.Max(0, centerY - radiusCells); y <= Math.Min(gridHeight - 1, centerY + radiusCells); y++)
                for (var x = Math.Max(0, centerX - radiusCells); x <= Math.Min(gridWidth - 1, centerX + radiusCells); x++)
                {
                    var dx = (x + .5) * cellSize - center.X;
                    var dy = (y + .5) * cellSize - center.Y;
                    if (dx * dx + dy * dy <= radius * radius) mask[y * gridWidth + x] = true;
                }
        }

        var nearestDepth = Enumerable.Repeat(double.PositiveInfinity, mask.Length).ToArray();
        var candidates = new List<PaintCandidate>();
        var added = new HashSet<(int ObjectIndex, int TriangleIndex)>();
        foreach (var obj in document.Objects)
        {
            for (var triangleIndex = 0; triangleIndex < obj.Triangles.Count; triangleIndex++)
            {
                var triangle = obj.Triangles[triangleIndex];
                var a = obj.Vertices[triangle.A];
                var b = obj.Vertices[triangle.B];
                var c = obj.Vertices[triangle.C];

                // The parent mesh can contain a single huge triangle.  Looking
                // only at its centre/vertices misses a small cursor in its
                // interior, so test the projected triangle itself before it is
                // locally subdivided.
                if (!precise && TryCoarseStrokeHit(a, b, c, centers, radius, projection, out var coarseScreen, out var coarseDepth))
                {
                    var coarseX = (int)(coarseScreen.X / cellSize);
                    var coarseY = (int)(coarseScreen.Y / cellSize);
                    if ((uint)coarseX < (uint)gridWidth && (uint)coarseY < (uint)gridHeight)
                    {
                        var coarseCell = coarseY * gridWidth + coarseX;
                        var coarseKey = (obj.Index, triangleIndex);
                        if (added.Add(coarseKey))
                        {
                            candidates.Add(new PaintCandidate(obj.Index, triangleIndex, coarseCell, coarseDepth));
                            if (coarseDepth < nearestDepth[coarseCell]) nearestDepth[coarseCell] = coarseDepth;
                        }
                    }
                    continue;
                }

                var center = new Point3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                // Coarse pass deliberately catches every parent face touched by
                // the cursor. Once it has been subdivided, however, checking a
                // shared vertex paints a fan of neighbouring fragments. The
                // precise pass therefore uses the fragment centre only.
                var samples = precise
                    ? new[] { center }
                    : new[]
                    {
                        center,
                        new Point3D(a.X, a.Y, a.Z), new Point3D(b.X, b.Y, b.Z), new Point3D(c.X, c.Y, c.Z),
                        new Point3D((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2),
                        new Point3D((b.X + c.X) / 2, (b.Y + c.Y) / 2, (b.Z + c.Z) / 2),
                        new Point3D((c.X + a.X) / 2, (c.Y + a.Y) / 2, (c.Z + a.Z) / 2),
                    };

                // Use the first sample that hits the mask for depth and cell
                var found = false;
                double bestDepth = 0;
                int bestCell = 0;
                foreach (var sample in samples)
                {
                    if (!TryProjectPoint(sample, projection, out var screen, out var depth)) continue;
                    var x = (int)(screen.X / cellSize);
                    var y = (int)(screen.Y / cellSize);
                    if ((uint)x >= (uint)gridWidth || (uint)y >= (uint)gridHeight) continue;
                    var cell = y * gridWidth + x;
                    if (!mask[cell]) continue;
                    bestDepth = depth;
                    bestCell = cell;
                    found = true;
                    break;
                }
                if (!found) continue;

                var key = (obj.Index, triangleIndex);
                if (added.Contains(key)) continue;
                added.Add(key);
                candidates.Add(new PaintCandidate(obj.Index, triangleIndex, bestCell, bestDepth));
                if (bestDepth < nearestDepth[bestCell]) nearestDepth[bestCell] = bestDepth;
            }
        }

        foreach (var candidate in candidates)
        {
            // Garde uniquement la peau visible de la figurine et évite de peindre sa face arrière.
            var candidateX = candidate.Cell % gridWidth;
            var candidateY = candidate.Cell / gridWidth;
            var visibleDepth = nearestDepth[candidate.Cell];
            for (var y = Math.Max(0, candidateY - 1); y <= Math.Min(gridHeight - 1, candidateY + 1); y++)
                for (var x = Math.Max(0, candidateX - 1); x <= Math.Min(gridWidth - 1, candidateX + 1); x++)
                    visibleDepth = Math.Min(visibleDepth, nearestDepth[y * gridWidth + x]);
            var tolerance = Math.Max(0.001, visibleDepth * .006);
            if (candidate.Depth > visibleDepth + tolerance) continue;
            if (!result.TryGetValue(candidate.ObjectIndex, out var triangles)) result[candidate.ObjectIndex] = triangles = [];
            triangles.Add(candidate.TriangleIndex);
        }
        return result;
    }

    static bool TryCoarseStrokeHit(
        Vertex a, Vertex b, Vertex c,
        IReadOnlyList<System.Windows.Point> centers,
        double radius,
        PaintProjection projection,
        out System.Windows.Point screen,
        out double depth)
    {
        screen = default;
        depth = 0;
        if (!TryProjectPoint(new Point3D(a.X, a.Y, a.Z), projection, out var pa, out _) ||
            !TryProjectPoint(new Point3D(b.X, b.Y, b.Z), projection, out var pb, out _) ||
            !TryProjectPoint(new Point3D(c.X, c.Y, c.Z), projection, out var pc, out _)) return false;
        var center = new Point3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
        if (!TryProjectPoint(center, projection, out _, out depth)) return false;
        foreach (var cursor in centers)
        {
            if (PointInTriangle(cursor, pa, pb, pc) ||
                DistanceToSegment(cursor, pa, pb) <= radius ||
                DistanceToSegment(cursor, pb, pc) <= radius ||
                DistanceToSegment(cursor, pc, pa) <= radius)
            {
                screen = cursor;
                return true;
            }
        }
        return false;
    }

    static bool PointInTriangle(System.Windows.Point point, System.Windows.Point a, System.Windows.Point b, System.Windows.Point c)
    {
        static double Cross(System.Windows.Point p, System.Windows.Point q, System.Windows.Point r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        var ab = Cross(a, b, point); var bc = Cross(b, c, point); var ca = Cross(c, a, point);
        return (ab >= 0 && bc >= 0 && ca >= 0) || (ab <= 0 && bc <= 0 && ca <= 0);
    }

    static double DistanceToSegment(System.Windows.Point point, System.Windows.Point a, System.Windows.Point b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-12) return (point - a).Length;
        var t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return (point - new System.Windows.Point(a.X + dx * t, a.Y + dy * t)).Length;
    }

    internal static HashSet<int> SelectVisibleTrianglesByHeight(
        ModelDocument document,
        int targetObjectIndex,
        double height,
        double halfRange,
        PaintProjection projection)
    {
        var result = new HashSet<int>();
        if (halfRange <= 0 || projection.Width <= 0 || projection.Height <= 0) return result;
        const int cellSize = 8;
        var width = Math.Max(1, (int)Math.Ceiling(projection.Width / cellSize));
        var heightCells = Math.Max(1, (int)Math.Ceiling(projection.Height / cellSize));
        var nearest = Enumerable.Repeat(double.PositiveInfinity, width * heightCells).ToArray();
        var candidates = new List<(int Triangle, int Cell, double Depth)>();
        foreach (var obj in document.Objects)
            for (var index = 0; index < obj.Triangles.Count; index++)
            {
                var triangle = obj.Triangles[index];
                var a = obj.Vertices[triangle.A]; var b = obj.Vertices[triangle.B]; var c = obj.Vertices[triangle.C];
                var center = new Point3D((a.X + b.X + c.X) / 3d, (a.Y + b.Y + c.Y) / 3d, (a.Z + b.Z + c.Z) / 3d);
                if (!TryProjectPoint(center, projection, out var screen, out var depth)) continue;
                var x = (int)(screen.X / cellSize); var y = (int)(screen.Y / cellSize);
                if ((uint)x >= (uint)width || (uint)y >= (uint)heightCells) continue;
                var cell = y * width + x;
                nearest[cell] = Math.Min(nearest[cell], depth);
                if (obj.Index == targetObjectIndex && Math.Abs(center.Z - height) <= halfRange)
                    candidates.Add((index, cell, depth));
            }
        foreach (var candidate in candidates)
        {
            var cellX = candidate.Cell % width;
            var cellY = candidate.Cell / width;
            var visibleDepth = nearest[candidate.Cell];
            // Perspective makes the centroid of a rear face shift a few
            // pixels. Inspect neighbouring depth cells too, otherwise a face
            // hidden behind the visible skin can leak into the height preview.
            for (var y = Math.Max(0, cellY - 1); y <= Math.Min(heightCells - 1, cellY + 1); y++)
                for (var x = Math.Max(0, cellX - 1); x <= Math.Min(width - 1, cellX + 1); x++)
                    visibleDepth = Math.Min(visibleDepth, nearest[y * width + x]);
            if (candidate.Depth <= visibleDepth + Math.Max(.001, visibleDepth * .006)) result.Add(candidate.Triangle);
        }
        return result;
    }

    internal static Dictionary<int, HashSet<int>> SelectTrianglesFromScreenRegion(
        ModelDocument document,
        IReadOnlyList<System.Windows.Point> points,
        bool rectangle,
        PaintProjection projection,
        bool precise = false)
    {
        var result = new Dictionary<int, HashSet<int>>();
        if (points.Count < 2 || projection.Width <= 0 || projection.Height <= 0) return result;
        IReadOnlyList<System.Windows.Point> polygon = rectangle
            ? new[]
            {
                points[0], new System.Windows.Point(points[^1].X, points[0].Y), points[^1],
                new System.Windows.Point(points[0].X, points[^1].Y)
            }
            : points;
        if (polygon.Count < 3) return result;

        var candidates = new List<(int Object, int Triangle, System.Windows.Point Screen, double Depth)>();
        var added = new HashSet<(int Object, int Triangle)>();
        const int cellSize = 3;
        var gridWidth = Math.Max(1, (int)Math.Ceiling(projection.Width / cellSize));
        var gridHeight = Math.Max(1, (int)Math.Ceiling(projection.Height / cellSize));
        var nearestDepth = Enumerable.Repeat(double.PositiveInfinity, gridWidth * gridHeight).ToArray();
        foreach (var obj in document.Objects)
            for (var triangleIndex = 0; triangleIndex < obj.Triangles.Count; triangleIndex++)
            {
                var triangle = obj.Triangles[triangleIndex];
                var a = obj.Vertices[triangle.A]; var b = obj.Vertices[triangle.B]; var c = obj.Vertices[triangle.C];

                var center = new Point3D((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                var samples = precise
                    ? new[] { center }
                    : new[]
                    {
                        center,
                        new Point3D(a.X, a.Y, a.Z), new Point3D(b.X, b.Y, b.Z), new Point3D(c.X, c.Y, c.Z),
                        new Point3D((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2),
                        new Point3D((b.X + c.X) / 2, (b.Y + c.Y) / 2, (b.Z + c.Z) / 2),
                        new Point3D((c.X + a.X) / 2, (c.Y + a.Y) / 2, (c.Z + a.Z) / 2),
                    };

                var found = false;
                System.Windows.Point bestScreen = default;
                double bestDepth = 0;
                foreach (var sample in samples)
                {
                    if (!TryProjectPoint(sample, projection, out var screen, out var depth) || !PointInPolygon(screen, polygon)) continue;
                    bestScreen = screen;
                    bestDepth = depth;
                    found = true;
                    break;
                }
                if (!found) continue;

                var key = (obj.Index, triangleIndex);
                if (added.Contains(key)) continue;
                added.Add(key);

                var sx = (int)(bestScreen.X / cellSize); var sy = (int)(bestScreen.Y / cellSize);
                if ((uint)sx >= (uint)gridWidth || (uint)sy >= (uint)gridHeight) continue;
                var cell = sy * gridWidth + sx;
                nearestDepth[cell] = Math.Min(nearestDepth[cell], bestDepth);
                candidates.Add((obj.Index, triangleIndex, bestScreen, bestDepth));
            }
        foreach (var candidate in candidates)
        {
            var x = (int)(candidate.Screen.X / cellSize); var y = (int)(candidate.Screen.Y / cellSize);
            var cell = y * gridWidth + x;
            var tolerance = Math.Max(.001, nearestDepth[cell] * .006);
            if (candidate.Depth > nearestDepth[cell] + tolerance) continue;
            if (!result.TryGetValue(candidate.Object, out var triangles)) result[candidate.Object] = triangles = [];
            triangles.Add(candidate.Triangle);
        }
        return result;
    }

    internal static bool PointInPolygon(System.Windows.Point point, IReadOnlyList<System.Windows.Point> polygon)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var j = i == 0 ? polygon.Count - 1 : i - 1;
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    internal static bool TryProjectPoint(Point3D point, PaintProjection projection, out System.Windows.Point screen, out double depth)
    {
        screen = default;
        var relative = point - projection.Position;
        depth = Vector3D.DotProduct(relative, projection.Forward);
        if (depth <= 1e-9 || !double.IsFinite(depth)) return false;
        var horizontal = Vector3D.DotProduct(relative, projection.Right);
        var vertical = Vector3D.DotProduct(relative, projection.Up);
        double normalizedX;
        double normalizedY;
        if (projection.Perspective)
        {
            var halfWidth = Math.Tan(projection.FieldOfViewOrWidth * Math.PI / 360);
            var halfHeight = halfWidth * projection.Height / projection.Width;
            if (halfWidth <= 0 || halfHeight <= 0) return false;
            normalizedX = horizontal / (depth * halfWidth);
            normalizedY = vertical / (depth * halfHeight);
        }
        else
        {
            var halfWidth = projection.FieldOfViewOrWidth / 2;
            var halfHeight = halfWidth * projection.Height / projection.Width;
            if (halfWidth <= 0 || halfHeight <= 0) return false;
            normalizedX = horizontal / halfWidth;
            normalizedY = vertical / halfHeight;
        }
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY)) return false;
        screen = new System.Windows.Point((normalizedX + 1) * projection.Width / 2, (1 - normalizedY) * projection.Height / 2);
        return screen.X >= 0 && screen.Y >= 0 && screen.X < projection.Width && screen.Y < projection.Height;
    }

    internal readonly record struct PaintProjection(
        Point3D Position,
        Vector3D Forward,
        Vector3D Up,
        Vector3D Right,
        bool Perspective,
        double FieldOfViewOrWidth,
        double Width,
        double Height);

    readonly record struct PaintCandidate(int ObjectIndex, int TriangleIndex, int Cell, double Depth);

    void ViewIso_Click(object sender, RoutedEventArgs e) { _yaw = -40; _pitch = 25; UpdateCamera(); }
    void ViewFront_Click(object sender, RoutedEventArgs e) { _yaw = 180; _pitch = 0; UpdateCamera(); }
    void ViewBack_Click(object sender, RoutedEventArgs e) { _yaw = 0; _pitch = 0; UpdateCamera(); }
    void ViewLeft_Click(object sender, RoutedEventArgs e) { _yaw = -90; _pitch = 0; UpdateCamera(); }
    void ViewRight_Click(object sender, RoutedEventArgs e) { _yaw = 90; _pitch = 0; UpdateCamera(); }
    void ViewTop_Click(object sender, RoutedEventArgs e) { _yaw = 0; _pitch = 89; UpdateCamera(); }
    void ViewBottom_Click(object sender, RoutedEventArgs e) { _yaw = 0; _pitch = -89; UpdateCamera(); }
    void Fit_Click(object sender, RoutedEventArgs e) => FitCamera();
    void ToggleGrid_Click(object sender, RoutedEventArgs e)
    {
        if (sender == PlateMenuItem) _grid = PlateMenuItem.IsChecked == true;
        else
        {
            _grid = !_grid;
            PlateMenuItem.IsChecked = _grid;
        }
        _settings.ShowBuildPlate = _grid;
        _settingsService.Save(_settings);
        Render();
        StatusText.Text = _grid ? "Plateau affiché." : "Plateau masqué.";
    }
    void ToggleInertia_Click(object sender, RoutedEventArgs e)
    {
        _settings.NavigationInertia = InertiaMenuItem.IsChecked == true;
        _settingsService.Save(_settings);
        if (!_settings.NavigationInertia) _navigationInertiaTimer.Stop();
        StatusText.Text = _settings.NavigationInertia ? "Inertie de navigation activée." : "Inertie de navigation désactivée.";
    }
    void TogglePerspective_Click(object sender, RoutedEventArgs e) { _perspective = !_perspective; UpdateCamera(); }

    void Settings_Click(object sender, RoutedEventArgs e) { var dialog = new SettingsWindow(_settings) { Owner = this }; if (dialog.ShowDialog() == true) { _settings = dialog.Value; _settingsService.Save(_settings); EnsurePreferredSlicer(); ApplyTheme(); UpdateSlicerButton(); Render(); } }
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
        // Only show when upgrading (saved version < current). Prevents old installed
        // versions from triggering the popup every time they overwrite LastSeenVersion.
        if (Version.TryParse(_settings.LastSeenVersion, out var savedVersion) &&
            Version.TryParse(current, out var currentVersion) &&
            savedVersion >= currentVersion) return;
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

    void SetPatternEditingUi(bool active)
    {
        if (active)
        {
            _paintModeBeforePattern = PaintMode.IsChecked == true;
            PaintMode.IsChecked = false;
            PaintModeMenu.IsChecked = false;
            MainMenu.Visibility = Visibility.Collapsed;
            MainToolbar.Visibility = Visibility.Collapsed;
            LeftPanel.Visibility = Visibility.Collapsed;
            RightPanel.Visibility = Visibility.Collapsed;
            PatternEditorPanel.Visibility = Visibility.Visible;
            LeftColumn.Width = new GridLength(0);
            RightColumn.Width = new GridLength(520);
            Viewer.Cursor = System.Windows.Input.Cursors.Arrow;
            PatternGizmoCanvas.Visibility = Visibility.Visible;
            if (_activePatternEditor?.Value is { HasSurfaceFrame: true } placed)
            {
                _patternGizmoWorldPoint = new Point3D(placed.SurfaceX, placed.SurfaceY, placed.SurfaceZ);
                PatternGizmo.Visibility = Visibility.Visible;
                UpdatePatternGizmoProjection();
            }
            else
            {
                _patternGizmoWorldPoint = null;
                PatternGizmo.Visibility = Visibility.Collapsed;
            }
            StatusText.Text = "Atelier motif : gauche = rotation · droit = déplacement · molette = zoom.";
        }
        else
        {
            PatternEditorPanel.Visibility = Visibility.Collapsed;
            RightPanel.Visibility = Visibility.Visible;
            LeftPanel.Visibility = Visibility.Visible;
            MainToolbar.Visibility = Visibility.Visible;
            MainMenu.Visibility = Visibility.Visible;
            LeftColumn.Width = new GridLength(280);
            RightColumn.Width = new GridLength(370);
            PaintMode.IsChecked = _paintModeBeforePattern;
            PaintModeMenu.IsChecked = _paintModeBeforePattern;
            _paintModeBeforePattern = false;
            PatternGizmoCanvas.Visibility = Visibility.Collapsed;
            PatternGizmo.Visibility = Visibility.Visible;
            _patternGizmoWorldPoint = null;
            _patternGizmoMode = null;
        }
        if (!active && _doc is not null)
            StatusText.Text = "Motif prêt — vous pouvez continuer à travailler sur le modèle.";
    }
    async void Update_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(false);


    async Task CheckForUpdatesAsync(bool automatic)
    {
        if (!automatic) { IsEnabled = false; StatusText.Text = "Recherche d’une mise à jour…"; SetActivity(true, "Recherche d’une mise à jour…"); }
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

            IsEnabled = false; SetActivity(true, "Téléchargement de la mise à jour… 0 %", true, 0);
            var progress = new Progress<double>(value => { StatusText.Text = $"Téléchargement de la mise à jour… {value:0}%"; SetActivity(true, $"Téléchargement de la mise à jour… {value:0} %", true, value); });
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
        finally { IsEnabled = true; SetActivity(false); }
    }
    void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
    void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_activePatternEditor is not null) return;
        // Familiar paint shortcuts: [ and ] change the real visible brush
        // diameter before the next stroke.
        if (e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets)
        {
            var delta = e.Key == Key.OemOpenBrackets ? -1 : 1;
            PaintBrushSize.SelectedIndex = Math.Clamp(PaintBrushSize.SelectedIndex + delta, 0, PaintBrushSize.Items.Count - 1);
            StatusText.Text = $"Taille du pinceau : {((ComboBoxItem)PaintBrushSize.SelectedItem).Content}.";
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.O) Import_Click(sender, e); else if (e.Key == Key.S) SaveProject_Click(sender, e); else if (e.Key == Key.E) Export_Click(sender, e); else if (e.Key == Key.Z) Undo_Click(sender, e); else if (e.Key == Key.Y) Redo_Click(sender, e); else if (e.Key == Key.N) New_Click(sender, e);
    }
    void Window_Closing(object? sender, CancelEventArgs e) { if (!_shutdownForUpdate && !ConfirmDiscard()) e.Cancel = true; if (!e.Cancel) _gpuViewport.Dispose(); }
    void Quit_Click(object sender, RoutedEventArgs e) => Close();

    static LogoProject DecodeLogoProject(byte[]? archive)
    {
        if (archive is not { Length: > 0 }) return new LogoProject();
        using var stream = new MemoryStream(archive, writable: false);
        return new LogoProjectStore().Load(stream);
    }

    static LogoProject CloneLogoProject(LogoProject source) => source with
    {
        Assets = source.Assets.Select(asset => asset with { Png = (byte[])asset.Png.Clone() }).ToList(),
        Layers = source.Layers.Select(layer => layer with
        {
            Instances = layer.Instances.Select(instance => instance with
            {
                Transform = instance.Transform with { Anchor = instance.Transform.Anchor with { } }
            }).ToList()
        }).ToList()
    };

    sealed record EditorState(
        int Selected,
        int Generation,
        bool FunMode,
        int ColorCount,
        List<ColorProposal> Proposals,
        PatternSettings? Pattern,
        ModelDocument? Document,
        List<ColorProposal> LayerBases,
        List<List<ColorLayer>> Layers,
        LogoProject Logos,
        List<ColorProposal>? LogoBases);
}
