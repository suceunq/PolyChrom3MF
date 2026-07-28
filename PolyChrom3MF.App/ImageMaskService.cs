using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PolyChrom3MF.App;

public enum MaskBrushMode
{
    Remove,
    Restore
}

public sealed class ImageMaskDocument
{
    readonly byte[] _original;
    readonly byte[] _pixels;

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }

    public ImageMaskDocument(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Extension.ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg"))
            throw new InvalidDataException("Choisissez une image PNG, JPG ou JPEG valide.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Les images multiframe ne sont pas prises en charge.");
        BitmapSource bitmap = decoder.Frames[0];
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) throw new InvalidDataException("La résolution de l’image est invalide.");
        if (bitmap.Format != PixelFormats.Bgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        Width = bitmap.PixelWidth;
        Height = bitmap.PixelHeight;
        Stride = checked(Width * 4);
        _original = new byte[checked(Stride * Height)];
        bitmap.CopyPixels(_original, Stride, 0);
        _pixels = (byte[])_original.Clone();
    }

    public void Reset() => Array.Copy(_original, _pixels, _pixels.Length);

    public void RemoveAutomatic(byte tolerance)
    {
        ValidateTolerance(tolerance);
        Reset();
        var references = BorderReferences();
        var visited = new bool[checked(Width * Height)];
        var queue = new Queue<int>();
        var threshold = tolerance * tolerance * 3;
        bool Similar(int index)
        {
            var offset = Offset(index);
            foreach (var reference in references)
            {
                var db = _pixels[offset] - reference.B;
                var dg = _pixels[offset + 1] - reference.G;
                var dr = _pixels[offset + 2] - reference.R;
                if (db * db + dg * dg + dr * dr <= threshold) return true;
            }
            return false;
        }
        void Add(int index)
        {
            if (visited[index] || !Similar(index)) return;
            visited[index] = true;
            queue.Enqueue(index);
        }
        for (var x = 0; x < Width; x++) { Add(x); Add((Height - 1) * Width + x); }
        for (var y = 1; y < Height - 1; y++) { Add(y * Width); Add(y * Width + Width - 1); }
        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index % Width;
            var y = index / Width;
            _pixels[Offset(index) + 3] = 0;
            if (x > 0) Add(index - 1);
            if (x + 1 < Width) Add(index + 1);
            if (y > 0) Add(index - Width);
            if (y + 1 < Height) Add(index + Width);
        }
    }

    public void RemoveDominantColor(byte tolerance)
    {
        ValidateTolerance(tolerance);
        Reset();
        var histogram = new Dictionary<int, int>();
        var step = Math.Max(1, Width * Height / 250_000);
        for (var index = 0; index < Width * Height; index += step)
        {
            var offset = Offset(index);
            if (_pixels[offset + 3] == 0) continue;
            var key = (_pixels[offset + 2] >> 3) << 10 | (_pixels[offset + 1] >> 3) << 5 | _pixels[offset] >> 3;
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
        }
        if (histogram.Count == 0) return;
        var dominant = histogram.MaxBy(pair => pair.Value).Key;
        var reference = (B: (byte)((dominant & 31) << 3), G: (byte)(((dominant >> 5) & 31) << 3), R: (byte)(((dominant >> 10) & 31) << 3));
        var threshold = tolerance * tolerance * 3;
        for (var index = 0; index < Width * Height; index++)
        {
            var offset = Offset(index);
            var db = _pixels[offset] - reference.B;
            var dg = _pixels[offset + 1] - reference.G;
            var dr = _pixels[offset + 2] - reference.R;
            if (db * db + dg * dg + dr * dr <= threshold) _pixels[offset + 3] = 0;
        }
    }

    public void Paint(double imageX, double imageY, double radius, MaskBrushMode mode)
    {
        if (!double.IsFinite(imageX) || !double.IsFinite(imageY) || !double.IsFinite(radius) || radius <= 0) return;
        var minX = Math.Max(0, (int)Math.Floor(imageX - radius));
        var maxX = Math.Min(Width - 1, (int)Math.Ceiling(imageX + radius));
        var minY = Math.Max(0, (int)Math.Floor(imageY - radius));
        var maxY = Math.Min(Height - 1, (int)Math.Ceiling(imageY + radius));
        var squared = radius * radius;
        for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - imageX;
                var dy = y - imageY;
                if (dx * dx + dy * dy > squared) continue;
                var offset = y * Stride + x * 4;
                _pixels[offset + 3] = mode == MaskBrushMode.Remove ? (byte)0 : _original[offset + 3];
            }
    }

    public BitmapSource Preview()
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, _pixels, Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public string SavePng()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyChrom3MF", "PatternCache");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Preview()));
        encoder.Save(output);
        return destination;
    }

    int Offset(int pixelIndex) => pixelIndex / Width * Stride + pixelIndex % Width * 4;

    (byte B, byte G, byte R)[] BorderReferences() =>
    [
        At(0, 0), At(Width - 1, 0), At(0, Height - 1), At(Width - 1, Height - 1),
        At(Width / 2, 0), At(Width / 2, Height - 1), At(0, Height / 2), At(Width - 1, Height / 2)
    ];

    (byte B, byte G, byte R) At(int x, int y)
    {
        var offset = y * Stride + x * 4;
        return (_pixels[offset], _pixels[offset + 1], _pixels[offset + 2]);
    }

    static void ValidateTolerance(byte tolerance)
    {
        if (tolerance is < 1 or > 160) throw new InvalidDataException("La tolérance doit être comprise entre 1 et 160.");
    }
}
