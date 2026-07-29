using System.IO.Compression;
using System.Text.Json;

namespace PolyChrom3MF.Logos;

public sealed class LogoProjectStore
{
    const string ManifestEntry = "polylogo/project.json";
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        IncludeFields = true
    };

    public void Save(Stream destination, LogoProject project)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(project);
        Validate(project);
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var asset in project.Assets)
        {
            var entry = archive.CreateEntry($"polylogo/assets/{asset.Id:N}.png", CompressionLevel.Optimal);
            using var stream = entry.Open();
            stream.Write(asset.Png);
        }
        var stored = project with
        {
            Assets = project.Assets.Select(asset => asset with { Png = [] }).ToList()
        };
        var manifest = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
        using var manifestStream = manifest.Open();
        JsonSerializer.Serialize(manifestStream, stored, JsonOptions);
    }

    public LogoProject Load(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count)
            throw new InvalidDataException("Le projet contient des entrées dupliquées.");
        var manifest = archive.GetEntry(ManifestEntry) ?? throw new InvalidDataException("Projet de logos incomplet.");
        LogoProject project;
        using (var stream = manifest.Open())
            project = JsonSerializer.Deserialize<LogoProject>(stream, JsonOptions)
                      ?? throw new InvalidDataException("Projet de logos invalide.");
        if (project.SchemaVersion != 1) throw new InvalidDataException("Version de projet de logos non prise en charge.");
        var assets = new List<LogoAsset>(project.Assets.Count);
        foreach (var asset in project.Assets)
        {
            var entry = archive.GetEntry($"polylogo/assets/{asset.Id:N}.png")
                        ?? throw new InvalidDataException($"Image absente pour « {asset.Name} ».");
            if (entry.Length <= 0 || entry.Length > int.MaxValue)
                throw new InvalidDataException($"Taille d'image invalide pour « {asset.Name} ».");
            using var stream = entry.Open();
            var png = new byte[(int)entry.Length];
            stream.ReadExactly(png);
            assets.Add(asset with { Png = png });
        }
        var expectedEntries = new HashSet<string>(
            project.Assets.Select(asset => $"polylogo/assets/{asset.Id:N}.png"),
            StringComparer.Ordinal)
        {
            ManifestEntry
        };
        if (archive.Entries.Any(entry => !expectedEntries.Contains(entry.FullName)) ||
            archive.Entries.Count != expectedEntries.Count)
            throw new InvalidDataException("Le projet contient des fichiers inattendus.");
        project = project with { Assets = assets };
        Validate(project);
        return project;
    }

    public static void Validate(LogoProject project)
    {
        if (project.SchemaVersion != 1) throw new InvalidDataException("Version de projet invalide.");
        var assetIds = new HashSet<Guid>();
        foreach (var asset in project.Assets)
        {
            if (asset.Id == Guid.Empty || !assetIds.Add(asset.Id)) throw new InvalidDataException("Identifiant d'image invalide.");
            if (string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Length > 260) throw new InvalidDataException("Nom d'image invalide.");
            if (asset.Png.Length > 0)
            {
                var raster = LogoImageImporter.DecodeNormalizedPng(asset.Png, asset.Name);
                if (raster.Width != asset.Width || raster.Height != asset.Height)
                    throw new InvalidDataException($"Dimensions incohérentes pour « {asset.Name} ».");
            }
        }
        var instanceIds = new HashSet<Guid>();
        foreach (var layer in project.Layers)
        {
            if (layer.Id == Guid.Empty) throw new InvalidDataException("Identifiant de calque invalide.");
            foreach (var instance in layer.Instances)
            {
                if (instance.Id == Guid.Empty || !instanceIds.Add(instance.Id)) throw new InvalidDataException("Identifiant de logo invalide.");
                if (!assetIds.Contains(instance.AssetId)) throw new InvalidDataException($"Image absente pour « {instance.Name} ».");
                LogoProjector.Validate(instance);
            }
        }
    }
}
