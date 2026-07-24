using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Control = System.Windows.Controls.Control;
using TextBox = System.Windows.Controls.TextBox;
using MessageBox = System.Windows.MessageBox;

namespace PolyChrom3MF.App;

public sealed class SettingsWindow : Window
{
    readonly SlicerDetectionService _slicerDetection = new();
    readonly System.Windows.Controls.ComboBox _theme = new() { ItemsSource = new[] { "Sombre", "Clair", "Système" }, Margin = new Thickness(0, 4, 0, 10) };
    readonly System.Windows.Controls.TextBox _folder = new() { Margin = new Thickness(0, 4, 0, 10) };
    readonly System.Windows.Controls.ComboBox _slicer = new() { Margin = new Thickness(0, 4, 0, 6), IsEditable = true, DisplayMemberPath = nameof(DetectedSlicer.Label) };
    readonly TextBlock _slicerStatus = new() { Margin = new Thickness(0, 0, 0, 10) };
    readonly System.Windows.Controls.CheckBox _open = new() { Content = "Ouvrir le dossier après export", Margin = new Thickness(0, 4, 0, 6) };
    readonly System.Windows.Controls.CheckBox _verify = new() { Content = "Vérifier le fichier après export", Margin = new Thickness(0, 4, 0, 14) };
    readonly TextBox _printer = new() { Margin = new Thickness(0, 4, 0, 8) };
    readonly TextBox _slots = new() { Margin = new Thickness(0, 4, 8, 8), Width = 90 };
    readonly TextBox _nozzle = new() { Margin = new Thickness(0, 4, 8, 8), Width = 90 };
    readonly TextBox _layerHeight = new() { Margin = new Thickness(0, 4, 0, 8), Width = 90 };
    public AppSettings Value { get; }

    public SettingsWindow(AppSettings settings)
    {
        Value = settings;
        Title = "Paramètres — PolyChrom 3MF"; Width = 720; Height = 680; MinWidth = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _theme.SelectedItem = settings.Theme; _folder.Text = settings.ExportFolder; _open.IsChecked = settings.OpenFolderAfterExport; _verify.IsChecked = settings.VerifyAfterExport;
        _printer.Text = settings.PrinterName; _slots.Text = settings.MaterialSlots.ToString(); _nozzle.Text = settings.NozzleDiameter.ToString("0.###"); _layerHeight.Text = settings.LayerHeight.ToString("0.###");
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Thème" }); panel.Children.Add(_theme);
        panel.Children.Add(new TextBlock { Text = "Dossier d’exportation par défaut" }); panel.Children.Add(_folder);
        panel.Children.Add(new TextBlock { Text = "Slicer préféré", FontWeight = FontWeights.Bold }); panel.Children.Add(_slicer);
        var slicerButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var refresh = new Button { Content = "Redétecter", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += (_, _) => RefreshSlicers(_slicer.Text);
        var browse = new Button { Content = "Parcourir…", Padding = new Thickness(12, 6, 12, 6) };
        browse.Click += (_, _) => { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Slicer Windows (*.exe)|*.exe", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) _slicer.Text = dialog.FileName; };
        slicerButtons.Children.Add(refresh); slicerButtons.Children.Add(browse); panel.Children.Add(slicerButtons); panel.Children.Add(_slicerStatus);
        panel.Children.Add(new TextBlock { Text = "Profil d’impression multicolore", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(new TextBlock { Text = "Nom de l’imprimante" }); panel.Children.Add(_printer);
        var printerFields = new StackPanel { Orientation = Orientation.Horizontal };
        printerFields.Children.Add(Labeled("Emplacements", _slots));
        printerFields.Children.Add(Labeled("Buse (mm)", _nozzle));
        printerFields.Children.Add(Labeled("Couche (mm)", _layerHeight));
        panel.Children.Add(printerFields);
        panel.Children.Add(_open); panel.Children.Add(_verify);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "Valeurs par défaut", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        reset.Click += (_, _) => { var d = new AppSettings(); _theme.SelectedItem = d.Theme; _folder.Text = d.ExportFolder; RefreshSlicers(); _printer.Text = d.PrinterName; _slots.Text = d.MaterialSlots.ToString(); _nozzle.Text = d.NozzleDiameter.ToString(); _layerHeight.Text = d.LayerHeight.ToString(); _open.IsChecked = d.OpenFolderAfterExport; _verify.IsChecked = d.VerifyAfterExport; };
        var ok = new Button { Content = "Enregistrer", IsDefault = true, Padding = new Thickness(12, 6, 12, 6) };
        ok.Click += (_, _) =>
        {
            if (!int.TryParse(_slots.Text, out var slots) || !double.TryParse(_nozzle.Text, out var nozzle) || !double.TryParse(_layerHeight.Text, out var layerHeight))
            { MessageBox.Show("Vérifiez les valeurs du profil d’imprimante.", "Paramètres invalides"); return; }
            Value.Theme = _theme.SelectedItem?.ToString() ?? "Sombre"; Value.ExportFolder = _folder.Text; Value.PreferredSlicer = _slicer.SelectedItem is DetectedSlicer detected ? detected.Path : _slicer.Text;
            Value.PrinterName = _printer.Text; Value.MaterialSlots = slots; Value.NozzleDiameter = nozzle; Value.LayerHeight = layerHeight;
            Value.OpenFolderAfterExport = _open.IsChecked == true; Value.VerifyAfterExport = _verify.IsChecked == true; DialogResult = true;
        };
        buttons.Children.Add(reset); buttons.Children.Add(ok); panel.Children.Add(buttons); Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        RefreshSlicers(settings.PreferredSlicer);
    }

    static StackPanel Labeled(string label, Control control)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(control);
        return panel;
    }

    void RefreshSlicers(string? preferred = null)
    {
        var detected = _slicerDetection.Detect(); _slicer.ItemsSource = detected;
        var selected = detected.FirstOrDefault(x => string.Equals(x.Path, preferred, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) _slicer.SelectedItem = selected;
        else if (!string.IsNullOrWhiteSpace(preferred)) _slicer.Text = preferred;
        else if (detected.Count > 0) _slicer.SelectedIndex = 0;
        _slicerStatus.Text = detected.Count == 0 ? "Aucun slicer détecté automatiquement — utilisez Parcourir." : $"{detected.Count} slicer(s) détecté(s) automatiquement.";
        _slicerStatus.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryText");
    }
}
