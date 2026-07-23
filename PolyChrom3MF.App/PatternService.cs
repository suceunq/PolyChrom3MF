using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PolyChrom3MF.App;

public enum PatternMode { Front, Cylindrical, Repeated, Triplanar }

public sealed record PatternSettings(string ImagePath, PatternMode Mode = PatternMode.Front, double Scale = 100, double Rotation = 0, double OffsetX = 0, double OffsetY = 0, int TargetObject = -1, bool FourVariants = true, byte AlphaThreshold = 24, string? DisplayName = null);

public sealed record PatternApplyResult(int ColoredTriangles, int TransparentTriangles);

public sealed class PatternService
{
    public PatternApplyResult Apply(ModelDocument document, ColorProposal proposal, PatternSettings settings, PatternMode? modeOverride = null, CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.TargetObject >= 0 && document.Objects.All(obj => obj.Index != settings.TargetObject))
            throw new InvalidDataException("L’objet ciblé par le motif n’existe pas dans ce modèle.");
        var image = Load(settings.ImagePath);
        var mode = modeOverride ?? settings.Mode;
        var bounds = Bounds(document);
        var colored = 0; var transparent = 0;
        foreach (var obj in document.Objects)
        {
            if (settings.TargetObject >= 0 && obj.Index != settings.TargetObject) continue;
            if (!proposal.TriangleAssignments.TryGetValue(obj.Index, out var assignments) || assignments.Length != obj.Triangles.Count)
            {
                assignments = new int[obj.Triangles.Count];
                Array.Fill(assignments, proposal.Assignments.GetValueOrDefault(obj.Index, 0));
                proposal.TriangleAssignments[obj.Index] = assignments;
            }
            for (var triangleIndex = 0; triangleIndex < obj.Triangles.Count; triangleIndex++)
            {
                if ((triangleIndex & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
                var triangle = obj.Triangles[triangleIndex];
                var a = obj.Vertices[triangle.A]; var b = obj.Vertices[triangle.B]; var c = obj.Vertices[triangle.C];
                var x = (a.X + b.X + c.X) / 3; var y = (a.Y + b.Y + c.Y) / 3; var z = (a.Z + b.Z + c.Z) / 3;
                var (u, v, repeats) = Coordinates(mode, x, y, z, a, b, c, bounds);
                (u, v) = Transform(u, v, settings, repeats);
                if (!repeats && (u < 0 || u > 1 || v < 0 || v > 1)) { transparent++; continue; }
                u = repeats ? Wrap(u) : Math.Clamp(u, 0, 1);
                v = repeats ? Wrap(v) : Math.Clamp(v, 0, 1);
                var pixel = image.Pixel(u, v);
                if (pixel.A < settings.AlphaThreshold) { transparent++; continue; }
                assignments[triangleIndex] = Nearest(pixel, proposal.Colors);
                colored++;
            }
        }
        return new PatternApplyResult(colored, transparent);
    }

    public static void ValidateSettings(PatternSettings settings)
    {
        if (settings is null || string.IsNullOrWhiteSpace(settings.ImagePath) || settings.ImagePath.Length > 1024 || !Enum.IsDefined(settings.Mode) || !double.IsFinite(settings.Scale) || settings.Scale is < 10 or > 400 || !double.IsFinite(settings.Rotation) || settings.Rotation is < -180 or > 180 || !double.IsFinite(settings.OffsetX) || settings.OffsetX is < -200 or > 200 || !double.IsFinite(settings.OffsetY) || settings.OffsetY is < -200 or > 200 || settings.TargetObject < -1 || settings.DisplayName?.Length > 260 || settings.DisplayName?.Any(char.IsControl) == true)
            throw new InvalidDataException("Les réglages du motif image sont invalides.");
    }

    public static void ValidateImage(string path) => _ = Load(path);

    public static string PrepareImage(string path, bool removeBackground, byte tolerance = 42)
    {
        if (tolerance is < 5 or > 120) throw new InvalidDataException("La tolérance de suppression du fond doit être comprise entre 5 et 120.");
        var bitmap = LoadBitmap(path);
        var stride = checked(bitmap.PixelWidth * 4);
        var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        if (removeBackground) RemoveEdgeBackground(pixels, bitmap.PixelWidth, bitmap.PixelHeight, stride, tolerance);
        var outputBitmap = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyChrom3MF", "PatternCache");
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(outputBitmap));
        encoder.Save(output);
        return destination;
    }

    static PatternImage Load(string path)
    {
        var bitmap = LoadBitmap(path);
        var stride = checked(bitmap.PixelWidth * 4); var pixels = new byte[checked(stride * bitmap.PixelHeight)];
        bitmap.CopyPixels(pixels, stride, 0);
        return new PatternImage(bitmap.PixelWidth, bitmap.PixelHeight, stride, pixels);
    }

    static BitmapSource LoadBitmap(string path)
    {
        var file = new FileInfo(path);
        var extension = file.Extension.ToLowerInvariant();
        if (!file.Exists || extension is not (".png" or ".jpg" or ".jpeg") || file.Length <= 0)
            throw new InvalidDataException("Le motif doit être une image PNG, JPG ou JPEG valide.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new InvalidDataException("Les images animées ou multiframe ne sont pas prises en charge.");
        BitmapSource bitmap = decoder.Frames[0];
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            throw new InvalidDataException("La résolution de l’image est invalide.");
        if (bitmap.Format != PixelFormats.Bgra32) bitmap = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        bitmap.Freeze();
        return bitmap;
    }

    static void RemoveEdgeBackground(byte[] pixels, int width, int height, int stride, byte tolerance)
    {
        var references = new[]
        {
            RgbAt(pixels, 0, 0, stride), RgbAt(pixels, width - 1, 0, stride),
            RgbAt(pixels, 0, height - 1, stride), RgbAt(pixels, width - 1, height - 1, stride)
        };
        var threshold = tolerance * tolerance * 3;
        var removed = new bool[checked(width * height)];
        var queue = new Queue<int>();
        bool Similar(int index)
        {
            var offset = (index / width) * stride + (index % width) * 4;
            foreach (var reference in references)
            {
                var db = pixels[offset] - reference.B;
                var dg = pixels[offset + 1] - reference.G;
                var dr = pixels[offset + 2] - reference.R;
                if (db * db + dg * dg + dr * dr <= threshold) return true;
            }
            return false;
        }
        void Add(int index)
        {
            if (removed[index] || !Similar(index)) return;
            removed[index] = true;
            queue.Enqueue(index);
        }
        for (var x = 0; x < width; x++) { Add(x); Add((height - 1) * width + x); }
        for (var y = 1; y < height - 1; y++) { Add(y * width); Add(y * width + width - 1); }
        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index % width;
            var y = index / width;
            pixels[y * stride + x * 4 + 3] = 0;
            if (x > 0) Add(index - 1);
            if (x + 1 < width) Add(index + 1);
            if (y > 0) Add(index - width);
            if (y + 1 < height) Add(index + width);
        }
    }

    static (byte B, byte G, byte R) RgbAt(byte[] pixels, int x, int y, int stride)
    {
        var offset = y * stride + x * 4;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    static (double U, double V, bool Repeats) Coordinates(PatternMode mode, double x, double y, double z, Vertex a, Vertex b, Vertex c, ModelBounds bounds)
    {
        var nx = (x - bounds.MinX) / bounds.SizeX; var ny = (y - bounds.MinY) / bounds.SizeY; var nz = (z - bounds.MinZ) / bounds.SizeZ;
        if (mode == PatternMode.Cylindrical) return ((Math.Atan2(y - bounds.CenterY, x - bounds.CenterX) + Math.PI) / (2 * Math.PI), 1 - nz, true);
        if (mode == PatternMode.Repeated) return (nx * 3, (1 - nz) * 3, true);
        if (mode != PatternMode.Triplanar) return (nx, 1 - nz, false);
        var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z; var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;
        var normalX = Math.Abs(uy * vz - uz * vy); var normalY = Math.Abs(uz * vx - ux * vz); var normalZ = Math.Abs(ux * vy - uy * vx);
        if (normalZ >= normalX && normalZ >= normalY) return (nx * 2, (1 - ny) * 2, true);
        return normalX >= normalY ? (ny * 2, (1 - nz) * 2, true) : (nx * 2, (1 - nz) * 2, true);
    }

    static (double U, double V) Transform(double u, double v, PatternSettings settings, bool repeats)
    {
        var coverage = settings.Scale / 100d;
        u = (u - .5) / coverage + .5 + settings.OffsetX / 100d;
        v = (v - .5) / coverage + .5 - settings.OffsetY / 100d;
        var radians = settings.Rotation * Math.PI / 180; var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var x = u - .5; var y = v - .5;
        u = x * cos - y * sin + .5; v = x * sin + y * cos + .5;
        return (u, v);
    }

    static int Nearest(Pixel pixel, IReadOnlyList<PaletteColor> colors)
    {
        var best = 0; var bestDistance = double.MaxValue;
        for (var i = 0; i < colors.Count; i++)
        {
            var color = colors[i].Color; var dr = pixel.R - color.R; var dg = pixel.G - color.G; var db = pixel.B - color.B;
            var distance = dr * dr * .30 + dg * dg * .59 + db * db * .11;
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    static double Wrap(double value) => value - Math.Floor(value);
    static ModelBounds Bounds(ModelDocument document)
    {
        var vertices = document.Objects.SelectMany(obj => obj.Vertices).ToArray();
        var minX = vertices.Min(v => v.X); var maxX = vertices.Max(v => v.X); var minY = vertices.Min(v => v.Y); var maxY = vertices.Max(v => v.Y); var minZ = vertices.Min(v => v.Z); var maxZ = vertices.Max(v => v.Z);
        return new ModelBounds(minX, minY, minZ, Math.Max(.000001, maxX - minX), Math.Max(.000001, maxY - minY), Math.Max(.000001, maxZ - minZ), (minX + maxX) / 2, (minY + maxY) / 2);
    }

    readonly record struct Pixel(byte B, byte G, byte R, byte A);
    sealed record PatternImage(int Width, int Height, int Stride, byte[] Pixels)
    {
        public Pixel Pixel(double u, double v)
        {
            var x = Math.Clamp((int)Math.Round(u * (Width - 1)), 0, Width - 1); var y = Math.Clamp((int)Math.Round(v * (Height - 1)), 0, Height - 1);
            var offset = y * Stride + x * 4; return new Pixel(Pixels[offset], Pixels[offset + 1], Pixels[offset + 2], Pixels[offset + 3]);
        }
    }
    readonly record struct ModelBounds(double MinX, double MinY, double MinZ, double SizeX, double SizeY, double SizeZ, double CenterX, double CenterY);
}
