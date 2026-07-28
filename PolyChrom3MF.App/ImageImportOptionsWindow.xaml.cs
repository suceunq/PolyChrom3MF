using System.Windows;
using System.Windows.Input;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace PolyChrom3MF.App;

public partial class ImageImportOptionsWindow : Window
{
    readonly ImageMaskDocument _mask;
    bool _painting;
    Point? _lastPoint;

    public string? PreparedImagePath { get; private set; }

    public ImageImportOptionsWindow(string imagePath)
    {
        InitializeComponent();
        _mask = new ImageMaskDocument(imagePath);
        RefreshPreview();
    }

    async void Auto_Click(object sender, RoutedEventArgs e)
    {
        var tolerance = (byte)Math.Round(ToleranceSlider.Value);
        await ApplyAsync(() => _mask.RemoveAutomatic(tolerance), "Fond relié aux bords supprimé.");
    }

    async void Dominant_Click(object sender, RoutedEventArgs e)
    {
        var tolerance = (byte)Math.Round(ToleranceSlider.Value);
        await ApplyAsync(() => _mask.RemoveDominantColor(tolerance), "Couleur dominante supprimée.");
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _mask.Reset();
        RefreshPreview();
        StatusText.Text = "Image originale restaurée.";
    }

    async Task ApplyAsync(Action action, string status)
    {
        IsEnabled = false;
        StatusText.Text = "Traitement du masque…";
        try
        {
            await Task.Run(action);
            RefreshPreview();
            StatusText.Text = status;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Détourage impossible", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsEnabled = true; }
    }

    void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _painting = true;
        _lastPoint = null;
        Checker.CaptureMouse();
        PaintAt(e.GetPosition(Preview));
        e.Handled = true;
    }

    void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_painting || e.LeftButton != MouseButtonState.Pressed) return;
        var current = e.GetPosition(Preview);
        if (_lastPoint is Point previous)
        {
            var distance = (current - previous).Length;
            var steps = Math.Max(1, (int)Math.Ceiling(distance / 4));
            for (var step = 1; step <= steps; step++)
                PaintAt(previous + (current - previous) * (step / (double)steps), false);
        }
        PaintAt(current);
    }

    void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _painting = false;
        _lastPoint = null;
        Checker.ReleaseMouseCapture();
        RefreshPreview();
        StatusText.Text = EraseTool.IsChecked == true ? "Zone supprimée à la gomme." : "Zone restaurée.";
    }

    void PaintAt(Point point, bool refresh = true)
    {
        if (Preview.ActualWidth <= 0 || Preview.ActualHeight <= 0) return;
        var scale = Math.Min(Preview.ActualWidth / _mask.Width, Preview.ActualHeight / _mask.Height);
        var renderedWidth = _mask.Width * scale;
        var renderedHeight = _mask.Height * scale;
        var left = (Preview.ActualWidth - renderedWidth) / 2;
        var top = (Preview.ActualHeight - renderedHeight) / 2;
        var x = (point.X - left) / scale;
        var y = (point.Y - top) / scale;
        if (x < 0 || y < 0 || x >= _mask.Width || y >= _mask.Height) return;
        var radius = BrushSlider.Value / Math.Max(.001, scale);
        _mask.Paint(x, y, radius, EraseTool.IsChecked == true ? MaskBrushMode.Remove : MaskBrushMode.Restore);
        _lastPoint = point;
        if (refresh) RefreshPreview();
    }

    void RefreshPreview() => Preview.Source = _mask.Preview();

    void ToleranceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ToleranceValue is not null) ToleranceValue.Text = Math.Round(e.NewValue).ToString();
    }

    void BrushChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrushValue is not null) BrushValue.Text = $"{Math.Round(e.NewValue)} px";
    }

    void Continue_Click(object sender, RoutedEventArgs e)
    {
        PreparedImagePath = _mask.SavePng();
        DialogResult = true;
        Close();
    }
}
