using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PolyChrom3MF.App;

public enum PatternMode { Front, Cylindrical, Repeated, Triplanar }

public sealed record PatternSettings(
    string ImagePath,
    PatternMode Mode = PatternMode.Front,
    double Scale = 100,
    double Rotation = 0,
    double OffsetX = 0,
    double OffsetY = 0,
    int TargetObject = -1,
    bool FourVariants = true,
    byte AlphaThreshold = 24,
    string? DisplayName = null,
    bool MonochromeLogo = false,
    int LogoColorIndex = 0,
    bool InvertLogo = false,
    byte LogoThreshold = 128,
    bool RepeatAcrossModel = true,
    double StretchX = 100,
    double StretchY = 100,
    int Copies = 1,
    double Spacing = 0,
    bool MirrorX = false,
    bool MirrorY = false,
    bool BackFacePreview = true);

public sealed record PatternApplyResult(int ColoredTriangles, int TransparentTriangles)
{
    public Dictionary<int, HashSet<int>> RefinementTriangles { get; init; } = [];
}

public sealed class PatternService
{
    public PatternApplyResult Apply(ModelDocument document, ColorProposal proposal, PatternSettings settings, PatternMode? modeOverride = null, CancellationToken cancellationToken = default, bool collectRefinement = false)
    {
        ValidateSettings(settings);
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.TargetObject >= 0 && document.Objects.All(obj => obj.Index != settings.TargetObject))
            throw new InvalidDataException("L’objet ciblé par le motif n’existe pas dans ce modèle.");
        var image = Load(settings.ImagePath);
        // Parsing hexadecimal WPF colors for every triangle was the dominant cost
        // on dense models. Resolve the printable palette once for this whole pass.
        var palette = proposal.Colors.Select(color => color.Color).ToArray();
        if (palette.Length == 0 || settings.MonochromeLogo && settings.LogoColorIndex >= palette.Length)
            throw new InvalidDataException("La couleur de filament choisie pour le logo n’existe pas dans cette proposition.");
        var mode = modeOverride ?? settings.Mode;
        var bounds = Bounds(document);
        var colored = 0; var transparent = 0;
        var refinement = new Dictionary<int, HashSet<int>>();
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
                int? Classify(double x, double y, double z)
                {
                    Pixel pixel;
                    if (mode == PatternMode.Triplanar)
                    {
                        pixel = TriplanarPixel(image, x, y, z, a, b, c, bounds, settings);
                    }
                    else
                    {
                        var (u, v, repeats) = Coordinates(mode, x, y, z, a, b, c, bounds);
                        (u, v, repeats) = ApplyCoverage(u, v, repeats, mode, settings.RepeatAcrossModel);
                        (u, v) = Transform(u, v, settings, repeats);
                        if (!repeats && (u < 0 || u > 1 || v < 0 || v > 1)) return null;
                        u = repeats ? SeamlessWrap(u) : Math.Clamp(u, 0, 1);
                        v = repeats ? SeamlessWrap(v) : Math.Clamp(v, 0, 1);
                        pixel = image.Pixel(u, v);
                    }
                    if (pixel.A < settings.AlphaThreshold) return null;
                    if (!settings.MonochromeLogo) return Nearest(pixel, palette);
                    if (!IsLogoPixel(pixel.R, pixel.G, pixel.B, settings.LogoThreshold, settings.InvertLogo))
                        return null;
                    return settings.LogoColorIndex;
                }

                var center = Classify((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                if (collectRefinement)
                {
                    var samples = new HashSet<int?>
                    {
                        center,
                        Classify(a.X, a.Y, a.Z), Classify(b.X, b.Y, b.Z), Classify(c.X, c.Y, c.Z),
                        Classify((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2),
                        Classify((b.X + c.X) / 2, (b.Y + c.Y) / 2, (b.Z + c.Z) / 2),
                        Classify((c.X + a.X) / 2, (c.Y + a.Y) / 2, (c.Z + a.Z) / 2)
                    };
                    if (samples.Count > 1)
                    {
                        if (!refinement.TryGetValue(obj.Index, out var triangles)) refinement[obj.Index] = triangles = [];
                        triangles.Add(triangleIndex);
                    }
                }
                if (center is int colorIndex) { assignments[triangleIndex] = colorIndex; colored++; }
                else transparent++;
            }
        }
        return new PatternApplyResult(colored, transparent) { RefinementTriangles = refinement };
    }

    static Pixel TriplanarPixel(PatternImage image, double x, double y, double z, Vertex a, Vertex b, Vertex c, ModelBounds bounds, PatternSettings settings)
    {
        var nx = (x - bounds.MinX) / bounds.SizeX;
        var ny = (y - bounds.MinY) / bounds.SizeY;
        var nz = (z - bounds.MinZ) / bounds.SizeZ;
        var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z;
        var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;
        var normalX = Math.Abs(uy * vz - uz * vy);
        var normalY = Math.Abs(uz * vx - ux * vz);
        var normalZ = Math.Abs(ux * vy - uy * vx);
        var total = Math.Max(.000001, normalX + normalY + normalZ);

        Pixel Sample(double u, double v)
        {
            (u, v, _) = ApplyCoverage(u, v, true, PatternMode.Triplanar, settings.RepeatAcrossModel);
            (u, v) = Transform(u, v, settings, true);
            return image.Pixel(SeamlessWrap(u), SeamlessWrap(v));
        }

        var yz = Sample(ny * 2, (1 - nz) * 2);
        var xz = Sample(nx * 2, (1 - nz) * 2);
        var xy = Sample(nx * 2, (1 - ny) * 2);
        byte Blend(byte first, byte second, byte third) =>
            (byte)Math.Clamp(Math.Round((first * normalX + second * normalY + third * normalZ) / total), 0, 255);
        return new Pixel(
            Blend(yz.B, xz.B, xy.B),
            Blend(yz.G, xz.G, xy.G),
            Blend(yz.R, xz.R, xy.R),
            Blend(yz.A, xz.A, xy.A));
    }

    public static void ValidateSettings(PatternSettings settings)
    {
        if (settings is null || string.IsNullOrWhiteSpace(settings.ImagePath) || settings.ImagePath.Length > 1024 || !Enum.IsDefined(settings.Mode) || !double.IsFinite(settings.Scale) || settings.Scale is < 10 or > 400 || !double.IsFinite(settings.Rotation) || settings.Rotation is < -180 or > 180 || !double.IsFinite(settings.OffsetX) || settings.OffsetX is < -200 or > 200 || !double.IsFinite(settings.OffsetY) || settings.OffsetY is < -200 or > 200 || settings.TargetObject < -1 || settings.DisplayName?.Length > 260 || settings.DisplayName?.Any(char.IsControl) == true || settings.LogoColorIndex is < 0 or > 31 || settings.LogoThreshold is < 1 or > 254 || !double.IsFinite(settings.StretchX) || settings.StretchX is < 10 or > 400 || !double.IsFinite(settings.StretchY) || settings.StretchY is < 10 or > 400 || settings.Copies is < 1 or > 32 || !double.IsFinite(settings.Spacing) || settings.Spacing is < 0 or > 300)
            throw new InvalidDataException("Les réglages du motif image sont invalides.");
    }

    internal static bool IsLogoPixel(byte red, byte green, byte blue, byte threshold, bool invert)
    {
        var luminance = (red * 299 + green * 587 + blue * 114) / 1000;
        return invert ? luminance <= threshold : luminance >= threshold;
    }

    internal static (double U, double V, bool Repeats) ApplyCoverage(double u, double v, bool repeats, PatternMode mode, bool repeatAcrossModel)
    {
        if (!repeatAcrossModel || mode == PatternMode.Repeated) return (u, v, repeats);
        // A single frontal projection can leave large parts of an articulated
        // model on one background pixel. Tiling gives every component several
        // opportunities to meet the actual logo while preserving its shape.
        return (u * 3, v * 3, true);
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
        var coverageX = settings.Scale / 100d * settings.StretchX / 100d;
        var coverageY = settings.Scale / 100d * settings.StretchY / 100d;
        u = (u - .5) / coverageX + .5 + settings.OffsetX / 100d;
        v = (v - .5) / coverageY + .5 - settings.OffsetY / 100d;
        var radians = settings.Rotation * Math.PI / 180; var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var x = u - .5; var y = v - .5;
        u = x * cos - y * sin + .5; v = x * sin + y * cos + .5;
        if (settings.Copies > 1)
        {
            var period = 1 + settings.Spacing / 100d;
            u = u * settings.Copies / period;
        }
        if (settings.MirrorX) u = 1 - u;
        if (settings.MirrorY) v = 1 - v;
        return (u, v);
    }

    static int Nearest(Pixel pixel, IReadOnlyList<System.Windows.Media.Color> colors)
    {
        var best = 0; var bestDistance = double.MaxValue;
        for (var i = 0; i < colors.Count; i++)
        {
            var color = colors[i]; var dr = pixel.R - color.R; var dg = pixel.G - color.G; var db = pixel.B - color.B;
            var distance = dr * dr * .30 + dg * dg * .59 + db * db * .11;
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    internal static double SeamlessWrap(double value)
    {
        var tile = Math.Floor(value);
        var fraction = value - tile;
        return ((long)tile & 1) == 0 ? fraction : 1 - fraction;
    }
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
            var px = Math.Clamp(u, 0, 1) * (Width - 1);
            var py = Math.Clamp(v, 0, 1) * (Height - 1);
            var x0 = (int)Math.Floor(px); var y0 = (int)Math.Floor(py);
            var x1 = Math.Min(x0 + 1, Width - 1); var y1 = Math.Min(y0 + 1, Height - 1);
            var tx = px - x0; var ty = py - y0;
            byte Channel(int channel)
            {
                var top = Pixels[y0 * Stride + x0 * 4 + channel] * (1 - tx) + Pixels[y0 * Stride + x1 * 4 + channel] * tx;
                var bottom = Pixels[y1 * Stride + x0 * 4 + channel] * (1 - tx) + Pixels[y1 * Stride + x1 * 4 + channel] * tx;
                return (byte)Math.Clamp(Math.Round(top * (1 - ty) + bottom * ty), 0, 255);
            }
            return new Pixel(Channel(0), Channel(1), Channel(2), Channel(3));
        }
    }
    readonly record struct ModelBounds(double MinX, double MinY, double MinZ, double SizeX, double SizeY, double SizeZ, double CenterX, double CenterY);
}
