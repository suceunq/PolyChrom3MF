using System.Windows;
using System.Windows.Media.Imaging;

namespace PolyChrom3MF.App;

public partial class ImageImportOptionsWindow : Window
{
    public bool RemoveBackground => RemoveBackgroundCheck.IsChecked == true;
    public byte Tolerance => (byte)Math.Round(ToleranceSlider.Value);

    public ImageImportOptionsWindow(string imagePath)
    {
        InitializeComponent();
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 540;
        image.UriSource = new Uri(imagePath);
        image.EndInit();
        image.Freeze();
        Preview.Source = image;
        UpdateControls();
    }

    void OptionChanged(object sender, RoutedEventArgs e) => UpdateControls();
    void ToleranceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ToleranceValue is not null) ToleranceValue.Text = Math.Round(e.NewValue).ToString();
    }
    void UpdateControls()
    {
        if (TolerancePanel is not null) TolerancePanel.IsEnabled = RemoveBackground;
    }
    void Continue_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
