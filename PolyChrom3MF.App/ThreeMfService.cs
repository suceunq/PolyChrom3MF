using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace PolyChrom3MF.App;

public sealed class ThreeMfService
{
    const long MaxUncompressed = 512L * 1024 * 1024;
    const int MaxEntries = 500;
    const double MaxCompressionRatio = 250;
    internal static readonly XNamespace Core = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";

    public ModelDocument Read(string path)
    {
        if (!File.Exists(path) || !path.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sélectionnez un fichier .3mf valide.");
        if (new FileInfo(path).Length == 0) throw new InvalidDataException("Le fichier 3MF est vide.");

        try
        {
            using var zip = ZipFile.OpenRead(path);
            ValidateArchive(zip);
            var mainEntry = FindModelEntry(zip) ?? throw new InvalidDataException("Document modèle 3MF absent.");
            var modelEntries = zip.Entries.Where(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase) && e.Length > 0).ToList();
            var parts = new List<(ZipArchiveEntry Entry, XDocument Xml)>();
            foreach (var entry in modelEntries)
            {
                var xml = LoadSecureXml(entry);
                if (xml.Root?.Name != Core + "model") throw new InvalidDataException($"Espace de noms 3MF non pris en charge dans {entry.FullName}.");
                parts.Add((entry, xml));
            }

            var mainXml = parts.First(p => p.Entry.FullName.Equals(mainEntry.FullName, StringComparison.OrdinalIgnoreCase)).Xml;
            var objects = new List<ModelObject>();
            var index = 0;
            foreach (var part in parts)
            {
                var unit = part.Xml.Root!.Attribute("unit")?.Value ?? "millimeter";
                var scale = UnitToMillimeters(unit);
                foreach (var element in part.Xml.Descendants(Core + "object"))
                {
                    var mesh = element.Element(Core + "mesh");
                    if (mesh is null) continue;
                    var vertices = mesh.Descendants(Core + "vertex")
                        .Select(v => new Vertex(Number(v, "x") * scale, Number(v, "y") * scale, Number(v, "z") * scale)).ToList();
                    var triangles = mesh.Descendants(Core + "triangle")
                        .Select(t => new Triangle(Integer(t, "v1"), Integer(t, "v2"), Integer(t, "v3"))).ToList();
                    if (vertices.Count == 0 || triangles.Count == 0) continue;
                    if (triangles.Any(t => t.A < 0 || t.B < 0 || t.C < 0 || t.A >= vertices.Count || t.B >= vertices.Count || t.C >= vertices.Count))
                        throw new InvalidDataException($"L’objet {element.Attribute("id")?.Value} contient des indices de triangles invalides.");
                    objects.Add(new ModelObject(index++, element.Attribute("id")?.Value ?? index.ToString(), vertices, triangles, part.Entry.FullName));
                }
            }

            if (objects.Count == 0) throw new InvalidDataException("Aucun maillage exploitable n’a été trouvé dans les fragments 3MF.");
            var all = objects.SelectMany(x => x.Vertices).ToArray();
            var componentCount = parts.Sum(p => p.Xml.Descendants(Core + "component").Count());
            var existingColors = parts.Sum(p => p.Xml.Descendants(Core + "base").Count() + p.Xml.Descendants().Count(x => x.Name.LocalName == "color"));
            var warnings = new List<string>();
            if (parts.Count > 1) warnings.Add($"Structure 3MF multipartie détectée ({parts.Count} fragments). ");
            if (objects.Count == 1 && componentCount == 0) warnings.Add("Objet fusionné : aucune séparation sémantique ne sera inventée.");

            return new ModelDocument(path, mainXml, mainEntry.FullName, objects,
                all.Max(x => x.X) - all.Min(x => x.X), all.Max(x => x.Y) - all.Min(x => x.Y), all.Max(x => x.Z) - all.Min(x => x.Z),
                zip.Entries.Select(e => e.FullName).ToList(), objects.Sum(o => o.Triangles.Count),
                warnings.Count == 0 ? null : string.Join(" ", warnings), mainXml.Root!.Attribute("unit")?.Value ?? "millimeter", componentCount, existingColors, "3MF");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or XmlException or NotSupportedException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Le fichier est corrompu ou incompatible avec le standard 3MF.", ex);
        }
    }

    public void Export(ModelDocument document, ColorProposal proposal, string destination) => ExportAndValidate(document, proposal, destination, true);

    public string ExportAndValidate(ModelDocument document, ColorProposal proposal, string destination, bool verify)
    {
        if (proposal.Colors.Count is < 4 or > 32) throw new InvalidDataException("Une proposition doit contenir entre quatre et trente-deux couleurs.");
        var fullDestination = Path.GetFullPath(destination);
        if (!fullDestination.EndsWith(".3mf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("L’export doit porter l’extension .3mf.");
        var destinationFolder = Path.GetDirectoryName(fullDestination)!;
        Directory.CreateDirectory(destinationFolder);
        var workingDestination = Path.Combine(destinationFolder, $".{Path.GetFileNameWithoutExtension(fullDestination)}.{Guid.NewGuid():N}.tmp.3mf");
        try
        {
        if (document.SourceFormat == "STL") CreatePackage(workingDestination, document.Xml);
        else File.Copy(document.Path, workingDestination, true);

        using (var zip = ZipFile.Open(workingDestination, ZipArchiveMode.Update))
        {
            var partNames = document.Objects.Select(o => o.PartPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var partName in partNames)
            {
                var entry = zip.GetEntry(partName) ?? throw new InvalidDataException($"Fragment 3MF absent pendant l’export : {partName}");
                var xml = LoadSecureXml(entry);
                var matches = document.Objects.Where(o => o.PartPath.Equals(partName, StringComparison.OrdinalIgnoreCase)).ToList();
                var resources = xml.Root!.Element(Core + "resources") ?? new XElement(Core + "resources");
                if (resources.Parent is null) xml.Root.AddFirst(resources);
                resources.Elements(Core + "basematerials").Where(x => (string?)x.Attribute("id") == "999").Remove();
                var materials = new XElement(Core + "basematerials", new XAttribute("id", "999"));
                foreach (var color in proposal.Colors)
                    materials.Add(new XElement(Core + "base", new XAttribute("name", color.Name), new XAttribute("displaycolor", color.Hex.ToUpperInvariant())));
                resources.Add(materials);
                foreach (var obj in xml.Descendants(Core + "object"))
                {
                    var match = matches.FirstOrDefault(o => o.Id == (string?)obj.Attribute("id") && obj.Element(Core + "mesh") is not null);
                    if (match is null) continue;
                    obj.SetAttributeValue("pid", "999");
                    obj.SetAttributeValue("pindex", proposal.Assignments.GetValueOrDefault(match.Index, match.Index % proposal.Colors.Count));
                    if (proposal.TriangleAssignments.TryGetValue(match.Index, out var triangleColors))
                    {
                        var triangleElements = obj.Element(Core + "mesh")?.Element(Core + "triangles")?.Elements(Core + "triangle").ToList() ?? [];
                        for (var triangleIndex = 0; triangleIndex < Math.Min(triangleElements.Count, triangleColors.Length); triangleIndex++)
                        {
                            var colorIndex = Math.Clamp(triangleColors[triangleIndex], 0, proposal.Colors.Count - 1);
                            triangleElements[triangleIndex].SetAttributeValue("pid", "999");
                            triangleElements[triangleIndex].SetAttributeValue("p1", colorIndex);
                            triangleElements[triangleIndex].SetAttributeValue("p2", colorIndex);
                            triangleElements[triangleIndex].SetAttributeValue("p3", colorIndex);
                        }
                    }
                }
                entry.Delete();
                var replacement = zip.CreateEntry(partName, CompressionLevel.Optimal);
                using var output = replacement.Open();
                using var writer = XmlWriter.Create(output, SafeWriterSettings());
                xml.Save(writer);
            }
        }

        var report = $"{document.Objects.Count} objets · {document.TriangleCount:N0} triangles · {proposal.Colors.Count} couleurs";
        if (verify)
        {
            var reopened = Read(workingDestination);
            if (reopened.Objects.Count != document.Objects.Count || reopened.TriangleCount != document.TriangleCount)
                throw new InvalidDataException("La vérification après export a échoué : objets ou triangles différents.");
            if (Math.Abs(reopened.SizeX - document.SizeX) > .0001 || Math.Abs(reopened.SizeY - document.SizeY) > .0001 || Math.Abs(reopened.SizeZ - document.SizeZ) > .0001)
                throw new InvalidDataException("La vérification après export a échoué : dimensions différentes.");
            if (reopened.ExistingColorCount < proposal.Colors.Count)
                throw new InvalidDataException("La vérification après export a échoué : matériaux absents.");
            report = $"{reopened.Objects.Count} objets · {reopened.TriangleCount:N0} triangles · dimensions identiques · {proposal.Colors.Count} couleurs";
        }
        File.Move(workingDestination, fullDestination, true);
        return report;
        }
        finally
        {
            try { if (File.Exists(workingDestination)) File.Delete(workingDestination); } catch { }
        }
    }

    static void CreatePackage(string destination, XDocument model)
    {
        using var file = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        SaveEntry(zip, "[Content_Types].xml", new XDocument(new XElement(types + "Types",
            new XElement(types + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(types + "Default", new XAttribute("Extension", "model"), new XAttribute("ContentType", "application/vnd.ms-package.3dmanufacturing-3dmodel+xml")))));
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        SaveEntry(zip, "_rels/.rels", new XDocument(new XElement(rel + "Relationships", new XElement(rel + "Relationship", new XAttribute("Target", "/3D/3dmodel.model"), new XAttribute("Id", "rel0"), new XAttribute("Type", "http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel")))));
        SaveEntry(zip, "3D/3dmodel.model", model);
    }

    static void SaveEntry(ZipArchive zip, string name, XDocument xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open(); using var writer = XmlWriter.Create(stream, SafeWriterSettings()); xml.Save(writer);
    }

    static XmlWriterSettings SafeWriterSettings() => new() { Encoding = new System.Text.UTF8Encoding(false), Indent = false };
    static void ValidateArchive(ZipArchive zip)
    {
        if (zip.Entries.Count == 0) throw new InvalidDataException("L’archive 3MF est vide.");
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("Archive refusée : trop d’entrées.");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Any(p => p == "..")) throw new InvalidDataException("Archive refusée : chemin ZIP non sûr.");
            checked { total += entry.Length; }
            if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > MaxCompressionRatio)
                throw new InvalidDataException("Archive refusée : taux de compression suspect.");
        }
        if (total > MaxUncompressed) throw new InvalidDataException("Archive refusée : taille décompressée excessive.");
    }

    static ZipArchiveEntry? FindModelEntry(ZipArchive zip) => zip.GetEntry("3D/3dmodel.model") ?? zip.Entries.FirstOrDefault(x => x.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
    static XDocument LoadSecureXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 100_000_000, MaxCharactersFromEntities = 0 });
        return XDocument.Load(reader, LoadOptions.None);
    }
    static double Number(XElement element, string name) => double.Parse(element.Attribute(name)?.Value ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);
    static int Integer(XElement element, string name) => int.Parse(element.Attribute(name)?.Value ?? "-1", NumberStyles.Integer, CultureInfo.InvariantCulture);
    internal static double UnitToMillimeters(string unit) => unit.ToLowerInvariant() switch { "micron" => .001, "centimeter" => 10, "inch" => 25.4, "foot" => 304.8, "meter" => 1000, _ => 1 };
}

public sealed record Vertex(double X, double Y, double Z);
public sealed record Triangle(int A, int B, int C);
public sealed record ModelObject(int Index, string Id, List<Vertex> Vertices, List<Triangle> Triangles, string PartPath)
{
    public override string ToString() => $"Objet {Id} — {Triangles.Count:N0} triangles";
}
public sealed record ModelDocument(string Path, XDocument Xml, string ModelEntry, List<ModelObject> Objects, double SizeX, double SizeY, double SizeZ, List<string> Entries, int TriangleCount, string? Warning, string Unit, int ComponentCount, int ExistingColorCount, string SourceFormat);
public sealed record PaletteColor(string Name, string Hex)
{
    public System.Windows.Media.Color Color => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(Hex)!;
    public SolidColorBrush Brush => new(Color);
    public string Label => $"{Name} {Hex}";
}
public sealed record ColorProposal(string Name, string Description, List<PaletteColor> Colors, Dictionary<int, int> Assignments)
{
    public Dictionary<int, int[]> TriangleAssignments { get; init; } = [];
}
