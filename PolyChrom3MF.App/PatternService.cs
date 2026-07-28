using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PolyChrom3MF.App;

public enum PatternMode { Front, Cylindrical, Repeated, Triplanar }
public enum PatternAlignment { Horizontal, Vertical, Circular, Manual }
public sealed record PatternOccurrence(double U, double V, double Rotation = 0, int TargetObject = -1, bool Enabled = true);

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
    bool BackFacePreview = true,
    double AnchorU = .5,
    double AnchorV = .5,
    double TiltX = 0,
    double TiltY = 0,
    double ReliefDepth = 0,
    PatternAlignment Alignment = PatternAlignment.Horizontal,
    IReadOnlyList<PatternOccurrence>? Occurrences = null,
    bool LocalSubdivision = true,
    bool HasSurfaceFrame = false,
    double SurfaceX = 0,
    double SurfaceY = 0,
    double SurfaceZ = 0,
    double SurfaceUx = 1,
    double SurfaceUy = 0,
    double SurfaceUz = 0,
    double SurfaceVx = 0,
    double SurfaceVy = 0,
    double SurfaceVz = 1,
    double SurfaceNx = 0,
    double SurfaceNy = -1,
    double SurfaceNz = 0,
    double SurfaceWorldSize = 1);

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
            if (settings.TargetObject >= 0 && settings.Alignment != PatternAlignment.Manual && obj.Index != settings.TargetObject) continue;
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
                    if (mode == PatternMode.Triplanar)
                    {
                        var pixel = TriplanarPixel(image, x, y, z, a, b, c, bounds, settings);
                        return ClassifyPixel(pixel);
                    }
                    double u; double v; bool repeats;
                    if (settings.HasSurfaceFrame)
                    {
                        var dx = x - settings.SurfaceX;
                        var dy = y - settings.SurfaceY;
                        var dz = z - settings.SurfaceZ;
                        var normalDistance = dx * settings.SurfaceNx + dy * settings.SurfaceNy + dz * settings.SurfaceNz;
                        // Keep the decal close to the tangent plane selected by
                        // the user. A deep slab makes the same image reappear on
                        // curved or opposite parts of the mesh, visually far
                        // outside the manipulation frame.
                        if (Math.Abs(normalDistance) > settings.SurfaceWorldSize * .12) return null;
                        var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z;
                        var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;
                        var nx = uy * vz - uz * vy; var ny = uz * vx - ux * vz; var nz = ux * vy - uy * vx;
                        var normalAgreement = nx * settings.SurfaceNx + ny * settings.SurfaceNy + nz * settings.SurfaceNz;
                        if (normalAgreement < 0 && !settings.BackFacePreview) return null;
                        u = .5 + (dx * settings.SurfaceUx + dy * settings.SurfaceUy + dz * settings.SurfaceUz) / settings.SurfaceWorldSize;
                        v = .5 - (dx * settings.SurfaceVx + dy * settings.SurfaceVy + dz * settings.SurfaceVz) / settings.SurfaceWorldSize;
                        repeats = false;
                    }
                    else (u, v, repeats) = Coordinates(mode, x, y, z, a, b, c, bounds);
                    (u, v, repeats) = ApplyCoverage(u, v, repeats, mode, settings.RepeatAcrossModel);
                    foreach (var occurrence in BuildOccurrences(settings))
                    {
                        if (!occurrence.Enabled || occurrence.TargetObject >= 0 && occurrence.TargetObject != obj.Index) continue;
                        var transformed = Transform(u, v, settings, repeats, occurrence);
                        if (!repeats && (transformed.U < 0 || transformed.U > 1 || transformed.V < 0 || transformed.V > 1)) continue;
                        var pixel = repeats
                            ? image.RepeatingPixel(transformed.U, transformed.V)
                            : image.Pixel(Math.Clamp(transformed.U, 0, 1), Math.Clamp(transformed.V, 0, 1));
                        if (ClassifyPixel(pixel) is int color) return color;
                    }
                    return null;

                    int? ClassifyPixel(Pixel pixel)
                    {
                    if (pixel.A < settings.AlphaThreshold) return null;
                    if (!settings.MonochromeLogo) return Nearest(pixel, palette);
                    if (!IsLogoPixel(pixel.R, pixel.G, pixel.B, settings.LogoThreshold, settings.InvertLogo))
                        return null;
                    return settings.LogoColorIndex;
                    }
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
                    if (settings.LocalSubdivision && (center is not null || samples.Count > 1))
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
            Pixel? fallback = null;
            foreach (var occurrence in BuildOccurrences(settings))
            {
                if (!occurrence.Enabled) continue;
                var transformed = Transform(u, v, settings, settings.RepeatAcrossModel, occurrence);
                if (!settings.RepeatAcrossModel && (transformed.U < 0 || transformed.U > 1 || transformed.V < 0 || transformed.V > 1)) continue;
                var pixel = settings.RepeatAcrossModel
                    ? image.RepeatingPixel(transformed.U, transformed.V)
                    : image.Pixel(Math.Clamp(transformed.U, 0, 1), Math.Clamp(transformed.V, 0, 1));
                fallback ??= pixel;
                if (pixel.A >= settings.AlphaThreshold) return pixel;
            }
            return fallback ?? new Pixel(0, 0, 0, 0);
        }

        var yz = Sample(ny * 2, (1 - nz) * 2);
        var xz = Sample(nx * 2, (1 - nz) * 2);
        var xy = Sample(nx * 2, (1 - ny) * 2);
        if (image.IsHighContrastMonochrome)
        {
            var candidates = new[] { (Pixel: yz, Weight: normalX), (Pixel: xz, Weight: normalY), (Pixel: xy, Weight: normalZ) };
            return candidates
                .Where(candidate => candidate.Weight / total >= .025)
                .OrderBy(candidate => Luminance(candidate.Pixel))
                .DefaultIfEmpty((Pixel: yz, Weight: normalX))
                .First().Pixel;
        }
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
        if (settings is null || string.IsNullOrWhiteSpace(settings.ImagePath) || settings.ImagePath.Length > 1024 || !Enum.IsDefined(settings.Mode) || !Enum.IsDefined(settings.Alignment) || !double.IsFinite(settings.Scale) || settings.Scale is < 10 or > 400 || !double.IsFinite(settings.Rotation) || settings.Rotation is < -180 or > 180 || !double.IsFinite(settings.OffsetX) || settings.OffsetX is < -200 or > 200 || !double.IsFinite(settings.OffsetY) || settings.OffsetY is < -200 or > 200 || settings.TargetObject < -1 || settings.DisplayName?.Length > 260 || settings.DisplayName?.Any(char.IsControl) == true || settings.LogoColorIndex is < 0 or > 31 || settings.LogoThreshold is < 1 or > 254 || !double.IsFinite(settings.StretchX) || settings.StretchX is < 10 or > 400 || !double.IsFinite(settings.StretchY) || settings.StretchY is < 10 or > 400 || settings.Copies is < 1 or > 32 || !double.IsFinite(settings.Spacing) || settings.Spacing is < 0 or > 300 || !double.IsFinite(settings.AnchorU) || settings.AnchorU is < -2 or > 3 || !double.IsFinite(settings.AnchorV) || settings.AnchorV is < -2 or > 3 || !double.IsFinite(settings.TiltX) || settings.TiltX is < -75 or > 75 || !double.IsFinite(settings.TiltY) || settings.TiltY is < -75 or > 75 || !double.IsFinite(settings.ReliefDepth) || settings.ReliefDepth is < -2 or > 5 || settings.Occurrences is { Count: > 64 } || settings.Occurrences?.Any(item => !double.IsFinite(item.U) || !double.IsFinite(item.V) || !double.IsFinite(item.Rotation) || item.U is < -2 or > 3 || item.V is < -2 or > 3 || item.Rotation is < -360 or > 360 || item.TargetObject < -1) == true || settings.HasSurfaceFrame && (!double.IsFinite(settings.SurfaceX) || !double.IsFinite(settings.SurfaceY) || !double.IsFinite(settings.SurfaceZ) || !double.IsFinite(settings.SurfaceUx) || !double.IsFinite(settings.SurfaceUy) || !double.IsFinite(settings.SurfaceUz) || !double.IsFinite(settings.SurfaceVx) || !double.IsFinite(settings.SurfaceVy) || !double.IsFinite(settings.SurfaceVz) || !double.IsFinite(settings.SurfaceNx) || !double.IsFinite(settings.SurfaceNy) || !double.IsFinite(settings.SurfaceNz) || !double.IsFinite(settings.SurfaceWorldSize) || settings.SurfaceWorldSize <= 1e-6))
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
        return new PatternImage(bitmap.PixelWidth, bitmap.PixelHeight, stride, pixels, IsHighContrastMonochrome(pixels, stride));
    }

    static bool IsHighContrastMonochrome(byte[] pixels, int stride)
    {
        var rowWidth = stride / 4;
        var pixelCount = pixels.Length / 4;
        var step = Math.Max(1, pixelCount / 20_000);
        var grayscale = 0; var dark = 0; var light = 0; var sampled = 0;
        for (var pixel = 0; pixel < pixelCount; pixel += step)
        {
            var offset = pixel / rowWidth * stride + pixel % rowWidth * 4;
            var b = pixels[offset]; var g = pixels[offset + 1]; var r = pixels[offset + 2];
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 24) grayscale++;
            var luminance = (r * 299 + g * 587 + b * 114) / 1000;
            if (luminance <= 64) dark++;
            if (luminance >= 192) light++;
            sampled++;
        }
        return sampled > 0 && grayscale >= sampled * .9 && dark >= sampled * .01 && light >= sampled * .01;
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
        if (mode == PatternMode.Cylindrical) return ((Math.Atan2(y - bounds.CenterY, x - bounds.CenterX) + Math.PI) / (2 * Math.PI), 1 - nz, false);
        if (mode == PatternMode.Repeated) return (nx * 3, (1 - nz) * 3, true);
        if (mode != PatternMode.Triplanar) return (nx, 1 - nz, false);
        var ux = b.X - a.X; var uy = b.Y - a.Y; var uz = b.Z - a.Z; var vx = c.X - a.X; var vy = c.Y - a.Y; var vz = c.Z - a.Z;
        var normalX = Math.Abs(uy * vz - uz * vy); var normalY = Math.Abs(uz * vx - ux * vz); var normalZ = Math.Abs(ux * vy - uy * vx);
        if (normalZ >= normalX && normalZ >= normalY) return (nx * 2, (1 - ny) * 2, true);
        return normalX >= normalY ? (ny * 2, (1 - nz) * 2, true) : (nx * 2, (1 - nz) * 2, true);
    }

    static (double U, double V) Transform(double u, double v, PatternSettings settings, bool repeats, PatternOccurrence? occurrence = null)
    {
        occurrence ??= new PatternOccurrence(settings.AnchorU, settings.AnchorV);
        var coverageX = settings.Scale / 100d * settings.StretchX / 100d;
        var coverageY = settings.Scale / 100d * settings.StretchY / 100d;
        u = (u - occurrence.U) / coverageX + .5 + settings.OffsetX / 100d;
        v = (v - occurrence.V) / coverageY + .5 - settings.OffsetY / 100d;
        var shearX = Math.Tan(settings.TiltX * Math.PI / 180) * .35;
        var shearY = Math.Tan(settings.TiltY * Math.PI / 180) * .35;
        var sx = u - .5; var sy = v - .5;
        u += sy * shearX;
        v += sx * shearY;
        var radians = (settings.Rotation + occurrence.Rotation) * Math.PI / 180; var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var x = u - .5; var y = v - .5;
        u = x * cos - y * sin + .5; v = x * sin + y * cos + .5;
        if (settings.MirrorX) u = 1 - u;
        if (settings.MirrorY) v = 1 - v;
        return (u, v);
    }

    public static IReadOnlyList<PatternOccurrence> BuildOccurrences(PatternSettings settings)
    {
        if (settings.Alignment == PatternAlignment.Manual && settings.Occurrences is { Count: > 0 })
            return settings.Occurrences.Where(item => item.Enabled).Take(64).ToArray();
        var count = Math.Clamp(settings.Copies, 1, 32);
        if (count == 1) return [new PatternOccurrence(settings.AnchorU, settings.AnchorV, TargetObject: settings.TargetObject)];
        var spacing = Math.Max(.02, settings.Spacing / 100d);
        var result = new List<PatternOccurrence>(count);
        for (var index = 0; index < count; index++)
        {
            var centered = index - (count - 1) / 2d;
            result.Add(settings.Alignment switch
            {
                PatternAlignment.Vertical => new(settings.AnchorU, settings.AnchorV + centered * spacing, TargetObject: settings.TargetObject),
                PatternAlignment.Circular => new(
                    settings.AnchorU + Math.Cos(index * Math.PI * 2 / count) * spacing,
                    settings.AnchorV + Math.Sin(index * Math.PI * 2 / count) * spacing,
                    index * 360d / count + 90,
                    settings.TargetObject),
                _ => new(settings.AnchorU + centered * spacing, settings.AnchorV, TargetObject: settings.TargetObject)
            });
        }
        return result;
    }

    public static (double U, double V) SurfaceCoordinates(ModelDocument document, PatternMode mode, double x, double y, double z)
    {
        var bounds = Bounds(document);
        var nx = (x - bounds.MinX) / bounds.SizeX;
        var ny = (y - bounds.MinY) / bounds.SizeY;
        var nz = (z - bounds.MinZ) / bounds.SizeZ;
        return mode switch
        {
            PatternMode.Cylindrical => ((Math.Atan2(y - bounds.CenterY, x - bounds.CenterX) + Math.PI) / (2 * Math.PI), 1 - nz),
            PatternMode.Triplanar => (nx, 1 - nz),
            _ => (nx, 1 - nz)
        };
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

    static double Luminance(Pixel pixel) => pixel.R * .299 + pixel.G * .587 + pixel.B * .114;

    internal static double Wrap(double value) => value - Math.Floor(value);

    internal static double SeamBlendWeight(double wrapped, double width = .12)
    {
        var distance = Math.Min(wrapped, 1 - wrapped);
        if (distance >= width) return 0;
        var amount = 1 - distance / width;
        return amount * amount * (3 - 2 * amount);
    }
    static ModelBounds Bounds(ModelDocument document)
    {
        var vertices = document.Objects.SelectMany(obj => obj.Vertices).ToArray();
        var minX = vertices.Min(v => v.X); var maxX = vertices.Max(v => v.X); var minY = vertices.Min(v => v.Y); var maxY = vertices.Max(v => v.Y); var minZ = vertices.Min(v => v.Z); var maxZ = vertices.Max(v => v.Z);
        return new ModelBounds(minX, minY, minZ, Math.Max(.000001, maxX - minX), Math.Max(.000001, maxY - minY), Math.Max(.000001, maxZ - minZ), (minX + maxX) / 2, (minY + maxY) / 2);
    }

    readonly record struct Pixel(byte B, byte G, byte R, byte A);
    sealed record PatternImage(int Width, int Height, int Stride, byte[] Pixels, bool IsHighContrastMonochrome)
    {
        public Pixel RepeatingPixel(double u, double v)
        {
            u = Wrap(u); v = Wrap(v);
            var pixel = Pixel(u, v);
            var horizontal = SeamBlendWeight(u);
            if (horizontal > 0) pixel = Mix(pixel, Pixel(Wrap(u + .5), v), horizontal);
            var vertical = SeamBlendWeight(v);
            if (vertical > 0) pixel = Mix(pixel, Pixel(u, Wrap(v + .5)), vertical);
            return pixel;
        }

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

        static Pixel Mix(Pixel first, Pixel second, double amount)
        {
            byte Blend(byte a, byte b) => (byte)Math.Clamp(Math.Round(a * (1 - amount) + b * amount), 0, 255);
            return new Pixel(Blend(first.B, second.B), Blend(first.G, second.G), Blend(first.R, second.R), Blend(first.A, second.A));
        }
    }
    readonly record struct ModelBounds(double MinX, double MinY, double MinZ, double SizeX, double SizeY, double SizeZ, double CenterX, double CenterY);
}
