using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace PolyChrom3MF.App;

public sealed record BeginnerChoice(string ModelPath, int ColorCount, int StyleIndex, bool FunMode);

public sealed class BeginnerWizardWindow : Window
{
    readonly TextBox _path = new() { IsReadOnly = true, MinWidth = 430, Margin = new Thickness(0, 4, 6, 10) };
    readonly ComboBox _colors = new() { ItemsSource = new[] { 2, 4, 6, 8, 12, 16, 24, 32 }, SelectedIndex = 1, Margin = new Thickness(0, 4, 0, 10) };
    readonly ComboBox _style = new() { ItemsSource = new[] { "Dégradé dynamique", "Camouflage", "Double personnalité", "Graffiti pop" }, SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 10) };
    readonly CheckBox _fun = new() { Content = "Créer des motifs fun sur toute la figurine", IsChecked = true, Margin = new Thickness(0, 5, 0, 15) };
    public BeginnerChoice? Choice { get; private set; }

    public BeginnerWizardWindow(AppSettings settings, string slicer)
    {
        Title = "Assistant simplifié PolyChrom";
        Width = 680; Height = 530; MinWidth = 560; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(24) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var start = new Button { Content = "Créer les propositions", IsDefault = true, MinWidth = 170 };
        var cancel = new Button { Content = "Annuler", IsCancel = true, MinWidth = 100 };
        start.Click += Start_Click; cancel.Click += (_, _) => Close(); actions.Children.Add(start); actions.Children.Add(cancel);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "MODE DÉBUTANT", FontSize = 26, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Importez, choisissez un style, prévisualisez puis exportez vers votre slicer.", Margin = new Thickness(0, 5, 0, 18) });
        panel.Children.Add(new TextBlock { Text = $"Imprimante : {settings.PrinterName} · {settings.MaterialSlots} couleurs\nSlicer détecté : {slicer}", Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "1. Modèle 3MF ou STL", FontWeight = FontWeights.Bold });
        var file = new DockPanel(); var browse = new Button { Content = "Parcourir…", MinWidth = 100 }; browse.Click += Browse_Click; DockPanel.SetDock(browse, Dock.Right); file.Children.Add(browse); file.Children.Add(_path); panel.Children.Add(file);
        panel.Children.Add(new TextBlock { Text = "2. Nombre de couleurs", FontWeight = FontWeights.Bold }); panel.Children.Add(_colors);
        panel.Children.Add(new TextBlock { Text = "3. Style de départ", FontWeight = FontWeights.Bold }); panel.Children.Add(_style);
        panel.Children.Add(_fun); root.Children.Add(panel); Content = root;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Modèles 3D (*.3mf;*.stl)|*.3mf;*.stl", CheckFileExists = true };
        if (dialog.ShowDialog() == true) _path.Text = dialog.FileName;
    }

    void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_path.Text)) { MessageBox.Show("Choisissez un modèle 3MF ou STL."); return; }
        Choice = new BeginnerChoice(_path.Text, (int)_colors.SelectedItem, _style.SelectedIndex, _fun.IsChecked == true);
        DialogResult = true;
    }
}
