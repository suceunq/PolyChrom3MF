using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PolyChrom3MF.Logos;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace PolyChrom3MF.App;

public partial class ImageImportOptionsWindow : Window
{
    readonly LogoRaster _source;
    readonly LogoMaskEditor _mask;
    bool _painting;
    Point? _lastPoint;
    readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    public string? PreparedImagePath { get; private set; }

    public ImageImportOptionsWindow(string imagePath)
    {
        InitializeComponent();
        _source = new LogoImageImporter().Import(imagePath);
        _mask = new LogoMaskEditor(_source);
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RefreshPreview(); };
        Closed += (_, _) => _previewTimer.Stop();
        RefreshPreview();
    }

    async void Auto_Click(object sender, RoutedEventArgs e)
    {
        var tolerance = (byte)Math.Round(ToleranceSlider.Value);
        await ApplyAsync(() => _mask.RemoveBorderBackground(tolerance), "Fond relié aux bords supprimé.");
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
        var scale = Math.Min(Preview.ActualWidth / _source.Width, Preview.ActualHeight / _source.Height);
        var renderedWidth = _source.Width * scale;
        var renderedHeight = _source.Height * scale;
        var left = (Preview.ActualWidth - renderedWidth) / 2;
        var top = (Preview.ActualHeight - renderedHeight) / 2;
        var x = (point.X - left) / scale;
        var y = (point.Y - top) / scale;
        if (x < 0 || y < 0 || x >= _source.Width || y >= _source.Height) return;
        var radius = BrushSlider.Value / Math.Max(.001, scale);
        _mask.ApplyBrush((float)x, (float)y, (float)radius,
            EraseTool.IsChecked == true ? LogoBrushMode.Erase : LogoBrushMode.Restore);
        _lastPoint = point;
        if (refresh)
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }
    }

    void RefreshPreview()
    {
        var png = LogoImageImporter.EncodePng(_mask.Snapshot());
        using var stream = new MemoryStream(png, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        Preview.Source = image;
    }

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
        PreparedImagePath = Path.Combine(Path.GetTempPath(), $"PolyChrom-logo-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(PreparedImagePath, LogoImageImporter.EncodePng(_mask.Snapshot()));
        DialogResult = true;
        Close();
    }
}
