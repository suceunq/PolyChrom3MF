using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using ListBox = System.Windows.Controls.ListBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PolyChrom3MF.App;

public sealed class StyleGalleryWindow : Window
{
    readonly StyleLibraryService _library = new();
    readonly ListBox _styles = new() { DisplayMemberPath = nameof(PolyStyleItem.Label), MinHeight = 250 };
    public PolyStyleData? SelectedStyle { get; private set; }

    public StyleGalleryWindow()
    {
        Title = "Galerie de styles PolyChrom";
        Width = 700; Height = 520; MinWidth = 560; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var import = new Button { Content = "Importer un style…", MinWidth = 140 };
        var apply = new Button { Content = "Appliquer le style", MinWidth = 140, IsDefault = true };
        var close = new Button { Content = "Fermer", MinWidth = 100, IsCancel = true };
        import.Click += Import_Click; apply.Click += Apply_Click; close.Click += (_, _) => Close();
        buttons.Children.Add(import); buttons.Children.Add(apply); buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "GALERIE DE STYLES", FontSize = 24, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Palettes, motifs, calques et profils partageables au format .polystyle.", Margin = new Thickness(0, 5, 0, 15) });
        panel.Children.Add(_styles); root.Children.Add(panel); Content = root;
        Refresh();
    }

    void Refresh() => _styles.ItemsSource = _library.List();

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Style PolyChrom (*.polystyle)|*.polystyle", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        try { _library.AddToLibrary(dialog.FileName); Refresh(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Style impossible à importer", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_styles.SelectedItem is not PolyStyleItem item) { MessageBox.Show("Sélectionnez un style."); return; }
        SelectedStyle = item.Data; DialogResult = true;
    }
}
