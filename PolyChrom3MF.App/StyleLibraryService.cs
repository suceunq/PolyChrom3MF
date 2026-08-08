using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace PolyChrom3MF.App;

public sealed record PolyStyleData(
    string Name,
    string Description,
    List<string> Colors,
    PatternSettings? Pattern,
    List<ColorLayerKind> LayerKinds,
    string PrinterName,
    int MaterialSlots,
    DateTimeOffset CreatedAt);

public sealed record PolyStyleItem(string Path, PolyStyleData Data)
{
    public string Label => $"{Data.Name} · {Data.Colors.Count} couleurs";
}

public sealed class StyleLibraryService
{
    public string LibraryFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PolyChrom 3MF", "Styles");

    public string Save(string path, PolyStyleData style)
    {
        Validate(style);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp";
        try
        {
            using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create);
            var data = style.Pattern is null ? style : style with { Pattern = style.Pattern with { ImagePath = "assets/motif.png" } };
            var entry = archive.CreateEntry("style.json", CompressionLevel.Optimal);
            using (var stream = entry.Open()) JsonSerializer.Serialize(stream, data);
            if (style.Pattern is not null)
            {
                PatternService.ValidateSettings(style.Pattern);
                var motif = archive.CreateEntry("assets/motif.png", CompressionLevel.Optimal);
                using var source = File.OpenRead(style.Pattern.ImagePath);
                using var destination = motif.Open();
                source.CopyTo(destination);
            }
            archive.Dispose();
            output.Dispose();
            File.Move(temporary, fullPath, true);
            return fullPath;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    public PolyStyleData Load(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 1 or > 2) throw new InvalidDataException("Le style contient des fichiers inattendus.");
        if (archive.Entries.Select(item => item.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count)
            throw new InvalidDataException("Le style contient des chemins dupliqués.");
        var entry = archive.GetEntry("style.json") ?? throw new InvalidDataException("Le style est incomplet.");
        if (entry.Length <= 0 || entry.Length > 4L * 1024 * 1024)
            throw new InvalidDataException("Les réglages du style sont absents ou trop volumineux.");
        PolyStyleData style;
        using (var stream = entry.Open()) style = JsonSerializer.Deserialize<PolyStyleData>(stream) ?? throw new InvalidDataException("Le style est invalide.");
        Validate(style);
        if (style.Pattern is null) return style;
        if (style.Pattern.ImagePath != "assets/motif.png") throw new InvalidDataException("Le chemin du motif est invalide.");
        var motif = archive.GetEntry("assets/motif.png") ?? throw new InvalidDataException("Le motif du style est absent.");
        if (motif.Length <= 0 || motif.Length > int.MaxValue)
            throw new InvalidDataException("Le motif du style est absent ou trop volumineux.");
        Directory.CreateDirectory(LibraryFolder);
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..16];
        var motifPath = Path.Combine(LibraryFolder, identity + ".png");
        var temporary = motifPath + ".tmp";
        try
        {
            using (var source = motif.Open())
            using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                source.CopyTo(destination);
            if (new FileInfo(temporary).Length != motif.Length)
                throw new InvalidDataException("Le motif du style est incomplet.");
            File.Move(temporary, motifPath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
        return style with { Pattern = style.Pattern with { ImagePath = motifPath } };
    }

    public IReadOnlyList<PolyStyleItem> List()
    {
        Directory.CreateDirectory(LibraryFolder);
        var result = new List<PolyStyleItem>();
        foreach (var path in Directory.EnumerateFiles(LibraryFolder, "*.polystyle"))
            try { result.Add(new PolyStyleItem(path, Load(path))); } catch { }
        return result.OrderByDescending(item => item.Data.CreatedAt).ToList();
    }

    public string AddToLibrary(string sourcePath)
    {
        var style = Load(sourcePath);
        Directory.CreateDirectory(LibraryFolder);
        var name = string.Concat(style.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var destination = Path.Combine(LibraryFolder, $"{name}-{Guid.NewGuid():N}.polystyle");
        File.Copy(sourcePath, destination);
        return destination;
    }

    public static void Validate(PolyStyleData style)
    {
        if (style is null || string.IsNullOrWhiteSpace(style.Name) || style.Name.Length > 100 ||
            style.Description?.Length > 1000 || style.Colors is null || style.Colors.Count is < 2 or > 32 ||
            style.Colors.Any(value => !System.Text.RegularExpressions.Regex.IsMatch(value ?? "", "^#[0-9A-Fa-f]{6}$")) ||
            style.LayerKinds is null || style.LayerKinds.Count > 64 || style.LayerKinds.Any(kind => !Enum.IsDefined(kind)) ||
            style.MaterialSlots is < 1 or > 64)
            throw new InvalidDataException("Le style PolyChrom contient des paramètres invalides.");
        if (style.Pattern is not null) PatternService.ValidateSettings(style.Pattern);
    }
}
