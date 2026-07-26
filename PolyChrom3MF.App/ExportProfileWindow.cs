using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Mouse = System.Windows.Input.Mouse;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace PolyChrom3MF.App;

public sealed class ExportProfileWindow : Window
{
    static readonly string[] Materials = ["PLA", "PETG", "ABS", "ASA", "TPU", "PVA", "PA", "PC", "HIPS"];
    readonly AppSettings _settings;
    readonly ColorProposal _proposal;
    readonly SlicerProfileCatalogService _catalogService = new();
    readonly List<DetectedSlicer> _slicers;
    readonly ComboBox _savedProfile = new() { DisplayMemberPath = nameof(ExportProfileSettings.Name), Margin = new Thickness(0, 4, 0, 10) };
    readonly ComboBox _slicer = new() { DisplayMemberPath = nameof(DetectedSlicer.Label), Margin = new Thickness(0, 4, 0, 10) };
    readonly ComboBox _printer = new() { DisplayMemberPath = nameof(InstalledSlicerPreset.Label), IsEditable = true, Margin = new Thickness(0, 4, 0, 10) };
    readonly ComboBox _process = new() { DisplayMemberPath = nameof(InstalledSlicerPreset.Label), IsEditable = true, Margin = new Thickness(0, 4, 0, 10) };
    readonly ComboBox _nozzle = new() { IsEditable = true, Width = 120, Margin = new Thickness(0, 4, 12, 10) };
    readonly TextBox _slots = new() { Width = 120, Margin = new Thickness(0, 4, 0, 10) };
    readonly TextBox _name = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly CheckBox _default = new() { Content = "Définir comme profil d’export par défaut", IsChecked = true, Margin = new Thickness(0, 6, 0, 4) };
    readonly CheckBox _confirm = new() { Content = "Toujours afficher cet assistant avant l’export", IsChecked = true, Margin = new Thickness(0, 0, 0, 12) };
    readonly StackPanel _filaments = new();
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
    readonly List<(TextBox Color, ComboBox Material, ComboBox Preset)> _filamentRows = [];
    SlicerProfileCatalog? _catalog;
    ExportProfileSettings? _initial;
    bool _filteringPresets;

    public ExportProfileSettings? Result { get; private set; }

    public ExportProfileWindow(AppSettings settings, ColorProposal proposal, IReadOnlyList<DetectedSlicer> detectedSlicers)
    {
        _settings = settings;
        _proposal = proposal;
        _slicers = detectedSlicers.ToList();
        Title = "Préparer l’export vers le slicer";
        Width = 820;
        Height = 800;
        MinWidth = 700;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _initial = settings.ExportProfiles.FirstOrDefault(profile =>
            profile.Name.Equals(settings.DefaultExportProfile, StringComparison.OrdinalIgnoreCase))
            ?? settings.ExportProfiles.FirstOrDefault();
        _confirm.IsChecked = settings.AlwaysConfirmExportProfile;

        var root = new DockPanel { Margin = new Thickness(24) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Utiliser ce profil et exporter", IsDefault = true, MinWidth = 220 };
        apply.Click += (_, _) => SaveAndClose();
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "PROFIL D’IMPRESSION ET DE SLICER", FontSize = 22, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock
        {
            Text = "PolyChrom utilise les profils réellement installés afin que le 3MF s’ouvre avec la bonne machine, la bonne buse et les bons filaments.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 18),
            Foreground = (Brush)Application.Current.Resources["SecondaryText"]
        });
        if (settings.ExportProfiles.Count > 0)
        {
            panel.Children.Add(Label("Profil d’export enregistré"));
            panel.Children.Add(_savedProfile);
        }
        panel.Children.Add(Label("Slicer de destination"));
        panel.Children.Add(_slicer);
        panel.Children.Add(_status);
        panel.Children.Add(Label("Imprimante / profil machine"));
        panel.Children.Add(_printer);
        panel.Children.Add(Label("Profil de qualité / processus"));
        panel.Children.Add(_process);
        var dimensions = new StackPanel { Orientation = Orientation.Horizontal };
        dimensions.Children.Add(Field("Diamètre de buse (mm)", _nozzle));
        dimensions.Children.Add(Field("Emplacements disponibles", _slots));
        panel.Children.Add(dimensions);
        panel.Children.Add(new TextBlock { Text = "Filaments du projet", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 7) });
        panel.Children.Add(_filaments);
        panel.Children.Add(Label("Nom du profil réutilisable"));
        panel.Children.Add(_name);
        panel.Children.Add(_default);
        panel.Children.Add(_confirm);
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;

        _slicer.ItemsSource = _slicers;
        _savedProfile.ItemsSource = settings.ExportProfiles;
        _savedProfile.SelectedItem = _initial;
        _savedProfile.SelectionChanged += (_, _) => LoadSavedProfile();
        _slicer.SelectionChanged += (_, _) => LoadSlicerCatalog();
        _printer.SelectionChanged += (_, _) => UpdatePrinterSelection();
        _slots.LostFocus += (_, _) =>
        {
            if (int.TryParse(_slots.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) &&
                count >= _proposal.Colors.Count && count <= 32 && count != _filamentRows.Count)
                BuildFilamentRows(count);
        };
        BuildFilamentRows(Math.Max(proposal.Colors.Count, _initial?.MaterialSlots ?? settings.MaterialSlots));
        SelectInitialSlicer();
    }

    void LoadSavedProfile()
    {
        if (_savedProfile.SelectedItem is not ExportProfileSettings profile || ReferenceEquals(profile, _initial)) return;
        _initial = profile;
        _default.IsChecked = profile.Name.Equals(_settings.DefaultExportProfile, StringComparison.OrdinalIgnoreCase);
        var slicer = _slicers.FirstOrDefault(item => item.Path.Equals(profile.SlicerPath, StringComparison.OrdinalIgnoreCase));
        if (slicer is null) return;
        if (Equals(_slicer.SelectedItem, slicer)) LoadSlicerCatalog();
        else _slicer.SelectedItem = slicer;
    }

    void SelectInitialSlicer()
    {
        var preferred = _initial?.SlicerPath;
        if (string.IsNullOrWhiteSpace(preferred)) preferred = _settings.PreferredSlicer;
        var selected = _slicers.FirstOrDefault(item => item.Path.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?? _slicers.FirstOrDefault(item => item.Name.Contains("Snapmaker", StringComparison.OrdinalIgnoreCase))
            ?? _slicers.FirstOrDefault();
        if (selected is not null) _slicer.SelectedItem = selected;
        else
        {
            _status.Text = "Aucun slicer installé n’a été détecté. Configurez d’abord le slicer préféré dans Paramètres.";
            _status.Foreground = Brushes.Orange;
        }
    }

    void LoadSlicerCatalog()
    {
        if (_slicer.SelectedItem is not DetectedSlicer slicer) return;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            _catalog = _catalogService.Detect(slicer);
            _printer.ItemsSource = _catalog.Printers;
            var preferredPrinter = _initial?.SlicerPath.Equals(slicer.Path, StringComparison.OrdinalIgnoreCase) == true
                ? _initial.PrinterPreset : _settings.PrinterName;
            var selectedPrinter = _catalog.Printers.FirstOrDefault(item => item.Name.Equals(preferredPrinter, StringComparison.OrdinalIgnoreCase))
                ?? _catalog.Printers.FirstOrDefault(item =>
                    item.PrinterModel.Contains(_settings.PrinterName, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(item.NozzleDiameter - _settings.NozzleDiameter) < .001)
                ?? _catalog.Printers.FirstOrDefault(item =>
                    item.Name.Contains("Snapmaker U1", StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(item.NozzleDiameter - _settings.NozzleDiameter) < .001)
                ?? _catalog.Printers.FirstOrDefault();
            if (selectedPrinter is not null) _printer.SelectedItem = selectedPrinter;
            else _printer.Text = preferredPrinter;
            _status.Text = _catalog.Printers.Count == 0
                ? "Aucun profil machine structuré n’a été trouvé : les valeurs manuelles seront exportées."
                : $"{_catalog.Printers.Count} profil(s) machine, {_catalog.Processes.Count} processus et {_catalog.Filaments.Count} filament(s) détectés dans {slicer.Name}.";
            _status.Foreground = _catalog.Printers.Count == 0 ? Brushes.Orange : Brushes.LightGreen;
            RefreshFilamentPresets();
        }
        catch (Exception ex)
        {
            _catalog = null;
            _status.Text = "Profils du slicer illisibles : " + ex.Message;
            _status.Foreground = Brushes.Orange;
        }
        finally { Mouse.OverrideCursor = null; }
    }

    void UpdatePrinterSelection()
    {
        if (_printer.SelectedItem is not InstalledSlicerPreset printer || _catalog is null) return;
        _nozzle.ItemsSource = _catalog.Printers.Select(item => item.NozzleDiameter).Distinct().OrderBy(value => value).Select(value => value.ToString("0.0##", CultureInfo.InvariantCulture));
        _nozzle.Text = printer.NozzleDiameter.ToString("0.0##", CultureInfo.InvariantCulture);
        var slotCount = Math.Max(Math.Max(printer.ExtruderCount, _proposal.Colors.Count), _initial?.MaterialSlots ?? 1);
        _slots.Text = slotCount.ToString(CultureInfo.InvariantCulture);
        if (_filamentRows.Count != slotCount) BuildFilamentRows(slotCount);
        var explicitlyCompatible = _catalog.Processes.Where(process =>
            process.CompatiblePrinters.Contains(printer.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var compatible = explicitlyCompatible.Count > 0
            ? explicitlyCompatible
            : _catalog.Processes.Where(process =>
                process.CompatiblePrinters.Count == 0 &&
                (process.Name.Contains(printer.PrinterModel, StringComparison.OrdinalIgnoreCase) ||
                 process.Name.Contains(printer.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (compatible.Count == 0) compatible = _catalog.Processes.Where(process => process.CompatiblePrinters.Count == 0).ToList();
        _process.ItemsSource = compatible;
        var preferred = _initial?.ProcessPreset;
        var selected = compatible.FirstOrDefault(item => item.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?? compatible.FirstOrDefault(item => item.Name.StartsWith($"{_settings.LayerHeight:0.00}", StringComparison.OrdinalIgnoreCase))
            ?? compatible.FirstOrDefault(item => item.Name.Contains("Standard", StringComparison.OrdinalIgnoreCase))
            ?? compatible.FirstOrDefault();
        if (selected is not null) _process.SelectedItem = selected;
        else _process.Text = preferred ?? "";
        _name.Text = _initial?.Name ?? $"{printer.Name} — {(_slicer.SelectedItem as DetectedSlicer)?.Name}";
    }

    void BuildFilamentRows(int count)
    {
        _filaments.Children.Clear();
        _filamentRows.Clear();
        count = Math.Clamp(count, _proposal.Colors.Count, 32);
        for (var index = 0; index < count; index++)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var used = index < _proposal.Colors.Count;
            var colorValue = used
                ? _proposal.Colors[index].Hex
                : _initial?.FilamentColors.ElementAtOrDefault(index) ?? _settings.FilamentColors.ElementAtOrDefault(index) ?? "#FFFFFF";
            var title = new TextBlock { Text = $"Filament {index + 1}\n{(used ? "utilisé" : "disponible")}", VerticalAlignment = VerticalAlignment.Center };
            var color = new TextBox { Text = colorValue, Margin = new Thickness(0, 0, 8, 0), IsReadOnly = used, ToolTip = used ? "Couleur issue du modèle" : "Couleur du filament chargé" };
            var material = new ComboBox { ItemsSource = Materials, Margin = new Thickness(8, 0, 8, 0) };
            material.SelectedItem = ExportProfileSettings.MaterialName(_initial?.FilamentMaterials.ElementAtOrDefault(index) ?? _settings.FilamentMaterials.ElementAtOrDefault(index));
            var preset = new ComboBox { IsEditable = true, DisplayMemberPath = nameof(InstalledSlicerPreset.Label) };
            material.SelectionChanged += (_, _) => RefreshFilamentPreset(index);
            Grid.SetColumn(title, 0); Grid.SetColumn(color, 1); Grid.SetColumn(material, 2); Grid.SetColumn(preset, 3);
            row.Children.Add(title); row.Children.Add(color); row.Children.Add(material); row.Children.Add(preset);
            _filaments.Children.Add(row);
            _filamentRows.Add((color, material, preset));
            ConfigurePresetSearch(index);
        }
        RefreshFilamentPresets();
    }

    void ConfigurePresetSearch(int index)
    {
        var preset = _filamentRows[index].Preset;
        preset.IsTextSearchEnabled = false;
        preset.StaysOpenOnEdit = true;
        preset.Loaded += (_, _) =>
        {
            preset.ApplyTemplate();
            if (preset.Template.FindName("PART_EditableTextBox", preset) is not TextBox editor) return;
            editor.PreviewKeyDown += (_, args) =>
            {
                if (args.Key != System.Windows.Input.Key.Space) return;
                args.Handled = true;
                var start = editor.SelectionStart;
                editor.Text = editor.Text.Remove(start, editor.SelectionLength).Insert(start, " ");
                editor.SelectionStart = start + 1;
                editor.SelectionLength = 0;
            };
            editor.TextChanged += (_, _) =>
            {
                if (_filteringPresets || !editor.IsKeyboardFocusWithin) return;
                var query = editor.Text;
                editor.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => FilterFilamentPresets(index, query, true)));
            };
        };
    }

    void FilterFilamentPresets(int index, string? search, bool open)
    {
        if (index < 0 || index >= _filamentRows.Count) return;
        var row = _filamentRows[index];
        var material = ExportProfileSettings.MaterialName(row.Material.SelectedItem?.ToString());
        var all = _catalog?.Filaments
            .Where(item => item.Material.Equals(material, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        var query = search?.Trim() ?? "";
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filtered = all
            .Where(item => tokens.All(token => item.Label.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => SearchRank(item, query))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
        _filteringPresets = true;
        try
        {
            row.Preset.ItemsSource = filtered;
            row.Preset.Text = query;
            row.Preset.ApplyTemplate();
            if (row.Preset.Template.FindName("PART_EditableTextBox", row.Preset) is TextBox editor)
            {
                editor.Text = query;
                editor.SelectionStart = editor.Text.Length;
                editor.SelectionLength = 0;
            }
            if (open && filtered.Count > 0) row.Preset.IsDropDownOpen = true;
        }
        finally { _filteringPresets = false; }
    }

    static int SearchRank(InstalledSlicerPreset preset, string query)
    {
        if (query.Length == 0)
            return preset.Name.StartsWith("Generic ", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        if (preset.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            preset.Label.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (preset.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (preset.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    void RefreshFilamentPresets()
    {
        for (var index = 0; index < _filamentRows.Count; index++) RefreshFilamentPreset(index);
    }

    void RefreshFilamentPreset(int index)
    {
        if (index < 0 || index >= _filamentRows.Count) return;
        var row = _filamentRows[index];
        var material = ExportProfileSettings.MaterialName(row.Material.SelectedItem?.ToString());
        var choices = _catalog?.Filaments
            .Where(item => item.Material.Equals(material, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name.StartsWith("Generic ", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        row.Preset.ItemsSource = choices;
        var preferred = _initial?.FilamentPresets.ElementAtOrDefault(index);
        var selected = choices.FirstOrDefault(item => item.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(item => item.Name.Equals($"Generic {material} @System", StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(item => item.Name.Equals($"Generic {material}", StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(item => item.Name.Equals($"Snapmaker {material}", StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(item => item.Name.StartsWith($"Generic {material} ", StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault();
        if (selected is not null) row.Preset.SelectedItem = selected;
        else row.Preset.Text = preferred ?? $"Generic {material}";
    }

    void SaveAndClose()
    {
        if (_slicer.SelectedItem is not DetectedSlicer slicer)
        {
            MessageBox.Show("Choisissez un slicer de destination.", "Profil incomplet", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var printer = _printer.SelectedItem is InstalledSlicerPreset printerPreset ? printerPreset.Name : _printer.Text.Trim();
        var process = _process.SelectedItem is InstalledSlicerPreset processPreset ? processPreset.Name : _process.Text.Trim();
        if (string.IsNullOrWhiteSpace(printer))
        {
            MessageBox.Show("Choisissez un profil d’imprimante.", "Profil incomplet", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!double.TryParse(_nozzle.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var nozzle) ||
            nozzle is < .1 or > 2 ||
            !int.TryParse(_slots.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slots) ||
            slots is < 1 or > 64)
        {
            MessageBox.Show("Vérifiez le diamètre de buse et le nombre d’emplacements.", "Profil invalide", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var profile = ExportProfileSettings.Normalize(new ExportProfileSettings
        {
            Name = _name.Text,
            SlicerPath = slicer.Path,
            SlicerName = slicer.Name,
            PrinterPreset = printer,
            ProcessPreset = process,
            NozzleDiameter = nozzle,
            MaterialSlots = slots,
            FilamentColors = _filamentRows.Select(row => row.Color.Text).ToList(),
            FilamentMaterials = _filamentRows.Select(row => row.Material.SelectedItem?.ToString() ?? "PLA").ToList(),
            FilamentPresets = _filamentRows.Select(row =>
                row.Preset.SelectedItem is InstalledSlicerPreset preset ? preset.Name : row.Preset.Text).ToList()
        });
        var existing = _settings.ExportProfiles.FindIndex(item => item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) _settings.ExportProfiles[existing] = profile;
        else _settings.ExportProfiles.Add(profile);
        if (_default.IsChecked == true) _settings.DefaultExportProfile = profile.Name;
        _settings.AlwaysConfirmExportProfile = _confirm.IsChecked == true;
        _settings.PreferredSlicer = profile.SlicerPath;
        _settings.PrinterName = profile.PrinterPreset;
        _settings.NozzleDiameter = profile.NozzleDiameter;
        _settings.MaterialSlots = profile.MaterialSlots;
        _settings.FilamentMaterials = profile.FilamentMaterials.ToList();
        Result = profile;
        DialogResult = true;
    }

    static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold };
    static StackPanel Field(string label, Control control)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(label));
        panel.Children.Add(control);
        return panel;
    }
}
