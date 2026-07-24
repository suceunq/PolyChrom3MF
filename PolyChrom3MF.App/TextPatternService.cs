using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using Point = System.Windows.Point;

namespace PolyChrom3MF.App;

public sealed class TextPatternService
{
    public string Render(string text)
    {
        text = new string((text ?? "").Where(character => !char.IsControl(character)).Take(120).ToArray()).Trim();
        if (text.Length == 0) throw new InvalidDataException("Le texte est vide.");
        const int width = 1600; const int height = 600;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Black"), 180, Brushes.White, 1.25) { TextAlignment = TextAlignment.Center, MaxTextWidth = width - 80, MaxTextHeight = height - 40 };
            context.DrawText(formatted, new Point(width / 2, Math.Max(20, (height - formatted.Height) / 2)));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyChrom 3MF", "PatternCache");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"texte-{Guid.NewGuid():N}.png");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
        return path;
    }
}
