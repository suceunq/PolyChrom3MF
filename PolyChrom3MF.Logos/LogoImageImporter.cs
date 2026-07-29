using System.Xml;
using System.Xml.Linq;
using SkiaSharp;
using Svg.Skia;

namespace PolyChrom3MF.Logos;

public sealed class LogoImageImporter
{
    static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".svg"
    };

    public LogoRaster Import(string path, int svgRasterWidth = 2048, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath);
        if (!SupportedExtensions.Contains(extension))
            throw new InvalidDataException("Format non pris en charge. Utilisez PNG, JPG, JPEG, WebP ou SVG.");
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Image introuvable.", fullPath);
        cancellationToken.ThrowIfCancellationRequested();
        return extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            ? ImportSvg(fullPath, svgRasterWidth, cancellationToken)
            : ImportRaster(fullPath, cancellationToken);
    }

    public LogoRaster Import(Stream stream, LogoImageFormat format, string displayName, int svgRasterWidth = 2048,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new InvalidDataException("Le flux de l'image est illisible.");
        cancellationToken.ThrowIfCancellationRequested();
        return format == LogoImageFormat.Svg
            ? ImportSvg(stream, displayName, svgRasterWidth, cancellationToken)
            : ImportRaster(stream, format, displayName, cancellationToken);
    }

    static LogoRaster ImportRaster(string path, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        return ImportRaster(stream, FormatFromExtension(Path.GetExtension(path)), Path.GetFileName(path), cancellationToken);
    }

    static LogoRaster ImportRaster(Stream stream, LogoImageFormat requestedFormat, string displayName,
        CancellationToken cancellationToken)
    {
        using var managed = new SKManagedStream(stream, false);
        using var codec = SKCodec.Create(managed) ?? throw new InvalidDataException("Image corrompue ou illisible.");
        var info = codec.Info;
        ValidateDimensions(info.Width, info.Height);
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException($"Décodage de l'image impossible ({result}).");
        cancellationToken.ThrowIfCancellationRequested();
        var format = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => LogoImageFormat.Png,
            SKEncodedImageFormat.Jpeg => LogoImageFormat.Jpeg,
            SKEncodedImageFormat.Webp => LogoImageFormat.WebP,
            _ => requestedFormat
        };
        return FromBitmap(bitmap, format, displayName);
    }

    static LogoRaster ImportSvg(string path, int width, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        return ImportSvg(stream, Path.GetFileName(path), width, cancellationToken);
    }

    static LogoRaster ImportSvg(Stream source, string displayName, int width, CancellationToken cancellationToken)
    {
        if (width < 16) throw new ArgumentOutOfRangeException(nameof(width));
        var safeSvg = SanitizeSvg(source);
        cancellationToken.ThrowIfCancellationRequested();
        using var svg = new SKSvg();
        using var stream = new MemoryStream(safeSvg);
        var picture = svg.Load(stream) ?? throw new InvalidDataException("SVG vide ou invalide.");
        var bounds = picture.CullRect;
        if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidDataException("Dimensions SVG invalides.");
        var height = checked((int)Math.Max(1, Math.Round(width * bounds.Height / bounds.Width)));
        ValidateDimensions(width, height);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(width / bounds.Width, height / bounds.Height);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
        canvas.Flush();
        cancellationToken.ThrowIfCancellationRequested();
        return FromBitmap(bitmap, LogoImageFormat.Svg, displayName);
    }

    static byte[] SanitizeSvg(Stream source)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true
        };
        using var reader = XmlReader.Create(source, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        if (document.Root?.Name.LocalName != "svg") throw new InvalidDataException("Document SVG invalide.");
        foreach (var unsafeElement in document.Descendants()
                     .Where(element => element.Name.LocalName is "script" or "foreignObject" or "iframe" or "audio" or "video")
                     .ToList())
            unsafeElement.Remove();
        foreach (var style in document.Descendants().Where(element => element.Name.LocalName == "style").ToList())
        {
            var css = style.Value;
            if (css.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
                css.Contains("javascript:", StringComparison.OrdinalIgnoreCase) ||
                (css.Contains("url(", StringComparison.OrdinalIgnoreCase) &&
                 !css.Contains("url(#", StringComparison.OrdinalIgnoreCase)))
                style.Remove();
        }
        foreach (var attribute in document.Root.DescendantsAndSelf().Attributes().ToList())
        {
            var value = attribute.Value.Trim();
            var isHref = attribute.Name.LocalName is "href" or "src";
            if (isHref && !value.StartsWith('#') && !value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                attribute.Remove();
            else if (value.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
                attribute.Remove();
            else if (value.Contains("url(", StringComparison.OrdinalIgnoreCase) &&
                     !value.Contains("url(#", StringComparison.OrdinalIgnoreCase))
                attribute.Remove();
        }
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = System.Text.Encoding.UTF8, Indent = false }))
            document.Save(writer);
        return output.ToArray();
    }

    static LogoRaster FromBitmap(SKBitmap bitmap, LogoImageFormat format, string displayName)
    {
        var bytes = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return new LogoRaster(bitmap.Width, bitmap.Height, bytes, format, displayName);
    }

    public static byte[] EncodePng(LogoRaster raster)
    {
        ValidateRaster(raster);
        using var bitmap = new SKBitmap(new SKImageInfo(raster.Width, raster.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(raster.Rgba, 0, bitmap.GetPixels(), raster.Rgba.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? throw new InvalidDataException("Encodage PNG impossible.");
    }

    public static LogoRaster DecodeNormalizedPng(byte[] png, string displayName = "logo.png")
    {
        using var stream = new MemoryStream(png, writable: false);
        return ImportRaster(stream, LogoImageFormat.Png, displayName, CancellationToken.None);
    }

    public static void ValidateRaster(LogoRaster raster)
    {
        ValidateDimensions(raster.Width, raster.Height);
        if (raster.Rgba.LongLength != checked((long)raster.Width * raster.Height * 4))
            throw new InvalidDataException("Tampon RGBA incohérent.");
    }

    static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new InvalidDataException("Dimensions d'image invalides.");
        var pixels = checked((long)width * height);
        if (pixels > int.MaxValue / 4)
            throw new InvalidDataException("L'image dépasse la capacité mémoire adressable.");
    }

    static LogoImageFormat FormatFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => LogoImageFormat.Png,
        ".jpg" or ".jpeg" => LogoImageFormat.Jpeg,
        ".webp" => LogoImageFormat.WebP,
        ".svg" => LogoImageFormat.Svg,
        _ => throw new InvalidDataException("Format d'image inconnu.")
    };
}
