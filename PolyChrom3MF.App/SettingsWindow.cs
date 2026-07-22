using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

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
    public AppSettings Value { get; }

    public SettingsWindow(AppSettings settings)
    {
        Value = settings;
        Title = "Paramètres — PolyChrom 3MF"; Width = 720; Height = 500; MinWidth = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _theme.SelectedItem = settings.Theme; _folder.Text = settings.ExportFolder; _open.IsChecked = settings.OpenFolderAfterExport; _verify.IsChecked = settings.VerifyAfterExport;
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
        panel.Children.Add(_open); panel.Children.Add(_verify);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "Valeurs par défaut", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        reset.Click += (_, _) => { var d = new AppSettings(); _theme.SelectedItem = d.Theme; _folder.Text = d.ExportFolder; RefreshSlicers(); _open.IsChecked = d.OpenFolderAfterExport; _verify.IsChecked = d.VerifyAfterExport; };
        var ok = new Button { Content = "Enregistrer", IsDefault = true, Padding = new Thickness(12, 6, 12, 6) };
        ok.Click += (_, _) => { Value.Theme = _theme.SelectedItem?.ToString() ?? "Sombre"; Value.ExportFolder = _folder.Text; Value.PreferredSlicer = _slicer.SelectedItem is DetectedSlicer detected ? detected.Path : _slicer.Text; Value.OpenFolderAfterExport = _open.IsChecked == true; Value.VerifyAfterExport = _verify.IsChecked == true; DialogResult = true; };
        buttons.Children.Add(reset); buttons.Children.Add(ok); panel.Children.Add(buttons); Content = panel;
        RefreshSlicers(settings.PreferredSlicer);
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
